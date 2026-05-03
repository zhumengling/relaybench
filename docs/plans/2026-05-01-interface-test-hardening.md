# 接口测试体系加固施工文档

> 日期：2026-05-01  
> 目标：把 RelayBench 的接口测试从“有一些覆盖”提升到“可解释、可回归、可扩展、可定位”的工程化水平。  
> 范围：`RelayBench.Core.Tests`、核心协议解析与 payload 构造、网络诊断服务、应用接入服务、缓存与协议选择、部分 ViewModel 行为测试。

---

## 1. 施工目标

本次施工不追求简单地“放宽判定”或“堆更多 case”，而是要把测试体系补成一个能长期维护的护栏：

1. 覆盖真实接口最容易坏的地方：协议解析、请求构造、错误透传、流式解析、连接超时、限流、畸形响应。
2. 覆盖真实应用最容易踩坑的地方：聊天会话、附件、多协议 fallback、应用接入、缓存一致性、批量测试调度。
3. 让测试从“反射扫私有方法”逐步过渡到“可直接测试的内部逻辑”。
4. 让测试框架至少具备最基本的工程能力：异步、超时、过滤、报告、fixture、可复用 HTTP 模拟器。
5. 保持测试信号，避免为了通过率把判定放松得失去意义。

---

## 2. 现状复核结果

我对你提供的缺口清单做了交叉核对，并结合仓库现状补了一轮查找。结论如下：

| 项目 | 结论 | 备注 |
| --- | --- | --- |
| `ChatSseParser` | 基本属实 | 没有独立单测，只有聊天 fallback 间接走到一小段路径。 |
| `ChatRequestPayloadBuilder` | 属实 | 仅有 Anthropic 间接/反射测试，`BuildChatCompletionsPayload` 和 `BuildResponsesPayload` 独立测试缺失。 |
| `ChatMarkdownBlockParser` | 属实 | 没有测试。 |
| `ProbeTraceRedactor` | 部分属实 | 有少量综合测试，但边界覆盖明显不足。 |
| `RouteDiagnosticsService` | 属实 | 没有测试。 |
| `StunProbeService` | 属实 | 没有测试。 |
| `ExitIpRiskReviewService` | 属实 | 体量很大，未发现测试覆盖。 |
| `PortScanDiagnosticsService` | 属实 | 体量很大，未发现测试覆盖。 |
| `ClientApiDiagnosticsService` | 属实 | 没有测试。 |
| `CloudflareSpeedTestService` | 属实 | 没有测试。 |
| `CodexFamilyConfigApplyService` | 部分属实 | 只有 `ResolveCodexWireApiPreference` 一条路径被测。 |
| `ClientAppConfigApplyService` | 部分属实 | 仅覆盖 Codex chat fallback。 |
| `ProxyEndpointModelCacheService` | 部分属实 | 只有 Anthropic 缓存写入/读取和“不要误标 Chat”这类薄覆盖。 |

额外补充两点：

- `BasicNetworkDiagnosticsService`、`SplitRoutingDiagnosticsService`、`UnlockCatalogDiagnosticsService`、`WebApiTraceService` 也没有看到成体系的测试。
- `UiWorkflowTests` 存在，但偏表面，主要是 XAML 字符串和一个批量模板按钮行为，没有覆盖完整 ViewModel 状态流。

---

## 3. 现有测试体系的短板

### 3.1 TestRunner 的局限

当前 `RelayBench.Core.Tests` 采用的是自研 `TestCase + TestRunner`：

- 串行执行，没有并行调度。
- 没有原生 async case 类型，异步测试依赖手动 `GetAwaiter().GetResult()` 或测试内部自己包 `CancellationTokenSource`。
- 没有 fixture 生命周期，不支持统一 setup / teardown。
- 没有参数化测试，不适合批量矩阵。
- 没有测试过滤，无法单独跑某一类或某一个 case。
- 没有 TRX / JUnit 报告输出，CI 集成弱。

当前仓库里大约有 77 个 `yield return new TestCase`，测试组织已经开始变大，但还没有分层成可长期维护的形态。

