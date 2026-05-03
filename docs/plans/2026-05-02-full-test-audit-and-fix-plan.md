# RelayBench 全量测试准确性排查与修复施工文档

> 面向施工者：本文基于 2026-05-02 对 `https://192.168.2.137/` 的 5 个模型全量真实跑测结果编写。API Key 已脱敏，证据文件保存在 `.verify_build/audit-results/relaybench-full-audit-20260502-161948.json`。  
> 目标：修正当前测试体系里「路径误判、协议误判、解析误判、评分矛盾」的问题，让单站测试、高级测试、批量快速、批量深测、并发、长流等测试结果更接近真实接口可用性。

## 1. 测试范围与执行记录

### 1.1 测试入口

本次覆盖了现阶段项目内主要接口测试链路：

| 模块 | 覆盖内容 |
| --- | --- |
| 单站快速测试 | `/models`、Anthropic Messages、Chat Completions、流式 Chat、Responses、结构化输出 |
| 单站深度测试 | System Prompt、Function Calling、错误透传、流式完整性、多模态、缓存机制、指令遵循、数据抽取、结构化边界、ToolCall 深测、推理一致性、代码块纪律 |
| 单站稳定性 | 5 轮稳定性、语义稳定性、健康分 |
| 单站吞吐 | 独立吞吐 3 轮 |
| 单站并发 | 1 / 2 / 4 / 8 / 16 并发阶梯 |
| 单站长流 | 72 段长流稳定简测 |
| 批量快速 | 模拟批量快速链路，包含快速基础能力与吞吐 |
| 批量深测 | 模拟批量深测链路，覆盖单站深度测试同一组 Core 探针 |
| 高级测试 | `AdvancedTesting` 默认测试集，含基础兼容、Tool Calling、JSON、Reasoning、长上下文、并发、Embeddings、模型一致性 |

### 1.2 测试配置

| 字段 | 值 |
| --- | --- |
| Base URL | `https://192.168.2.137/` |
| API Key | `1**` |
| TLS | 忽略证书错误 |
| Timeout | 45 秒 |
| 最终结果 JSON | `.verify_build/audit-results/relaybench-full-audit-20260502-161948.json` |
| 旧对照 JSON | `.verify_build/audit-results/relaybench-full-audit-20260502-153028.json` |

### 1.3 模型列表

| 显示名 | 实际请求模型名 | 用户意图 |
| --- | --- | --- |
| `gpt-5.5` | `gpt-5.5` | OpenAI |
| `deepseek-v4-flash OpenAI` | `deepseek-v4-flash OpenAI` | OpenAI |
| `kimi-k2.6` | `kimi-k2.6` | Auto |
| `mimo-v2.5-pro claude` | `mimo-v2.5-pro claude` | Anthropic |
| `mimo-v2.5-pro OpenAI` | `mimo-v2.5-pro OpenAI` | OpenAI |

> 重要发现：模型名后缀不能被程序或测试工具擅自剥离。该中转站会把 `OpenAI` / `claude` 后缀作为路由信息的一部分。旧对照跑测中剥离后缀会导致 `unknown provider for model mimo-v2.5-pro`，属于测试工具误用，不应算接口本身失败。

## 2. 总体结论

### 2.1 当前测试项是否准确

整体判断：**单站与批量的基础测试大体可靠，高级测试当前存在 P0 级路径误判；深度测试中有 4 类探针需要修正，否则会把「协议转换问题」或「输出格式偏差」误判成接口不可用。**

| 结论 | 说明 |
| --- | --- |
| 单站快速测试 | 可用。它能正确识别基础能力，并且已通过统一协议探测选择 `responses` 或 `anthropic`。 |
| 批量快速测试 | 可用。结果与单站快速高度一致，说明批量快速复用 Core 链路基本正确。 |
| 单站深度测试 | 部分可靠。能发现真实兼容问题，但 Function Calling、Responses 文本提取、ReasonMath、长流顺序检查存在误判风险。 |
| 批量深测 | 部分可靠。与单站深测结果一致，问题根因在 Core 探针，不在批量 UI。 |
| 高级测试 | 当前不可靠。用户填根地址时会请求 `/models`、`/chat/completions`，不会自动补 `/v1`，导致所有模型根地址测试几乎全失败。 |
| 并发测试 | 需要修正评分口径。存在「稳定并发上限 16」同时摘要显示「高风险档 4 / 2」的冲突。 |
| 长流测试 | 需要拆分。当前把「SSE 是否完整」和「模型是否严格输出编号模板」混在一起。 |

