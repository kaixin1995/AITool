# Run after the central Desktop build; this script never builds or restores anything.
# pwsh -NoProfile -File tests/AITool.Desktop.Smoke/AccountNetworkSerialization.ps1 -DesktopAssemblyPath <absolute path to AITool.Desktop.dll>
# Requires PowerShell 7.4+ (.NET 8+), using the assembly and dependencies from the central build output.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $DesktopAssemblyPath,

    [string] $ViewModelSourcePath = (Join-Path $PSScriptRoot '../../src/AITool.Desktop/ViewModels/CodexViewModel.cs')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or [Environment]::Version.Major -lt 8) {
    throw 'Run with PowerShell 7.4+ on .NET 8+; Windows PowerShell is not supported.'
}

$assembly = [System.Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $DesktopAssemblyPath).Path)
$proxyType = $assembly.GetType('AITool.Desktop.Models.AccountNetworkProxyOption', $true)
$policyType = $assembly.GetType('AITool.Desktop.Models.AccountNetworkPolicy', $true)
$selectionType = $assembly.GetType('AITool.Desktop.Models.AccountNetworkSelection', $true)
$capabilitiesType = $assembly.GetType('AITool.Desktop.Models.AccountNetworkCapabilities', $true)
$apiType = $assembly.GetType('AITool.Desktop.Services.ApiService', $true)
$deserializeResponse = $apiType.GetMethod('DeserializeResponse', [System.Reflection.BindingFlags]'NonPublic,Static')
if ($null -eq $deserializeResponse) { throw 'ApiService.DeserializeResponse was not found.' }

function Assert-Smoke([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

# Static regression guard: both credential endpoints must follow the awaited CAS policy PUT.
# This checks source ordering, not runtime failure behavior; central UI/integration checks remain needed.
$source = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $ViewModelSourcePath).Path)
$saveStart = $source.IndexOf('private async Task SaveAccountAsync()', [StringComparison]::Ordinal)
Assert-Smoke ($saveStart -ge 0) 'Could not locate SaveAccountAsync source.'
$saveEnd = $source.IndexOf('private async Task ResetQuotaAsync(', $saveStart, [StringComparison]::Ordinal)
Assert-Smoke ($saveEnd -gt $saveStart) 'Could not locate the end of SaveAccountAsync source.'
$saveSource = $source.Substring($saveStart, $saveEnd - $saveStart)
$policyPut = $saveSource.IndexOf('await _apiService.SendAsync<AccountNetworkPolicy>(HttpMethod.Put, NetworkApiPath', [StringComparison]::Ordinal)
$cacheRevision = $saveSource.IndexOf('_editingNetworkPolicy = policy;', [StringComparison]::Ordinal)
$codexPut = $saveSource.IndexOf('/api/admin/oauth/accounts/{editingAccount.Id}', [StringComparison]::Ordinal)
$googlePut = $saveSource.IndexOf('GoogleApiPath($"/accounts/{editingAccount.Id}")', [StringComparison]::Ordinal)
Assert-Smoke ($policyPut -ge 0 -and $cacheRevision -gt $policyPut -and $codexPut -gt $cacheRevision -and $googlePut -gt $cacheRevision) 'CAS policy PUT and revision caching must precede both credential PUTs.'
Assert-Smoke ($saveSource.Contains('expectedRevision = originalPolicy?.Revision') -and $saveSource.Contains('AccountPolicyNeedsReload = true;')) 'Save must retain CAS and require reload after policy/conflict/partial failure.'
Assert-Smoke ($saveSource.Contains('网络配置已保存，账号资料更新失败') -and $saveSource.Contains('账号资料 / 凭证请求未发送')) 'Save must distinguish policy-first partial success from a policy failure before credential mutation.'

# Static regression guard: check each literal branch separately, not nearby comments or other statuses.
# Deliberately fail if the assignment stops being a simple ternary so this guard is reviewed too.
$statusMatch = [regex]::Match($source, 'NetworkStatus\s*=\s*options\.Count\s*==\s*0\s*\?\s*"(?<empty>[^"\r\n]*)"\s*:\s*"(?<populated>[^"\r\n]*)"\s*;')
Assert-Smoke $statusMatch.Success 'Could not locate the literal NetworkStatus ternary in RefreshNetworkAsync.'
$emptyStatus = $statusMatch.Groups['empty'].Value
$populatedStatus = $statusMatch.Groups['populated'].Value
Assert-Smoke ($emptyStatus -match '暂无代理方案' -and $emptyStatus -match '显式选择默认或直连' -and $emptyStatus -match '未指定时保留服务端策略') 'Empty status must explain the lack of profiles, available choices, and preservation of the server policy.'
Assert-Smoke ($populatedStatus -match '未指定时保留服务端策略' -and $populatedStatus -match '默认：跟随站点配置[^；]*系统代理[^；]*未配置代理即为直连' -and $populatedStatus -match '直连：不走任何代理') 'Populated status must distinguish the default and direct modes and preserve the unspecified policy.'
Assert-Smoke ($populatedStatus -match '代理方案：失效时阻断请求[^；]*不回退直连') 'Populated status must say that an invalid proxy profile blocks requests without falling back to direct connection.'
Assert-Smoke (-not $emptyStatus.Contains('默认不是直连') -and -not $populatedStatus.Contains('默认不是直连')) 'Neither NetworkStatus branch may claim that default is never direct connection.'

# Exercise the actual ApiService / System.Text.Json options, including API envelope handling.
function Read-ApiFixture([type] $ResponseType, [string] $Json) {
    $method = $deserializeResponse.MakeGenericMethod([type[]]@($ResponseType))
    return ,($method.Invoke($null, [object[]]@($Json)))
}

