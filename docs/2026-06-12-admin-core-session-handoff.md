# 2026-06-12 Admin / Core 联调与页面问题交接总结

## 文档目的

这份文档用于给下一窗口/下一轮继续工作的上下文交接，重点记录本回合内：

- 已确认的架构事实
- 已完成的修复
- 当前仍未彻底解决的问题
- 为什么会出现这些问题
- 下一步建议优先处理顺序

---

## 一、当前架构事实（非常重要）

### 1. Admin 和 Core 的数据职责边界

当前项目中，**Admin 宿主并不等于 Core 宿主**，两者职责边界如下：

- `Admin`：
  - 持有 SQLite 数据库（站点、模型、密钥、UsageLogs 等）
  - 持有本地会话 / 对话记录存储（JSONL）
  - 持有从 Core 拉取来的部分运行时事件缓存（如 developer-trace、route-fallback）
- `Core`：
  - 持有代理运行时内存态
  - 负责真正的代理转发、选路、并发控制、Developer runtime 数据

本回合明确确认了一个原则：

> **能从 Admin 本地读取的数据，就不要绕 Core。只有必须依赖代理运行时的动作，才应该走 Core。**

---

### 2. 当前三类页面数据源仍未完全统一

用户提出：

- `/Admin/Developer/Invocations`
- `/Admin/UsageLogs`
- `/Admin/Chat#conversationLogPane`

这三处是否能共用一份获取源。

本回合分析结论：

**当前还不是统一源。** 目前仍是三套来源：

- `UsageLogs`：查 `Admin` 本地数据库 `ProxyUsageLogs`
- `conversationLogPane`：查 `Admin` 本地 `IConversationLogStore`
- `Developer/Invocations`：原本查 `Core /api/core/developer/*`

后续已确定要往“统一拉取源 / 统一读取入口”的方向推进，但这轮只完成了部分收口，没有最终全部统一。

---

## 二、本回合已经完成的修复

以下内容是已经改过并提交过的，下一轮不要重复排查同一个点。

### 1. Admin 启动同步与登录落点

已修复：

- Admin 启动时如果库里没有 `Sites` / `AccessKeys`，不再无意义地向 Core 做 full-sync 然后反复报 `400`
- 登录默认跳转、仪表盘首页路由问题

相关提交（历史已推送）：

- `5d479ed` 修复 Admin 启动同步与登录默认跳转
- `21c5688` 修复 Admin 仪表盘首页与同步回退
- `3cce492` 恢复 Admin 仪表盘默认首页

---

### 2. Chat 页面跨宿主调用问题

已修复：

- `/Admin/Chat#chatTestPane` 不再误请求 Admin 自己不存在的 `send/send-stream` 端点
- 页面中需要走 Core 的请求已显式使用 Core base URL
- `targets/models` 查询改为由 Admin 本地提供，不再必须依赖 Core

相关提交：

- `774c47e` 修复 Chat 页面跨宿主接口调用
- `8e443a7` 调整 Chat 查询为 Admin 本地读取

---

### 3. 仪表盘增强

已完成：

- 仪表盘显示 `Core` 连接状态
- 仪表盘显示最近同步状态
- 仪表盘数据库路径展示曾经加过，但**后来按用户要求删掉了**

相关提交：

- `be500c3` 增强仪表盘 Core 状态并细化 Chat 加载错误
- `4e63253` 仪表盘展示 Core 最近同步状态
- `342ec2c` 仪表盘显示 Admin 实际数据库路径（后续被撤回）
- `6fbfcf2` 移除仪表盘数据库路径展示

当前状态：

- 仪表盘**不再显示数据库路径**
- 只保留 Core 状态和最近同步状态

---

### 4. UsageLogs 页面部分展示已调整

已按用户要求做过这些改动：

- 模型列不再显示“最终”标签
- 流 / 非流只保留圆角包裹文本，不带图标
- 模型列已改为优先显示**真实最终调用模型 `attemptedModel`**，不是 `requestModel`

相关提交：

- `2ef7c23` 调整 UsageLogs 模型列与流式标记展示
- `b7bdb2d` 修正 UsageLogs 模型列显示真实调用模型

---

### 5. Developer 页面已经部分切到 Admin 本地源

这是本回合最重要的技术改造之一。

原来 `Developer/Invocations` 完全依赖 `Core /api/core/developer/*`，导致：

- 如果 Core 端点不存在 / 没跑对 / 返回 404，则整页异常
- 模拟器默认模型与默认密钥也依赖 Core metadata

本回合已改成：

- 调用追踪列表 / 详情：优先从 `AdminDeveloperTraceStore` 读取
- 模拟器默认访问密钥：优先从 Admin 本地数据库读取
- 模拟器模型列表：改为使用 Admin 本地 `AdminQueryMetadataService`
- 并发页：暂时改为本地降级视图，不再硬依赖 Core developer concurrency 端点

相关提交：

- `3dfc7c3` 调整 Developer 页面读取 Admin 本地源
- `ac368e6` 完善调试页模拟器本地默认模型选择