### 2.2 5 个模型结果摘要

| 模型 | 协议探测 | 单站快速 | 单站深测 | 稳定性 | 高级测试（根地址） | 高级测试（补 `/v1`） |
| --- | --- | --- | --- | --- | --- | --- |
| `gpt-5.5` | Responses + Anthropic 通，优先 Responses | 适合长期挂载 | 基础可用，高级待复核 | 92，很稳，5/5 | 0 通过，26 失败，10.8 分 | 22 通过，2 部分，4 失败，92.5 分 |
| `deepseek-v4-flash OpenAI` | Responses + Anthropic 通，优先 Responses | 适合长期挂载 | 基础可用，高级待复核 | 78，稳定，4/5 | 0 通过，26 失败，10.8 分 | 20 通过，2 部分，6 失败，87.5 分 |
| `kimi-k2.6` | Responses + Anthropic 通，优先 Responses | 适合 Anthropic 接入 | Anthropic 可用，高级待复核 | 90，很稳，5/5 | 0 通过，26 失败，10.8 分 | 22 通过，2 部分，4 失败，92.5 分 |
| `mimo-v2.5-pro claude` | Responses + Anthropic 通，优先 Responses | 适合 Anthropic 接入 | Anthropic 可用，高级待复核 | 68，一般，5/5 | 0 通过，26 失败，10.8 分 | 18 通过，4 部分，6 失败，77.2 分 |
| `mimo-v2.5-pro OpenAI` | Anthropic 通，Responses 不通，优先 Anthropic | 适合 Anthropic 接入 | 适合 Anthropic 接入 | 10，不稳定，1/5 | 0 通过，26 失败，10.8 分 | 21 通过，1 部分，6 失败，88.4 分 |

> 解释：协议探测中 `Chat=false` 不等于 Chat 不支持。当前策略是「Messages 和 Responses 有一个成功，就不继续探 Chat」，所以 `Chat=false` 多数表示「未探测」或「未作为首选」，不应在 UI 上直接显示为“不支持 Chat”。

## 3. 关键问题清单

### P0-1：高级测试没有自动补 `/v1`

**现象：**

高级测试用根地址 `https://192.168.2.137/` 时，5 个模型全部为 `0 通过 / 2 部分 / 26 失败 / 10.8 分`。同样模型把地址改成 `https://192.168.2.137/v1` 后，分数恢复到 77.2 到 92.5。

**根因：**

`RelayBench.Core/AdvancedTesting/Clients/HttpModelClient.cs:166-170` 直接拼接：

```csharp
var baseUrl = _endpoint.BaseUrl.Trim().TrimEnd('/');
var path = relativePath.Trim().TrimStart('/');
return $"{baseUrl}/{path}";
```

而单站测试在 `RelayBench.Core/Services/ProxyDiagnosticsService.Probes.Payloads.cs:45-54` 会根据 base path 自动补 `/v1`。

**影响：**

高级测试会把「用户填了根地址」误判成「接口能力全失败」，这是最严重误判。

### P0-2：高级测试固定走 OpenAI Chat，不复用三协议探测

**现象：**

高级测试中的 `chat_non_stream`、Tool Calling、JSON、Reasoning、长上下文等测试都写死请求 `chat/completions`，但单站链路已经支持 `responses`、`anthropic`、`chat` 三种协议。

**根因：**

高级测试 `IModelClient` 只有 `GetAsync`、`PostJsonAsync`、`PostJsonStreamAsync`，没有「当前模型首选 wire_api」概念。测试用例直接传 `chat/completions`。

**影响：**

对 `mimo`、`kimi`、Responses 优先模型会出现两类误判：

- 协议本身通，但高级测试用错路径。
- 高级测试结果和单站 / 批量结果冲突。

### P0-3：模型 ID 不能被剥离后缀

**现象：**

