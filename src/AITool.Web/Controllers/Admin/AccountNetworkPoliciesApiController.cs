using AITool.Application.Networking;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Web.Contracts;
using AITool.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

[ApiController]
[Authorize]
[Route("api/admin/oauth/network-policies")]
[ServiceFilter(typeof(OAuthFeatureToggleAttribute))]
public sealed class AccountNetworkPoliciesApiController(
    AppDbContext db, IAccountNetworkPolicyStore store, IAccountNetworkPolicyResolver resolver) : ControllerBase
{
    // Enabled only when all login, refresh, quota and forwarding paths have been verified.
    public const bool PipelineReady = true;

    [HttpGet("capabilities")]
    public IActionResult Capabilities() => Ok(ApiResponse.Ok(new { ready = PipelineReady }));

    // Discovery deliberately does not depend on the developer settings-page visibility gate.
    [HttpGet("proxy-options")]
    public async Task<IActionResult> ProxyOptions(CancellationToken ct)
    {
        var profiles = await db.ProxyProfiles.OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync(ct);
        return Ok(ApiResponse.Ok(profiles.Select(p => new
        {
            p.Id, p.Name, p.IsEnabled, p.Revision,
            HasCredentials = ProxyProfileSecretService.HasCredentials(p)
        })));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? providerKey, [FromQuery] Guid? accountId, CancellationToken ct)
    {
        if (providerKey is not null && !ValidProvider(providerKey) || accountId == Guid.Empty)
            return BadRequest(ApiResponse.Fail("Invalid account identity.", "invalid_account_identity"));
        var accounts = await Accounts(ct);
        return Ok(ApiResponse.Ok(await Summaries(accounts.Where(a =>
            (providerKey is null || a.ProviderKey == providerKey) && (accountId is null || a.AccountId == accountId)).ToArray(), ct)));
    }

    [HttpPost("summary")]
    public async Task<IActionResult> Summary([FromBody] AccountNetworkSummaryRequest request, CancellationToken ct)
    {
        if (request?.Accounts is null || request.Accounts.Length > 1000 || request.Accounts.Any(a => !ValidAccount(a)))
            return BadRequest(ApiResponse.Fail("Invalid account identities (maximum 1000).", "invalid_account_identity"));
        var existing = (await Accounts(ct)).ToHashSet();
        return Ok(ApiResponse.Ok(await Summaries(request.Accounts.Distinct().Where(existing.Contains).ToArray(), ct)));
    }

    [HttpPut]
    public async Task<IActionResult> Set([FromBody] AccountNetworkPolicyRequest request, CancellationToken ct)
    {
        if (request is null || !ValidAccount(new(request.ProviderKey, request.AccountId)) || request.NetworkSelection is null)
            return BadRequest(ApiResponse.Fail("An explicit network selection and account identity are required.", "invalid_network_selection"));
        try
        {
            await store.SetAsync(new(request.ProviderKey, request.AccountId), request.NetworkSelection, request.ExpectedRevision, ct);
            return Ok(ApiResponse.Ok((await Summaries([new(request.ProviderKey, request.AccountId)], ct)).Single()));
        }
        catch (NetworkPolicyException e) { return PolicyError(e); }
    }

    [HttpPost("batch")]
    public async Task<IActionResult> Batch([FromBody] AccountNetworkBatchRequest request, CancellationToken ct)
    {
        if (request?.Accounts is null || request.Accounts.Length is < 1 or > 1000
            || request.Accounts.Any(a => !ValidAccount(a)) || request.NetworkSelection is null)
            return BadRequest(ApiResponse.Fail("Accounts and an explicit network selection are required (maximum 1000).", "invalid_network_selection"));
        var results = new List<object>();
        var succeeded = 0;
        foreach (var account in request.Accounts.Distinct())
        {
            try
            {
                await store.SetAsync(account, request.NetworkSelection, cancellationToken: ct);
                succeeded++;
                results.Add(new { account.ProviderKey, account.AccountId, Success = true, ErrorCode = (string?)null, Error = (string?)null });
            }
            catch (NetworkPolicyException e)
            {
                results.Add(new { account.ProviderKey, account.AccountId, Success = false, ErrorCode = e.Code, Error = e.Message });
            }
        }
        return Ok(ApiResponse.Ok(new { results, succeeded, failed = results.Count - succeeded }));
    }

    private async Task<List<AccountIdentity>> Accounts(CancellationToken ct)
    {
        // Project IDs only: summary/discovery must never materialize token-bearing accounts.
        var result = new List<AccountIdentity>();
        result.AddRange((await db.CodexAccounts.Select(a => a.Id).ToListAsync(ct)).Select(id => new AccountIdentity("codex", id)));
        result.AddRange((await db.GoogleAccounts.Select(a => a.Id).ToListAsync(ct)).Select(id => new AccountIdentity("google", id)));
        result.AddRange((await db.KimiAccounts.Where(a => !a.IsDeleted).Select(a => a.Id).ToListAsync(ct)).Select(id => new AccountIdentity("kimi", id)));
        result.AddRange((await db.XaiAccounts.Where(a => !a.IsDeleted).Select(a => a.Id).ToListAsync(ct)).Select(id => new AccountIdentity("xai", id)));
        result.AddRange((await db.DeviceOAuthAccounts.Where(a => !a.IsDeleted).Select(a => a.Id).ToListAsync(ct)).Select(id => new AccountIdentity("device-oauth", id)));
        return result;
    }

    private async Task<IReadOnlyList<AccountNetworkSummary>> Summaries(IReadOnlyCollection<AccountIdentity> accounts, CancellationToken ct)
    {
        var policies = (await db.AccountNetworkPolicies.ToListAsync(ct)).ToDictionary(p => new AccountIdentity(p.ProviderKey, p.AccountId));
        var profiles = await db.ProxyProfiles.ToListAsync(ct);
        var names = profiles.ToDictionary(p => p.Id, p => p.Name);
        var errors = new Dictionary<Guid, string?>();
        // Resolve once per referenced profile, never once per account; failure remains fail-closed.
        foreach (var id in accounts.Where(policies.ContainsKey).Select(a => policies[a])
                     .Where(p => p.Mode == NetworkMode.ProxyProfile && p.ProxyProfileId.HasValue).Select(p => p.ProxyProfileId!.Value).Distinct())
        {
            try
            {
                await resolver.ResolveAsync(new(null, "admin-network-summary", new(NetworkMode.ProxyProfile, id)), ct);
                errors[id] = null;
            }
            catch (NetworkPolicyException e) { errors[id] = e.Code; }
        }
        return accounts.Select(account =>
        {
            policies.TryGetValue(account, out var policy);
            var selection = new AccountNetworkSelection(policy?.Mode ?? NetworkMode.Legacy, policy?.ProxyProfileId);
            var id = selection.ProxyProfileId;
            var invalid = !Enum.IsDefined(selection.Mode) || (selection.Mode == NetworkMode.ProxyProfile ? id is null || id == Guid.Empty : id is not null);
            return new AccountNetworkSummary(account.ProviderKey, account.AccountId, selection, policy?.Revision,
                id is { } profileId ? names.GetValueOrDefault(profileId) : null,
                invalid ? "invalid_network_selection" : id is { } selectedId ? errors.GetValueOrDefault(selectedId) : null);
        }).ToArray();
    }

    private IActionResult PolicyError(NetworkPolicyException e) => e.Code == "account_network_revision_conflict"
        ? Conflict(ApiResponse.Fail(e.Message, e.Code))
        : e.Code == "account_not_found" ? NotFound(ApiResponse.Fail(e.Message, e.Code)) : BadRequest(ApiResponse.Fail(e.Message, e.Code));
    private static bool ValidProvider(string? provider) => provider is "codex" or "google" or "kimi" or "xai" or "device-oauth";
    private static bool ValidAccount(AccountIdentity? account) => account is not null && account.AccountId != Guid.Empty && ValidProvider(account.ProviderKey);
}

public sealed record AccountNetworkSummary(string ProviderKey, Guid AccountId, AccountNetworkSelection NetworkSelection,
    string? Revision, string? ProxyProfileName, string? BlockReason);
public sealed class AccountNetworkPolicyRequest
{
    public string ProviderKey { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
    public AccountNetworkSelection? NetworkSelection { get; set; }
    public string? ExpectedRevision { get; set; }
}
public sealed class AccountNetworkSummaryRequest
{
    public AccountIdentity[] Accounts { get; set; } = [];
}
public sealed class AccountNetworkBatchRequest
{
    public AccountIdentity[] Accounts { get; set; } = [];
    public AccountNetworkSelection? NetworkSelection { get; set; }
}
