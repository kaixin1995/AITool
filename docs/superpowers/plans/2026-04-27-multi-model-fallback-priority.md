# 多模型优先级回退 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让同一个对外模型请求可以按“同名实例优先、再按整体优先级跨模型回退”的顺序依次尝试多个上游模型实例，并记录完整 fallback 过程，同时支持可配置的超时与单路由重试策略。

**Architecture:** 保留现有 `ProxyRouteRule` 作为核心路由表，但扩展它以表达“请求模型”与“实际尝试的上游模型组”之间的关系。代理控制器继续串行遍历候选路由，`ProxyForwardService` 负责单路由内部的超时/重试，`ProxyUsageLog` 负责记录每一次尝试与最终结果，管理端路由页面改为允许为一个对外模型编排多个不同上游模型组的实例顺序。

**Tech Stack:** .NET 10、ASP.NET Core、EF Core(SQLite/EnsureCreated)、Razor Pages、xUnit、FluentAssertions、WebApplicationFactory

---

## File Map

### Existing files to modify
- `src/AITool.Domain/Proxy/ProxyRouteRule.cs` — 路由规则实体，补充上游模型组信息与显示字段。
- `src/AITool.Domain/Proxy/ProxyUsageLog.cs` — 使用日志实体，补充请求链路级日志字段。
- `src/AITool.Application/Proxy/IProxyForwardService.cs` — 扩展转发请求参数，承载超时/重试配置。
- `src/AITool.Application/UsageLogs/IUsageLogService.cs` — 扩展日志 DTO，支持按尝试记录 fallback 过程。
- `src/AITool.Infrastructure/Persistence/AppDbContext.cs` — 更新 EF Core 实体映射与兼容性补丁。
- `src/AITool.Infrastructure/Routing/RouteSelectionService.cs` — 保证候选路由按“模型组优先级 + 组内优先级”稳定返回。
- `src/AITool.Infrastructure/Proxy/ProxyForwardService.cs` — 实现可配置超时与单路由重试。
- `src/AITool.Infrastructure/Proxy/UsageLogService.cs` — 持久化新增的链路日志字段。
- `src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs` — 记录每次尝试、fallback、最终结果。
- `src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs` — 同上。
- `src/AITool.Web/Controllers/Admin/ChatApiController.cs` — 对话测试页与正式代理保持一致的候选顺序与日志字段。
- `src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs` — 管理 API 支持跨模型组发现、保存和回显。
- `src/AITool.Web/Pages/Admin/Routes/Index.cshtml` — 路由规则页面支持从多个模型组装配 fallback 链。
- `src/AITool.Web/Program.cs` — 注册转发配置与数据库兼容补丁。
- `src/AITool.Web/appsettings.json` — 默认代理超时/重试配置。
- `src/AITool.Web/appsettings.Development.json` — 开发环境代理超时/重试配置。
- `tests/AITool.ApplicationTests/Routing/RouteSelectionServiceTests.cs` — 路由顺序规则测试。
- `tests/AITool.ApplicationTests/Proxy/UsageLogServiceTests.cs` — 日志新增字段测试。

### New files to create
- `src/AITool.Application/Proxy/ProxyForwardingOptions.cs` — 代理转发配置对象。
- `tests/AITool.ApplicationTests/Proxy/ProxyForwardServiceTests.cs` — 单路由超时/重试测试。
- `tests/AITool.IntegrationTests/Proxy/ProxyFallbackFlowTests.cs` — 端到端验证 fallback 顺序与日志写入。

---

### Task 1: 扩展路由与日志模型，先把数据结构补齐

**Files:**
- Create: `src/AITool.Application/Proxy/ProxyForwardingOptions.cs`
- Modify: `src/AITool.Domain/Proxy/ProxyRouteRule.cs`
- Modify: `src/AITool.Domain/Proxy/ProxyUsageLog.cs`
- Modify: `src/AITool.Application/Proxy/IProxyForwardService.cs`
- Modify: `src/AITool.Application/UsageLogs/IUsageLogService.cs`
- Modify: `src/AITool.Infrastructure/Persistence/AppDbContext.cs`
- Test: `tests/AITool.ApplicationTests/Proxy/UsageLogServiceTests.cs`

- [ ] **Step 1: 写失败测试，先锁定日志字段与总 Token 行为**

