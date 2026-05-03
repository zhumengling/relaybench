# BenchLocal 扩展探针施工文档

> 面向 AI 代理的工作说明：本文是 RelayBench 新增 4 类大模型接口深测探针的施工文档。实现时优先沿用现有 `ProxyDiagnosticsService`、`ProxyProbeScenarioResult`、`SemanticProbeEvaluator`、图表弹窗和历史报告链路，不移植 BenchLocal 的插件系统、Registry、Docker verifier 或桌面宿主结构。

**目标：** 在现有接口深测能力基础上，加入 `StructOutput` 加强版、`ToolCall` 深测、`ReasonMath` 一致性、`BugFind` 代码块纪律 4 类探针，并让图表详情弹窗能展示完整输入、输出和判定证据。

**架构：** 新探针作为现有深度测试的补充场景接入。核心层负责构造请求、发送请求、解析响应、生成结构化判定；App 层负责图表、弹窗、历史报告和批量深测展示。每个探针都必须可单独执行、可被稳定性抽样调用、可在批量深测中聚合。

**技术栈：** WPF、.NET、C#、`System.Text.Json`、OpenAI 兼容 `/v1/chat/completions`、工具调用协议、现有 RelayBench 图表和报告链路。

---

## 1. 范围与边界

### 1.1 本次新增能力

| 探针 | 定位 | 主要价值 | 接口稳定性价值 |
| --- | --- | --- | --- |
| `StructOutputEdge` | 结构化输出加强版 | 检查 JSON / CSV / 转义 / 类型保真 | 发现中转站包装、清洗、截断、格式漂移 |
| `ToolCallDeep` | 工具调用深测 | 检查工具选择、参数精度、错误恢复 | 发现 tool_calls 协议不兼容、参数丢失、模型不适合 Agent |
| `ReasonMathConsistency` | 推理一致性 | 检查固定答案任务的稳定输出 | 发现模型路由漂移、temperature 失控、上下文拼接异常 |
| `CodeBlockDiscipline` | 代码块纪律 | 检查代码块语言、修复内容、输出边界 | 支撑大模型对话代码块识别与真实开发使用体验 |

### 1.2 明确不做的内容

- 不移植 BenchLocal 的 Bench Pack 安装、更新、Registry 和插件机制。
- 不引入 Docker verifier，不执行模型生成的任意代码。
- 不做公开模型排行榜。
- 不要求所有模型都通过所有语义探针；探针结果用于诊断当前接口和模型是否适合真实接入。
- 不把 API Key、Authorization、Cookie 等敏感字段写入 Trace 明文。

### 1.3 与现有能力的关系

项目当前已有 `InstructionFollowing` 和 `DataExtraction` 语义探针。新增 4 类探针按下面方式扩展：

- `InstructionFollowing` 和 `DataExtraction` 保留，用作语义稳定性基础项。
- `StructOutputEdge` 接在 `DataExtraction` 后，专门测结构化边界和转义。
- `ToolCallDeep` 接在现有 `FunctionCalling` 后，专门测工具选择和参数质量。
- `ReasonMathConsistency` 和 `CodeBlockDiscipline` 默认进入「语义稳定性扩展测试」，可在稳定性巡检中抽样执行。

---

## 2. 用户可见效果

### 2.1 单站测试

「单站测试」深度测试区域增加 4 个可配置开关：

| 开关 | 默认 | 所属分组 | 显示短标 |
| --- | --- | --- | --- |
| 结构化边界 | 开启 | 接口稳定性 | `SO` |
| 工具调用深测 | 开启 | 应用接入可用性 | `TC` |
| 推理一致性 | 关闭 | 语义稳定性扩展 | `RM` |
| 代码块纪律 | 开启 | 真实使用体验 | `CB` |

深测摘要示例：

```text
基础能力 5/5；增强测试 6/7；语义扩展 3/4
SO 通过；TC 参数精度异常；RM 未执行；CB 通过
```

### 2.2 批量深测

批量深测结果行增加徽标：

```text
SO OK   TC RV   RM SK   CB OK
```

状态含义：

| 状态 | 含义 |
| --- | --- |
| `OK` | 通过 |
| `RV` | 有响应但判定未通过 |
| `CFG` | 配置不足，例如无可用模型 |
| `SK` | 用户关闭或当前模式跳过 |
| `NO` | 当前接口或模型不支持 |
| `ER` | 请求异常、超时或解析失败 |

### 2.3 图表详情弹窗

所有新增探针的图表行都必须提供「详情」按钮。弹窗显示：

- 基本信息：探针名、模型、协议、URL、状态码、耗时、Request ID、Trace ID。
- 输入：请求路径、请求参数、请求 body，敏感字段脱敏。
- 输出：响应头、原始响应 body、提取后的模型文本或 tool_calls。
- 判定：通过项、失败项、失败原因、建议排查方向。
- 操作：复制输入、复制输出、复制完整 Trace。