### 3.2 反射测试过重

`TestReflectionHelpers.cs` 已经接近 700 行，`BindingFlags.NonPublic` 出现了很多次。这样做的优点是可以快速锁行为，缺点也很明显：

- 方法签名一变，测试通常不是编译期失败，而是运行期炸掉。
- 测试和实现绑定太紧，重构成本高。
- 反射测试越积越多，最后会变成“知道有测，但不敢改”。

### 3.3 HTTP fixture 过于原始

`TestHttpServerFixtures.cs` 现在是裸 `TcpListener` 手写 HTTP：

- 能模拟最基础的请求/响应。
- 但对 chunked、keep-alive、连接重置、慢响应、429、分段流式等场景支持有限。
- 适合“简易兼容性测试”，不适合长期作为唯一的网络模拟层。

---

## 4. 施工原则

1. **先保信号，再扩覆盖。** 不要为了通过率把判定一口气放得很松。
2. **先补最常坏的路径。** 解析、协议、错误、流式、超时、限流优先。
3. **先做可复现的测试，再改生产代码。**
4. **先把核心逻辑提成可测单元，再逐步减少反射。**
5. **测试框架不一定一次推翻，但必须补基本工程能力。**
6. **每一条放宽规则都要配反例。** 不能只看“能过”，还要看“该不过的能不过”。

---

## 5. 分阶段施工方案

### 阶段 0：测试护栏升级

**目标：** 先让测试框架能撑住后续增长。

**建议修改文件：**

- `RelayBench.Core.Tests/TestRunner.cs`
- `RelayBench.Core.Tests/TestCase.cs`
- `RelayBench.Core.Tests/TestCatalog.cs`
- `RelayBench.Core.Tests/Program.cs`
- `RelayBench.Core.Tests/TestHttpServerFixtures.cs`
- `RelayBench.Core.Tests/TestReflectionHelpers.cs`

**施工内容：**

- 给测试 runner 增加异步 case 支持，避免所有异步逻辑都靠 `.GetAwaiter().GetResult()`。
- 增加每个 case 的超时包裹，避免卡死时整套测试无响应。
- 增加按组过滤能力，例如只跑 `Protocol`、`Trace`、`Network`、`UI`。
- 增加简单报告汇总，至少打印总数、失败数、耗时。
- 给 `TestHttpServerFixtures` 抽一个更清晰的响应脚本层，支持：
  - 固定状态码
  - 固定响应头
  - 固定 body
  - 延迟响应
  - 连接关闭
  - 流式 SSE

**验收标准：**

- 单条测试不会因为异常卡死整套测试。
- 能单独跑某一类测试。
- 失败时能快速看出是哪一类出问题。

---

### 阶段 1：基础解析与请求构造

**目标：** 把聊天链路最基础、最容易坏的地方先测牢。

**建议新增测试文件：**

- `RelayBench.Core.Tests/ChatSseParserTests.cs`
- `RelayBench.Core.Tests/ChatRequestPayloadBuilderTests.cs`
- `RelayBench.Core.Tests/ChatMarkdownBlockParserTests.cs`

**建议覆盖内容：**

#### `ChatSseParser`

- `TryReadDataLine`
  - `data:` 正常行。
  - 大小写混合。
  - 空数据。
  - 非 `data:` 前缀。
- `IsDone`
  - `[DONE]`
  - `{"type":"message_stop"}`
  - 其他类型不应误判。
- `TryExtractDelta`
  - OpenAI Chat Completions `choices[0].delta.content`
  - Responses `response.output_text.delta`
  - Anthropic `content_block_delta`
  - `content_block.text`
  - `completion`
  - `error` body
  - 畸形 JSON

#### `ChatRequestPayloadBuilder`

- `BuildChatCompletionsPayload`
  - system prompt 注入。
  - `temperature` 下限/上限。
  - `max_tokens` 上限裁剪。
  - 用户消息与附件组合。
- `BuildResponsesPayload`
  - `max_output_tokens`
  - `reasoning` 字段映射
  - system prompt 的输入组织方式