```csharp
[Fact]
public async Task LogAsync_persists_attempt_metadata_for_fallback_flow()
{
    var requestId = Guid.NewGuid();
    var entry = new UsageLogEntry
    {
        AccessKeyId = Guid.NewGuid(),
        ProtocolType = "OpenAI",
        RequestModel = "gpt-5.5",
        AttemptedModel = "glm-5.1",
        TargetSiteId = Guid.NewGuid(),
        Status = "fail",
        Source = "proxy",
        RetryCount = 2,
        AttemptIndex = 3,
        IsFinalResult = false,
        FallbackTriggered = true,
        RequestId = requestId,
        ErrorMessage = "upstream timeout",
        InputTokens = 0,
        OutputTokens = 0
    };

    await _service.LogAsync(entry);

    var log = await _dbContext.ProxyUsageLogs.SingleAsync();
    log.RequestId.Should().Be(requestId);
    log.AttemptedModel.Should().Be("glm-5.1");
    log.AttemptIndex.Should().Be(3);
    log.IsFinalResult.Should().BeFalse();
    log.FallbackTriggered.Should().BeTrue();
    log.ErrorMessage.Should().Be("upstream timeout");
}
```

- [ ] **Step 2: 运行测试确认当前实现失败**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~UsageLogServiceTests.LogAsync_persists_attempt_metadata_for_fallback_flow"`
Expected: FAIL，提示 `UsageLogEntry` 或 `ProxyUsageLog` 缺少 `RequestId`、`AttemptedModel`、`AttemptIndex`、`FallbackTriggered`、`IsFinalResult`、`ErrorMessage` 等属性。

- [ ] **Step 3: 补充路由规则、日志实体和转发配置最小结构**

```csharp
// src/AITool.Domain/Proxy/ProxyRouteRule.cs
public sealed class ProxyRouteRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ExternalModelName { get; set; } = string.Empty;
    public string UpstreamModelName { get; set; } = string.Empty;
    public Guid SiteId { get; set; }
    public string SiteModelName { get; set; } = string.Empty;
    public int Priority { get; set; }
    public int ModelPriority { get; set; }
    public int InstancePriority { get; set; }
    public bool IsEnabled { get; set; } = true;
}

// src/AITool.Domain/Proxy/ProxyUsageLog.cs
public sealed class ProxyUsageLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public Guid AccessKeyId { get; set; }
    public string ProtocolType { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public Guid TargetSiteId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = "proxy";
    public int RetryCount { get; set; }
    public int AttemptIndex { get; set; }
    public bool IsFinalResult { get; set; }
    public bool FallbackTriggered { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}

// src/AITool.Application/Proxy/ProxyForwardingOptions.cs
namespace AITool.Application.Proxy;

public sealed class ProxyForwardingOptions
{
    public const string SectionName = "ProxyForwarding";
    public int RequestTimeoutSeconds { get; set; } = 60;
    public int RetryCount { get; set; } = 0;
}

// src/AITool.Application/Proxy/IProxyForwardService.cs
public sealed class ProxyForwardRequest
{
    public string TargetBaseUrl { get; set; } = string.Empty;
    public string TargetApiKey { get; set; } = string.Empty;
    public string ProtocolType { get; set; } = "OpenAI";
    public string TargetModelName { get; set; } = string.Empty;
    public string RequestBody { get; set; } = string.Empty;
    public int RequestTimeoutSeconds { get; set; }
    public int RetryCount { get; set; }
}

// src/AITool.Application/UsageLogs/IUsageLogService.cs
public sealed class UsageLogEntry
{
    public Guid RequestId { get; set; }
    public Guid AccessKeyId { get; set; }
    public string ProtocolType { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public Guid TargetSiteId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = "proxy";
    public int RetryCount { get; set; }
    public int AttemptIndex { get; set; }
    public bool IsFinalResult { get; set; }
    public bool FallbackTriggered { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
```

- [ ] **Step 4: 更新 EF 映射与 SQLite 兼容补丁**

```csharp
// src/AITool.Infrastructure/Persistence/AppDbContext.cs
modelBuilder.Entity<ProxyRouteRule>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.ExternalModelName).IsRequired().HasMaxLength(200);
    entity.Property(e => e.UpstreamModelName).IsRequired().HasMaxLength(200);
    entity.Property(e => e.SiteModelName).IsRequired().HasMaxLength(200);
    entity.HasIndex(e => new { e.ExternalModelName, e.Priority });
});

modelBuilder.Entity<ProxyUsageLog>(entity =>
{
    entity.HasKey(e => e.Id);
    entity.Property(e => e.ProtocolType).IsRequired().HasMaxLength(50);
    entity.Property(e => e.RequestModel).IsRequired().HasMaxLength(200);
    entity.Property(e => e.AttemptedModel).IsRequired().HasMaxLength(200);
    entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
    entity.Property(e => e.ErrorMessage).IsRequired().HasMaxLength(2000);
    entity.HasIndex(e => e.RequestedAt);
    entity.HasIndex(e => e.RequestId);
});
```