---

## 三、本回合确认过的关键问题与原因

### 1. 为什么 `UsageLogs` 有数据，但 `Developer/Invocations` 没数据？

这个问题本回合用户多次问到，已经确认：

- `UsageLogs` 看的是 `Admin` 本地数据库
- `Developer/Invocations` 原来看的却是 `Core` 运行时 developer 查询接口

因此会出现：

- `UsageLogs` 正常有数据
- `Developer/Invocations` 404 / 空 / 报错

这并不矛盾。

本回合已经开始改造，让 `Developer/Invocations` 改为走 Admin 本地统一源。

但要注意：

> 目前这个改造只是“开始做了”，不代表所有前端脚本与细节都完全闭环验证通过。

---

### 2. 为什么用户明明改了 `Routes`，Simulator 还命中旧路由？

这说明至少在某些链路上：

- Admin 本地数据库视图已经变了
- 但 Core 运行时快照 / 运行时缓存还没同步过去，或者 Simulator 仍在看 Core 那边的旧视图

这是**Admin 本地数据**与**Core 运行时数据**不一致的经典症状。

换句话说：

- `Routes` 页面改的是 Admin 本地数据
- `Simulator` 发请求时可能仍然走的是 Core 旧快照 / 旧缓存

本回合没有彻底解决这个一致性问题，只是确认了现象与方向。

---

### 3. 为什么本地联调时 `Core /api/core/developer/metadata` 还是 404？

本回合做了临时端口联调验证：

- 临时 `Core`：5229
- 临时 `Admin`：5230

结果：

- `Core /health` 正常
- `Admin /health` 正常
- 但 `http://127.0.0.1:5229/api/core/developer/metadata` 返回 `404`

这说明：

> 即使 Core 和 Admin 都启动，当前 `developer` 这组能力也并没有形成稳定可用闭环。

也就是说，用户在另一台机器上“Core 和 Admin 都启动了仍然有问题”这个反馈是合理的，不是单纯启动错进程。

这个点非常关键，下一轮不要再简单把它归因成“5029 跑错了实例”。

---

## 四、本回合仍未彻底解决的问题（重点）

以下是下一轮应优先继续追的事项。

### 1. `/Admin/Routes` 页面：拖拽顺序仍未被用户确认真实修复

本回合对 `Routes` 做过几轮修复：

- 一度改坏过拖拽逻辑
- 后来恢复回 `master` 风格实现
- 又补了 `siteName` 返回，修复站点名不显示
- 又加了“只有顺序真的变化才提示已更新”

相关提交：

- `1b0b3f9` 恢复 Routes 页面拖拽实现
- `cbeaf90` 修复 Routes 站点名展示与拖拽提示

**当前已知状态：**

- 站点名现在应该能显示（接口已返回）
- 但用户最后一次明确反馈是：
  - 样式根本没对上（后来已分析其实样式主结构和 master 很接近，根因主要是数据没回）
  - 依旧拖拽不动 / 只提示顺序已更新

因此：

> 这个页面**不能视为已彻底修复**。

下一轮建议：

- 若用户仍反馈拖不动，直接放弃 HTML5 原生 DnD
- 改成不依赖浏览器原生拖拽事件的纯鼠标 / pointer 拖拽实现

---

### 2. `/Admin/Detection` 页面：用户仍反馈“点检测无响应”

本回合做过修复：

- 前端补了 `response.ok` 判断
- 轮询失败时明确在 alert 里显示错误，而不是静默重试

相关提交：

- `3aa8c8d` 修复 Routes 拖拽与 Detection 反馈并调整 UsageLogs 展示

但用户最终并没有再次明确确认 `Detection` 已恢复。

所以当前应视为：

> 前端反馈链路已经加强，但是否彻底修好仍需继续验证。

下一轮优先查：

- `ModelHealthRequestService` 在 Admin 宿主里的依赖是否全都可用
- `IProxyForwardService` 在 Admin 宿主中是否真正注册并能发请求
- 是否存在因为本地站点协议 / key / path mode 不一致导致请求实际失败但页面没直观显示的问题

---

### 3. `/Admin/Developer/Invocations` 页面：当前已经部分切本地，但是否完全符合用户预期还未最终确认

目前已经做的：

- 列表 / 详情：改读 `AdminDeveloperTraceStore`
- 模拟器默认 key：改成本地库第一条启用 key
- 模拟器默认模型：改成本地可用模型中选默认值
- 并发页：暂时本地降级

但仍有几个风险：

#### 风险 A：前端展示字段与 `CoreDeveloperInvocationDetail` 本地拼装 DTO 是否完全匹配

本回合为了让页面不再依赖 Core 端点，手工拼了一份 detail/list response。虽然已编译通过，但页面是否还存在字段缺口，需要继续实测。

#### 风险 B：`AdminDeveloperTraceStore` 只存摘要，不存完整请求体 / 响应体

它和原 Core 的完整 trace 数据不是同一粒度。

这意味着：

