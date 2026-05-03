# RelayBench 全项目代码优化施工文档

> 日期：2026-05-01  
> 状态：待施工  
> 面向执行者：先按本文做结构优化和质量护栏，不在同一轮里追加新业务功能。每个阶段完成后都要运行对应验证命令，并确认没有把 `bin/`、`obj/`、发布包、临时测试文件混入源码提交。

## 1. 目标与边界

### 1.1 优化目标

本次优化的核心目标是让 RelayBench 从「功能已经快速堆起来」进入「可持续维护、可稳定扩展」阶段：

- 降低 `MainWindowViewModel.*`、`ProxyDiagnosticsService.*`、`MainWindow.xaml`、`WorkbenchTheme.xaml` 的维护压力。
- 收敛 OpenAI Chat Completions、Responses、Anthropic Messages 三种协议的探测、转换、对话、深测执行路径。
- 统一单站测试、批量快测、批量深测、稳定性测试、应用接入之间的执行状态、实时进度、图表排序和错误解释。
- 把 UI 样式、弹窗、滚动条、动画、悬停、按钮状态收进稳定的资源体系，减少页面里重复写法。
- 补齐测试分层，让协议选择、语义评估、Trace 脱敏、图表数据排序、批量深测调度都能被自动化覆盖。
- 建立发布前检查清单，避免乱码、临时文件、缓存、构建产物进入源码。

### 1.2 本次不做

- 不更换 WPF 技术栈。
- 不移植 OpenWebUI、OpenCode 或 BenchLocal 的整体架构。
- 不重写所有页面，不做破坏现有绑定的大规模 MVVM 框架替换。
- 不为了「代码更漂亮」改动探针判定口径；测试放宽或收紧必须单独有业务理由和回归样例。
- 不在代码优化阶段顺手新增大功能，除非该功能是为了解耦、验证或消除重复代码所必需。

## 2. 当前项目判断

### 2.1 代码体量热点

根据当前源码行数，维护压力主要集中在这些文件：

| 文件 | 行数级别 | 主要风险 |
| --- | ---: | --- |
| `RelayBench.App/MainWindow.xaml` | 2700+ | Shell、导航、多个 Overlay、弹窗内容混在一个 XAML 文件里，弹层层级和样式容易互相影响。 |
| `RelayBench.App/Resources/WorkbenchTheme.xaml` | 2200+ | 全局样式、控件模板、滚动条、按钮动画都在一起，局部修改容易引发全局视觉回归。 |
| `RelayBench.Core/Services/ProxyDiagnosticsService.Advanced.Protocol.cs` | 1800+ | 深测探针、协议适配、payload 构建、Trace 附加、判定整合耦合过高。 |
| `RelayBench.Core/Services/ProxyDiagnosticsService.Probes.Payloads.cs` | 1100+ | payload 模板集中，新增协议和新增探针时容易复制分支。 |
| `RelayBench.App/ViewModels/MainWindowViewModel.ModelChat.cs` | 1000+ | 会话、预设、发送、附件、多模型、UI 状态混合。 |
| `RelayBench.App/ViewModels/MainWindowViewModel.Operations.cs` | 800+ | 单站测试执行计划、按钮入口、取消、状态、深测参数耦合。 |
| `RelayBench.App/ViewModels/MainWindowViewModel.ProxyTrends.LiveCharts.cs` | 800+ | 实时图表、占位、补充探针顺序、稳定性图表刷新逻辑集中。 |
| `RelayBench.App/ViewModels/MainWindowViewModel.BatchDeepChart.cs` | 800+ | 批量深测状态机、徽标、摘要、图表数据都在同一文件里。 |
| `RelayBench.Core.Tests/Program.cs` | 1500+ | 测试覆盖已经有价值，但单文件测试入口太大，后续增加测试会越来越难定位。 |

这些文件不是「坏代码」，而是项目快速迭代后的自然结果。施工策略应该是小步拆分、先保行为、再抽模型，避免一次性推倒。

### 2.2 已暴露的典型问题

近期改动和问题反馈已经暴露出几类重复风险：

- 协议判断路径不统一：单站、批量、应用接入、聊天窗口曾经存在不同入口各自判断模型协议的问题。
- 深测和图表实时状态耦合：测试过程中某些项未出现、结束后才出现，说明执行计划和展示占位没有完全同源。
- 批量深测调度语义不稳定：同站点排队、多模型逐项实时展示、并发上限之间缺少独立调度模型。
- 弹窗层级容易互挡：图表详情、入口组维护、拉取模型、应用到软件等弹窗共享主窗口区域但缺少统一 Overlay 层级规则。
- UI 样式重复：页面、弹窗、按钮、表格、滚动条、hover 放大、暗色面板 hover 状态在多个位置散落。
- 测试入口集中：已有很多测试，但测试组织方式不利于快速判断某一类回归。
- 中文字符串和编码敏感：项目内中文 UI 文案多，发布和提交时必须明确 UTF-8、避免乱码。