### 2.4 历史报告

历史报告新增「深测证据」章节：

- 单站报告记录每个新增探针的摘要和 Trace。
- 稳定性报告记录每轮抽样的探针名、结果和失败原因。
- 批量报告记录每个入口的 `SO`、`TC`、`RM`、`CB` 聚合结果。
- 导出报告时附带 `raw/probe-traces.json`，便于复盘。

---

## 3. 统一 Trace 设计

### 3.1 新增模型

创建 `RelayBench.Core/Models/ProxyProbeTrace.cs`：

```csharp
namespace RelayBench.Core.Models;

public sealed record ProxyProbeTrace(
    string Scenario,
    string DisplayName,
    string BaseUrl,
    string Path,
    string Model,
    string WireApi,
    string RequestBody,
    IReadOnlyList<string> RequestHeaders,
    int? StatusCode,
    string? ResponseBody,
    IReadOnlyList<string> ResponseHeaders,
    string? ExtractedOutput,
    IReadOnlyList<ProxyProbeEvaluationCheck> Checks,
    string Verdict,
    string? FailureReason,
    string? RequestId,
    string? TraceId,
    long? LatencyMilliseconds,
    long? FirstTokenLatencyMilliseconds,
    long? DurationMilliseconds);

public sealed record ProxyProbeEvaluationCheck(
    string Name,
    bool Passed,
    string Expected,
    string Actual,
    string Detail);
```

### 3.2 扩展结果模型

修改 `RelayBench.Core/Models/ProxyProbeScenarioResult.cs`：

```csharp
public sealed record ProxyProbeScenarioResult(
    ...
    string? TraceId = null,
    ProxyProbeTrace? Trace = null);
```

如果为了减少序列化影响，也可以保留 `Trace` 为可空字段，并只在深测、批量深测和历史报告需要时生成。

### 3.3 脱敏规则

创建 `RelayBench.Core/Services/ProbeTraceRedactor.cs`：

- `Authorization` 显示为 `Bearer sk-...abcd`。
- `api_key`、`apiKey`、`OPENAI_API_KEY`、`ANTHROPIC_API_KEY` 显示为 `***`。
- URL query 中的 `key`、`token`、`apikey` 显示为 `***`。
- 请求 body 中保留 `model`、`messages`、`tools`、`response_format` 等诊断必要字段。
- 文件内容和图片 base64 只显示摘要：文件名、大小、MIME、hash 前 8 位。

---

## 4. 探针 1：StructOutput 加强版

### 4.1 目标

确认接口在普通 `/v1/chat/completions` 场景下能稳定输出机器可解析结构，不被中转站包装成 Markdown，不丢字段，不改变类型，不破坏特殊字符。

### 4.2 场景设计

| 场景 | 名称 | 测点 | 通过条件 |
| --- | --- | --- | --- |
| `SO-EDGE-01` | JSON 边界值 | `null`、`false`、`0`、空数组、空对象、转义字符 | 严格 JSON object，字段和值完全匹配 |
| `SO-EDGE-02` | CSV 转义 | 逗号、双引号、换行、空字段、公式样文本 | 可被 `TextFieldParser` 或自定义 CSV parser 正确解析 |
| `SO-EDGE-03` | 嵌套结构保真 | 数组、对象、字符串数字、邮编、URL | 字段类型、数组长度、URL query 完整 |

### 4.3 请求模板：JSON 边界值

```json
{
  "model": "<model>",
  "max_tokens": 320,
  "temperature": 0,
  "messages": [
    {
      "role": "system",
      "content": "You are a structured output probe. Return exactly one compact JSON object. Do not use markdown. Do not add explanation."
    },
    {
      "role": "user",
      "content": "Return JSON with exactly these fields: empty_string is an empty string, null_value is null, zero is 0, false_value is false, empty_array is [], empty_object is {}, special_chars is a string containing a backslash, a double quote, a newline and a tab, nested_null is an object with a set to null and b set to [null, 1]."
    }
  ]
}
```

### 4.4 请求模板：CSV 转义

```json
{
  "model": "<model>",
  "max_tokens": 360,
  "temperature": 0,
  "messages": [
    {
      "role": "system",
      "content": "You are a CSV output probe. Return only CSV text. Do not wrap it in markdown. Use RFC4180-compatible quoting."
    },
    {
      "role": "user",
      "content": "Create CSV with headers id,name,note,total. Rows: 1, ACME, contains comma: alpha,beta, 12.50. Row 2: name is \"Quoted Team\", note contains a line break between first and second, total is empty. Row 3: name is Formula Safe, note is =SUM(A1:A2), total is 0."
    }
  ]
}
```

### 4.5 判定规则

`SemanticProbeEvaluator` 新增：