```csharp
// src/AITool.Web/Program.cs
using Microsoft.Extensions.Options;

builder.Services.Configure<ProxyForwardingOptions>(
    builder.Configuration.GetSection(ProxyForwardingOptions.SectionName));

// 启动补丁示意：缺列时补齐 RouteRules / UsageLogs 新字段
cmd.CommandText = "ALTER TABLE ProxyRouteRules ADD COLUMN UpstreamModelName TEXT NOT NULL DEFAULT ''";
cmd.ExecuteNonQuery();
cmd.CommandText = "ALTER TABLE ProxyRouteRules ADD COLUMN ModelPriority INTEGER NOT NULL DEFAULT 0";
cmd.ExecuteNonQuery();
cmd.CommandText = "ALTER TABLE ProxyRouteRules ADD COLUMN InstancePriority INTEGER NOT NULL DEFAULT 0";
cmd.ExecuteNonQuery();
cmd.CommandText = "ALTER TABLE ProxyUsageLogs ADD COLUMN RequestId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'";
cmd.ExecuteNonQuery();
cmd.CommandText = "ALTER TABLE ProxyUsageLogs ADD COLUMN AttemptedModel TEXT NOT NULL DEFAULT ''";
cmd.ExecuteNonQuery();
cmd.CommandText = "ALTER TABLE ProxyUsageLogs ADD COLUMN AttemptIndex INTEGER NOT NULL DEFAULT 0";
cmd.ExecuteNonQuery();
cmd.CommandText = "ALTER TABLE ProxyUsageLogs ADD COLUMN IsFinalResult INTEGER NOT NULL DEFAULT 0";
cmd.ExecuteNonQuery();
cmd.CommandText = "ALTER TABLE ProxyUsageLogs ADD COLUMN FallbackTriggered INTEGER NOT NULL DEFAULT 0";
cmd.ExecuteNonQuery();
cmd.CommandText = "ALTER TABLE ProxyUsageLogs ADD COLUMN ErrorMessage TEXT NOT NULL DEFAULT ''";
cmd.ExecuteNonQuery();
```

- [ ] **Step 5: 让测试通过并确认没有破坏旧行为**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~UsageLogServiceTests"`
Expected: PASS，原有总 Token 测试继续通过，新字段测试通过。

- [ ] **Step 6: 提交本任务**

```bash
git add src/AITool.Domain/Proxy/ProxyRouteRule.cs src/AITool.Domain/Proxy/ProxyUsageLog.cs src/AITool.Application/Proxy/IProxyForwardService.cs src/AITool.Application/Proxy/ProxyForwardingOptions.cs src/AITool.Application/UsageLogs/IUsageLogService.cs src/AITool.Infrastructure/Persistence/AppDbContext.cs src/AITool.Web/Program.cs tests/AITool.ApplicationTests/Proxy/UsageLogServiceTests.cs
git commit -m "feat: add routing metadata and fallback log fields"
```

---

### Task 2: 实现稳定排序、超时与单路由重试

**Files:**
- Modify: `src/AITool.Infrastructure/Routing/RouteSelectionService.cs`
- Modify: `src/AITool.Infrastructure/Proxy/ProxyForwardService.cs`
- Modify: `src/AITool.Web/appsettings.json`
- Modify: `src/AITool.Web/appsettings.Development.json`
- Test: `tests/AITool.ApplicationTests/Routing/RouteSelectionServiceTests.cs`
- Test: `tests/AITool.ApplicationTests/Proxy/ProxyForwardServiceTests.cs`

- [ ] **Step 1: 写失败测试，锁定“同名实例优先，再跨模型组回退”的排序**

```csharp
[Fact]
public async Task SelectAllRoutesAsync_returns_grouped_then_global_priority_order()
{
    var siteA = Guid.NewGuid();
    var siteB = Guid.NewGuid();
    var siteC = Guid.NewGuid();
    var siteD = Guid.NewGuid();

    _dbContext.ProxyRouteRules.AddRange(
        new ProxyRouteRule { ExternalModelName = "chat-prod", UpstreamModelName = "gpt-5.5", SiteId = siteA, SiteModelName = "gpt-5.5-a", Priority = 0, ModelPriority = 0, InstancePriority = 0, IsEnabled = true },
        new ProxyRouteRule { ExternalModelName = "chat-prod", UpstreamModelName = "gpt-5.5", SiteId = siteB, SiteModelName = "gpt-5.5-b", Priority = 1, ModelPriority = 0, InstancePriority = 1, IsEnabled = true },
        new ProxyRouteRule { ExternalModelName = "chat-prod", UpstreamModelName = "glm-5.1", SiteId = siteC, SiteModelName = "glm-5.1-a", Priority = 2, ModelPriority = 1, InstancePriority = 0, IsEnabled = true },
        new ProxyRouteRule { ExternalModelName = "chat-prod", UpstreamModelName = "glm-5.1", SiteId = siteD, SiteModelName = "glm-5.1-b", Priority = 3, ModelPriority = 1, InstancePriority = 1, IsEnabled = true }
    );
    await _dbContext.SaveChangesAsync();

    var routes = await _service.SelectAllRoutesAsync("chat-prod");

    routes.Select(r => r.Route!.SiteModelName).Should().ContainInOrder(
        "gpt-5.5-a",
        "gpt-5.5-b",
        "glm-5.1-a",
        "glm-5.1-b");
}
```

