using AITool.Domain.Codex;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace AITool.Web.Services;

/// <summary>
/// 发布轮换后的凭证：账号五列先独立事务落库，站点 ApiKey 同步随后独立提交。
/// 写序合同（788394c 八轮反审确立，写序测试锁定）：偏态永远落在可自愈的
/// 「账号新 / site 旧」侧——site 写失败只保留旧值并告警，账号凭证绝不回滚丢
/// 失（下一次 401 刷新会重写 site）。绝不能改成单事务原子提交：那会把失败推向
/// 「凭证丢失」的不可自愈侧（234557a 曾引入，写序测试当场抓回）。
/// </summary>
internal static class CodexCredentialPersistence
{
    internal static async Task SaveAsync(AppDbContext db, CodexAccount account, CancellationToken ct, ILogger? logger = null)
    {
        // 第一段：账号凭证五列（独立事务，先行提交）。
        using (var client = db.Client.CopyNew())
        {
            client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
            await client.Ado.BeginTranAsync();
            try
            {
                var count = await client.Updateable(account)
                    .UpdateColumns(a => new { a.AccessToken, a.RefreshToken, a.IdToken, a.TokenExpiresAt, a.LastRefreshAt })
                    .ExecuteCommandAsync(ct);
                if (count != 1) throw new InvalidOperationException("Codex account no longer exists.");
                await client.Ado.CommitTranAsync();
            }
            catch
            {
                await client.Ado.RollbackTranAsync();
                throw;
            }
        }

        // 第二段：站点 ApiKey 同步（独立事务；失败不抛——可自愈偏态，账号凭证已安全落库）。
        try
        {
            using var siteClient = db.Client.CopyNew();
            siteClient.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
            await siteClient.Ado.BeginTranAsync();
            try
            {
                var siteCount = await siteClient.Updateable<Site>()
                    .SetColumns(s => new Site { ApiKey = account.AccessToken ?? string.Empty })
                    .Where(s => s.Id == account.LinkedSiteId)
                    .ExecuteCommandAsync(ct);
                if (siteCount != 1) throw new InvalidOperationException("Codex linked site no longer exists.");
                await siteClient.Ado.CommitTranAsync();
            }
            catch
            {
                await siteClient.Ado.RollbackTranAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Codex linked site ApiKey sync failed for account {Id}; site keeps the old key (self-healing on next refresh)",
                account.Id);
        }
    }
}