- `BuildAnthropicMessagesPayload`
  - `thinking` 关闭
  - `max_tokens` 最小值
  - 图像附件转换
  - 文本文件附件转换

#### `ChatMarkdownBlockParser`

- 普通文本。
- 单个代码块。
- 多个代码块。
- 未闭合代码块。
- 代码块语言标签。
- 前后混合文本与代码。
- 空字符串和纯空白。

**验收标准：**

- 这三类测试能直接定位聊天核心链路的 bug。
- payload 和 parser 的行为不再只依赖集成测试发现问题。

---

### 阶段 2：Trace 与脱敏

**目标：** 让错误解释和调试输出更稳，避免“看起来脱敏了，其实漏了”。

**建议新增测试文件：**

- `RelayBench.Core.Tests/ProbeTraceRedactorTests.cs`
- `RelayBench.Core.Tests/ProbeTraceBuilderTests.cs`

**建议覆盖内容：**

#### `ProbeTraceRedactor`

- 空 header。
- 没有冒号的 header。
- `Authorization: Bearer ...`
- `x-api-key` / `api_key` / `token` / `cookie`
- URL 没有 query。
- URL query 中只有敏感字段。
- URL query 中敏感字段和普通字段混排。
- 嵌套 JSON body。
- 数组里的对象。
- 大字符串 / 类 base64 内容。

#### `ProxyProbeTraceBuilder`

- 成功和失败的说明顺序。
- `RequestBody` / `ResponseBody` / `Headers` 的组织顺序。
- 错误响应的摘要是否优先展示人能看懂的原因。

**验收标准：**

- 低级敏感信息不会漏。
- 说明文字的顺序稳定，方便用户阅读和排障。

---

### 阶段 3：协议探测与聊天传输

**目标：** 把“到底走 `messages`、`responses` 还是 `chat/completions`”测牢，避免模型名称误导协议选择。

**建议新增测试文件：**

- `RelayBench.Core.Tests/ProtocolProbeTests.cs`（扩充）
- `RelayBench.Core.Tests/ChatConversationServiceTests.cs`
- `RelayBench.Core.Tests/ProxyWireApiProbeServiceTests.cs`
- `RelayBench.Core.Tests/ProxyEndpointModelCacheTests.cs`

**建议覆盖内容：**

- Anthropic Messages 成功时，不能再误判成 OpenAI Chat。
- Responses 成功时，不能再回退 Chat。
- Anthropic 和 Responses 都失败时，才进入 Chat fallback。
- 协议别名归一化。
- 端点缓存不会把 Anthropic 误标成 Chat 支持。
- 聊天会话发送失败不会导致程序崩溃。
- 流式 SSE 断开后要能给出清楚错误，而不是沉默失败。

**验收标准：**

- 单站、批量、应用接入、聊天窗口的协议判断一致。
- `mimo-v2.5-pro claude` 这种混合输入不会再被模型名误导。

---

### 阶段 4：网络诊断服务

**目标：** 补齐接口测试最常见的网络层错误与协议层错误。

**建议新增测试文件：**

- `RelayBench.Core.Tests/RouteDiagnosticsServiceTests.cs`
- `RelayBench.Core.Tests/StunProbeServiceTests.cs`
- `RelayBench.Core.Tests/ExitIpRiskReviewServiceTests.cs`
- `RelayBench.Core.Tests/PortScanDiagnosticsServiceTests.cs`
- `RelayBench.Core.Tests/ClientApiDiagnosticsServiceTests.cs`
- `RelayBench.Core.Tests/CloudflareSpeedTestServiceTests.cs`
- `RelayBench.Core.Tests/SplitRoutingDiagnosticsServiceTests.cs`
- `RelayBench.Core.Tests/WebApiTraceServiceTests.cs`
- `RelayBench.Core.Tests/BasicNetworkDiagnosticsServiceTests.cs`

**建议覆盖内容：**

#### `RouteDiagnosticsService`

- MTR / traceroute 解析。
- hop 序号解析。
- 路由跳缺失。
- trace 输出中 stderr 的处理。
- 解析失败时的 fallback 说明。