- [ ] **Step 2: 写失败测试，锁定转发超时与单路由重试**

```csharp
[Fact]
public async Task ForwardAsync_retries_before_returning_failure()
{
    var handler = new SequenceHandler(
        new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"first\"}")
        },
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}")
        });

    var httpClient = new HttpClient(handler);
    var service = new ProxyForwardService(httpClient);

    var result = await service.ForwardAsync(new ProxyForwardRequest
    {
        TargetBaseUrl = "https://unit.test",
        TargetApiKey = "token",
        ProtocolType = "OpenAI",
        TargetModelName = "gpt-5.5-a",
        RequestBody = "{\"model\":\"chat-prod\"}",
        RetryCount = 1,
        RequestTimeoutSeconds = 5
    });

    result.Success.Should().BeTrue();
    handler.CallCount.Should().Be(2);
    result.InputTokens.Should().Be(1);
    result.OutputTokens.Should().Be(2);
}
```

- [ ] **Step 3: 运行测试确认现在失败**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~RouteSelectionServiceTests|FullyQualifiedName~ProxyForwardServiceTests"`
Expected: FAIL，当前排序只按 `Priority`，且 `ProxyForwardService` 不会按配置重试。

- [ ] **Step 4: 修改排序逻辑，保证顺序稳定且可解释**

```csharp
// src/AITool.Infrastructure/Routing/RouteSelectionService.cs
public async Task<List<RouteSelectionResult>> SelectAllRoutesAsync(
    string externalModelName,
    CancellationToken cancellationToken = default)
{
    var routes = await _dbContext.ProxyRouteRules
        .Where(r => r.ExternalModelName == externalModelName && r.IsEnabled)
        .OrderBy(r => r.ModelPriority)
        .ThenBy(r => r.InstancePriority)
        .ThenBy(r => r.Priority)
        .ToListAsync(cancellationToken);

    return routes.Select(r => new RouteSelectionResult { Route = r }).ToList();
}
```

- [ ] **Step 5: 在转发服务中实现最小可用的超时与重试**

```csharp
// src/AITool.Infrastructure/Proxy/ProxyForwardService.cs
public async Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
{
    var attempts = Math.Max(0, request.RetryCount) + 1;

    for (var attempt = 0; attempt < attempts; attempt++)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.RequestTimeoutSeconds)));

        try
        {
            var response = await _httpClient.SendAsync(BuildRequestMessage(request), timeoutCts.Token);
            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var (inputTokens, outputTokens) = ExtractTokenUsage(responseBody, request.ProtocolType);

            if (response.IsSuccessStatusCode)
            {
                return new ProxyForwardResult
                {
                    Success = true,
                    StatusCode = (int)response.StatusCode,
                    ResponseBody = responseBody,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens
                };
            }

            if (attempt == attempts - 1)
            {
                return new ProxyForwardResult
                {
                    Success = false,
                    StatusCode = (int)response.StatusCode,
                    ResponseBody = responseBody,
                    ErrorMessage = responseBody
                };
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (attempt == attempts - 1)
            {
                return new ProxyForwardResult
                {
                    Success = false,
                    ErrorMessage = $"Request timed out after {request.RequestTimeoutSeconds}s: {ex.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            if (attempt == attempts - 1)
            {
                return new ProxyForwardResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
    }

    return new ProxyForwardResult { Success = false, ErrorMessage = "Unknown proxy forwarding error" };
}
```

- [ ] **Step 6: 加入默认配置，避免控制器写死数值**

```json
// src/AITool.Web/appsettings.json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "ProxyForwarding": {
    "RequestTimeoutSeconds": 60,
    "RetryCount": 1
  },
  "AllowedHosts": "*"
}
```

```json
// src/AITool.Web/appsettings.Development.json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "ProxyForwarding": {
    "RequestTimeoutSeconds": 20,
    "RetryCount": 0
  }
}
```

- [ ] **Step 7: 运行测试确认排序与重试策略都已通过**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~RouteSelectionServiceTests|FullyQualifiedName~ProxyForwardServiceTests"`
Expected: PASS，路由顺序稳定，单路由内部可按配置重试。

- [ ] **Step 8: 提交本任务**

```bash
git add src/AITool.Infrastructure/Routing/RouteSelectionService.cs src/AITool.Infrastructure/Proxy/ProxyForwardService.cs src/AITool.Web/appsettings.json src/AITool.Web/appsettings.Development.json tests/AITool.ApplicationTests/Routing/RouteSelectionServiceTests.cs tests/AITool.ApplicationTests/Proxy/ProxyForwardServiceTests.cs
git commit -m "feat: add ordered fallback and configurable proxy retries"
```

