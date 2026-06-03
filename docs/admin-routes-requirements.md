# `/Admin/Routes` 路由规则管理需求文档

## 1. 文档目的

本文档用于梳理 `/Admin/Routes` 后台页面及其配套运行机制的业务需求，便于将该能力移植到另一套软件中。

本文档重点描述：

- 用户可见的功能需求
- 页面结构与交互流程
- 核心数据语义
- 配套接口职责
- 运行时路由与失败切换规则
- 边界条件与迁移时待确认事项

不以当前代码实现细节作为需求本身，但会在必要处说明当前系统的实现约束，以便迁移时评估差异。

## 1.1 相关代码

- 页面入口菜单：[src/AITool.Web/Pages/Shared/_Layout.cshtml](src/AITool.Web/Pages/Shared/_Layout.cshtml)
- 首页入口：[src/AITool.Web/Pages/Index.cshtml](src/AITool.Web/Pages/Index.cshtml)
- 页面主体：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml](src/AITool.Web/Pages/Admin/Routes/Index.cshtml)
- 页面模型：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml.cs](src/AITool.Web/Pages/Admin/Routes/Index.cshtml.cs)
- 后台接口控制器：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs)
- 路由规则实体：[src/AITool.Domain/Proxy/ProxyRouteRule.cs](src/AITool.Domain/Proxy/ProxyRouteRule.cs)
- 主入口实体：[src/AITool.Domain/Proxy/ProxyRouteEntry.cs](src/AITool.Domain/Proxy/ProxyRouteEntry.cs)
- 数据库映射与索引：[src/AITool.Infrastructure/Persistence/AppDbContext.cs](src/AITool.Infrastructure/Persistence/AppDbContext.cs)
- 运行时选路服务：[src/AITool.Infrastructure/Routing/RouteSelectionService.cs](src/AITool.Infrastructure/Routing/RouteSelectionService.cs)
- OpenAI 代理转发与失败切换：[src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs)
- Anthropic 代理转发与失败切换：[src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs)
- 熔断状态存储：[src/AITool.Infrastructure/Proxy/RouteCircuitStateStore.cs](src/AITool.Infrastructure/Proxy/RouteCircuitStateStore.cs)
- 后台权限与路由保护：[src/AITool.Web/Program.cs](src/AITool.Web/Program.cs)

---

## 2. 功能概述

`/Admin/Routes` 是后台“路由规则管理”页面，用于维护“主入口”与“候选实例队列”的映射关系。

其核心能力是：

- 为一个主入口配置多个候选实例
- 为候选实例维护严格顺序
- 运行时按顺序依次尝试候选实例
- 当前实例失败时自动切换到下一个实例
- 某个实例成功后立即停止后续尝试
- 全部候选失败时返回最终失败结果

因此，这一功能不是简单的“名称映射”配置，而是一套“有序候选队列 + 失败自动切换”的路由编排能力。

相关代码：