旧对照跑测把 `mimo-v2.5-pro claude` 和 `mimo-v2.5-pro OpenAI` 都请求成 `mimo-v2.5-pro` 后，返回 `unknown provider for model mimo-v2.5-pro`。完整模型名重跑后，两者都能进入有效协议探测。

**根因：**

项目里后续如果存在「从模型名里推断协议并裁剪后缀」的逻辑，会破坏中转站路由。模型名应按用户选择原样发送；协议意图应单独存储。

**影响：**

应用接入、单站测试、批量测试、高级测试、大模型对话都会受到影响。尤其用户选到 `xxx claude` 或 `xxx OpenAI` 时，不能把 suffix 当 UI 标签丢掉。

### P1-1：Function Calling 探针对 Responses / Anthropic 转换不够稳

**现象：**

- `gpt-5.5`：首轮 ToolCall 可能成功，但工具结果回填失败，返回 `No tool call found for function call output with call_id ...`。
- `deepseek-v4-flash OpenAI`：`tool_choice` 结构被服务端拒绝。
- `kimi-k2.6`：`tool_choice` 类型校验失败。
- `mimo-v2.5-pro claude`：返回类 Claude Code 文本工具调用格式，程序判最终回答异常。

**判断：**

这不应全部算「接口不支持 Tool Calling」。应拆成：

1. `tools` 参数是否被接受；
2. `tool_choice=auto` 是否被接受；
3. 指定工具选择的格式是否被接受；
4. 首轮 tool call 是否可解析；
5. 工具结果回填是否使用了该协议正确格式；
6. 最终回答是否可消费。

### P1-2：Responses / Anthropic 文本解析不完整

**现象：**

`kimi-k2.6` 与部分 `deepseek-v4-flash OpenAI` 深测返回 HTTP 200，响应体是标准 `object=response`，但多个探针显示「没有解析到可读内容」。

**判断：**

这更像项目的 `ParseResponsesPreview` / 文本提取逻辑没有覆盖该中转站的 Responses 返回结构，不应直接判接口失败。

### P1-3：ReasonMath 过度依赖输出格式

**现象：**

`gpt-5.5` 输出：

```text
ANSWER 34.50 CNY
CHECKS tax=9.60, tip=8.40, total=138.00, split=34.50
```

答案正确，但因不是严格 `ANSWER:` / `CHECKS:` 两行格式而失败。

**判断：**

这个测试应保持答案和关键检查点严格，但格式解析应更宽松。否则它测到的是「提示词服从格式」，不是「推理一致性」。

### P1-4：并发摘要存在自相矛盾

**现象：**

`gpt-5.5` 摘要显示「稳定并发上限 16；实用并发上限 16；高风险档 4」。  
`deepseek-v4-flash OpenAI` 摘要显示「稳定并发上限 16；高风险档 --」，但旧对照出现过「高风险档 2」。

**判断：**

稳定上限和高风险档不能互相打架。如果某档已高风险，则稳定上限不能越过它，或者高风险必须降级为「性能风险提示」。

### P1-5：长流测试把协议完整性和模型模板遵循混在一起

**现象：**

- `deepseek-v4-flash OpenAI`：实际 79/72 段，DONE 正常，顺序失败。
- `kimi-k2.6`：实际 84/72 段，DONE 正常，顺序失败。
- `mimo-v2.5-pro claude`：实际 74/72 段，DONE 正常，顺序失败。

**判断：**

这些不是典型断流。它们更像模型额外输出、重复编号或编号模板没严格遵循。应拆成：

- SSE 协议层：连接、data 行、delta、DONE、finish_reason、chunk 间隔。
- 内容层：编号段数量、顺序、重复、遗漏、模板遵循。

## 4. 施工目标