---

### Task 3: 让正式代理和对话测试都记录完整 fallback 流程

**Files:**
- Modify: `src/AITool.Infrastructure/Proxy/UsageLogService.cs`
- Modify: `src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs`
- Modify: `src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs`
- Modify: `src/AITool.Web/Controllers/Admin/ChatApiController.cs`
- Test: `tests/AITool.IntegrationTests/Proxy/ProxyFallbackFlowTests.cs`

- [ ] **Step 1: 写失败集成测试，锁定代理 fallback 顺序与日志行为**

```csharp
[Fact]
public async Task Post_chat_completions_falls_back_to_next_route_and_persists_attempt_logs()
{
    using var factory = new ProxyFallbackWebApplicationFactory();
    using var client = factory.CreateClient();

    var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
    {
        Content = new StringContent("{\"model\":\"chat-prod\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

    var response = await client.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();

    response.StatusCode.Should().Be(HttpStatusCode.OK);
    body.Should().Contain("success-from-second-route");

    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logs = await db.ProxyUsageLogs.OrderBy(x => x.AttemptIndex).ToListAsync();

    logs.Should().HaveCount(2);
    logs[0].AttemptedModel.Should().Be("gpt-5.5");
    logs[0].Status.Should().Be("fail");
    logs[0].FallbackTriggered.Should().BeTrue();
    logs[1].AttemptedModel.Should().Be("glm-5.1");
    logs[1].Status.Should().Be("success");
    logs[1].IsFinalResult.Should().BeTrue();
}
```

- [ ] **Step 2: 运行测试确认当前实现失败**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~ProxyFallbackFlowTests"`
Expected: FAIL，当前只记录最终日志，且没有 `AttemptedModel`、`AttemptIndex`、`FallbackTriggered` 等链路字段。

- [ ] **Step 3: 更新日志落库逻辑，确保新增字段被完整保存**

```csharp
// src/AITool.Infrastructure/Proxy/UsageLogService.cs
await _dbContext.ProxyUsageLogs.AddAsync(new ProxyUsageLog
{
    RequestId = entry.RequestId,
    AccessKeyId = entry.AccessKeyId,
    ProtocolType = entry.ProtocolType,
    RequestModel = entry.RequestModel,
    AttemptedModel = entry.AttemptedModel,
    TargetSiteId = entry.TargetSiteId,
    Status = entry.Status,
    Source = entry.Source,
    RetryCount = entry.RetryCount,
    AttemptIndex = entry.AttemptIndex,
    IsFinalResult = entry.IsFinalResult,
    FallbackTriggered = entry.FallbackTriggered,
    ErrorMessage = entry.ErrorMessage,
    InputTokens = entry.InputTokens,
    OutputTokens = entry.OutputTokens,
    TotalTokens = entry.InputTokens + entry.OutputTokens
}, cancellationToken);
```

- [ ] **Step 4: 在两个代理控制器中按每次尝试记录日志**

```csharp
// OpenAiProxyController / AnthropicProxyController 的循环内共用模式
var requestId = Guid.NewGuid();
var attemptIndex = 0;

foreach (var routeResult in allRoutes)
{
    var route = routeResult.Route!;
    if (blockedSiteIds.Contains(route.SiteId))
        continue;

    attemptIndex++;
    var result = await _forwardService.ForwardAsync(new ProxyForwardRequest
    {
        TargetBaseUrl = site.BaseUrl,
        TargetApiKey = site.ApiKey,
        ProtocolType = "OpenAI",
        TargetModelName = route.SiteModelName,
        RequestBody = requestBody,
        RequestTimeoutSeconds = _forwardingOptions.RequestTimeoutSeconds,
        RetryCount = _forwardingOptions.RetryCount
    }, cancellationToken);

    await _usageLogService.LogAsync(new UsageLogEntry
    {
        RequestId = requestId,
        AccessKeyId = accessKey.Id,
        ProtocolType = "OpenAI",
        RequestModel = modelName,
        AttemptedModel = route.UpstreamModelName,
        TargetSiteId = site.Id,
        Status = result.Success ? "success" : "fail",
        Source = "proxy",
        RetryCount = result.Success ? attemptIndex - 1 : attemptIndex,
        AttemptIndex = attemptIndex,
        IsFinalResult = result.Success,
        FallbackTriggered = !result.Success,
        ErrorMessage = result.Success ? string.Empty : (result.ErrorMessage ?? string.Empty),
        InputTokens = result.InputTokens,
        OutputTokens = result.OutputTokens
    }, cancellationToken);

    if (result.Success)
    {
        _circuitStore.Succeed(site.Id);
        return Content(result.ResponseBody, "application/json");
    }

    _circuitStore.Block(site.Id);
    blockedSiteIds.Add(site.Id);
}
```