- 页面主体与交互入口：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:1-87](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L1-L87)
- 页面菜单入口：[src/AITool.Web/Pages/Shared/_Layout.cshtml:56-63](src/AITool.Web/Pages/Shared/_Layout.cshtml#L56-L63)
- 首页快捷入口：[src/AITool.Web/Pages/Index.cshtml:29-33](src/AITool.Web/Pages/Index.cshtml#L29-L33)、[src/AITool.Web/Pages/Index.cshtml:68-72](src/AITool.Web/Pages/Index.cshtml#L68-L72)

---

## 3. 适用对象与权限要求

该功能面向后台管理员使用。

要求：

- 页面仅允许管理员访问
- 配套后台接口仅允许管理员调用
- 普通调用方不直接接触该管理页面

迁移到新系统时，应将其纳入后台管理权限体系，而不是开放为普通业务配置页。

相关代码：

- 后台认证与 Admin 保护：[src/AITool.Web/Program.cs:50-77](src/AITool.Web/Program.cs#L50-L77)
- `/Admin/**` 与 `/api/admin/**` 路由保护：[src/AITool.Web/Program.cs:203-244](src/AITool.Web/Program.cs#L203-L244)、[src/AITool.Web/Program.cs:260-286](src/AITool.Web/Program.cs#L260-L286)

---

## 4. 核心业务概念

## 4.1 主入口

主入口是对外暴露的本地逻辑入口名，用于承载一整组候选实例配置。

主入口的正确语义是：

- 它代表一个本地路由入口
- 它对应一组有顺序的候选实例
- 调用方只关心主入口名
- 系统内部根据主入口名决定实际尝试哪些上游实例

约束说明：

- 主入口名不要求与任何上游模型同名
- 主入口名不要求和某个固定站点绑定
- 主入口名本质上是路由标识，不是上游模型身份

示例：

- 主入口：`auto`
- 候选实例：
  - 站点 A / 模型 X
  - 站点 B / 模型 Y
  - 站点 A / 模型 Z

调用方始终请求 `auto`，系统内部再按配置顺序依次尝试真实上游。

相关代码：

- 主入口实体定义：[src/AITool.Domain/Proxy/ProxyRouteEntry.cs:2-13](src/AITool.Domain/Proxy/ProxyRouteEntry.cs#L2-L13)
- 主入口列表与创建/删除接口：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:127-216](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L127-L216)

## 4.2 候选实例

候选实例是主入口下的一项可尝试目标。

每个候选实例至少应包含：

- 所属站点
- 上游模型实例名
- 在当前队列中的顺序
- 启用状态（如新系统需要保留该能力）

约束说明：

- 候选队列的对象是“实例”，不是“站点”
- 同一站点允许在同一个主入口下重复出现
- 同一上游模型实例是否允许重复，建议保留人工配置自由度，不做系统层强制去重，除非新系统另有约束

相关代码：

- 路由规则实体定义：[src/AITool.Domain/Proxy/ProxyRouteRule.cs:2-31](src/AITool.Domain/Proxy/ProxyRouteRule.cs#L2-L31)
- 数据库映射与索引：[src/AITool.Infrastructure/Persistence/AppDbContext.cs:101-118](src/AITool.Infrastructure/Persistence/AppDbContext.cs#L101-L118)

---

## 5. 页面目标

页面目标是让管理员能够长期维护“主入口 -> 有序候选实例队列”的配置关系。

页面不应仅表现为一次性临时编辑器，而应满足：

- 主入口集合可持续维护
- 任一主入口的完整候选队列可持续维护
- 候选项可随时增加、删除、排序
- 变更后可以即时生效

相关代码：

- 页面整体结构：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:14-87](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L14-L87)
- 页面模型无业务逻辑，仅前端调用接口：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml.cs:4-8](src/AITool.Web/Pages/Admin/Routes/Index.cshtml.cs#L4-L8)

---

## 6. 页面结构需求

页面采用左右双栏结构。

## 6.1 左栏：主入口列表

左栏用于展示和管理所有主入口。

应展示的信息：

- 主入口名称
- 当前候选数量

应支持的操作：

- 新建主入口
- 选中某个主入口
- 删除某个主入口

设计要求：

- 左栏始终作为全局主入口集合视图
- 用户首先管理的是“主入口集合”，而不是某次临时编辑会话

相关代码：

- 左栏结构：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:14-45](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L14-L45)
- 主入口列表渲染：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:297-323](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L297-L323)

## 6.2 右栏：当前主入口的候选实例队列

右栏用于展示并维护当前选中主入口的完整候选实例队列。

应展示的信息：

- 当前主入口名称
- 当前主入口下的完整候选实例列表
- 可添加的候选实例操作区

每个候选项至少应展示：

- 站点名
- 上游模型实例名
- 当前顺序位置
- 排序操作入口
- 删除操作入口

设计要求：

- 右栏永远表达“当前主入口的真实完整队列”
- 不是一次性编辑草稿区
- 进入某个主入口后，其完整队列应始终可见、可编辑、可排序

相关代码：

- 右栏结构：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:48-87](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L48-L87)
- 当前主入口规则加载与渲染逻辑：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:419-436](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L419-L436)、[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:479-630](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L479-L630)

---

## 7. 详细功能需求

## 7.1 主入口管理

### 7.1.1 查看主入口列表

系统应展示所有已创建的主入口。

每个主入口至少展示：

- 主入口名称
- 当前候选实例数量

用途：

- 便于管理员快速识别每个主入口的配置规模
- 便于切换目标主入口进行维护

相关代码：

- 主入口列表接口：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:127-155](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L127-L155)
- 前端列表渲染：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:297-323](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L297-L323)