1. 高级测试与单站测试使用同一套 URL 归一化，用户填根地址也能正确请求 `/v1/...`。
2. 高级测试接入统一协议探测，按 `responses`、`anthropic`、`chat` 选择请求路径和 payload。
3. 项目全局保留完整模型 ID，不根据显示后缀裁剪模型名。
4. 深度测试细化 Tool Calling 判定，不把「某一步失败」笼统写成「不支持」。
5. 增强 Responses / Anthropic 文本提取，避免 HTTP 200 可读内容被判空。
6. 修正 ReasonMath：格式宽松、答案严格、检查点严格。
7. 修正并发稳定上限、高风险档、实用上限的计算关系。
8. 长流测试拆分协议分和内容分，让用户知道失败到底是 SSE 断了，还是模型没按模板输出。
9. 批量快速、批量深测、高级测试、单站测试共享同一套判定语义，减少页面间结果冲突。

## 5. 代码施工方案

### 5.1 P0：统一 API 路径归一化

**新增文件：**

- `RelayBench.Core/Services/EndpointPathBuilder.cs`

**修改文件：**

- `RelayBench.Core/AdvancedTesting/Clients/HttpModelClient.cs`
- `RelayBench.Core/Services/ProxyDiagnosticsService.Probes.Payloads.cs`
- `RelayBench.Core/Services/ChatConversationService.cs`

**实现要点：**

把单站和聊天里重复的 `BuildApiPath` 抽成公共工具。高级测试请求 `models`、`chat/completions`、`responses`、`messages`、`embeddings` 时都通过该工具拼路径。

```csharp
namespace RelayBench.Core.Services;

public static class EndpointPathBuilder
{
    public static string BuildOpenAiCompatiblePath(string baseUrl, string endpoint)
    {
        var uri = new Uri(baseUrl.Trim(), UriKind.Absolute);
        return BuildOpenAiCompatiblePath(uri, endpoint);
    }

    public static string BuildOpenAiCompatiblePath(Uri baseUri, string endpoint)
    {
        var normalizedPath = baseUri.AbsolutePath.TrimEnd('/');
        var normalizedEndpoint = endpoint.Trim().TrimStart('/');
        return normalizedPath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? normalizedEndpoint
            : $"v1/{normalizedEndpoint}";
    }

    public static string CombineAbsolute(string baseUrl, string endpoint)
    {
        var baseUri = new Uri(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute);
        var path = BuildOpenAiCompatiblePath(baseUri, endpoint);
        return new Uri(baseUri, path).ToString();
    }
}
```

**HttpModelClient 改法：**

```csharp
private string BuildUrl(string relativePath)
    => EndpointPathBuilder.CombineAbsolute(_endpoint.BaseUrl, relativePath);
```

**验收标准：**

- 高级测试使用 `https://192.168.2.137/` 和 `https://192.168.2.137/v1` 两种输入时，`GET /models` 实际请求都应落到 `/v1/models`。
- 5 个模型高级测试根地址分数不再全部是 `10.8`。
- 增加单元测试覆盖根地址、`/v1` 地址、带尾斜杠地址。

### 5.2 P0：高级测试接入统一协议探测

**修改文件：**

- `RelayBench.Core/AdvancedTesting/Models/AdvancedTestModels.cs`
- `RelayBench.Core/AdvancedTesting/IModelClient.cs`
- `RelayBench.Core/AdvancedTesting/Clients/HttpModelClient.cs`
- `RelayBench.Core/AdvancedTesting/Runners/AdvancedTestRunner.cs`
- `RelayBench.Core/AdvancedTesting/TestCases/*.cs`
- `RelayBench.App/ViewModels/AdvancedTesting/AdvancedTestLabViewModel.cs`

**数据结构增加：**

```csharp
public sealed record AdvancedEndpoint(
    string BaseUrl,
    string ApiKey,
    string Model,
    bool IgnoreTlsErrors,
    int TimeoutSeconds,
    string? PreferredWireApi = null,
    string? DisplayModelName = null,
    string? ProtocolHint = null);
```

**新增运行上下文：**

```csharp
public sealed record AdvancedWireApiState(
    bool ChatSupported,
    bool ResponsesSupported,
    bool AnthropicSupported,
    string PreferredWireApi,
    string ProbeSummary);
```

**Runner 流程：**

1. 运行前调用 `ProxyDiagnosticsService.ProbeProtocolAsync`。
2. 将 `PreferredWireApi` 写入 `AdvancedTestRunContext`。
3. 测试项根据 `context.WireApi.PreferredWireApi` 构造 payload。
4. 如果某测试只支持 Chat，例如 Embeddings 以外的部分 OpenAI 兼容测试，UI 要显示「该测试按 Chat 兼容路径验证」，而不是把 Anthropic 路径失败写成模型失败。