```csharp
public static SemanticProbeEvaluation EvaluateStructuredOutputEdge(
    string scenarioId,
    string? rawPreview)
```

判定轴：

- `Parseable`：能否按目标格式解析。
- `Correctness`：字段、值、类型、数组长度是否符合预期。
- `Discipline`：是否夹带 Markdown、解释文字、额外字段。

失败归因：

| 失败类型 | FailureKind | 提示 |
| --- | --- | --- |
| JSON / CSV 解析失败 | `ProtocolMismatch` | 当前输出不是目标格式 |
| 字段值或类型错误 | `SemanticMismatch` | 模型或中转层发生格式漂移 |
| 包进 Markdown | `SemanticMismatch` | 检查中转站 prompt 包装或输出清洗 |
| 响应截断 | `StreamNoDone` 或 `Timeout` | 检查 max tokens、超时或流式截断 |

### 4.6 UI 展示

单站图表显示：

```text
结构化边界  通过 / 异常
指标：SO 2/3
详情：JSON 通过，CSV 异常，嵌套结构通过
```

详情弹窗判定区示例：

```text
Parseable: 通过
Correctness: 失败
Discipline: 通过

失败项：
- 字段 special_chars 缺少 tab 转义。
- 响应未包含额外解释文字。
```

---

## 5. 探针 2：ToolCall 深测

### 5.1 目标

确认当前接口不仅能返回 `tool_calls`，还能够在多工具干扰、复杂参数、错误恢复和不该调用工具时保持正确行为。这个探针对 Codex、Claude CLI、Agent 工具链最关键。

### 5.2 场景设计

| 场景 | 名称 | 测点 | 通过条件 |
| --- | --- | --- | --- |
| `TC-DEEP-01` | 工具选择 | 12 个工具中选择正确工具 | 调用 `search_docs`，不调用无关工具 |
| `TC-DEEP-02` | 参数精度 | 日期、单位、多值参数 | 参数名、类型、值完全匹配 |
| `TC-DEEP-03` | 多步链路 | 第一次工具结果作为第二次参数 | 两次 tool call 顺序和参数正确 |
| `TC-DEEP-04` | 克制与错误恢复 | 不该调用工具、空结果后澄清 | 无工具调用或发起合理澄清 |

### 5.3 工具定义

使用固定工具池，避免不同场景工具数量不同导致比较不稳定：

```json
[
  {
    "type": "function",
    "function": {
      "name": "search_docs",
      "description": "Search internal documentation.",
      "parameters": {
        "type": "object",
        "additionalProperties": false,
        "properties": {
          "query": { "type": "string" },
          "limit": { "type": "integer" }
        },
        "required": ["query", "limit"]
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "create_ticket",
      "description": "Create an issue ticket.",
      "parameters": {
        "type": "object",
        "additionalProperties": false,
        "properties": {
          "title": { "type": "string" },
          "priority": { "type": "string", "enum": ["low", "medium", "high"] },
          "tags": { "type": "array", "items": { "type": "string" } }
        },
        "required": ["title", "priority", "tags"]
      }
    }
  }
]
```

实际实现中补齐 8 到 12 个干扰工具，例如 `get_weather`、`send_email`、`calculate_price`、`lookup_user`、`convert_units`。干扰工具必须固定，防止测试结果难以比较。

### 5.4 请求模板：工具选择

```json
{
  "model": "<model>",
  "temperature": 0,
  "tool_choice": "auto",
  "tools": ["<fixed-tool-pool>"],
  "messages": [
    {
      "role": "system",
      "content": "You are a tool-calling probe. Use tools only when needed. Do not answer from memory if a tool is required."
    },
    {
      "role": "user",
      "content": "Find the internal document about relay cache isolation. Return only the tool call."
    }
  ]
}
```

期望：

```json
{
  "name": "search_docs",
  "arguments": {
    "query": "relay cache isolation",
    "limit": 5
  }
}
```

### 5.5 判定规则

新增 `ToolCallProbeEvaluator`：

```csharp
public static ToolCallProbeEvaluation Evaluate(
    string scenarioId,
    string responseBody,
    IReadOnlyList<ToolCallExpectation> expectations)
```

判定轴：

- `ToolSelection`：工具名是否正确。
- `ArgumentPrecision`：参数名、类型、枚举和值是否匹配。
- `CallDiscipline`：不多调、不漏调、不把工具调用写成普通文本。
- `Recovery`：空结果或错误时是否合理澄清或重试。

失败归因：

| 失败类型 | FailureKind | 提示 |
| --- | --- | --- |
| 无 `tool_calls` | `ProtocolMismatch` | 当前接口可能不支持工具调用 |
| 工具名错误 | `SemanticMismatch` | 模型选择能力或工具描述理解异常 |
| 参数类型错误 | `SemanticMismatch` | 中转层可能损坏 JSON schema 或模型参数能力不足 |
| 返回文本模拟工具调用 | `ProtocolMismatch` | 客户端无法真实消费 |

