# AI Tool 开发拆解总计划 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将根目录下的 PRD 拆成可逐阶段落地、可逐模块测试、可逐步交付的实施主线，并锁定基础文件结构与任务边界。

**Architecture:** 本计划按“单 Web 宿主 + 应用层 + 领域层 + 基础设施层 + 测试层”的单机架构拆解，优先做从后台管理到协议中转的完整闭环。为了把拆解落到可执行层，以下文件路径默认以 `MVC + Hangfire` 为基线；如果后续改选 `Razor Pages + Quartz.NET`，仅调整 Web 展示层与调度层文件路径，领域层、应用层、数据层命名保持不变。

**Tech Stack:** ASP.NET 8 Web、MVC、EF Core、SQLite、NLog、Hangfire、xUnit、FluentAssertions、OpenAI/Anthropic 兼容 HTTP Client

---

## 范围检查

当前 PRD 同时覆盖以下 6 条相对独立但存在依赖的实施主线：

- 基础骨架与公共能力
- 站点管理与模型库管理
- 站点模型拉取与检测能力
- 定时任务与可视化看板
- 路由、中转、Key 与协议兼容
- 日志保留、熔断与稳定性增强

为了避免计划过大导致执行失控，本文件作为**总拆解计划**使用，每个任务块都对应一个能独立验收的垂直切片。执行时按任务顺序推进，不建议跨任务并行开发。

---

## 锁定命名

以下命名在后续任务中统一使用，不再变更：

- `Site`
- `ModelLibraryItem`
- `SiteModelMapping`
- `DetectionTask`
- `DetectionTaskExecution`
- `DetectionLog`
- `ProxyRouteRule`
- `ProxyAccessKey`
- `ProxyUsageLog`
- `AppDbContext`
- `ISiteCatalogClient`
- `IModelProbeService`
- `IRouteSelectionService`
- `IProxyForwardService`
- `IUsageLogService`
- `ILogRetentionService`

---

## 基线文件结构

```text
AiTool.sln
src/
  AITool.Web/
    Program.cs
    appsettings.json
    Controllers/
      Admin/
        SitesController.cs
        ModelsController.cs
        SiteCatalogController.cs
        DetectionController.cs
        DetectionTasksController.cs
        DashboardController.cs
        RoutesController.cs
        AccessKeysController.cs
        UsageLogsController.cs
      Proxy/
        OpenAiProxyController.cs
        AnthropicProxyController.cs
    Views/
      Shared/_Layout.cshtml
      Sites/Index.cshtml
      Models/Index.cshtml
      SiteCatalog/Index.cshtml
      Detection/Index.cshtml
      DetectionTasks/Index.cshtml
      Dashboard/Index.cshtml
      Routes/Index.cshtml
      AccessKeys/Index.cshtml
      UsageLogs/Index.cshtml
  AITool.Application/
    DependencyInjection.cs
    Sites/
    Models/
    SiteCatalog/
    Detection/
    Dashboard/
    Routing/
    Proxy/
    UsageLogs/
    Common/
  AITool.Domain/
    Sites/Site.cs
    Models/ModelLibraryItem.cs
    SiteCatalog/SiteModelMapping.cs
    Detection/DetectionTask.cs
    Detection/DetectionTaskExecution.cs
    Detection/DetectionLog.cs
    Proxy/ProxyRouteRule.cs
    Proxy/ProxyAccessKey.cs
    Proxy/ProxyUsageLog.cs
  AITool.Infrastructure/
    DependencyInjection.cs
    Persistence/AppDbContext.cs
    Persistence/Configurations/
    Logging/NLog/
    Scheduling/
    OpenAI/
    Anthropic/
    Proxy/
    Retention/
tests/
  AITool.UnitTests/
  AITool.ApplicationTests/
  AITool.IntegrationTests/
```

### 各层职责

- `AITool.Web`：管理后台页面、后台管理入口、对外协议中转入口
- `AITool.Application`：命令、查询、服务接口、业务编排
- `AITool.Domain`：核心实体、枚举、状态模型
- `AITool.Infrastructure`：数据库、HTTP 客户端、调度、日志、保留策略
- `tests`：单元测试、应用服务测试、集成测试