### 7.1.2 新建主入口

系统应允许管理员创建新的主入口。

规则要求：

- 主入口名称不能为空
- 主入口名称不能重复
- 创建成功后应立即出现在主入口列表中
- 创建成功后建议自动切换为当前选中项

说明：

- 主入口名称只代表本地逻辑入口，不要求与上游模型同名

相关代码：

- 前端输入与提交：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:23-29](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L23-L29)、[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:360-386](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L360-L386)
- 创建接口：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:157-181](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L157-L181)

### 7.1.3 删除主入口

系统应允许管理员删除某个主入口。

业务含义：

- 删除主入口即删除该主入口下整组候选实例规则

规则要求：

- 删除前需要二次确认
- 删除后主入口及其关联队列一并移除
- 删除不存在的主入口时应返回明确失败提示

相关代码：

- 前端删除入口与确认：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:43-45](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L43-L45)、[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:388-417](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L388-L417)
- 删除接口：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:183-216](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L183-L216)

### 7.1.4 选择主入口

系统应允许管理员切换当前主入口。

规则要求：

- 点击某个主入口后，右栏应立即加载该主入口完整候选队列
- 如果该主入口暂无候选实例，应展示空状态

相关代码：

- 主入口切换逻辑：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:325-337](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L325-L337)
- 当前主入口规则加载：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:419-436](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L419-L436)
- 规则列表接口：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:381-413](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L381-L413)

---

## 7.2 候选实例池

### 7.2.1 可选实例来源

系统应提供“可选实例池”，供管理员将实例追加到当前主入口的候选队列中。

当前业务语义下，实例池应来自系统中可被路由使用的上游实例集合。

迁移时建议至少保留以下筛选语义：

- 仅展示可用站点
- 仅展示可用站点下可用模型实例

相关代码：

- 可选实例池接口：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:218-244](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L218-L244)

### 7.2.2 搜索过滤

系统应支持按关键字过滤可选实例池。

建议支持的过滤维度：

- 站点名称
- 模型实例名称

目标：

- 当实例池规模较大时，便于管理员快速定位目标实例

相关代码：

- 搜索输入区：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:54-71](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L54-L71)
- 前端过滤逻辑：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:454-477](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L454-L477)

---

## 7.3 当前主入口的候选实例队列维护

### 7.3.1 查看当前候选队列

系统应展示当前主入口下完整的候选实例队列。

展示要求：

- 严格按当前配置顺序展示
- 清楚区分每个候选实例的站点与模型信息
- 如果当前主入口无候选项，应展示空状态

相关代码：

- 当前队列渲染与空状态：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:48-52](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L48-L52)、[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:80-82](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L80-L82)、[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:511-515](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L511-L515)
- 当前主入口规则接口：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:381-413](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L381-L413)

### 7.3.2 添加候选实例

系统应允许管理员从实例池中向当前主入口追加候选实例。

规则要求：

- 必须先选中一个主入口
- 新候选应追加到当前队列末尾
- 添加后应可立即生效，或通过统一保存后立即生效

说明：

- 候选对象是“站点 + 上游模型实例”的组合
- 同一站点允许重复出现
- 不能将站点本身当作唯一候选对象

相关代码：

- 添加区与候选列表交互：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:74-85](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L74-L85)、[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:479-630](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L479-L630)
- 保存接口：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:415-484](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L415-L484)

### 7.3.3 调整候选顺序

系统应允许管理员调整当前候选实例队列的顺序。

规则要求：

- 顺序必须严格由人工维护
- 顺序越靠前，优先级越高
- 调整后应立即生效，或通过统一保存后立即生效
- 系统不能根据成功率、失败率等运行结果自动调整顺序

该能力是本页面的核心功能之一。