**验收标准：**

- `mimo-v2.5-pro claude` 高级测试不再固定走 `/v1/chat/completions`。
- 高级测试顶部显示「协议探测：Responses / Anthropic / Chat」和「本轮使用协议」。
- `Chat=false` 且 `Responses=true` 或 `Anthropic=true` 时，UI 文案显示「Chat 未作为首选探测」而不是「Chat 不支持」。

### 5.3 P0：保留完整模型 ID

**修改文件：**

- `RelayBench.App/ViewModels/MainWindowViewModel.ProxyModelPicker.cs`
- `RelayBench.App/ViewModels/MainWindowViewModel.ModelChat.cs`
- `RelayBench.App/ViewModels/AdvancedTesting/AdvancedTestLabViewModel.cs`
- `RelayBench.App/ViewModels/ProxyBatchEditorItemViewModel.cs`
- `RelayBench.Core/Services/ClientAppConfigApplyService.cs`
- `RelayBench.Core/Services/CodexFamilyConfigApplyService.cs`

**规则：**

| 字段 | 作用 |
| --- | --- |
| `DisplayName` | UI 显示，例如 `mimo-v2.5-pro claude` |
| `RequestModelId` | 原样发给接口，默认等于用户选择的完整模型名 |
| `ProtocolHint` | 用户或程序记录的协议倾向，例如 `Anthropic`、`OpenAI`、`Responses` |
| `DetectedWireApi` | 实测得到的协议，例如 `anthropic`、`responses`、`chat` |

**禁止：**

- 禁止通过 `Replace(" claude", "")`、`Replace(" OpenAI", "")` 得到请求模型名。
- 禁止用模型名称决定协议；协议必须通过实际请求探测决定。

**验收标准：**

- 选择 `mimo-v2.5-pro claude` 时，所有请求体中的 `model` 都是完整字符串。
- 应用到软件时，配置文件里写入的是完整模型 ID，协议字段另行写入。
- 历史接口记录也保存完整模型 ID。

### 5.4 P1：增强 Responses / Anthropic 文本提取

**修改文件：**

- `RelayBench.Core/Services/ProxyDiagnosticsService.Probes.Payloads.cs`
- `RelayBench.Core/Services/ProxyDiagnosticsService.Advanced.Protocol.cs`
- `RelayBench.Core/AdvancedTesting/TestCases/AdvancedTestCaseBase.cs`

**需要覆盖的响应形态：**

```json
{
  "object": "response",
  "output": [
    {
      "type": "message",
      "content": [
        { "type": "output_text", "text": "..." }
      ]
    }
  ]
}
```

```json
{
  "content": [
    { "type": "text", "text": "..." }
  ]
}
```

```json
{
  "choices": [
    {
      "message": { "content": "..." }
    }
  ]
}
```

**建议新增工具：**

```csharp
public static class ModelResponseTextExtractor
{
    public static string? TryExtractAssistantText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (TryExtractChatText(root, out var chatText))
        {
            return chatText;
        }

        if (TryExtractAnthropicText(root, out var anthropicText))
        {
            return anthropicText;
        }

        if (TryExtractResponsesText(root, out var responsesText))
        {
            return responsesText;
        }

        return null;
    }
}
```

**验收标准：**

- `kimi-k2.6` 深测不再大量出现「HTTP 200 但没有解析到可读内容」。
- `deepseek-v4-flash OpenAI` 的 Responses 结构化边界和 ReasonMath 至少能进入语义判定，而不是 ProtocolMismatch。

### 5.5 P1：Function Calling 分层判定

**修改文件：**

- `RelayBench.Core/Services/ProxyDiagnosticsService.Advanced.Protocol.cs`
- `RelayBench.Core/Services/ProxyProbePayloadFactory.cs`
- `RelayBench.Core/AdvancedTesting/TestCases/ToolCallingTestCases.cs`
- `RelayBench.Core/AdvancedTesting/TestCases/SecondBatchTestCases.cs`

**拆分测试结果：**