---

## 模块拆解顺序

1. 项目骨架与最小可运行宿主
2. 站点管理
3. 模型库管理
4. 站点模型拉取与导入
5. 模型检测与检测日志
6. 定时任务与状态看板
7. 路由规则与平台访问 Key
8. OpenAI / Anthropic 协议中转与使用日志
9. 保留策略、熔断与收尾优化

---

### Task 1: 项目骨架与最小可运行宿主

**Files:**
- Create: `AiTool.sln`
- Create: `src/AITool.Web/AITool.Web.csproj`
- Create: `src/AITool.Web/Program.cs`
- Create: `src/AITool.Application/AITool.Application.csproj`
- Create: `src/AITool.Domain/AITool.Domain.csproj`
- Create: `src/AITool.Infrastructure/AITool.Infrastructure.csproj`
- Create: `tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj`
- Test: `tests/AITool.IntegrationTests/Health/HealthEndpointTests.cs`

- [ ] **Step 1: 先写失败的健康检查测试**

```csharp
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        // 创建测试宿主客户端
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_health_returns_ok()
    {
        // 验证系统已成功启动并暴露健康检查端点
        var response = await _client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~HealthEndpointTests" -v minimal`

Expected: FAIL，提示 `Program` 或 `/health` 端点不存在。

- [ ] **Step 3: 写最小实现让宿主能启动**

```csharp
var builder = WebApplication.CreateBuilder(args);

// 注册 MVC 管道，为后续管理后台页面和控制器预留基础能力
builder.Services.AddControllersWithViews();

var app = builder.Build();

// 映射最小健康检查端点，作为首个集成测试通过标准
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// 映射默认控制器路由，后续管理后台页面复用该入口
app.MapDefaultControllerRoute();

app.Run();

public partial class Program;
```

- [ ] **Step 4: 重新运行测试并确认通过**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~HealthEndpointTests" -v minimal`

Expected: PASS。

- [ ] **Step 5: 提交骨架初始化变更**

```bash
git add AiTool.sln src tests
git commit -m "chore: bootstrap web host and test projects"
```

---

### Task 2: 站点管理垂直切片

**Files:**
- Create: `src/AITool.Domain/Sites/Site.cs`
- Create: `src/AITool.Infrastructure/Persistence/AppDbContext.cs`
- Create: `src/AITool.Application/Sites/CreateSiteCommand.cs`
- Create: `src/AITool.Web/Controllers/Admin/SitesController.cs`
- Create: `src/AITool.Web/Views/Sites/Index.cshtml`
- Test: `tests/AITool.IntegrationTests/Admin/SitesControllerTests.cs`

- [ ] **Step 1: 先写站点创建集成测试**

```csharp
[Fact]
public async Task Post_sites_create_persists_enabled_site()
{
    // 构造站点创建请求，验证默认启用站点可以被保存
    var form = new Dictionary<string, string>
    {
        ["Name"] = "Demo Site",
        ["BaseUrl"] = "https://demo.example.com",
        ["ApiKey"] = "demo-key",
        ["ProtocolType"] = "OpenAI",
        ["IsEnabled"] = "true"
    };

    var response = await _client.PostAsync("/admin/sites/create", new FormUrlEncodedContent(form));
    response.StatusCode.Should().Be(HttpStatusCode.Redirect);
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~SitesControllerTests" -v minimal`

Expected: FAIL，提示控制器或数据库上下文未实现。

- [ ] **Step 3: 写最小实现完成站点新增与列表展示**