### 5.6 应用接入联动

`ToolCallDeep` 通过时，在应用接入页面的协议兼容信息中增加：

```text
工具调用：深测通过，适合 Agent / Codex 类工具链。
```

如果基础 `FunctionCalling` 通过但 `ToolCallDeep` 未通过：

```text
工具调用：基础协议可用，但参数精度或多步链路存在风险。
```

---

## 6. 探针 3：ReasonMath 一致性

### 6.1 目标

确认同一接口在固定答案任务上输出稳定、可解析、低漂移。它不是网络连通性测试，而是「模型路由和语义稳定性」测试。

### 6.2 场景设计

| 场景 | 名称 | 测点 | 期望答案 |
| --- | --- | --- | --- |
| `RM-CONS-01` | 账单拆分 | 税费、服务费、四舍五入 | 每人 `34.50` |
| `RM-CONS-02` | 单位换算链 | 英里、公里、分钟、速度 | `96.56 km/h` |
| `RM-CONS-03` | 排程冲突 | 时间区间重叠 | `14:30-15:00` 冲突 |
| `RM-CONS-04` | 陷阱题 | 抑制直觉错误 | 正确解释并给出目标答案 |

第一期建议接入 `RM-CONS-01` 和 `RM-CONS-03`。它们更贴近日常用户任务，答案容易 deterministic 判定。

### 6.3 输出契约

统一要求：

```text
Return exactly two lines:
ANSWER: <final answer>
CHECKS: <comma-separated short checkpoints>
```

示例：

```text
ANSWER: 34.50
CHECKS: subtotal 120.00,tax 9.60,tip 18.00,total 138.00,split 4
```

### 6.4 请求模板：账单拆分

```json
{
  "model": "<model>",
  "max_tokens": 180,
  "temperature": 0,
  "messages": [
    {
      "role": "system",
      "content": "You are a reasoning consistency probe. Return exactly two lines: ANSWER and CHECKS. Do not use markdown."
    },
    {
      "role": "user",
      "content": "A meal subtotal is 120.00 CNY. Tax is 8% of subtotal. Tip is 15% of subtotal before tax. Four people split the final total equally. What should each person pay? Round to 2 decimals."
    }
  ]
}
```

### 6.5 判定规则

新增 `ReasonMathProbeEvaluator`：

```csharp
public static SemanticProbeEvaluation EvaluateReasonMathConsistency(
    string scenarioId,
    string? rawPreview)
```

判定轴：

- `AnswerAccuracy`：`ANSWER` 是否等于 canonical answer。
- `TraceConsistency`：`CHECKS` 是否包含必要中间量。
- `OutputDiscipline`：是否严格两行，无 Markdown，无额外解释。

失败归因：

| 失败类型 | FailureKind | 提示 |
| --- | --- | --- |
| 答案错误 | `SemanticMismatch` | 模型推理或路由不稳定 |
| 中间量矛盾 | `SemanticMismatch` | 输出看似正确但推理链不可靠 |
| 输出格式不符合 | `ProtocolMismatch` | 当前模型难以遵守固定输出契约 |

### 6.6 稳定性巡检联动

`ReasonMathConsistency` 不建议每轮都跑。推荐策略：

- 稳定性巡检开启「语义抽样」时，每 3 轮执行 1 次。
- 奇数抽样跑 `InstructionFollowing` 或 `DataExtraction`。
- 偶数抽样跑 `StructOutputEdge` 或 `ReasonMathConsistency`。
- 失败时在趋势图中标记「语义异常」。

---

## 7. 探针 4：BugFind 代码块纪律

### 7.1 目标

确认模型在代码修复场景中能够稳定输出可识别代码块，并遵守「只返回指定代码块」的边界。这个探针直接服务大模型对话窗口的代码块识别和真实开发体验。

### 7.2 场景设计

| 场景 | 名称 | 测点 | 通过条件 |
| --- | --- | --- | --- |
| `CB-DISC-01` | Python 小修复 | 语言标签、单代码块、最小修复 | 只输出一个 `python` 代码块，修复 off-by-one |
| `CB-DISC-02` | JavaScript 异步缺失 | 不夹解释，保留函数名 | 只输出一个 `javascript` 代码块，加入 `await` |
| `CB-DISC-03` | 无 bug 陷阱 | 不强行改代码 | 输出固定 `no_bug` 契约 |

第一期建议接入 `CB-DISC-01` 和 `CB-DISC-03`。前者测代码块识别，后者测是否乱修。

### 7.3 输出契约

修复类：

```text
Return exactly one fenced code block. The language tag must be python. Do not add explanation before or after the code block.
```

无 bug 类：