相关代码：

- 前端排序与保存逻辑：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:479-630](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L479-L630)
- 后端整队列保存：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:415-484](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L415-L484)

### 7.3.4 删除候选实例

系统应允许管理员从当前主入口队列中移除某个候选实例。

规则要求：

- 删除后应立即更新页面展示
- 删除后应立即生效，或通过统一保存后立即生效

相关代码：

- 前端删除与重绘逻辑：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:479-630](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L479-L630)
- 后端整队列保存：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:415-484](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L415-L484)

### 7.3.5 保存规则

系统应支持将当前主入口对应的整条候选队列保存为正式配置。

建议采用的业务语义：

- 保存对象是“当前主入口的完整候选队列”
- 不是针对单条规则做零散更新
- 保存后路由行为立即按最新顺序执行

这样更符合“主入口拥有一个完整有序候选列表”的业务抽象。

相关代码：

- 保存接口：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:415-484](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L415-L484)
- 当前实现为删除旧规则后按新顺序重建：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:435-480](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L435-L480)

---

## 8. 运行时路由需求

如果目标系统需要完整移植该功能，则不能只迁移后台页面，还需要同步迁移运行时选路逻辑。

## 8.1 基本执行规则

当请求命中某个主入口时，系统应：

- 读取该主入口下当前启用的候选实例队列
- 按人工配置顺序串行尝试候选实例
- 某个候选成功后立即停止后续尝试
- 当前候选失败时自动切换到下一个候选
- 只有当所有候选都失败时，才返回最终失败

相关代码：