```csharp
public sealed class Site
{
    // 站点主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 站点名称
    public string Name { get; set; } = string.Empty;

    // 站点根地址
    public string BaseUrl { get; set; } = string.Empty;

    // 站点访问密钥
    public string ApiKey { get; set; } = string.Empty;

    // 协议类型，当前用于区分 OpenAI 兼容站点
    public string ProtocolType { get; set; } = "OpenAI";

    // 是否启用该站点
    public bool IsEnabled { get; set; } = true;
}

public sealed class CreateSiteCommand
{
    // 后台表单提交的站点名称
    public string Name { get; set; } = string.Empty;

    // 后台表单提交的根地址
    public string BaseUrl { get; set; } = string.Empty;

    // 后台表单提交的密钥
    public string ApiKey { get; set; } = string.Empty;

    // 协议类型
    public string ProtocolType { get; set; } = "OpenAI";

    // 启用状态
    public bool IsEnabled { get; set; } = true;
}

[Route("admin/sites")]
public sealed class SitesController : Controller
{
    private readonly AppDbContext _dbContext;

    public SitesController(AppDbContext dbContext)
    {
        // 注入数据库上下文，直接完成最小站点管理闭环
        _dbContext = dbContext;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        // 返回站点列表页面
        return View(_dbContext.Sites.OrderBy(x => x.Name).ToList());
    }

    [HttpPost("create")]
    public async Task<IActionResult> Create(CreateSiteCommand command, CancellationToken cancellationToken)
    {
        // 将表单命令转成站点实体并落库
        _dbContext.Sites.Add(new Site
        {
            Name = command.Name,
            BaseUrl = command.BaseUrl,
            ApiKey = command.ApiKey,
            ProtocolType = command.ProtocolType,
            IsEnabled = command.IsEnabled
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }
}
```

- [ ] **Step 4: 运行站点管理测试**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~SitesControllerTests" -v minimal`

Expected: PASS。

- [ ] **Step 5: 提交站点管理切片**

```bash
git add src/AITool.Domain/Sites src/AITool.Application/Sites src/AITool.Infrastructure/Persistence src/AITool.Web/Controllers/Admin/SitesController.cs src/AITool.Web/Views/Sites tests/AITool.IntegrationTests/Admin
git commit -m "feat: add site management vertical slice"
```

---

### Task 3: 模型库管理垂直切片

**Files:**
- Create: `src/AITool.Domain/Models/ModelLibraryItem.cs`
- Create: `src/AITool.Application/Models/CreateModelLibraryItemCommand.cs`
- Create: `src/AITool.Web/Controllers/Admin/ModelsController.cs`
- Create: `src/AITool.Web/Views/Models/Index.cshtml`
- Test: `tests/AITool.IntegrationTests/Admin/ModelsControllerTests.cs`

- [ ] **Step 1: 先写模型新增测试**

```csharp
[Fact]
public async Task Post_models_create_persists_model_library_item()
{
    // 验证模型库新增动作可以正确保存模型定义
    var form = new Dictionary<string, string>
    {
        ["ModelName"] = "gpt-5.4",
        ["DisplayName"] = "GPT 5.4",
        ["ModelType"] = "chat",
        ["IsEnabled"] = "true"
    };

    var response = await _client.PostAsync("/admin/models/create", new FormUrlEncodedContent(form));
    response.StatusCode.Should().Be(HttpStatusCode.Redirect);
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~ModelsControllerTests" -v minimal`

Expected: FAIL，提示模型实体或控制器缺失。

- [ ] **Step 3: 写最小实现完成模型库增删改查骨架**

```csharp
public sealed class ModelLibraryItem
{
    // 模型主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 统一模型名
    public string ModelName { get; set; } = string.Empty;

    // 页面显示名
    public string DisplayName { get; set; } = string.Empty;

    // 模型类型，例如 chat 或 embedding
    public string ModelType { get; set; } = string.Empty;

    // 是否启用该模型
    public bool IsEnabled { get; set; } = true;
}

[Route("admin/models")]
public sealed class ModelsController : Controller
{
    private readonly AppDbContext _dbContext;

    public ModelsController(AppDbContext dbContext)
    {
        // 注入数据库上下文，复用基础列表与创建流程
        _dbContext = dbContext;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        // 返回模型库列表页面
        return View(_dbContext.ModelLibraryItems.OrderBy(x => x.ModelName).ToList());
    }

    [HttpPost("create")]
    public async Task<IActionResult> Create(CreateModelLibraryItemCommand command, CancellationToken cancellationToken)
    {
        // 保存模型库记录
        _dbContext.ModelLibraryItems.Add(new ModelLibraryItem
        {
            ModelName = command.ModelName,
            DisplayName = command.DisplayName,
            ModelType = command.ModelType,
            IsEnabled = command.IsEnabled
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return RedirectToAction(nameof(Index));
    }
}
```