Assert-Smoke ($proxyType.GetProperty('Revision').PropertyType -eq [Nullable[long]]) 'Proxy revision must be nullable Int64, not string or Int32.'
Assert-Smoke ($policyType.GetProperty('Revision').PropertyType -eq [string]) 'Account-policy revision must remain a string.'
Assert-Smoke ($selectionType.GetProperty('Mode').PropertyType -eq [int]) 'Network mode must use numeric wire values.'
Assert-Smoke ($proxyType.GetProperty('Id').PropertyType -eq [guid]) 'Proxy ID must be a GUID.'
Assert-Smoke ($policyType.GetProperty('AccountId').PropertyType -eq [guid]) 'Account ID must be a GUID.'
Assert-Smoke ($selectionType.GetProperty('ProxyProfileId').PropertyType -eq [Nullable[guid]]) 'Selected profile ID must be a nullable GUID.'

$proxyListType = [System.Collections.Generic.List[object]].GetGenericTypeDefinition().MakeGenericType([type[]]@($proxyType))
$proxyFixture = @'
{"success":true,"data":[
  {"id":"11111111-1111-1111-1111-111111111111","name":"Enabled fixture","isEnabled":true,"revision":1,"hasCredentials":true},
  {"id":"22222222-2222-2222-2222-222222222222","name":"Int64 fixture","isEnabled":false,"revision":9223372036854775807,"hasCredentials":false},
  {"id":"33333333-3333-3333-3333-333333333333","name":"Older fixture","isEnabled":true,"hasCredentials":false},
  {"id":"44444444-4444-4444-4444-444444444444","name":"Null fixture","isEnabled":false,"revision":null,"hasCredentials":false}
]}
'@
$profiles = Read-ApiFixture $proxyListType $proxyFixture
Assert-Smoke ($profiles.Count -eq 4) 'Expected four proxy-option fixtures.'
Assert-Smoke ($profiles[0].Revision -eq 1 -and $profiles[0].IsEnabled -and $profiles[0].HasCredentials) 'Numeric revision / boolean profile fields failed.'
Assert-Smoke ($profiles[1].Revision -eq [long]::MaxValue -and -not $profiles[1].IsEnabled) 'Int64 profile revision was truncated or rejected.'
Assert-Smoke ($null -eq $profiles[2].Revision -and $null -eq $profiles[3].Revision) 'Missing/null profile revisions must remain null.'

$policyListType = [System.Collections.Generic.List[object]].GetGenericTypeDefinition().MakeGenericType([type[]]@($policyType))
$policyFixture = @'
[
  {"providerKey":"codex","accountId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","networkSelection":{"mode":0,"proxyProfileId":null},"revision":null,"blockReason":null,"proxyProfileName":null},
  {"providerKey":"google","accountId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","networkSelection":{"mode":1,"proxyProfileId":null},"revision":"55555555-5555-5555-5555-555555555555","blockReason":null,"proxyProfileName":null},
  {"providerKey":"codex","accountId":"cccccccc-cccc-cccc-cccc-cccccccccccc","networkSelection":{"mode":2,"proxyProfileId":"11111111-1111-1111-1111-111111111111"},"revision":"66666666-6666-6666-6666-666666666666","blockReason":"proxy_profile_disabled","proxyProfileName":"Enabled fixture"}
]
'@
$policies = Read-ApiFixture $policyListType $policyFixture
Assert-Smoke ($policies.Count -eq 3 -and $policies[0].NetworkSelection.Mode -eq 0 -and $policies[1].NetworkSelection.Mode -eq 1 -and $policies[2].NetworkSelection.Mode -eq 2) 'Legacy/Direct/Profile numeric modes failed.'
Assert-Smoke ($null -eq $policies[0].Revision -and $policies[1].Revision -ceq '55555555-5555-5555-5555-555555555555') 'Account policy null/string revisions failed.'
Assert-Smoke ($null -eq $policies[0].NetworkSelection.ProxyProfileId -and $policies[2].NetworkSelection.ProxyProfileId -eq [guid]'11111111-1111-1111-1111-111111111111') 'Nullable profile GUID fields failed.'
Assert-Smoke ($policies[2].BlockReason -ceq 'proxy_profile_disabled' -and $policies[2].ProxyProfileName -ceq 'Enabled fixture') 'Policy status fields failed.'

$notReady = Read-ApiFixture $capabilitiesType '{"success":true,"data":{"ready":false}}'
$ready = Read-ApiFixture $capabilitiesType '{"ready":true}'
Assert-Smoke (-not $notReady.Ready -and $ready.Ready) 'Capability readiness boolean failed.'

$options = [System.Text.Json.JsonSerializerOptions]::new([System.Text.Json.JsonSerializerDefaults]::Web)
$selectionJson = [System.Text.Json.JsonSerializer]::Serialize($policies[2].NetworkSelection, $selectionType, $options)
$selectionDocument = [System.Text.Json.JsonDocument]::Parse($selectionJson)
try {
    Assert-Smoke ($selectionDocument.RootElement.GetProperty('mode').ValueKind -eq [System.Text.Json.JsonValueKind]::Number) 'Outgoing network mode must remain a JSON number.'
} finally {
    $selectionDocument.Dispose()
}
Write-Output 'PASS: Desktop ApiService System.Text.Json network fixtures (numeric Int64 proxy revisions, string policy revisions, numeric modes, GUIDs, booleans, nulls, and API envelopes).'