## 3. 总体优化方案

### 3.1 推荐路线

采用「护栏优先、纵向切片、保守抽离」路线：

1. 先补检查和测试组织，确保后续重构有安全网。
2. 把协议探测、协议选择、payload 转换从业务流程里抽成稳定服务。
3. 把单站、稳定性、批量深测的执行计划和实时进度统一成一套模型。
4. 把图表数据生成从 ViewModel 中抽出，保证排序、占位、实时更新可测试。
5. 把弹窗和 UI 资源拆分成可复用资源字典和轻量协调器。
6. 最后做性能、日志、发布清单收口。

### 3.2 设计原则

- **先测后拆**：每个要抽离的行为先补最小测试，尤其是协议选择、探针顺序、图表排序。
- **保持绑定稳定**：XAML 绑定名称短期不大改，先在 ViewModel 内委托到新服务，降低 UI 回归。
- **纯逻辑优先抽离**：优先抽 `static` builder、排序器、payload converter、evaluator、state projector；暂不强行引入复杂依赖注入。
- **一阶段一类问题**：不要在协议优化阶段同时改按钮样式，不要在 UI 阶段调整探针判定。
- **源码提交干净**：只提交 `.cs`、`.xaml`、`.csproj`、`.md`、资源文件等源码资产，不提交构建产物。

## 4. 目标架构

### 4.1 Core 层目标

Core 层应该聚焦无 UI 的诊断能力：

```text
RelayBench.Core
├─ Models
│  ├─ 协议探测结果
│  ├─ 探针结果和 Trace
│  ├─ 稳定性、并发、吞吐、模型目录结果
│  └─ 应用接入目标和协议能力
├─ Services
│  ├─ ProxyDiagnosticsService.*           诊断编排
│  ├─ ProxyWireApiProbeService            协议探测和选择
│  ├─ ProxyConversationTransportAdapter   三协议转换和请求发送
│  ├─ ProxyProbePayloadFactory            探针 payload 生成
│  ├─ ProxyProbeTraceBuilder              Trace 组装和脱敏入口
│  ├─ SemanticProbeEvaluator              语义类判定
│  └─ ToolCallProbeEvaluator              工具调用判定
└─ Support
   ├─ 打分、Token 估算、命令执行、区域目录
   └─ 纯工具方法
```

短期不需要完全创建所有文件，但每次碰到对应区域时都应该朝这个边界靠拢。

### 4.2 App 层目标

App 层保留 WPF 和绑定职责：

```text
RelayBench.App
├─ ViewModels
│  ├─ MainWindowViewModel.*               仍作为主绑定入口
│  ├─ Chat*ViewModel                      大模型对话局部状态
│  ├─ Proxy*RowViewModel                  图表、表格行
│  └─ ClientApplyTargetItemViewModel      应用接入弹窗行
├─ Services
│  ├─ ProxySingleChartModelFactory        单站图表数据投影
│  ├─ ProxyBatchDeepStateProjector        批量深测状态和徽标投影
│  ├─ OverlayDialogCoordinator            弹窗层级和关闭策略
│  ├─ ChatAttachmentImportService         附件导入
│  └─ DiagnosticReportService             报告导出
├─ Resources
│  ├─ WorkbenchTheme.xaml                 基础颜色和通用控件
│  ├─ Motion.xaml                         动效 token 和动效行为
│  ├─ DialogTheme.xaml                    弹窗和 Overlay 样式
│  ├─ ChartTheme.xaml                     图表、Tooltip、图例样式
│  └─ PageTheme.xaml                      页面区块、KPI、工具栏样式
└─ Views/Pages
   └─ 各页面只负责布局和绑定，不写复杂业务规则
```

## 5. 分阶段施工计划

### 阶段 0：基线冻结和检查护栏

**目标：** 在正式重构前留下可重复验证的基线。

**涉及文件：**

- 修改：`RelayBench.Core.Tests/Program.cs`
- 修改：`README.md` 或新增：`docs/plans/2026-05-01-project-code-optimization.md`
- 检查：`RelayBenchSuite.slnx`

**施工步骤：**