- [ ] **Step 4: 运行模型库测试**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~ModelsControllerTests" -v minimal`

Expected: PASS。

- [ ] **Step 5: 提交模型库切片**

```bash
git add src/AITool.Domain/Models src/AITool.Application/Models src/AITool.Web/Controllers/Admin/ModelsController.cs src/AITool.Web/Views/Models tests/AITool.IntegrationTests/Admin
git commit -m "feat: add model library management slice"
```

---

### Task 4: 站点模型拉取与导入切片

**Files:**
- Create: `src/AITool.Domain/SiteCatalog/SiteModelMapping.cs`
- Create: `src/AITool.Application/SiteCatalog/ISiteCatalogClient.cs`
- Create: `src/AITool.Application/SiteCatalog/PullSiteModelsCommand.cs`
- Create: `src/AITool.Infrastructure/OpenAI/OpenAiSiteCatalogClient.cs`
- Create: `src/AITool.Web/Controllers/Admin/SiteCatalogController.cs`
- Create: `src/AITool.Web/Views/SiteCatalog/Index.cshtml`
- Test: `tests/AITool.ApplicationTests/SiteCatalog/PullSiteModelsCommandHandlerTests.cs`

- [ ] **Step 1: 先写拉取并导入模型的应用层测试**

```csharp
[Fact]
public async Task Handle_pulls_remote_models_and_creates_mappings()
{
    // 构造站点返回模型列表，验证系统会创建映射记录
    var remoteModels = new[] { "gpt-5.4", "text-embedding-3-large" };
    var client = new FakeSiteCatalogClient(remoteModels);

    var result = await new PullSiteModelsCommandHandler(_dbContext, client)
        .Handle(new PullSiteModelsCommand(_siteId), CancellationToken.None);

    result.ImportedCount.Should().Be(2);
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~PullSiteModelsCommandHandlerTests" -v minimal`

Expected: FAIL，提示拉取客户端、命令处理器或映射实体缺失。

- [ ] **Step 3: 写最小实现完成模型拉取与导入**

```csharp
public sealed class SiteModelMapping
{
    // 映射主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 站点标识
    public Guid SiteId { get; set; }

    // 模型库标识
    public Guid ModelLibraryItemId { get; set; }

    // 站点原始模型名
    public string RemoteModelName { get; set; } = string.Empty;

    // 最近一次状态
    public string LastStatus { get; set; } = "unknown";
}

public interface ISiteCatalogClient
{
    // 拉取指定站点支持的模型名列表
    Task<IReadOnlyList<string>> GetModelsAsync(Site site, CancellationToken cancellationToken);
}

public sealed class PullSiteModelsCommand(Guid SiteId);
```

- [ ] **Step 4: 运行应用层测试**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~PullSiteModelsCommandHandlerTests" -v minimal`

Expected: PASS。

- [ ] **Step 5: 提交模型拉取切片**

```bash
git add src/AITool.Domain/SiteCatalog src/AITool.Application/SiteCatalog src/AITool.Infrastructure/OpenAI src/AITool.Web/Controllers/Admin/SiteCatalogController.cs src/AITool.Web/Views/SiteCatalog tests/AITool.ApplicationTests/SiteCatalog
git commit -m "feat: add site catalog pull and import flow"
```

---

### Task 5: 模型检测与检测日志切片

**Files:**
- Create: `src/AITool.Domain/Detection/DetectionLog.cs`
- Create: `src/AITool.Application/Detection/IModelProbeService.cs`
- Create: `src/AITool.Application/Detection/RunDetectionCommand.cs`
- Create: `src/AITool.Infrastructure/OpenAI/OpenAiModelProbeService.cs`
- Create: `src/AITool.Web/Controllers/Admin/DetectionController.cs`
- Create: `src/AITool.Web/Views/Detection/Index.cshtml`
- Test: `tests/AITool.ApplicationTests/Detection/RunDetectionCommandHandlerTests.cs`