#### `StunProbeService`

- NAT 分类。
- STUN 响应解析。
- UDP / TCP 失败。
- 转发地址缺失。

#### `ExitIpRiskReviewService`

- 风险源返回 429。
- 风险源返回空 JSON。
- JSON 结构偏离。
- 单个源失败不应该拖垮整个结果。

#### `PortScanDiagnosticsService`

- TCP connect 成功 / 失败。
- banner 探测。
- TLS 探测。
- HTTP 探测。
- Redis / DNS / STUN / UDP 分支。
- 解析失败 / 连接重置 / 超时。

#### `ClientApiDiagnosticsService`

- 429、401、403、404、5xx。
- 错误文案分类。
- 真接口可达但上游拒绝的区分。

#### `CloudflareSpeedTestService`

- 分档测速逻辑。
- 空闲延迟、抖动、丢包。
- 样本不足时的回退逻辑。

#### `BasicNetworkDiagnosticsService` / `SplitRoutingDiagnosticsService` / `WebApiTraceService`

- DNS 分歧。
- Trace 出口与网页出口不一致。
- 解析失败时的兜底说明。

**验收标准：**

- 网络复核类服务不再“看起来能跑”，而是能对错误场景给出稳定判断。

---

### 阶段 5：应用接入与配置写入

**目标：** 让“应用到软件”不只是能写配置，而是能对不同协议给出明确可用性结论。

**建议新增测试文件：**

- `RelayBench.Core.Tests/CodexFamilyConfigApplyServiceTests.cs`
- `RelayBench.Core.Tests/ClientAppConfigApplyServiceTests.cs`
- `RelayBench.Core.Tests/ClientApiConfigMutationTests.cs`
- `RelayBench.Core.Tests/ProxyEndpointModelCacheServiceTests.cs`

**建议覆盖内容：**

- Codex family 的 `wire_api` 选择。
- OpenAI compatible / Responses / Anthropic 三协议的应用路径。
- 不兼容目标的警告与二次确认。
- 用户确认后可继续勾选并应用。
- 配置写入失败后的回滚与状态提示。
- 缓存读取与写入的一致性。
- 过期、冲突、多模型并发读取。

**验收标准：**

- “能不能应用”有清晰的判断。
- “是否兼容”不再只靠模型名猜。

---

### 阶段 6：Proxy 诊断矩阵

**目标：** 让单站、批量、深测、稳定性、并发、流式、语义探针都有够用的回归点。

**建议新增或扩充测试文件：**

- `RelayBench.Core.Tests/ProxyDiagnosticsProbeTests.cs`
- `RelayBench.Core.Tests/SemanticProbeEvaluatorTests.cs`
- `RelayBench.Core.Tests/ToolCallProbeEvaluatorTests.cs`
- `RelayBench.Core.Tests/ChartProjectionTests.cs`
- `RelayBench.Core.Tests/TestHttpServerFixtures.cs`

**建议覆盖内容：**

- `ProxyDiagnosticsService` 的协议路径回退。
- `RunSupplementalScenariosAsync` 的各探针结果。
- `RunConcurrencyPressureAsync` 的阶梯逻辑。
- `ResolvePracticalConcurrencyLimit` 的临界值。
- `SemanticProbeEvaluator`
  - 指令遵循
  - 数据抽取
  - 结构化输出边界
  - 推理一致性
  - 代码块纪律
- `ToolCallProbeEvaluator`
  - 工具名
  - 参数类型
  - 大小写比较
  - Anthropic / Responses / OpenAI 三种工具调用形状

**验收标准：**

- 深测不再只验证“能不能跑通”，还要能验证“为什么通过、为什么失败”。
- 放宽规则时，必须同时有反例证明不会放太宽。

---

### 阶段 7：UI 和 ViewModel 行为测试

**目标：** 让界面上的“状态流”也有回归保护，而不是只测 XAML 文本。

**建议新增测试文件：**