- [ ] 运行现有测试：`dotnet run --project .\RelayBench.Core.Tests\RelayBench.Core.Tests.csproj`
- [ ] 运行构建：`dotnet build .\RelayBenchSuite.slnx`
- [ ] 运行空白字符检查：`git diff --check`
- [ ] 记录当前失败项。如果失败来自已有工作区改动，先单独修到绿灯，再进入结构优化。
- [ ] 在测试输出里确认协议探测、语义探针、图表排序、Trace 解读相关测试都能跑到。

**验收标准：**

- 测试和构建能在本地稳定复现。
- 如果存在已知失败，文档中明确失败原因和归属，不带着未知失败进入重构。
- 不产生新的发布包、缓存、临时输出文件。

**风险：**

- 当前工作区已有大量未提交改动，不能用 `git reset` 或 `git checkout --` 回滚。
- 如果测试依赖前一次临时产物，需要先清理测试逻辑，而不是清理用户改动。

### 阶段 1：测试组织和用例分层

**目标：** 让后续拆分有足够安全网，先治理 `RelayBench.Core.Tests/Program.cs` 单文件过大的问题。

**涉及文件：**

- 修改：`RelayBench.Core.Tests/Program.cs`
- 创建：`RelayBench.Core.Tests/TestRunner.cs`
- 创建：`RelayBench.Core.Tests/SemanticProbeEvaluatorTests.cs`
- 创建：`RelayBench.Core.Tests/ToolCallProbeEvaluatorTests.cs`
- 创建：`RelayBench.Core.Tests/ProtocolProbeTests.cs`
- 创建：`RelayBench.Core.Tests/ProbeTraceTests.cs`
- 创建：`RelayBench.Core.Tests/ChartProjectionTests.cs`
- 创建：`RelayBench.Core.Tests/BatchDeepWorkflowTests.cs`

**施工步骤：**

- [ ] 抽出极简 `TestRunner`，保留当前无外部测试框架的运行方式，避免引入包依赖。
- [ ] 将 `SemanticProbeEvaluator` 相关测试移到 `SemanticProbeEvaluatorTests.cs`。
- [ ] 将 `ToolCallProbeEvaluator` 相关测试移到 `ToolCallProbeEvaluatorTests.cs`。
- [ ] 将协议选择测试移到 `ProtocolProbeTests.cs`，覆盖：
  - Anthropic Messages 成功时优先选 `anthropic`。
  - Responses 成功时优先选 `responses`。
  - Anthropic 和 Responses 都失败时才回退 Chat Completions。
  - 不通过模型名称判断协议。
- [ ] 将 Trace 脱敏和详情解读测试移到 `ProbeTraceTests.cs`。
- [ ] 将单站深测图表排序、占位、补充探针显示测试移到 `ChartProjectionTests.cs`。
- [ ] 将批量深测逐项实时刷新、同站点排队、完成计数测试移到 `BatchDeepWorkflowTests.cs`。

**验收标准：**

- `Program.cs` 只负责注册测试类和汇总结果。
- 单个测试文件不超过 500 行。
- 测试名称能直接表达行为，不需要打开实现文件才能知道覆盖内容。
- `dotnet run --project .\RelayBench.Core.Tests\RelayBench.Core.Tests.csproj` 通过。

### 阶段 2：协议探测与三协议传输收敛

**目标：** 把「模型到底走 messages / responses / chat 哪条路」从各业务入口收敛到一处，保证单站、批量、应用接入、聊天窗口一致。

**涉及文件：**

- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.ProtocolProbe.cs`
- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.ConversationTransport.cs`
- 修改：`RelayBench.Core/Services/ChatConversationService.cs`
- 修改：`RelayBench.Core/Services/ChatRequestPayloadBuilder.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ProxyEndpointModelCache.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ClientApplyTargets.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.CodexApplyGuards.cs`
- 创建：`RelayBench.Core/Services/ProxyWireApiDecision.cs`
- 创建：`RelayBench.Core/Services/ProxyWireApiProbeService.cs`

**施工步骤：**

- [ ] 创建 `ProxyWireApiDecision`，字段包含：
  - `PreferredWireApi`
  - `ChatCompletionsSupported`
  - `ResponsesSupported`
  - `AnthropicMessagesSupported`
  - `ProbeModel`
  - `Summary`
  - `ScenarioResults`