```text
Return exactly:
<solution verdict="no_bug"></solution>
```

### 7.4 请求模板：Python 小修复

```json
{
  "model": "<model>",
  "max_tokens": 260,
  "temperature": 0,
  "messages": [
    {
      "role": "system",
      "content": "You are a code block discipline probe. Follow the output contract exactly."
    },
    {
      "role": "user",
      "content": "Fix this Python function. Return exactly one fenced python code block and no explanation.\n\n```python\ndef sum_list(values):\n    total = 0\n    for i in range(len(values) + 1):\n        total += values[i]\n    return total\n```"
    }
  ]
}
```

### 7.5 判定规则

新增 `CodeBlockDisciplineEvaluator`：

```csharp
public static SemanticProbeEvaluation EvaluateCodeBlockDiscipline(
    string scenarioId,
    string? rawPreview)
```

判定轴：

- `BlockShape`：是否只有一个 fenced code block。
- `LanguageTag`：语言标签是否精确。
- `PatchQuality`：是否包含目标修复，例如 `range(len(values))`。
- `NoExtraText`：代码块前后是否没有解释文字。
- `TrapDiscipline`：无 bug 场景是否输出 `no_bug` 契约。

失败归因：

| 失败类型 | FailureKind | 提示 |
| --- | --- | --- |
| 没有代码块 | `SemanticMismatch` | 大模型对话代码块识别风险高 |
| 多个代码块或夹解释 | `SemanticMismatch` | 输出纪律不足 |
| 修复不正确 | `SemanticMismatch` | 模型代码修复能力不足 |
| 无 bug 场景强行改代码 | `SemanticMismatch` | 模型容易幻觉 bug |

### 7.6 大模型对话联动

如果 `CodeBlockDiscipline` 通过，可以在模型能力摘要中显示：

```text
代码块：可识别，适合对话页复制和折叠展示。
```

如果不通过：

```text
代码块：输出边界不稳定，建议在对话页开启更严格的代码块解析提示。
```

---

## 8. 核心代码施工范围

### 8.1 新增或修改文件

| 文件 | 操作 | 责任 |
| --- | --- | --- |
| `RelayBench.Core/Models/ProxyProbeScenarioKind.cs` | 修改 | 新增 4 个场景枚举 |
| `RelayBench.Core/Models/ProxyProbeScenarioResult.cs` | 修改 | 挂载可选 `ProxyProbeTrace` |
| `RelayBench.Core/Models/ProxyProbeTrace.cs` | 创建 | 保存输入、输出、判定证据 |
| `RelayBench.Core/Services/ProbeTraceRedactor.cs` | 创建 | 脱敏请求、响应和 Trace |
| `RelayBench.Core/Services/SemanticProbeEvaluator.cs` | 修改 | 增加 `StructOutput`、`ReasonMath`、`CodeBlock` 评估 |
| `RelayBench.Core/Services/ToolCallProbeEvaluator.cs` | 创建 | 评估工具调用深测 |
| `RelayBench.Core/Services/ProxyDiagnosticsService.Probes.Payloads.cs` | 修改 | 新增 4 类 payload builder |
| `RelayBench.Core/Services/ProxyDiagnosticsService.Advanced.Protocol.cs` | 修改 | 接入新增深测探针 |
| `RelayBench.Core/Services/ProxyDiagnosticsService.Evaluation.cs` | 修改 | 更新判定、推荐语、失败优先级 |
| `RelayBench.Core/Services/ProxyDiagnosticsService.Stability.cs` | 修改 | 语义抽样加入新探针 |
| `RelayBench.Core/Models/ProxyStabilityResult.cs` | 修改 | 增加新增探针统计 |
| `RelayBench.App/ViewModels/MainWindowViewModel.ProxyAdvanced.cs` | 修改 | 新增开关与配置摘要 |
| `RelayBench.App/ViewModels/MainWindowViewModel.ProxyTrends.ChartBuilders.cs` | 修改 | 图表行显示新增探针 |
| `RelayBench.App/ViewModels/MainWindowViewModel.ProxyChartView.cs` | 修改 | 支持选中图表行详情 |
| `RelayBench.App/Views/Pages/SingleStationPage.xaml` | 修改 | 深测配置和行内详情按钮 |
| `RelayBench.App/MainWindow.xaml` | 修改 | 新增统一 Probe Trace 弹窗 |
| `RelayBench.App/ViewModels/MainWindowViewModel.Reporting.Sections.cs` | 修改 | 报告和导出包含 Trace |
| `RelayBench.Core.Tests/Program.cs` | 修改 | 增加 evaluator 单元测试 |

### 8.2 枚举新增

```csharp
public enum ProxyProbeScenarioKind
{
    ...
    InstructionFollowing,
    DataExtraction,
    StructuredOutputEdge,
    ToolCallDeep,
    ReasonMathConsistency,
    CodeBlockDiscipline
}
```