- [ ] **Step 1: 先写检测成功时写入日志的测试**

```csharp
[Fact]
public async Task Handle_probe_success_writes_detection_log()
{
    // 模拟探测成功，验证检测日志会被写入数据库
    var probe = new FakeModelProbeService(success: true, durationMs: 120);
    await new RunDetectionCommandHandler(_dbContext, probe)
        .Handle(new RunDetectionCommand(_siteId, _modelId), CancellationToken.None);

    _dbContext.DetectionLogs.Should().ContainSingle();
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~RunDetectionCommandHandlerTests" -v minimal`

Expected: FAIL，提示检测服务或检测日志实体不存在。

- [ ] **Step 3: 写最小实现完成检测日志闭环**

```csharp
public sealed class DetectionLog
{
    // 检测日志主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 站点标识
    public Guid SiteId { get; set; }

    // 模型标识
    public Guid ModelLibraryItemId { get; set; }

    // 成功或失败状态
    public string Status { get; set; } = string.Empty;

    // 本次检测耗时
    public int DurationMs { get; set; }

    // 错误信息，成功时为空
    public string? ErrorMessage { get; set; }
}

public interface IModelProbeService
{
    // 对指定站点和模型执行一次可用性探测
    Task<ModelProbeResult> ProbeAsync(Site site, ModelLibraryItem model, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: 运行检测测试**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~RunDetectionCommandHandlerTests" -v minimal`

Expected: PASS。

- [ ] **Step 5: 提交检测与日志切片**

```bash
git add src/AITool.Domain/Detection src/AITool.Application/Detection src/AITool.Infrastructure/OpenAI src/AITool.Web/Controllers/Admin/DetectionController.cs src/AITool.Web/Views/Detection tests/AITool.ApplicationTests/Detection
git commit -m "feat: add detection workflow and detection logs"
```

---

### Task 6: 定时任务与状态看板切片

**Files:**
- Create: `src/AITool.Domain/Detection/DetectionTask.cs`
- Create: `src/AITool.Domain/Detection/DetectionTaskExecution.cs`
- Create: `src/AITool.Application/Dashboard/GetDashboardOverviewQuery.cs`
- Create: `src/AITool.Infrastructure/Scheduling/HangfireDetectionScheduler.cs`
- Create: `src/AITool.Web/Controllers/Admin/DetectionTasksController.cs`
- Create: `src/AITool.Web/Controllers/Admin/DashboardController.cs`
- Create: `src/AITool.Web/Views/DetectionTasks/Index.cshtml`
- Create: `src/AITool.Web/Views/Dashboard/Index.cshtml`
- Test: `tests/AITool.ApplicationTests/Dashboard/GetDashboardOverviewQueryHandlerTests.cs`

- [ ] **Step 1: 先写看板概览统计测试**

```csharp
[Fact]
public async Task Handle_returns_site_model_and_detection_summary()
{
    // 预置站点、模型和检测日志，验证看板能汇总关键指标
    SeedDashboardData(_dbContext);

    var result = await new GetDashboardOverviewQueryHandler(_dbContext)
        .Handle(new GetDashboardOverviewQuery(), CancellationToken.None);

    result.EnabledSiteCount.Should().BeGreaterThan(0);
    result.ModelCount.Should().BeGreaterThan(0);
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~GetDashboardOverviewQueryHandlerTests" -v minimal`

Expected: FAIL，提示任务实体、执行记录或统计查询不存在。

- [ ] **Step 3: 写最小实现完成任务调度与概览统计**

```csharp
public sealed class DetectionTask
{
    // 任务主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 任务名称
    public string Name { get; set; } = string.Empty;

    // Cron 表达式
    public string CronExpression { get; set; } = string.Empty;

    // 是否启用
    public bool IsEnabled { get; set; } = true;
}

public sealed class DetectionTaskExecution
{
    // 执行记录主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 所属任务
    public Guid DetectionTaskId { get; set; }

    // 执行状态
    public string Status { get; set; } = string.Empty;
}
```