| 子项 | 通过标准 | 失败文案 |
| --- | --- | --- |
| `ToolsAccepted` | 请求携带 `tools` 返回 2xx 或明确工具调用 | 接口拒绝 `tools` 参数 |
| `ToolChoiceAuto` | `tool_choice=auto` 被接受 | 不支持自动工具选择 |
| `ToolChoiceForced` | 指定工具格式被接受 | 指定工具选择格式不兼容 |
| `ToolCallJson` | 返回 tool_calls 且 arguments 可解析 | 返回工具调用结构不兼容 |
| `ToolResultRoundtrip` | 工具结果回填后可继续对话 | 工具结果回填格式不兼容 |
| `StreamingToolCall` | 流式 tool_calls 可拼完整 | 流式工具参数缺片或顺序错误 |

**协议差异：**

- Chat Completions：`messages + tools + tool_choice`。
- Responses：首轮和 follow-up 需要保留 `call_id`，工具结果应使用 Responses 正确 item 结构。
- Anthropic Messages：工具定义、`tool_use`、`tool_result` 使用 Anthropic 消息块格式。

**验收标准：**

- `gpt-5.5` 的工具结果回填失败应显示为 `ToolResultRoundtrip` 失败，不应把整个 Function Calling 写成「不支持」。
- `deepseek-v4-flash OpenAI` 的 `tool_choice` 错误应显示为「指定工具选择格式不兼容」，基础 `tools` 是否支持另行判断。

### 5.6 P1：ReasonMath 宽松格式、严格语义

**修改文件：**

- `RelayBench.Core/Services/SemanticProbeEvaluator.cs`

**改法：**

保留答案和检查点严格性，放宽标签和换行格式。

```csharp
private static bool TryParseReasonMathContract(
    IReadOnlyList<string> lines,
    out string answer,
    out string checks)
{
    answer = string.Empty;
    checks = string.Empty;

    var text = string.Join('\n', lines);
    var answerMatch = Regex.Match(text, @"ANSWER\s*:?\s*(?<value>[^\n]+)", RegexOptions.IgnoreCase);
    var checksMatch = Regex.Match(text, @"CHECKS\s*:?\s*(?<value>[\s\S]+)$", RegexOptions.IgnoreCase);

    if (!answerMatch.Success || !checksMatch.Success)
    {
        return false;
    }

    answer = answerMatch.Groups["value"].Value.Trim();
    checks = checksMatch.Groups["value"].Value.Trim();
    return true;
}
```

**验收标准：**

- `ANSWER 34.50 CNY`、`ANSWER: 34.50`、`ANSWER\n34.50` 都能提取答案。
- 如果答案是 `34.20`，必须失败。
- 如果答案正确但缺少 `tax`、`tip`、`total` 等检查点，必须失败。

### 5.7 P1：并发稳定上限与高风险档修正

**修改文件：**

- `RelayBench.Core/Services/ProxyDiagnosticsService.Advanced.Concurrency.cs`
- `RelayBench.App/ViewModels/MainWindowViewModel.ProxyAdvanced.Concurrency.cs`
- `RelayBench.App/Services/ProxyConcurrencyChartRenderService.cs`

**规则：**

1. 稳定上限：成功率达标、超时可接受、5xx 可接受、P95 不爆炸。
2. 实用上限：允许少量 429 或轻微延迟升高。
3. 高风险档：成功率低于阈值、超时明显、P95 相对基线暴涨、服务端错误上升。
4. 如果 `HighRiskConcurrency <= StableConcurrencyLimit`，必须重新计算稳定上限，不能同时显示冲突结论。

**验收标准：**

- 摘要不再出现「稳定 16，高风险 4」。
- 图表上高风险档之后的柱子不应仍标为稳定。

### 5.8 P2：长流拆分为协议完整性与内容纪律

**修改文件：**

- `RelayBench.Core/Services/ProxyDiagnosticsService.Advanced.Streaming.cs`
- `RelayBench.Core/Models/ProxyStreamingStabilityResult.cs`
- `RelayBench.App/ViewModels/MainWindowViewModel.ProxyTrends.ChartBuilders.cs`
- `RelayBench.App/ViewModels/MainWindowViewModel.Results.cs`