- [ ] 将 `ResolvePreferredWireApi` 和 `ShouldProbeChatCompletionsForProtocolProbe` 移入独立服务或独立静态类。
- [ ] 保持当前策略：先探测 Anthropic Messages 和 Responses；如果两者都失败，再探测 OpenAI Chat Completions。
- [ ] 删除业务入口里基于模型名猜协议的判断，只保留用户可见文案中的模型名称提示。
- [ ] `ChatConversationService` 使用缓存的协议探测结果；没有缓存时允许按统一探测策略现探一次。
- [ ] `ProxyDiagnosticsService.RunSupplementalScenariosAsync` 中所有补充探针都通过统一传输适配层发送。
- [ ] 应用接入弹窗只展示探测结果，不自己重新拼接协议支持逻辑。

**验收标准：**

- 同一组 Base URL、Key、Model 在单站深测、批量深测、聊天窗口、应用接入中得到一致协议结论。
- Anthropic-only 接口不会再被深测流程强行当成 `/v1/chat/completions`。
- Chat Completions 只作为 Anthropic Messages 和 Responses 都失败后的回退路径。
- 协议选择测试全部通过。

**回归样例：**

- Anthropic Messages 接口：`/anthropic/v1/messages` 能跑通聊天和深测。
- Responses-only 模型：深测走 `/v1/responses`。
- OpenAI 兼容接口：前两者失败后走 `/v1/chat/completions`。
- 混合模型目录：每个模型按自身探测结果决定应用目标兼容性。

### 阶段 3：探针 payload 与 evaluator 解耦

**目标：** 降低 `ProxyDiagnosticsService.Advanced.Protocol.cs` 和 `ProxyDiagnosticsService.Probes.Payloads.cs` 的复杂度，让「构造请求」「发送请求」「评估结果」「写 Trace」四件事分开。

**涉及文件：**

- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.Advanced.Protocol.cs`
- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.Probes.Payloads.cs`
- 修改：`RelayBench.Core/Services/SemanticProbeEvaluator.cs`
- 修改：`RelayBench.Core/Services/ToolCallProbeEvaluator.cs`
- 修改：`RelayBench.Core/Services/ProbeTraceRedactor.cs`
- 创建：`RelayBench.Core/Services/ProxyProbePayloadFactory.cs`
- 创建：`RelayBench.Core/Services/ProxyProbeTraceBuilder.cs`
- 创建：`RelayBench.Core/Services/ProxyProbeEvaluationService.cs`

**施工步骤：**

- [ ] 将各种探针 payload 方法按场景移动到 `ProxyProbePayloadFactory`：
  - 基础 Chat、Stream、Responses、Structured Output。
  - System Prompt、Function Calling、Error Transparency。
  - MultiModal、Cache、Cache Isolation。
  - Instruction Following、Data Extraction、StructOutput、ToolCall、ReasonMath、CodeBlock。
- [ ] `ProxyDiagnosticsService.Advanced.Protocol.cs` 保留编排代码，不直接拼大型 JSON。
- [ ] `ProxyProbeEvaluationService` 负责把 `ExtractedOutput` 交给 evaluator，并把检查结果写回 `ProxyProbeScenarioResult`。
- [ ] `ProxyProbeTraceBuilder` 统一生成请求头、请求体、响应头、响应体、判定证据。
- [ ] `ProbeTraceRedactor` 只负责脱敏，不承担业务判断。
- [ ] evaluator 保持纯函数输入输出，禁止访问网络、配置、ViewModel 或 UI 文案。

**验收标准：**

- `ProxyDiagnosticsService.Advanced.Protocol.cs` 行数明显下降，核心方法以「执行哪些探针」为主。
- 新增一个探针时，改动路径固定为：`ProxyProbeScenarioKind`、payload factory、evaluator、图表展示、测试。
- Trace 详情的「判定解读」「关键证据」「原始 Trace」结构不回退。
- 已有深测测试全部通过。

### 阶段 4：单站、稳定性、批量深测执行计划统一

**目标：** 解决测试项目顺序、实时显示、结束后补项、批量调度等问题的根因。

**涉及文件：**

- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.Operations.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.BatchWorkflow.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.BatchDeepChart.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ProxyTrends.LiveCharts.cs`
- 修改：`RelayBench.Core/Models/ProxyDiagnosticsLiveProgress.cs`
- 创建：`RelayBench.Core/Models/ProxyProbeExecutionPlan.cs`
- 创建：`RelayBench.Core/Models/ProxyProbeExecutionStep.cs`
- 创建：`RelayBench.App/Services/ProxySingleExecutionPlanFactory.cs`
- 创建：`RelayBench.App/Services/ProxyBatchDeepScheduler.cs`

**施工步骤：**

- [ ] 将 `ProxySingleExecutionPlan` 从 `MainWindowViewModel.Operations.cs` 移出，变成可测试模型。
- [ ] 定义 `ProxyProbeExecutionStep`，包含：
  - `ScenarioKind`
  - `DisplayName`
  - `Phase`
  - `IsEnabled`
  - `Order`
  - `ExpectedBehavior`
- [ ] 单站深测开始前先生成完整 step 列表，图表根据 step 列表生成占位。
- [ ] 深测执行过程中，每完成一个 step 就推送 `ProxyDiagnosticsLiveProgress`，图表只更新对应 step，不改变顺序。
- [ ] 稳定性测试也使用 step 列表区分基础轮次和语义抽样。
- [ ] 批量深测的候选队列使用 `ProxyBatchDeepScheduler`，明确：
  - 多模型一项一项执行。
  - 同一站点入口串行。
  - 不同站点可并行，但并行数受配置限制。
  - 结果实时写入对应行，不等全部完成。
- [ ] ViewModel 只负责响应状态变化和触发刷新，不直接管理复杂调度循环。

**验收标准：**

- 深度测试所有图表项在开始时就固定出现，测试中不跳动，结束后不重排。
- 批量深测按模型或入口逐项出现结果，用户能看到当前执行到哪一项。
- 取消操作能取消未开始任务，并让已完成结果保留。
- 同站点多入口不会并发打爆同一上游。
- `BatchDeepWorkflowTests` 和 `ChartProjectionTests` 通过。

### 阶段 5：图表投影和渲染服务治理

**目标：** 让图表数据生成可测试，渲染服务只负责画图，不承担业务排序和状态判断。

**涉及文件：**

- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ProxyTrends.LiveCharts.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ProxyTrends.ChartBuilders.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.BatchDeepChart.cs`
- 修改：`RelayBench.App/Services/ProxySingleCapabilityChartRenderService.cs`
- 修改：`RelayBench.App/Services/ProxyBatchDeepComparisonChartRenderService.cs`
- 修改：`RelayBench.App/Services/ProxyTrendChartRenderService*.cs`
- 创建：`RelayBench.App/Services/ProxySingleCapabilityChartModelFactory.cs`
- 创建：`RelayBench.App/Services/ProxyBatchDeepChartModelFactory.cs`

**施工步骤：**

- [ ] 把 `BuildFinalSingleCapabilityChartItems`、`BuildLiveSingleCapabilityChartItems`、补充探针占位生成抽到 `ProxySingleCapabilityChartModelFactory`。
- [ ] 把批量深测徽标、摘要、状态计算抽到 `ProxyBatchDeepChartModelFactory`。
- [ ] 渲染服务输入只接受已经排序好的 chart item。
- [ ] 图表 tooltip 位置规则统一处理：
  - 值为 0 或 1、靠近左边界时，数值标签放到点右侧。
  - 靠近右边界时，标签向左收。
  - 标签永远不被图表边界裁切。
- [ ] 稳定性两张图表统一使用同一套样式，不保留两套视觉规则。
- [ ] 图表右侧空间根据可用宽度延展，不在中间提前结束。

**验收标准：**

- 图表 builder 单元测试覆盖排序、占位、0/1 标签位置、右侧延展。
- 渲染服务不再读取 ViewModel 私有状态。
- 单站、稳定性、批量深测的图表视觉和 tooltip 规则一致。

### 阶段 6：MainWindowViewModel 职责收敛

**目标：** 保留主 ViewModel 作为 WPF 绑定入口，但把纯逻辑、状态投影、弹窗协调逐步移出去。

**涉及文件：**

- 修改：`RelayBench.App/ViewModels/MainWindowViewModel*.cs`
- 创建：`RelayBench.App/Services/OverlayDialogCoordinator.cs`
- 创建：`RelayBench.App/Services/ApplicationCenterStateMapper.cs`
- 创建：`RelayBench.App/Services/ChatSessionCoordinator.cs`
- 创建：`RelayBench.App/Services/ProxyEndpointSelectionCoordinator.cs`

**施工步骤：**

- [ ] 梳理 `MainWindowViewModel.cs` 字段，把服务实例、持久状态、UI 状态、运行态状态分组。
- [ ] 不直接删除绑定属性，先将内部计算委托给服务。
- [ ] `ModelChat` 中拆出：
  - 会话加载和保存。
  - 消息编辑重发。
  - 多模型请求编排。
  - 附件导入和消息 block 生成。
- [ ] `ApplicationCenter` 中拆出：
  - 当前接口状态映射。
  - 历史接口同步策略。
  - 应用目标兼容性文本生成。