### 8.3 Payload builder 新增方法

```csharp
private static string BuildStructuredOutputEdgePayload(string model, string scenarioId);

private static string BuildToolCallDeepPayload(string model, string scenarioId);

private static string BuildReasonMathConsistencyPayload(string model, string scenarioId);

private static string BuildCodeBlockDisciplinePayload(string model, string scenarioId);
```

### 8.4 执行方法新增

在 `ProxyDiagnosticsService.Advanced.Protocol.cs` 中新增：

```csharp
private async Task<ProxyProbeScenarioResult> ProbeStructuredOutputEdgeAsync(
    HttpClient client,
    string model,
    ProxyEndpointSettings settings,
    CancellationToken cancellationToken);

private async Task<ProxyProbeScenarioResult> ProbeToolCallDeepAsync(
    HttpClient client,
    string model,
    ProxyEndpointSettings settings,
    CancellationToken cancellationToken);

private async Task<ProxyProbeScenarioResult> ProbeReasonMathConsistencyAsync(
    HttpClient client,
    string model,
    ProxyEndpointSettings settings,
    CancellationToken cancellationToken);

private async Task<ProxyProbeScenarioResult> ProbeCodeBlockDisciplineAsync(
    HttpClient client,
    string model,
    ProxyEndpointSettings settings,
    CancellationToken cancellationToken);
```

### 8.5 综合评分权重

新增探针不应该压过基础连通性。建议综合深测权重：

| 分组 | 权重 |
| --- | ---: |
| 基础 5 项 | 45% |
| 协议兼容增强项 | 25% |
| 语义稳定性基础项 | 12% |
| 新增 4 类扩展探针 | 18% |

新增 4 类内部权重：

| 探针 | 权重 |
| --- | ---: |
| `StructOutputEdge` | 5% |
| `ToolCallDeep` | 6% |
| `ReasonMathConsistency` | 3% |
| `CodeBlockDiscipline` | 4% |

---

## 9. UI 施工范围

### 9.1 单站测试页面

文件：`RelayBench.App/Views/Pages/SingleStationPage.xaml`

修改点：

- 深测配置抽屉增加 4 个开关卡片。
- 每张开关卡片包含名称、短标、说明、预计额外请求数。
- 行内图表的每一行右侧增加详情按钮。
- 详情按钮禁用条件：当前行未执行且无 Trace。

### 9.2 图表弹窗

文件：`RelayBench.App/MainWindow.xaml`

新增统一弹窗区域：

```text
ProbeTraceOverlay
├─ Header：探针名、状态、关闭按钮
├─ Summary Strip：模型、协议、状态码、耗时
├─ Tab / Segmented：概览、输入、输出、判定、网络
├─ Body：等宽文本 + 结构化判定列表
└─ Footer：复制输入、复制输出、复制 Trace、关闭
```

样式要求：

- 输入和输出区域使用等宽字体。
- JSON 内容尽量格式化缩进。
- 大响应体默认折叠为前 20 KB，提供「复制完整」。
- 失败项用红色左边线，不用整块红底。
- 通过项用绿色小圆点和文本，不使用大面积绿色背景。

### 9.3 批量深测页面

文件：`RelayBench.App/Views/Pages/BatchComparisonPage.xaml`

修改点：

- 深测徽标 `SO`、`TC`、`RM`、`CB` 可点击。
- 点击徽标打开该入口对应探针的 Trace 弹窗。
- 排名表增加「语义扩展」列，显示 `4/4`、`3/4` 等紧凑结果。

### 9.4 历史报告页面

文件：`RelayBench.App/Views/Pages/HistoryReportsPage.xaml`

修改点：

- 报告详情增加「深测证据」区域。
- 每个探针结果显示状态、失败原因、查看 Trace。
- 导出报告时写入 `raw/probe-traces.json`。

---

## 10. 交互细节

### 10.1 详情按钮

- 鼠标悬停显示 `查看输入输出与判定依据`。
- 图标按钮尺寸 28 x 28。
- 行 hover 时按钮保持可见，不依赖 hover 才出现。
- 键盘 Tab 可聚焦，Enter 打开弹窗。

### 10.2 弹窗关闭

- 点击右上角关闭。
- 按 `Esc` 关闭。
- 点击遮罩关闭。
- 点击弹窗内容区不关闭。

### 10.3 复制行为

- `复制输入` 只复制脱敏请求。
- `复制输出` 复制原始响应 body 或提取输出。
- `复制完整 Trace` 复制脱敏后的 JSON。
- 复制成功用轻量 Toast，不弹确认框。

### 10.4 长文本处理

- 文本区域支持水平滚动。
- 默认使用等宽字体 `Cascadia Code`，不存在时回退 `Consolas`。
- JSON 格式化失败时展示原文，并在顶部提示「响应不是合法 JSON」。