**新增字段：**

```csharp
public sealed record ProxyStreamingStabilityBreakdown(
    bool TransportSucceeded,
    bool ReceivedDone,
    bool DeltaParseSucceeded,
    bool FinishReasonObserved,
    bool SequenceDisciplinePassed,
    int ExpectedSegmentCount,
    int ActualSegmentCount,
    int DuplicateSegmentCount,
    int MissingSegmentCount);
```

**文案示例：**

- 协议层通过：SSE 正常结束，DONE 正常，未观察到断流。
- 内容层待复核：模型输出 84/72 段，存在额外编号或重复编号，说明模板遵循不稳定。

**验收标准：**

- `kimi-k2.6`、`deepseek-v4-flash OpenAI`、`mimo-v2.5-pro claude` 不再简单显示「长流失败」，而是区分为「SSE 正常，编号纪律失败」。

## 6. UI 与文案施工

### 6.1 高级测试顶部配置区

高级测试页面顶部应显示：

- 当前 Base URL：归一化后路径提示，例如「实际请求：`/v1/...`」。
- 当前模型：完整模型 ID。
- 协议探测结果：Messages / Responses / Chat 三个 badge。
- 本轮首选协议：`responses` / `anthropic` / `chat`。
- 路径风险提示：如果用户输入根地址，显示「已自动补 `/v1`」。

### 6.2 测试项详情文案

每个失败项详情必须按以下结构展示：

```text
结论：部分通过 / 失败 / 待复核
测试层级：协议层 / 语义层 / 性能层 / 配置层
为什么失败：
- ...
如何判断：
- ...
建议操作：
- ...
原始 Trace：
- request / response / headers
```

### 6.3 批量页面一致性

批量快速、批量深测的行内结果要和单站使用同一套状态词：

| 状态 | 含义 |
| --- | --- |
| 通过 | 测试目标明确达标 |
| 部分通过 | 基础链路可用，但有成本、usage、错误透传、性能风险 |
| 待复核 | HTTP 成功但语义、协议变体或输出格式存在不确定性 |
| 不支持 | 目标能力明确被拒绝，例如 404、明确 unsupported |
| 异常 | 非预期错误、超时、5xx、解析器失败 |

## 7. 单元测试施工

### 7.1 新增测试文件

- `RelayBench.Core.Tests/EndpointPathBuilderTests.cs`
- `RelayBench.Core.Tests/AdvancedTesting/HttpModelClientPathTests.cs`
- `RelayBench.Core.Tests/AdvancedTesting/AdvancedWireApiSelectionTests.cs`
- `RelayBench.Core.Tests/ModelResponseTextExtractorTests.cs`
- `RelayBench.Core.Tests/SemanticProbeEvaluatorReasonMathTests.cs`
- `RelayBench.Core.Tests/FunctionCallingProtocolPayloadTests.cs`
- `RelayBench.Core.Tests/ConcurrencyLimitClassificationTests.cs`
- `RelayBench.Core.Tests/StreamingStabilityBreakdownTests.cs`

### 7.2 必测用例

| 测试 | 用例 |
| --- | --- |
| URL 归一化 | 根地址、`/v1`、尾斜杠、子路径 |
| 协议选择 | Responses 通、Anthropic 通、Chat 回退、全失败 |
| 完整模型 ID | `mimo-v2.5-pro claude` 不被裁剪 |
| Responses 提取 | `output[].content[].text`、`output_text`、嵌套 message |
| Anthropic 提取 | `content[].text`、tool_use 前后文本 |
| ReasonMath | 冒号、无冒号、换行标签、答案错误、检查点缺失 |
| Tool Calling | auto、forced、Responses follow-up、Anthropic tool_result |
| 并发分类 | 高风险档早于稳定上限时重新计算 |
| 长流拆分 | DONE 正常但编号多 / 少 / 重复 |

## 8. 开发阶段清单

### 阶段 1：路径与模型 ID 基线修复

**文件：**

- 新增 `RelayBench.Core/Services/EndpointPathBuilder.cs`
- 修改 `HttpModelClient.cs`
- 修改 `ProxyDiagnosticsService.Probes.Payloads.cs`
- 修改 `ChatConversationService.cs`
- 补测试 `EndpointPathBuilderTests.cs`