- [ ] **Step 5: 让 Chat 测试页使用同样的尝试序列与日志字段**

```csharp
// src/AITool.Web/Controllers/Admin/ChatApiController.cs
var requestId = Guid.NewGuid();
var attemptIndex = 0;

foreach (var routeResult in allRoutes)
{
    var route = routeResult.Route!;
    if (blockedSiteIds.Contains(route.SiteId))
        continue;

    attemptIndex++;
    var forwardResult = await _forwardService.ForwardAsync(new ProxyForwardRequest
    {
        TargetBaseUrl = site.BaseUrl,
        TargetApiKey = site.ApiKey,
        ProtocolType = site.ProtocolType,
        TargetModelName = route.SiteModelName,
        RequestBody = requestBody,
        RequestTimeoutSeconds = _forwardingOptions.RequestTimeoutSeconds,
        RetryCount = _forwardingOptions.RetryCount
    }, cancellationToken);

    await _usageLogService.LogAsync(new UsageLogEntry
    {
        RequestId = requestId,
        ProtocolType = site.ProtocolType,
        RequestModel = model.ModelName,
        AttemptedModel = route.UpstreamModelName,
        TargetSiteId = site.Id,
        Status = forwardResult.Success ? "success" : "fail",
        Source = "chat",
        RetryCount = forwardResult.Success ? attemptIndex - 1 : attemptIndex,
        AttemptIndex = attemptIndex,
        IsFinalResult = forwardResult.Success,
        FallbackTriggered = !forwardResult.Success,
        ErrorMessage = forwardResult.Success ? string.Empty : (forwardResult.ErrorMessage ?? string.Empty),
        InputTokens = forwardResult.InputTokens,
        OutputTokens = forwardResult.OutputTokens
    }, cancellationToken);

    if (forwardResult.Success)
    {
        var content = ExtractContent(forwardResult.ResponseBody, site.ProtocolType);
        return Ok(new ChatSendResult { Success = true, Content = content, DurationMs = sw.ElapsedMilliseconds });
    }
}
```

- [ ] **Step 6: 运行集成测试确认 fallback 流程真实生效**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~ProxyFallbackFlowTests"`
Expected: PASS，首个候选失败后自动尝试下一个，数据库中存在按顺序写入的 2 条 attempt 日志。

- [ ] **Step 7: 提交本任务**

```bash
git add src/AITool.Infrastructure/Proxy/UsageLogService.cs src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs src/AITool.Web/Controllers/Admin/ChatApiController.cs tests/AITool.IntegrationTests/Proxy/ProxyFallbackFlowTests.cs
git commit -m "feat: log each fallback attempt in proxy flows"
```

---

### Task 4: 改造路由规则管理页，支持跨模型组编排 fallback 链

**Files:**
- Modify: `src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs`
- Modify: `src/AITool.Web/Pages/Admin/Routes/Index.cshtml`
- Test: `tests/AITool.IntegrationTests/Proxy/ProxyFallbackFlowTests.cs`

- [ ] **Step 1: 写失败测试，先锁定保存 API 能接收跨模型组规则**

```csharp
[Fact]
public async Task Save_route_rules_accepts_multiple_upstream_model_groups()
{
    using var factory = new ProxyFallbackWebApplicationFactory();
    using var client = factory.CreateClient();

    var response = await client.PostAsJsonAsync("/api/admin/route-rules/save", new
    {
        externalModelName = "chat-prod",
        rules = new[]
        {
            new { upstreamModelName = "gpt-5.5", siteId = "11111111-1111-1111-1111-111111111111", siteModelName = "gpt-5.5-a" },
            new { upstreamModelName = "glm-5.1", siteId = "22222222-2222-2222-2222-222222222222", siteModelName = "glm-5.1-a" }
        }
    });

    response.StatusCode.Should().Be(HttpStatusCode.OK);
}
```

- [ ] **Step 2: 运行测试确认当前 API 结构不支持该需求**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~Save_route_rules_accepts_multiple_upstream_model_groups"`
Expected: FAIL，`SaveRouteRuleEntry` 目前缺少 `UpstreamModelName`，页面也只能发现一个模型的站点。

- [ ] **Step 3: 扩展保存/查询 DTO，并按全局顺序回写优先级**