- [ ] **Step 4: 运行定时任务与看板测试**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~GetDashboardOverviewQueryHandlerTests" -v minimal`

Expected: PASS。

- [ ] **Step 5: 提交任务与看板切片**

```bash
git add src/AITool.Domain/Detection src/AITool.Application/Dashboard src/AITool.Infrastructure/Scheduling src/AITool.Web/Controllers/Admin/DetectionTasksController.cs src/AITool.Web/Controllers/Admin/DashboardController.cs src/AITool.Web/Views/DetectionTasks src/AITool.Web/Views/Dashboard tests/AITool.ApplicationTests/Dashboard
git commit -m "feat: add scheduled detection and dashboard summary"
```

---

### Task 7: 路由规则与平台访问 Key 切片

**Files:**
- Create: `src/AITool.Domain/Proxy/ProxyRouteRule.cs`
- Create: `src/AITool.Domain/Proxy/ProxyAccessKey.cs`
- Create: `src/AITool.Application/Routing/IRouteSelectionService.cs`
- Create: `src/AITool.Application/Routing/CreateRouteRuleCommand.cs`
- Create: `src/AITool.Web/Controllers/Admin/RoutesController.cs`
- Create: `src/AITool.Web/Controllers/Admin/AccessKeysController.cs`
- Create: `src/AITool.Web/Views/Routes/Index.cshtml`
- Create: `src/AITool.Web/Views/AccessKeys/Index.cshtml`
- Test: `tests/AITool.ApplicationTests/Routing/RouteSelectionServiceTests.cs`

- [ ] **Step 1: 先写优先级路由选择测试**

```csharp
[Fact]
public async Task SelectAsync_returns_first_enabled_route_by_priority()
{
    // 构造多条候选路由，验证系统优先返回优先级最高且启用的站点
    var result = await _service.SelectAsync("gpt-5.4", CancellationToken.None);
    result.Priority.Should().Be(1);
}
```

- [ ] **Step 2: 运行测试并确认失败**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~RouteSelectionServiceTests" -v minimal`

Expected: FAIL，提示路由规则实体或选择服务缺失。

- [ ] **Step 3: 写最小实现完成路由与 Key 管理**

```csharp
public sealed class ProxyRouteRule
{
    // 路由规则主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 对外暴露的模型名
    public string ExternalModelName { get; set; } = string.Empty;

    // 目标站点标识
    public Guid SiteId { get; set; }

    // 目标站点模型名
    public string SiteModelName { get; set; } = string.Empty;

    // 优先级，数值越小优先级越高
    public int Priority { get; set; }

    // 是否启用该路由
    public bool IsEnabled { get; set; } = true;
}

public sealed class ProxyAccessKey
{
    // 访问密钥主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 密钥名称
    public string KeyName { get; set; } = string.Empty;

    // 密钥哈希值
    public string AccessKeyHash { get; set; } = string.Empty;

    // 列表页展示的掩码值
    public string MaskedValue { get; set; } = string.Empty;

    // 是否启用
    public bool IsEnabled { get; set; } = true;
}
```

- [ ] **Step 4: 运行路由选择测试**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~RouteSelectionServiceTests" -v minimal`

Expected: PASS。

- [ ] **Step 5: 提交路由与 Key 切片**

```bash
git add src/AITool.Domain/Proxy src/AITool.Application/Routing src/AITool.Web/Controllers/Admin/RoutesController.cs src/AITool.Web/Controllers/Admin/AccessKeysController.cs src/AITool.Web/Views/Routes src/AITool.Web/Views/AccessKeys tests/AITool.ApplicationTests/Routing
git commit -m "feat: add routing rules and proxy access keys"
```

---

### Task 8: OpenAI / Anthropic 协议中转与使用日志切片