- [ ] `ProxyBatch` 中拆出：
  - 入口组清理。
  - 全部加入 / 全部关闭。
  - 快测结果到深测候选的映射。
- [ ] 所有命令仍在 `CommandBindings` 注册，避免 XAML 大面积变更。

**验收标准：**

- 单个 `MainWindowViewModel.*.cs` 文件尽量控制在 500 行以下；确实需要更长的文件必须职责单一。
- ViewModel 中不再直接拼复杂 JSON，不直接做图表排序，不直接推导协议兼容矩阵。
- 页面绑定名称保持稳定，现有页面不需要大规模改绑定。

### 阶段 7：XAML、资源字典和弹窗层级治理

**目标：** 解决 UI 细节不精细、弹窗互挡、样式散落、hover 放大被裁切等问题。

**涉及文件：**

- 修改：`RelayBench.App/App.xaml`
- 修改：`RelayBench.App/MainWindow.xaml`
- 修改：`RelayBench.App/Resources/WorkbenchTheme.xaml`
- 修改：`RelayBench.App/Resources/Motion.xaml`
- 创建：`RelayBench.App/Resources/DialogTheme.xaml`
- 创建：`RelayBench.App/Resources/ChartTheme.xaml`
- 创建：`RelayBench.App/Resources/PageTheme.xaml`
- 修改：`RelayBench.App/Views/Pages/*.xaml`

**施工步骤：**

- [ ] 将 `WorkbenchTheme.xaml` 拆分为：
  - 基础颜色、字体、阴影、输入框、按钮保留在 `WorkbenchTheme.xaml`。
  - 弹窗、遮罩、详情面板进入 `DialogTheme.xaml`。
  - 图表、tooltip、图例进入 `ChartTheme.xaml`。
  - 页面标题、KPI、区块、工具栏进入 `PageTheme.xaml`。
- [ ] `MainWindow.xaml` 中只保留 Shell、导航、页面容器和 Overlay Host。
- [ ] 所有 Overlay 使用统一层级：
  - Level 10：普通菜单和参数面板。
  - Level 20：业务弹窗。
  - Level 30：图表详情和 Trace 详情。
  - Level 40：确认弹窗。
- [ ] 任何新弹窗必须挂到统一 Overlay Host，不允许放在页面内部被图表或父容器遮挡。
- [ ] hover 放大保留，但按钮本体尺寸预留缩小，父容器设置足够 padding，避免放大后被裁切。
- [ ] 暗色面板 hover 不使用白底；改为同色系提亮、边框高亮或轻微阴影。
- [ ] 所有 ScrollViewer 使用 `WorkbenchScrollViewerStyle`，DataGrid、TextBox 内部滚动条也要覆盖。
- [ ] 窗口默认高度增加 1/5 后，对各页面做窗口化检查，确保没有底部遮挡和滚动条异常。

**验收标准：**

- 图表详情弹窗、入口组维护、拉取模型、应用到软件、确认弹窗不会互相遮挡。
- 单站、大模型对话、批量、应用接入、网络复核、历史报告页面的滚动条统一。
- 鼠标 hover 后没有白字白底、内容裁切、按钮挤压。
- 资源字典拆分后 `dotnet build` 通过，无缺失资源 key。

### 阶段 8：聊天窗口工程化整理

**目标：** 大模型对话能力继续增强时，代码结构能撑住多模型、文件、图片、Markdown、编辑重发和预设。

**涉及文件：**

- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ModelChat.cs`
- 修改：`RelayBench.Core/Services/ChatConversationService.cs`
- 修改：`RelayBench.Core/Services/ChatRequestPayloadBuilder.cs`
- 修改：`RelayBench.App/Controls/MarkdownViewer.cs`
- 修改：`RelayBench.App/Views/Pages/ModelChatPage.xaml`
- 创建：`RelayBench.App/Services/ChatSessionCoordinator.cs`
- 创建：`RelayBench.App/Services/ChatMessageProjectionService.cs`

**施工步骤：**

- [ ] 会话列表只维护顺序和选中，不在点击时重排。
- [ ] 用户消息右侧、大模型消息左侧的布局规则进入统一消息模板。
- [ ] 多模型对比以 `ChatModelAnswerViewModel` 为最小单元，单模型走气泡，多模型走列。
- [ ] Markdown 渲染只接收解析后的 block，不在 XAML 里拼解析逻辑。
- [ ] 附件导入统一转换成 `ChatAttachment`，payload 构建时按协议转换为对应格式。
- [ ] 思考强度只作为请求选项，不直接耦合 UI 控件值。
- [ ] 发送失败不能导致程序退出，所有网络异常都进入消息错误态和状态栏。

**验收标准：**

- 输入文字发送失败时应用不崩溃。
- 当前模型在右上角稳定显示。
- 多模型 2-4 列时流式输出互不串列。
- Markdown 代码块、语言标签、复制按钮、普通文本都正常。

### 阶段 9：异常、日志、Trace 和脱敏统一

**目标：** 所有用户可见错误都能解释，所有调试信息都可脱敏导出。

**涉及文件：**

- 修改：`RelayBench.App/Infrastructure/AppDiagnosticLog.cs`
- 修改：`RelayBench.Core/Services/ProbeTraceRedactor.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ProxyProbeTrace.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.Reporting.Sections.cs`
- 创建：`RelayBench.Core/Services/DiagnosticErrorClassifier.cs`

**施工步骤：**

- [ ] 建立统一错误分类：
  - 参数错误。
  - 鉴权错误。
  - 模型不存在。
  - 协议不兼容。
  - 上游限流。
  - 超时。
  - TLS / 网络错误。
  - 响应格式偏离。
  - 语义判定失败。
- [ ] Trace 详情中「为什么成功 / 为什么失败」优先展示普通用户能理解的结论。
- [ ] 原始 Trace 保留在底部，默认脱敏。
- [ ] 报告导出包含脱敏 trace json，不包含 API Key 明文。
- [ ] App 全局未处理异常写入 `AppDiagnosticLog`，并弹出温和错误提示，不直接退出。

**验收标准：**

- 用户看到的错误不是单纯堆栈或 HTTP 文本。
- 导出的报告可用于排查，但不泄漏 Key、Authorization、token、图片 base64 大块内容。
- 关键命令异常都有 `HandleNonFatalCommandException` 或等价路径兜底。

### 阶段 10：性能和资源释放

**目标：** 避免长时间测试、批量深测、图表刷新、聊天流式输出导致 UI 卡顿或资源泄漏。

**涉及文件：**

- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService*.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.BatchWorkflow.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ProxyTrends.LiveCharts.cs`
- 修改：`RelayBench.App/Services/*ChartRenderService*.cs`
- 修改：`RelayBench.App/Infrastructure/AsyncRelayCommand.cs`

**施工步骤：**

- [ ] 所有网络入口都接收 `CancellationToken`，取消后不继续写 UI 状态。
- [ ] 图表渲染结果按输入 hash 或测试 run id 做轻量缓存，避免相同数据重复绘制。
- [ ] 批量深测状态更新节流，例如 100-200 ms 合并一次 UI 刷新。
- [ ] 大文本输出框追加内容设置最大保留长度，避免长时间测试把内存撑高。
- [ ] `HttpClient` 使用方式保持当前安全策略，但统一超时、TLS 忽略和 header 配置。
- [ ] 所有 `CancellationTokenSource` 用完 Dispose，替换前先取消旧任务。

**验收标准：**

- 批量深测 20 个候选项时 UI 仍能拖动、滚动、取消。
- 稳定性测试 50 轮不会明显内存持续增长。
- 聊天流式输出长文本不会让输入框和会话列表卡死。

## 6. 测试矩阵

### 6.1 自动化测试

每个阶段至少运行：

```powershell
dotnet run --project .\RelayBench.Core.Tests\RelayBench.Core.Tests.csproj
dotnet build .\RelayBenchSuite.slnx
git diff --check
```

关键测试覆盖：

| 模块 | 必测内容 |
| --- | --- |
| 协议探测 | Anthropic / Responses / Chat 回退顺序、缓存、失败摘要。 |
| 对话 payload | 图片、文件、系统提示词、思考强度、max tokens、三协议字段转换。 |
| 语义 evaluator | ReasonMath、CodeBlock、StructOutput、ToolCall 的成功和失败样例。 |
| Trace 脱敏 | Header、URL query、JSON body、base64、错误响应。 |
| 图表投影 | 深测排序固定、占位、完成后不重排、tooltip 边界。 |
| 批量深测 | 同站点串行、不同站点并行、取消、逐项实时结果。 |
| UI 状态 | 弹窗层级、菜单点击外部关闭、会话点击不重排。 |

### 6.2 手工冒烟测试

每次阶段性完成后至少手工走这些流程：