```csharp
// src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs
public sealed class SaveRouteRuleEntry
{
    public string UpstreamModelName { get; set; } = string.Empty;
    public Guid SiteId { get; set; }
    public string SiteModelName { get; set; } = string.Empty;
}

public sealed class RouteRuleListItem
{
    public Guid RuleId { get; set; }
    public Guid SiteId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string UpstreamModelName { get; set; } = string.Empty;
    public string SiteModelName { get; set; } = string.Empty;
    public int Priority { get; set; }
    public int ModelPriority { get; set; }
    public int InstancePriority { get; set; }
    public bool IsEnabled { get; set; }
}

for (int i = 0; i < request.Rules.Count; i++)
{
    var entry = request.Rules[i];
    var sameModelEarlierCount = request.Rules
        .Take(i)
        .Count(x => x.UpstreamModelName == entry.UpstreamModelName);
    var modelPriority = request.Rules
        .Select(x => x.UpstreamModelName)
        .Distinct()
        .ToList()
        .IndexOf(entry.UpstreamModelName);

    _dbContext.ProxyRouteRules.Add(new ProxyRouteRule
    {
        ExternalModelName = request.ExternalModelName,
        UpstreamModelName = entry.UpstreamModelName,
        SiteId = entry.SiteId,
        SiteModelName = entry.SiteModelName,
        Priority = i,
        ModelPriority = modelPriority,
        InstancePriority = sameModelEarlierCount,
        IsEnabled = true
    });
}
```

- [ ] **Step 4: 给页面增加“从任意模型添加候选实例”的最小交互**

```html
<!-- src/AITool.Web/Pages/Admin/Routes/Index.cshtml -->
<div class="row g-2 align-items-end">
    <div class="col-md-4">
        <label class="form-label">对外模型</label>
        <select id="modelSelect" class="form-select"></select>
    </div>
    <div class="col-md-4">
        <label class="form-label">追加上游模型组</label>
        <select id="upstreamModelSelect" class="form-select"></select>
    </div>
    <div class="col-auto">
        <button type="button" class="btn btn-secondary" onclick="appendUpstreamModel()">添加候选实例</button>
    </div>
</div>
```

```javascript
async function appendUpstreamModel() {
    var upstreamModelName = document.getElementById('upstreamModelSelect').value;
    if (!upstreamModelName) {
        showMsg('请先选择要追加的上游模型', false);
        return;
    }

    var discoveredSites = await fetch('/api/admin/route-rules/discover-sites?modelName=' + encodeURIComponent(upstreamModelName))
        .then(function(r) { return r.json(); });

    discoveredSites.forEach(function(site) {
        var exists = _currentItems.some(function(item) {
            return String(item.siteId) === String(site.siteId) && item.siteModelName === site.remoteModelName;
        });

        if (!exists) {
            _currentItems.push({
                siteId: site.siteId,
                siteName: site.siteName,
                upstreamModelName: upstreamModelName,
                siteModelName: site.remoteModelName
            });
        }
    });

    renderRouteList();
}

function saveRules() {
    var modelName = document.getElementById('modelSelect').value;
    var rules = _currentItems.map(function(item) {
        return {
            upstreamModelName: item.upstreamModelName,
            siteId: item.siteId,
            siteModelName: item.siteModelName
        };
    });

    return fetch('/api/admin/route-rules/save', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ externalModelName: modelName, rules: rules })
    });
}
```

- [ ] **Step 5: 把列表展示改成能看出“模型组 + 组内实例顺序”**

```javascript
row.innerHTML =
    '<span class="drag-handle">⠿</span>' +
    '<span class="priority-num">' + (index + 1) + '</span>' +
    '<div class="site-info">' +
        '<div class="site-name">' + escHtml(item.siteName) + '</div>' +
        '<div class="remote-name">上游模型组：' + escHtml(item.upstreamModelName) + '</div>' +
        '<div class="remote-name">站点模型：' + escHtml(item.siteModelName) + '</div>' +
    '</div>' +
    '<button type="button" class="btn btn-sm btn-outline-danger" onclick="removeItem(this)" title="移除">✕</button>';
```

- [ ] **Step 6: 运行集成测试并手动验证页面数据结构**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~Save_route_rules_accepts_multiple_upstream_model_groups|FullyQualifiedName~ProxyFallbackFlowTests"`
Expected: PASS，保存 API 能持久化多个不同上游模型组，回退链可被正式代理读取。

- [ ] **Step 7: 提交本任务**

```bash
git add src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs src/AITool.Web/Pages/Admin/Routes/Index.cshtml tests/AITool.IntegrationTests/Proxy/ProxyFallbackFlowTests.cs
git commit -m "feat: support cross-model fallback chain editing"
```

---

### Task 5: 做完整回归，确认文档里的需求项全部落地

**Files:**
- Modify: `tests/AITool.ApplicationTests/Routing/RouteSelectionServiceTests.cs`
- Modify: `tests/AITool.ApplicationTests/Proxy/UsageLogServiceTests.cs`
- Modify: `tests/AITool.ApplicationTests/Proxy/ProxyForwardServiceTests.cs`
- Modify: `tests/AITool.IntegrationTests/Proxy/ProxyFallbackFlowTests.cs`

- [ ] **Step 1: 补最后一组回归测试，覆盖所有关键需求**