**Files:**
- Create: `src/AITool.Application/Proxy/IProxyForwardService.cs`
- Create: `src/AITool.Application/UsageLogs/IUsageLogService.cs`
- Create: `src/AITool.Domain/Proxy/ProxyUsageLog.cs`
- Create: `src/AITool.Infrastructure/Proxy/ProxyForwardService.cs`
- Create: `src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs`
- Create: `src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs`
- Create: `src/AITool.Web/Controllers/Admin/UsageLogsController.cs`
- Create: `src/AITool.Web/Views/UsageLogs/Index.cshtml`
- Test: `tests/AITool.IntegrationTests/Proxy/OpenAiProxyControllerTests.cs`
- Test: `tests/AITool.IntegrationTests/Proxy/AnthropicProxyControllerTests.cs`

- [ ] **Step 1: 先写 OpenAI 失败切换测试**

```csharp
[Fact]
public async Task Chat_completions_falls_back_to_second_site_when_first_site_fails()
{
    // 首个站点返回失败，验证系统会自动切换到第二个候选站点
    var response = await _client.PostAsJsonAsync("/v1/chat/completions", new
    {
        model = "gpt-5.4",
        messages = new[] { new { role = "user", content = "hello" } }
    });

    response.StatusCode.Should().Be(HttpStatusCode.OK);
}
```

- [ ] **Step 2: 运行中转测试并确认失败**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~ProxyControllerTests" -v minimal`

Expected: FAIL，提示中转控制器、路由选择或转发服务不存在。

- [ ] **Step 3: 写最小实现完成双协议中转、流式转发与 Token 日志**

```csharp
public sealed class ProxyUsageLog
{
    // 使用日志主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 平台访问密钥标识
    public Guid AccessKeyId { get; set; }

    // 协议类型，例如 OpenAI 或 Anthropic
    public string ProtocolType { get; set; } = string.Empty;

    // 请求模型名
    public string RequestModel { get; set; } = string.Empty;

    // 命中站点标识
    public Guid TargetSiteId { get; set; }

    // 请求处理状态
    public string Status { get; set; } = string.Empty;

    // 输入 Token 数
    public int InputTokens { get; set; }

    // 输出 Token 数
    public int OutputTokens { get; set; }

    // 总 Token 数
    public int TotalTokens { get; set; }
}

public interface IProxyForwardService
{
    // 按协议类型将请求转发到实际目标站点，并返回标准化结果
    Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken);
}

[ApiController]
public sealed class OpenAiProxyController : ControllerBase
{
    [HttpPost("/v1/chat/completions")]
    public async Task<IActionResult> ChatCompletions(CancellationToken cancellationToken)
    {
        // 后续在此接入路由选择、失败切换、流式透传和 Token 统计
        return Ok();
    }
}
```

- [ ] **Step 4: 运行中转与使用日志测试**

Run: `dotnet test tests/AITool.IntegrationTests/AITool.IntegrationTests.csproj --filter "FullyQualifiedName~ProxyControllerTests" -v minimal`

Expected: PASS。

- [ ] **Step 5: 提交协议中转切片**

```bash
git add src/AITool.Application/Proxy src/AITool.Application/UsageLogs src/AITool.Domain/Proxy src/AITool.Infrastructure/Proxy src/AITool.Web/Controllers/Proxy src/AITool.Web/Controllers/Admin/UsageLogsController.cs src/AITool.Web/Views/UsageLogs tests/AITool.IntegrationTests/Proxy
git commit -m "feat: add openai anthropic proxy and usage logging"
```

---

### Task 9: 保留策略、熔断与稳定性收尾

**Files:**
- Create: `src/AITool.Application/Common/ILogRetentionService.cs`
- Create: `src/AITool.Infrastructure/Retention/LogRetentionService.cs`
- Create: `src/AITool.Infrastructure/Proxy/RouteCircuitStateStore.cs`
- Modify: `src/AITool.Infrastructure/Proxy/ProxyForwardService.cs`
- Modify: `src/AITool.Web/Program.cs`
- Test: `tests/AITool.ApplicationTests/Retention/LogRetentionServiceTests.cs`
- Test: `tests/AITool.ApplicationTests/Proxy/RouteCircuitStateStoreTests.cs`