- `RelayBench.Core.Tests/UiWorkflowTests.cs`（继续扩充）
- `RelayBench.Core.Tests/MainWindowViewModelTests.cs`
- `RelayBench.Core.Tests/ChatMessageViewModelTests.cs`
- `RelayBench.Core.Tests/BatchWorkflowTests.cs`

**建议覆盖内容：**

- 批量模板全选 / 全关切换。
- 会话列表点击不重排，只切换选中。
- 当前模型显示、参数面板关闭、弹窗层级。
- 发送失败不退出。
- 多模型聊天列布局。
- 深度测试中实时状态和结束状态一致。
- 图表详情弹窗不被遮挡。

**验收标准：**

- UI 的关键状态流有可重复测试。
- 不再只靠人工点界面发现回归。

---

### 阶段 8：测试框架现代化

**目标：** 在不破坏当前测试资产的前提下，逐步提升维护性。

**建议方向：**

1. 保留当前自研 runner 作为过渡层，但补齐 async、过滤、报告。
2. 对高频纯逻辑方法逐步改成 `internal` + `InternalsVisibleTo`，减少反射。
3. `TestHttpServerFixtures` 从“裸 TCP 拼 HTTP”升级到更结构化的脚本层。
4. 如果测试数量继续增长，再评估是否要迁到 `xUnit` 或至少提供一层兼容适配。

**验收标准：**

- 新测试的写法比现在更顺手。
- 反射 helper 的数量开始下降，而不是持续上升。

---

## 6. 推荐施工顺序

建议按这个顺序做：

1. `ChatSseParser`、`ChatRequestPayloadBuilder`、`ChatMarkdownBlockParser`
2. `ProbeTraceRedactor`、`ProxyProbeTraceBuilder`
3. 协议探测、聊天传输、端点缓存
4. 网络诊断服务
5. 应用接入与配置写入
6. Proxy 深测矩阵
7. UI 和 ViewModel 行为
8. 测试框架现代化

这个顺序的好处是：先补基础解析与协议判断，后面的网络服务和 UI 逻辑才更容易定位问题。

---

## 7. 重点风险与控制点

### 风险 1：为了通过率放松判定太多

**控制方式：**

- 每放宽一条规则，都必须补至少 2 个正向样例和 2 个负向样例。
- 如果只是“看上去过得去”，不允许改规则。

### 风险 2：测试跑太慢

**控制方式：**

- 先按分类过滤执行。
- 共享 fixture，避免重复起服务。
- 先补超时，再谈并行。

### 风险 3：反射测试继续膨胀

**控制方式：**

- 先把最常改的纯逻辑方法改成 `internal`。
- 再逐步减少 `TestReflectionHelpers.cs` 的体量。

### 风险 4：HTTP fixture 不够真实

**控制方式：**

- 先保留 `TcpListener` 作为底层能力。
- 在上层抽一层脚本化 fixture，支持延迟、关闭、分段和错误码。

---

## 8. 验收标准

这套施工完成后，至少要达到：

- `ChatSseParser`、`ChatRequestPayloadBuilder`、`ChatMarkdownBlockParser` 有独立测试。
- `RouteDiagnosticsService`、`StunProbeService`、`ExitIpRiskReviewService`、`PortScanDiagnosticsService`、`ClientApiDiagnosticsService`、`CloudflareSpeedTestService` 有成体系测试。
- `CodexFamilyConfigApplyService`、`ClientAppConfigApplyService`、`ProxyEndpointModelCacheService` 有完整路径测试，不止一条 fallback。
- `ProxyTrace`、`redactor`、`protocol probe`、`tool call`、`semantic evaluator` 都有边界覆盖。
- 测试框架能单独跑组、能超时、能输出结果。
- 现有 UI 关键状态流有行为测试，不只测样式字符串。

---

## 9. 结论

如果按这个顺序施工，收益最大的地方不是“测试数字涨了”，而是：

- 出问题时更快知道是解析、协议、网络、配置还是 UI 状态。
- 改协议选择、payload 结构、错误解释时不再心虚。
- 接口测试从“点状覆盖”升级成“分层覆盖”。
- 后续再加新服务、新协议、新模型时，不会又回到全靠手工验证的状态。