---

## 11. 任务拆分

### 任务 1：Trace 基础设施

**文件：**
- 创建：`RelayBench.Core/Models/ProxyProbeTrace.cs`
- 创建：`RelayBench.Core/Services/ProbeTraceRedactor.cs`
- 修改：`RelayBench.Core/Models/ProxyProbeScenarioResult.cs`
- 测试：`RelayBench.Core.Tests/Program.cs`

- [ ] 编写 `ProbeTraceRedactor` 测试，覆盖 header、URL query、JSON body 脱敏。
- [ ] 创建 `ProxyProbeTrace` 和 `ProxyProbeEvaluationCheck`。
- [ ] 在 `ProxyProbeScenarioResult` 增加可空 `Trace` 字段。
- [ ] 运行 `dotnet run --project RelayBench.Core.Tests\RelayBench.Core.Tests.csproj`。

### 任务 2：StructOutput 加强版

**文件：**
- 修改：`RelayBench.Core/Models/ProxyProbeScenarioKind.cs`
- 修改：`RelayBench.Core/Services/SemanticProbeEvaluator.cs`
- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.Probes.Payloads.cs`
- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.Advanced.Protocol.cs`
- 测试：`RelayBench.Core.Tests/Program.cs`

- [ ] 新增 `StructuredOutputEdge` 枚举。
- [ ] 增加 JSON 边界值和 CSV 转义 payload builder。
- [ ] 增加 `EvaluateStructuredOutputEdge`。
- [ ] 接入深测执行流程并生成 Trace。
- [ ] 增加成功、Markdown 包裹、字段类型错误、CSV 转义错误测试。

### 任务 3：ToolCall 深测

**文件：**
- 创建：`RelayBench.Core/Services/ToolCallProbeEvaluator.cs`
- 修改：`RelayBench.Core/Models/ProxyProbeScenarioKind.cs`
- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.Probes.Payloads.cs`
- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.Advanced.Protocol.cs`
- 测试：`RelayBench.Core.Tests/Program.cs`

- [ ] 新增 `ToolCallDeep` 枚举。
- [ ] 定义固定工具池。
- [ ] 实现工具选择和参数精度两个第一期场景。
- [ ] 解析 `tool_calls`，兼容常见 OpenAI-compatible 响应形态。
- [ ] 对「文本模拟工具调用」判为 `ProtocolMismatch`。
- [ ] 增加成功、无 tool_calls、工具名错误、参数类型错误测试。

### 任务 4：ReasonMath 一致性

**文件：**
- 修改：`RelayBench.Core/Models/ProxyProbeScenarioKind.cs`
- 修改：`RelayBench.Core/Services/SemanticProbeEvaluator.cs`
- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.Probes.Payloads.cs`
- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.Advanced.Protocol.cs`
- 测试：`RelayBench.Core.Tests/Program.cs`

- [ ] 新增 `ReasonMathConsistency` 枚举。
- [ ] 实现账单拆分和排程冲突两个场景。
- [ ] 严格解析 `ANSWER:` 和 `CHECKS:`。
- [ ] 将答案错误、中间量缺失、额外解释分开归因。
- [ ] 接入稳定性语义抽样。

### 任务 5：BugFind 代码块纪律

**文件：**
- 修改：`RelayBench.Core/Models/ProxyProbeScenarioKind.cs`
- 修改：`RelayBench.Core/Services/SemanticProbeEvaluator.cs`
- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.Probes.Payloads.cs`
- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.Advanced.Protocol.cs`
- 测试：`RelayBench.Core.Tests/Program.cs`

- [ ] 新增 `CodeBlockDiscipline` 枚举。
- [ ] 实现 Python 小修复和无 bug 陷阱两个场景。
- [ ] 复用或扩展 `ChatMarkdownBlockParser` 的代码块识别思路。
- [ ] 校验语言标签、单代码块、无额外解释和关键修复点。
- [ ] 增加无代码块、多代码块、错误语言标签、无 bug 误修测试。

### 任务 6：评分、摘要和报告

**文件：**
- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.Evaluation.cs`
- 修改：`RelayBench.Core/Services/ProxyDiagnosticsService.Stability.cs`
- 修改：`RelayBench.Core/Models/ProxyStabilityResult.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.Results.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.Reporting.Sections.cs`

- [ ] 更新 `IsAdvancedScenario`。
- [ ] 更新失败优先级。
- [ ] 更新综合推荐语。
- [ ] 稳定性巡检统计新增 4 类探针结果。
- [ ] 历史报告和导出文件包含 Trace。

### 任务 7：图表和详情弹窗

**文件：**
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ProxyTrends.ChartBuilders.cs`
- 修改：`RelayBench.App/ViewModels/ProxySingleCapabilityChartRowViewModel.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ProxyChartView.cs`
- 修改：`RelayBench.App/MainWindow.xaml`
- 修改：`RelayBench.App/Views/Pages/SingleStationPage.xaml`
- 修改：`RelayBench.App/Views/Pages/BatchComparisonPage.xaml`