- 单站测试：基础测试、深度测试、稳定性测试、查看图表、查看详情。
- 批量测试：导入入口组、快速测试、勾选候选、深度测试、打开深测图表。
- 应用接入：历史接口、拉取模型、应用当前接口、兼容性警告、强制勾选确认。
- 大模型对话：新建会话、发送普通文本、发送带代码块内容、编辑重发、切换预设、上传附件。
- 网络复核：切换子菜单、打开图表、导出报告。
- 历史报告：查看长报告、查看 Trace 导出、滚动条检查。
- 窗口状态：默认窗口、窗口化、全屏、高 DPI 下检查布局。

## 7. 提交和发布约束

### 7.1 提交粒度

建议按阶段或子阶段提交：

```text
test: split core regression harness
refactor: centralize proxy wire api probing
refactor: extract proxy probe payload factory
refactor: stabilize single and batch chart projection
refactor: split workbench xaml resources
fix: normalize overlay z-order and outside click handling
```

每个提交只解决一类问题。不要把 UI 大改、协议重构和测试迁移放进一个提交。

### 7.2 源码范围

允许提交：

- `*.cs`
- `*.xaml`
- `*.csproj`
- `*.slnx`
- `*.md`
- 必要的图片、图标、资源文件

禁止提交：

- `bin/`
- `obj/`
- `publish/`
- `Release/`
- `Debug/`
- 临时测试输出
- 本地运行数据
- API Key、真实 token、未脱敏 Trace

### 7.3 编码要求

- 文档、XAML、C# 统一 UTF-8。
- 中文 UI 文案不使用 shell 管道批量重写，避免编码错乱。
- 发布前用真实应用打开检查中文，不只看终端输出。
- `git diff --check` 只允许已知换行警告，不允许尾随空格和冲突标记。

## 8. 风险与回滚策略

| 风险 | 触发场景 | 控制方式 |
| --- | --- | --- |
| UI 绑定断裂 | 拆 ViewModel 属性或命令 | 先保留原属性名，内部委托新服务；每次只改一页。 |
| 协议路径回归 | 抽协议探测服务 | 先写 Anthropic / Responses / Chat 回退测试，再移动代码。 |
| 深测结果顺序错乱 | 图表投影抽离 | 以执行计划 step 顺序为唯一排序来源。 |
| 弹窗互相遮挡 | Overlay 拆分 | 统一 Overlay Host 和层级枚举，不允许页面内部自建高层弹窗。 |
| 测试迁移丢覆盖 | 拆 `Program.cs` | 迁移前后记录测试数量，保证数量不减少。 |
| 中文乱码 | 文档或 XAML 写入方式不当 | 使用正常编辑路径和 UTF-8，避免 shell 拼接写文件。 |
| 工作区已有改动被覆盖 | 多阶段重构 | 每阶段开始前看 `git status --short`，只改本阶段文件。 |

如果某阶段出现大面积回归，回滚方式不是整仓重置，而是撤回该阶段新增文件和对应调用点，保留之前阶段已验证成果。

## 9. 推荐执行顺序

第一轮建议只做到阶段 0-3：

1. 基线验证。
2. 测试拆分。
3. 协议探测服务收敛。
4. 探针 payload / evaluator 初步解耦。

这轮完成后，项目核心链路会稳很多，后续 UI 和图表治理风险更小。

第二轮做阶段 4-6：

1. 执行计划统一。
2. 图表投影抽离。
3. ViewModel 职责收敛。

第三轮做阶段 7-10：

1. XAML 和资源字典拆分。
2. 聊天窗口工程化整理。
3. 异常、Trace、日志统一。
4. 性能和发布清单收口。

## 10. 完成标准

本次「全项目代码优化」可以认为完成，需要满足：

- 三协议探测和使用路径在单站、批量、应用接入、聊天窗口中一致。
- 深测图表开始即完整占位，实时更新不跳动，结束不重排。
- 批量深测按计划逐项执行，并实时显示每项结果。
- `MainWindowViewModel.*` 中复杂纯逻辑明显减少，新增业务不需要继续堆到一个文件里。
- `ProxyDiagnosticsService.Advanced.Protocol.cs` 不再同时承担 payload、执行、评估、Trace 全部职责。
- XAML 弹窗层级统一，不再出现图表或维护窗口遮挡详情弹窗。
- 全局样式资源拆分后仍保持视觉一致，滚动条、按钮、hover、弹窗、图表风格统一。
- 自动化测试覆盖协议、evaluator、Trace、图表、批量调度关键路径。
- `dotnet run --project .\RelayBench.Core.Tests\RelayBench.Core.Tests.csproj` 通过。
- `dotnet build .\RelayBenchSuite.slnx` 通过。
- `git diff --check` 无新增格式问题。
- 源码提交不包含临时文件、构建文件、缓存、测试输出和未脱敏敏感信息。