- 页面能否完整展示 detail 区块，未必完全和旧 Core detail 接口等价
- 如果用户要求 detail 卡 100% 还原，可能还得扩展 developer-trace 事件结构或引入新的统一 snapshot 存储

#### 风险 C：并发页目前只是本地降级视图

当前并发页不再 404，但它不是原本 Core runtime 实时并发态，只是本地限制配置视图，不是完全等价实现。

所以这个页面当前状态应标记为：

> 已从“硬依赖 Core 404”降级到“本地可运行”，但还不是最终目标态。

---

### 4. `DeveloperSimulatorPane` 仍可能与 Core 真实运行时视图不一致

现在它的：

- 默认访问密钥
- 模型候选列表
- 默认模型
- 本地协议能力判断

都尽量改成从 Admin 本地数据出发。

但真正请求发出去时，目标仍然是 Core 代理链路，所以如果：

- Core 快照没同步
- Core 运行时缓存没刷新
- 路由规则刚改但 Core 还在旧视图

依旧可能出现：

- 页面看起来本地列表是对的
- 真正发请求时仍命中旧路由 / 旧模型 / 旧站点

因此下一轮如果继续追这个问题，重点不是继续改页面，而是：

> 检查 Admin → Core 配置同步后，Core 运行时元数据缓存是否真正刷新到了用户当前请求用到的模型和路由。

---

## 五、数据库与运行目录的重要结论

本回合还确认了一件非常重要的事情：

### 1. 当前分支 Admin 默认数据库位置

`src/AITool.Admin/Program.cs` 中当前写法是：

- 默认 `Data Source=<当前进程运行目录>\aitool.db`

这意味着：

- 启动目录不同，就可能连到不同的数据库文件
- 如果运行目录旁边没有真实主库，就会新建一份空库

### 2. 用户机器上已知真实数据库

本回合查到：

- 存在真实数据库：`D:\Tool\AiTool\aitool.db`
- 当前仓库 `D:\Code\AI-Tool` 下没有同名库

这解释了很多“为什么站点/模型/数据都不显示”的问题。

### 3. 本地验证结果

本回合做过一次只启动 Admin 的临时验证：

- 把 `D:\Tool\AiTool\aitool.db` 复制到临时运行目录
- 用临时端口启动 Admin
- 验证结果说明：**只要运行目录旁边放的是这份数据库，Admin 可以正常吃到它**

所以当前阶段应该明确：

> 数据不显示时，先看 Admin 实际启动目录旁边是不是那份正确的 `aitool.db`。

---

## 六、本回合新增/修改的代表性提交（按主题）

### Chat / Developer / UsageLogs / Routes 相关

- `8e443a7` 调整 Chat 查询为 Admin 本地读取
- `3dfc7c3` 调整 Developer 页面读取 Admin 本地源
- `ac368e6` 完善调试页模拟器本地默认模型选择
- `b7bdb2d` 修正 UsageLogs 模型列显示真实调用模型
- `cbeaf90` 修复 Routes 站点名展示与拖拽提示
- `1b0b3f9` 恢复 Routes 页面拖拽实现
- `3aa8c8d` 修复 Routes 拖拽与 Detection 反馈并调整 UsageLogs 展示
- `2ef7c23` 调整 UsageLogs 模型列与流式标记展示

### 仪表盘 / 同步状态

- `be500c3` 增强仪表盘 Core 状态并细化 Chat 加载错误
- `4e63253` 仪表盘展示 Core 最近同步状态
- `6fbfcf2` 移除仪表盘数据库路径展示

### 设计文档

- `fdd0e82` 新增统一观测拉取源设计文档

---

## 七、建议下一轮优先级

### 第一优先级
1. **彻底解决 Routes 拖拽无法生效**
   - 如果用户仍反馈拖不动，改成自实现 pointer 拖拽，不再依赖原生 DnD

2. **继续收口 Developer 页面剩余 Core 依赖**
   - 尤其是并发页，如果用户要求实时并发，就需要新的 Admin 本地统一观测模型或事件扩展

3. **验证 Simulator 实际请求是否仍命中旧路由**
   - 核查 Admin → Core 同步后的 Core 运行时缓存刷新是否真正生效

### 第二优先级
4. **补齐统一观测源真正的服务化设计落地**
   - 现在只是局部切换，没有完整统一 service / snapshot / mapping 层

5. **继续 1:1 收口 UsageLogs / Routes / Developer 页面体验**
   - 特别是如果用户再指出具体样式差异，要按具体区域逐项对齐

---

## 八、给下一窗口的简短结论

如果只看一句话：

> 本回合最大的实质性工作，是把原本严重依赖 `Core` developer 端点的 `Developer/Invocations` 和 `Simulator`，开始切换到 `Admin` 本地源；但 `Routes` 拖拽、`Detection` 真正可用性、以及 `Simulator` 与 Core 运行时一致性，仍然没有彻底闭环。

下一轮继续时，建议直接围绕这三件事展开：

- `Routes` 真拖拽
- `Detection` 真检测
- `Simulator` / `Core` 运行时一致性