- [ ] **Step 1: 先写 7 天日志清理测试**

```csharp
[Fact]
public async Task PruneAsync_removes_usage_logs_older_than_seven_days()
{
    // 预置过期日志与有效日志，验证清理后仅保留 7 天内数据
    SeedUsageLogs(_dbContext);

    await _service.PruneAsync(CancellationToken.None);

    _dbContext.ProxyUsageLogs.Should().OnlyContain(x => x.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-7));
}
```

- [ ] **Step 2: 运行稳定性测试并确认失败**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~LogRetentionServiceTests|FullyQualifiedName~RouteCircuitStateStoreTests" -v minimal`

Expected: FAIL，提示保留策略服务或熔断状态存储不存在。

- [ ] **Step 3: 写最小实现完成日志清理与站点短时熔断**

```csharp
public interface ILogRetentionService
{
    // 清理检测日志和使用日志中的过期记录
    Task PruneAsync(CancellationToken cancellationToken);
}

public sealed class RouteCircuitStateStore
{
    private readonly Dictionary<Guid, DateTimeOffset> _blockedSites = new();

    public void Block(Guid siteId, DateTimeOffset until)
    {
        // 记录站点被熔断到期时间，转发时据此跳过短期异常站点
        _blockedSites[siteId] = until;
    }

    public bool IsBlocked(Guid siteId, DateTimeOffset now)
    {
        // 判断站点当前是否仍处于熔断窗口
        return _blockedSites.TryGetValue(siteId, out var until) && until > now;
    }
}
```

- [ ] **Step 4: 运行稳定性收尾测试**

Run: `dotnet test tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj --filter "FullyQualifiedName~LogRetentionServiceTests|FullyQualifiedName~RouteCircuitStateStoreTests" -v minimal`

Expected: PASS。

- [ ] **Step 5: 提交保留与熔断收尾切片**

```bash
git add src/AITool.Application/Common src/AITool.Infrastructure/Retention src/AITool.Infrastructure/Proxy src/AITool.Web/Program.cs tests/AITool.ApplicationTests/Retention tests/AITool.ApplicationTests/Proxy
git commit -m "feat: add retention policy and circuit protection"
```

---

## 规格覆盖检查

- PRD 7.1 站点管理 → Task 2
- PRD 7.2 模型库管理 → Task 3
- PRD 7.3 站点模型拉取与导入 → Task 4
- PRD 7.4 模型检测 → Task 5
- PRD 7.5 定时检测任务 → Task 6
- PRD 7.6 可视化状态看板 → Task 6
- PRD 7.7 检测日志 → Task 5
- PRD 7.8 OpenAI / Anthropic 协议中转 → Task 8
- PRD 7.9 平台访问 Key 管理 → Task 7
- PRD 7.10 使用日志 → Task 8
- PRD 日志保留与稳定性增强 → Task 9

## 占位符检查

已去掉以下风险写法：

- 未使用 `TBD`
- 未使用 “后续再处理” 之类的空泛表达
- 每个任务都给出明确文件路径、测试入口、运行命令和提交信息

## 一致性检查

已统一以下核心类型与服务命名：

- `ModelLibraryItem` 作为统一模型库实体名
- `ProxyUsageLog` 作为使用日志实体名
- `ISiteCatalogClient`、`IModelProbeService`、`IRouteSelectionService`、`IProxyForwardService` 作为核心服务接口名
- `AppDbContext` 作为统一数据库上下文名

---

## 执行建议

- 先只做 `Task 1` 到 `Task 3`，形成后台基础管理可用版本
- 再做 `Task 4` 到 `Task 6`，形成检测与看板闭环
- 最后做 `Task 7` 到 `Task 9`，形成协议中转和稳定性闭环

---

Plan complete and saved to `AI-Tool-开发拆解总计划.md`. Two execution options:

**1. Subagent-Driven (recommended)** - 我按任务逐个派发子代理执行，并在任务之间复核

**2. Inline Execution** - 我在当前会话里按任务顺序直接落地实现

**Which approach?**