- 运行时选路顺序：[src/AITool.Infrastructure/Routing/RouteSelectionService.cs:31-58](src/AITool.Infrastructure/Routing/RouteSelectionService.cs#L31-L58)
- OpenAI 代理失败切换：[src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:151-273](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L151-L273)
- Anthropic 代理失败切换：[src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs:113-315](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs#L113-L315)

## 8.2 顺序规则

顺序必须满足：

- 严格按配置顺序执行
- 系统不能擅自重排
- 系统不能基于历史运行结果自动优化顺序

也就是说，顺序是人工维护的静态优先级，而不是系统自适应优先级。

相关代码：

- 路由排序依据：[src/AITool.Infrastructure/Routing/RouteSelectionService.cs:31-58](src/AITool.Infrastructure/Routing/RouteSelectionService.cs#L31-L58)

## 8.3 失败判定规则

候选实例是否失败，不应仅以网络异常作为唯一判断条件。

至少以下情况都应视为当前候选失败：

- HTTP 状态码异常
- 响应为空
- 返回结果缺少有效业务数据
- 上游返回明确业务错误

判断标准应当是：

- 当前候选是否返回了可用结果

只要结果不可用，就应进入下一个候选。

相关代码：

- OpenAI 代理失败判定与继续尝试：[src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:151-273](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L151-L273)
- Anthropic 代理失败判定与继续尝试：[src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs:113-315](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs#L113-L315)

## 8.4 全部失败时的返回

当所有候选实例都失败时，系统应返回最终失败结果。

建议迁移时重点确认：

- 是否保留“返回最后一次真实尝试的错误”这一语义
- 是否需要统一包装为新系统标准错误结构

如果新系统已有统一错误规范，可在不破坏诊断能力的前提下做适配。

相关代码：

- OpenAI 代理最终错误出口：[src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:151-273](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L151-L273)
- Anthropic 代理最终错误出口：[src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs:113-315](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs#L113-L315)

## 8.5 熔断与临时避让

当前系统运行时已包含失败后的短时熔断/避让能力。

迁移时需要明确：

- 是否保留熔断能力
- 熔断阈值是否可配置
- 熔断恢复时长是否可配置
- 熔断是否需要可视化监控或后台展示

如果目标系统只迁移最小可用版本，可先保留“失败自动切换”主能力，再视需要补充熔断配置。

相关代码：

- 熔断状态存储：[src/AITool.Infrastructure/Proxy/RouteCircuitStateStore.cs:2-61](src/AITool.Infrastructure/Proxy/RouteCircuitStateStore.cs#L2-L61)
- 代理侧熔断配合逻辑：[src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs:151-273](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs#L151-L273)、[src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs:113-315](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs#L113-L315)

---

## 9. 数据模型需求

从可移植角度，建议至少保留以下业务对象。

## 9.1 主入口对象

建议字段：

- 主键 ID
- 主入口名称
- 创建时间

用途：

- 唯一标识一个本地逻辑入口
- 承载其下完整候选实例队列

相关代码：

- 主入口实体：[src/AITool.Domain/Proxy/ProxyRouteEntry.cs:2-13](src/AITool.Domain/Proxy/ProxyRouteEntry.cs#L2-L13)

## 9.2 路由规则对象

建议字段：

- 主键 ID
- 主入口标识
- 目标站点标识
- 目标站点名称（如需冗余展示）
- 上游模型实例标识或名称
- 队列顺序号
- 启用状态
- 创建时间 / 更新时间（如新系统需要审计能力）

语义要求：

- 一条规则代表一个候选实例
- 一个主入口对应多条规则
- 多条规则按顺序组成候选实例队列

相关代码：

- 路由规则实体：[src/AITool.Domain/Proxy/ProxyRouteRule.cs:2-31](src/AITool.Domain/Proxy/ProxyRouteRule.cs#L2-L31)
- 实体索引与约束映射：[src/AITool.Infrastructure/Persistence/AppDbContext.cs:101-118](src/AITool.Infrastructure/Persistence/AppDbContext.cs#L101-L118)

---

## 10. 接口职责需求

迁移到新系统时，建议按“主入口 + 整体队列”的语义组织接口，而不是围绕临时编辑动作组织。

## 10.1 主入口列表接口

职责：

- 返回所有主入口
- 每个主入口返回名称与候选数量

用于：

- 左栏主入口列表展示

相关代码：

- `GET /entries`：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:127-155](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L127-L155)

## 10.2 主入口详情接口

职责：

- 返回指定主入口下的完整候选实例队列
- 返回顺序必须与当前生效顺序一致

用于：

- 右栏当前队列展示

相关代码：

- `GET /list`：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:381-413](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L381-L413)

## 10.3 可选实例池接口

职责：

- 返回所有可供追加的上游实例
- 至少包含站点信息与模型实例信息
- 支持关键字过滤

用于：

- 添加候选实例操作区

相关代码：

- `GET /site-instances`：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:218-244](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L218-L244)

## 10.4 整队列保存接口

职责：

- 接收某个主入口的完整候选实例队列
- 按提交顺序覆盖保存当前主入口规则
- 保存完成后立即生效

用途：

- 添加、删除、排序后的统一持久化

相关代码：

- `POST /save`：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:415-484](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L415-L484)

## 10.5 主入口删除接口

职责：

- 删除指定主入口及其下全部候选规则

用途：

- 左栏主入口删除操作

相关代码：

- `POST /entries/delete`：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:183-216](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L183-L216)

## 10.6 当前已实现但页面未使用的接口

当前后端还实现了部分未在该页面主流程中直接使用的接口，迁移时可评估是否保留。

相关代码：

- `GET /models`、`GET /discover-sites`：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:245-379](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L245-L379)
- `POST /toggle/{ruleId}`：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:486-499](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L486-L499)

---

## 11. 页面状态与边界条件

迁移时应明确处理以下页面状态。

## 11.1 空状态

至少包括：

- 没有任何主入口
- 未选择主入口
- 当前主入口没有候选实例
- 可选实例池为空

这些状态都应提供明确提示，避免页面空白。

相关代码：

- 左栏与右栏空状态：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:31-35](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L31-L35)、[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:48-52](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L48-L52)、[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:80-82](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L80-L82)
- 主入口列表与当前队列空渲染：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:301-305](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L301-L305)、[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:511-515](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L511-L515)

## 11.2 输入校验

至少校验：

- 主入口名称不能为空
- 主入口名称不能重复
- 删除目标必须存在
- 保存时必须指定合法主入口
- 提交的候选实例数据必须完整合法

相关代码：

- 创建与删除校验：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:157-216](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L157-L216)
- 保存校验：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:415-484](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L415-L484)