**验收：**

- 高级测试根地址和 `/v1` 地址结果一致。
- 完整模型 ID 保留到请求体。

### 阶段 2：高级测试三协议化

**文件：**

- 修改 `AdvancedTestModels.cs`
- 修改 `AdvancedTestRunner.cs`
- 修改 `IModelClient.cs`
- 修改所有 `AdvancedTesting/TestCases/*.cs`

**验收：**

- 高级测试可显示协议探测结果。
- `mimo-v2.5-pro claude` 不被固定到 Chat Completions。

### 阶段 3：文本提取与语义误判修复

**文件：**

- 新增 `ModelResponseTextExtractor.cs`
- 修改 `SemanticProbeEvaluator.cs`
- 修改 `ProxyDiagnosticsService.Advanced.Protocol.cs`
- 修改 `AdvancedTestCaseBase.cs`

**验收：**

- `kimi-k2.6` 的 200 响应能提取正文。
- ReasonMath 格式宽松但答案错误仍失败。

### 阶段 4：Function Calling 分层测试

**文件：**

- 修改 `ProxyProbePayloadFactory.cs`
- 修改 `ToolCallingTestCases.cs`
- 修改 `SecondBatchTestCases.cs`

**验收：**

- UI 能显示 Tool Calling 六个子项。
- `tool_choice` 格式不兼容不再覆盖 `tools` 基础能力结果。

### 阶段 5：并发与长流判定修正

**文件：**

- 修改 `ProxyDiagnosticsService.Advanced.Concurrency.cs`
- 修改 `ProxyDiagnosticsService.Advanced.Streaming.cs`
- 修改相关 result model 和 UI chart builder

**验收：**

- 不再出现稳定上限和高风险档互相矛盾。
- 长流结果分为协议层和内容层。

### 阶段 6：全量回归

**命令：**

```powershell
dotnet test RelayBenchSuite.slnx
dotnet build RelayBenchSuite.slnx
```

**人工回归：**

使用本次 5 个模型重跑：

- 单站快速
- 单站深测
- 稳定性
- 并发
- 长流
- 批量快速
- 批量深测
- 高级测试

**验收：**

- 高级测试根地址不再全灭。
- 单站与批量同一测试项结论一致。
- 高级测试与单站在协议层结论一致。
- 所有报告、详情弹窗、复制摘要默认脱敏。

## 9. 施工风险

| 风险 | 影响 | 处理 |
| --- | --- | --- |
| 三协议 payload 转换复杂 | 可能引入新误判 | 先做共享 payload factory，再逐项接入 |
| 中转站协议变体多 | 某些 200 响应仍提取失败 | 保留原始 Trace，新增 extractor 用例 |
| Function Calling 标准差异大 | 不同模型表现差异明显 | 分层评分，不做单一通过 / 失败 |
| 长流内容不稳定 | 模型输出多段或少段 | 拆分协议分和内容分 |
| 模型 ID 后缀兼具显示与路由 | 容易被 UI 清洗 | 数据模型分离 DisplayName / RequestModelId / ProtocolHint |

## 10. 最终验收标准

1. 用 `https://192.168.2.137/` 跑高级测试时，实际请求路径必须自动进入 `/v1/...`。
2. 5 个模型完整 ID 原样进入请求体。
3. 协议探测结论在单站、批量、高级测试中一致展示。
4. `Chat=false` 不再被 UI 误写成「Chat 不支持」，除非 Chat 确实被探测且失败。
5. `kimi-k2.6`、`deepseek-v4-flash OpenAI` 的 Responses 200 响应能提取文本并进入语义判定。
6. ReasonMath 对正确答案的格式变体不误判，对错误答案仍严格失败。
7. Function Calling 能显示子项结果，用户能看出是 `tool_choice`、`tool_call JSON` 还是工具结果回填失败。
8. 并发摘要无「稳定上限高于高风险档」的矛盾。
9. 长流详情能区分 SSE 协议正常与编号模板不稳定。
10. 单站快速、批量快速、单站深测、批量深测对同一 Core 探针给出一致结论。