```csharp
[Fact]
public async Task SelectAllRoutesAsync_skips_disabled_routes_but_preserves_remaining_order()
{
    var siteA = Guid.NewGuid();
    var siteB = Guid.NewGuid();
    var siteC = Guid.NewGuid();

    _dbContext.ProxyRouteRules.AddRange(
        new ProxyRouteRule { ExternalModelName = "chat-prod", UpstreamModelName = "gpt-5.5", SiteId = siteA, SiteModelName = "gpt-5.5-a", Priority = 0, ModelPriority = 0, InstancePriority = 0, IsEnabled = true },
        new ProxyRouteRule { ExternalModelName = "chat-prod", UpstreamModelName = "gpt-5.5", SiteId = siteB, SiteModelName = "gpt-5.5-b", Priority = 1, ModelPriority = 0, InstancePriority = 1, IsEnabled = false },
        new ProxyRouteRule { ExternalModelName = "chat-prod", UpstreamModelName = "glm-5.1", SiteId = siteC, SiteModelName = "glm-5.1-a", Priority = 2, ModelPriority = 1, InstancePriority = 0, IsEnabled = true }
    );
    await _dbContext.SaveChangesAsync();

    var routes = await _service.SelectAllRoutesAsync("chat-prod");

    routes.Select(r => r.Route!.SiteModelName).Should().ContainInOrder("gpt-5.5-a", "glm-5.1-a");
    routes.Select(r => r.Route!.SiteModelName).Should().NotContain("gpt-5.5-b");
}

[Fact]
public async Task ForwardAsync_returns_timeout_error_after_configured_retry_limit()
{
    var handler = new DelayedHandler(TimeSpan.FromMilliseconds(200));
    var httpClient = new HttpClient(handler);
    var service = new ProxyForwardService(httpClient);

    var result = await service.ForwardAsync(new ProxyForwardRequest
    {
        TargetBaseUrl = "https://unit.test",
        TargetApiKey = "token",
        ProtocolType = "OpenAI",
        TargetModelName = "gpt-5.5-a",
        RequestBody = "{\"model\":\"chat-prod\"}",
        RetryCount = 1,
        RequestTimeoutSeconds = 1
    }, new CancellationTokenSource(TimeSpan.FromMilliseconds(50)).Token);

    result.Success.Should().BeFalse();
    result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
}

[Fact]
public async Task Post_messages_returns_error_when_all_routes_fail()
{
    using var factory = new ProxyFallbackWebApplicationFactory(allRoutesFail: true, protocolType: "Anthropic");
    using var client = factory.CreateClient();

    var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
    {
        Content = new StringContent("{\"model\":\"chat-prod\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
    };
    request.Headers.Add("x-api-key", "test-key");

    var response = await client.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();

    response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    body.Should().Contain("All upstream routes failed");

    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var lastLog = await db.ProxyUsageLogs.OrderByDescending(x => x.AttemptIndex).FirstAsync();
    lastLog.IsFinalResult.Should().BeTrue();
    lastLog.Status.Should().Be("fail");
}
```

- [ ] **Step 2: 运行应用层测试**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj`
Expected: PASS，路由顺序、日志字段、重试策略全部通过。

- [ ] **Step 3: 运行集成测试**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj`
Expected: PASS，OpenAI/Anthropic 代理链路与管理 API 都通过。

- [ ] **Step 4: 启动站点并做一次人工冒烟验证**

Run: `dotnet run --project src/AITool.Web/AITool.Web.csproj`
Expected: Web 宿主启动成功，可以进入 `/Admin/Routes` 页面，为一个对外模型添加多个不同上游模型组实例并保存。

- [ ] **Step 5: 手工检查以下场景**

```text
1. 对外模型 chat-prod 绑定 2 个 gpt-5.5 实例 + 2 个 glm-5.1 实例。
2. 将 gpt-5.5 的一个实例故意配置为失败地址。
3. 调用 /v1/chat/completions，请确认：
   - 先尝试 gpt-5.5 的实例；
   - 同组失败后仍继续尝试同组剩余实例；
   - 同组全部失败后再进入 glm-5.1；
   - 成功后立即返回；
   - UsageLogs 页面能看到每次 attempt 的顺序、状态和错误信息。
4. 将 RequestTimeoutSeconds 调小，确认超时信息进入日志。
```

- [ ] **Step 6: 提交最终回归结果**

```bash
git add tests/AITool.ApplicationTests/Routing/RouteSelectionServiceTests.cs tests/AITool.ApplicationTests/Proxy/UsageLogServiceTests.cs tests/AITool.ApplicationTests/Proxy/ProxyForwardServiceTests.cs tests/AITool.IntegrationTests/Proxy/ProxyFallbackFlowTests.cs
git commit -m "test: cover multi-model fallback routing behavior"
```

---