## 11.3 一致性要求

当管理员执行添加、删除、排序后，应确保：

- 页面展示与后端真实配置一致
- 后续请求使用的是最新持久化结果
- 不依赖前端本地临时状态来“伪即时生效”

相关代码：

- 前端重绘与同步保存逻辑：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:479-630](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L479-L630)
- 后端整队列覆盖保存：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:415-484](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L415-L484)

---

## 12. 当前未覆盖或未完全产品化的能力

按现状看，以下能力不是当前页面的主要组成部分，迁移时需按目标系统需求决定是否补充：

- 导入
- 导出
- 批量操作
- 审计日志
- 更细粒度权限控制
- 单条规则启停开关的前端入口
- 草稿 / 发布流
- 分页
- 复杂筛选
- 路由复制能力

如果新系统更偏企业后台，建议优先评估审计日志、批量复制和单条启停能力。

相关代码：

- 前端当前未暴露单条启停，仅后端有接口：[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs:486-499](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs#L486-L499)
- 删除确认当前使用浏览器确认框：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml:393-395](src/AITool.Web/Pages/Admin/Routes/Index.cshtml#L393-L395)

---

## 13. 迁移建议

建议将该功能拆分为三个层次进行迁移。

## 13.1 基础配置层

最小可用能力包括：

- 主入口管理
- 可选实例池查询
- 候选实例追加
- 候选实例删除
- 候选实例排序
- 配置保存生效

## 13.2 运行时选路层

完整能力包括：

- 按顺序选路
- 候选失败自动切换
- 全部失败后统一返回失败结果

## 13.3 增强治理层

按目标系统复杂度补充：

- 熔断配置
- 审计日志
- 批量复制
- 导入导出
- 发布确认
- 监控与告警可视化

相关代码：

- 基础配置页与接口：[src/AITool.Web/Pages/Admin/Routes/Index.cshtml](src/AITool.Web/Pages/Admin/Routes/Index.cshtml)、[src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs](src/AITool.Web/Controllers/Admin/RouteRulesApiController.cs)
- 运行时选路与失败切换：[src/AITool.Infrastructure/Routing/RouteSelectionService.cs](src/AITool.Infrastructure/Routing/RouteSelectionService.cs)、[src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs](src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs)、[src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs](src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs)
- 熔断状态管理：[src/AITool.Infrastructure/Proxy/RouteCircuitStateStore.cs](src/AITool.Infrastructure/Proxy/RouteCircuitStateStore.cs)

---

## 14. 迁移前必须确认的开放问题

## 14.1 主入口是否允许自由命名

需要确认：

- 主入口是否继续允许任意命名
- 还是必须绑定到固定业务模型、协议入口或产品枚举

## 14.2 是否保留单条规则启停能力

当前后端存在相关能力，但前端未完整暴露。

迁移时需确认：

- 是否支持临时停用某个候选实例而不删除
- 是否需要在页面中直接展示启停开关

## 14.3 保存模式

需要确认新系统采用哪种交互模式：

- 操作后立即保存
- 编辑后统一保存
- 草稿保存后再发布

不同模式会影响用户体验和接口设计。

## 14.4 失败切换策略细节

需要确认：

- 哪些错误类型触发切换
- 是否保留最后错误透传语义
- 是否需要记录每次候选失败原因
- 是否需要提供人工禁用熔断候选的能力

## 14.5 是否补充治理能力

需要确认是否新增：

- 审计日志
- 分页筛选
- 批量复制
- 导入导出
- 操作人信息记录

相关代码：

- 主入口、规则、运行时代理与熔断逻辑的现状入口，均见“1.1 相关代码”章节

---

## 15. 总结

`/Admin/Routes` 的本质不是普通配置列表，而是一套“主入口 -> 有序候选实例队列 -> 按顺序失败切换”的后台路由编排能力。

如果目标是完整移植，建议至少同时覆盖以下三部分：

- 后台配置页面
- 配套管理接口
- 运行时选路与失败切换逻辑

只有三者一起迁移，才能保证该能力在新系统中具备完整业务价值。