- [ ] 图表行 ViewModel 暴露 `HasTrace`、`OpenTraceCommand`、`TraceStatusText`。
- [ ] 单站行内图表增加详情按钮。
- [ ] 弹窗图表增加详情按钮。
- [ ] 批量深测徽标增加详情入口。
- [ ] 新增 Probe Trace 弹窗。
- [ ] 复制按钮接入剪贴板和 Toast。

### 任务 8：配置持久化

**文件：**
- 修改：`RelayBench.App/Infrastructure/AppStateSnapshot.cs`
- 修改：`RelayBench.App/Infrastructure/AppStateStore.cs`
- 修改：`RelayBench.App/ViewModels/MainWindowViewModel.ProxyAdvanced.cs`

- [ ] 增加 4 个探针开关字段。
- [ ] 旧配置加载时使用推荐默认值。
- [ ] 深测配置摘要显示启用数量。
- [ ] 单站和批量深测共享同一组选项。

---

## 12. 测试计划

### 12.1 核心测试

运行：

```powershell
dotnet run --project RelayBench.Core.Tests\RelayBench.Core.Tests.csproj
```

覆盖：

- `StructOutputEdge`：JSON 成功、JSON 包裹 Markdown、类型错误、CSV 转义错误。
- `ToolCallDeep`：正确工具、错误工具、参数类型错误、文本模拟工具调用。
- `ReasonMathConsistency`：正确两行、答案错误、中间量缺失、额外解释。
- `CodeBlockDiscipline`：正确单代码块、多个代码块、错误语言标签、无 bug 误修。
- `ProbeTraceRedactor`：Authorization、API Key、URL token、图片 base64 脱敏。

### 12.2 构建验证

运行：

```powershell
dotnet build RelayBenchSuite.slnx
dotnet build RelayBenchSuite.slnx -c Release
git diff --check
```

预期：

- Debug 构建通过。
- Release 构建通过。
- `git diff --check` 不出现尾随空格错误。

### 12.3 人工验证

人工验证路径：

1. 打开单站测试。
2. 启用结构化边界、工具调用深测、代码块纪律。
3. 运行深度测试。
4. 打开图表。
5. 点击每个新增探针的详情按钮。
6. 检查输入、输出、判定、网络信息完整。
7. 检查 API Key 已脱敏。
8. 运行批量深测。
9. 点击 `SO`、`TC`、`RM`、`CB` 徽标查看详情。
10. 导出历史报告，确认包含 `raw/probe-traces.json`。

---

## 13. 验收标准

### 13.1 功能验收

- 新增 4 类探针可独立启用和关闭。
- 单站深测能执行新增探针并显示结果。
- 批量深测能聚合新增探针状态。
- 稳定性巡检能按抽样策略记录语义扩展结果。
- 历史报告能复盘新增探针结果。
- 图表详情弹窗能展示完整输入、输出、判定和网络信息。

### 13.2 质量验收

- 任意 Trace 不泄露 API Key。
- 任意新增探针失败时都有明确失败原因。
- 任意新增探针被跳过时都有可理解说明。
- 评估器测试覆盖成功、失败和格式异常。
- UI 详情按钮不挤压原有图表内容。
- 弹窗在窗口化和全屏状态下都能完整显示。

### 13.3 体验验收

- 用户看到一个失败分数后，可以点开详情判断「为什么没过」。
- 用户看到一个通过分数后，可以点开详情判断「到底测了什么」。
- 应用接入页面能利用 `ToolCallDeep` 和 `CodeBlockDiscipline` 提示 Agent 类软件兼容风险。
- 大模型对话页面能利用 `CodeBlockDiscipline` 结果解释代码块识别风险。

---

## 14. 推荐施工顺序

1. Trace 基础设施。
2. StructOutput 加强版。
3. ToolCall 深测。
4. 图表详情弹窗。
5. 批量深测和历史报告接入。
6. ReasonMath 一致性。
7. BugFind 代码块纪律。
8. 稳定性抽样和综合评分。
9. 全量构建与人工验收。

这个顺序的好处是：先建立证据链，再加入最贴近接口稳定性的两类探针，最后补模型质量和真实使用体验探针。每一步都有可运行、可展示、可回滚的成果。

---

## 15. 参考来源

- BenchLocal：https://github.com/stevibe/BenchLocal
- StructOutput-15：https://github.com/stevibe/StructOutput-15
- ToolCall-15：https://github.com/stevibe/ToolCall-15
- ReasonMath-15：https://github.com/stevibe/ReasonMath-15
- BugFind-15：https://github.com/stevibe/BugFind-15
