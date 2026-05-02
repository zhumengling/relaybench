namespace RelayBench.Core.AdvancedTesting.Models;

public static class AdvancedErrorCatalog
{
    public static AdvancedErrorDescriptor Describe(AdvancedErrorKind kind)
        => kind switch
        {
            AdvancedErrorKind.NetworkTimeout => new(kind, "请求超时", "HTTP 请求在超时时间内没有完成。", "中转站排队、上游响应慢、网络链路抖动或超时配置过低。", "提高超时时间后复测；如果仍频繁出现，降低并发或更换入口。", AdvancedRiskLevel.High),
            AdvancedErrorKind.DnsFailure => new(kind, "DNS 解析失败", "域名没有解析到可用地址。", "DNS 污染、解析器不可用、域名写错或本机网络异常。", "检查 Base URL、DNS 设置和本机代理分流。", AdvancedRiskLevel.High),
            AdvancedErrorKind.TlsFailure => new(kind, "TLS 握手失败", "HTTPS 握手或证书校验失败。", "证书过期、自签证书、中间人代理或 SNI 不匹配。", "确认站点证书；仅在可信内网环境下考虑忽略 TLS 错误。", AdvancedRiskLevel.High),
            AdvancedErrorKind.Unauthorized => new(kind, "鉴权失败", "服务端返回 401/403。", "API Key 错误、权限不足、模型不可用或账户被限制。", "重新确认 Key、模型权限和中转站账户状态。", AdvancedRiskLevel.High),
            AdvancedErrorKind.RateLimited => new(kind, "被限流", "服务端返回 429 或限流相关错误。", "并发过高、额度不足、站点限速或上游限流。", "降低并发；检查 Retry-After；必要时切换备用入口。", AdvancedRiskLevel.Medium),
            AdvancedErrorKind.InvalidRequest => new(kind, "请求不兼容", "服务端返回 400/422。", "协议参数、模型名、工具定义、JSON Schema 或路径不兼容。", "查看原始响应 message 字段，确认是参数问题还是协议能力缺失。", AdvancedRiskLevel.Medium),
            AdvancedErrorKind.ServerError => new(kind, "服务端异常", "服务端返回 5xx。", "中转站或上游服务临时故障。", "稍后重试；如果稳定复现，联系站点维护方。", AdvancedRiskLevel.High),
            AdvancedErrorKind.BadGateway => new(kind, "网关异常", "服务端返回 502/503/504/520/524。", "网关到上游失败、Cloudflare 超时或上游断流。", "复测并观察路由；高频出现时不建议用于长期挂载。", AdvancedRiskLevel.High),
            AdvancedErrorKind.StreamBroken => new(kind, "流式断开", "SSE 流没有完整结束或连接中途断开。", "代理缓冲、网关超时、上游断流或客户端读取失败。", "查看流式原始响应；优先复测 stream 能力。", AdvancedRiskLevel.High),
            AdvancedErrorKind.StreamMalformed => new(kind, "流式格式异常", "SSE data 行或 delta 结构无法解析。", "OpenAI 兼容格式偏离、chunk 拼接错误或返回了非 SSE 内容。", "不要直接接入 Agent 客户端；先确认 stream 协议兼容性。", AdvancedRiskLevel.High),
            AdvancedErrorKind.JsonMalformed => new(kind, "JSON 格式异常", "模型输出不是可解析 JSON。", "模型没有遵守结构化输出约束或中转站修改了内容。", "查看输出是否被 Markdown 包裹；必要时启用更强的 JSON Schema。", AdvancedRiskLevel.Medium),
            AdvancedErrorKind.ToolCallMalformed => new(kind, "Tool Calling 异常", "tool_calls 缺失、结构错误或 arguments 不是合法 JSON。", "中转站不支持 tools 参数、流式 tool_call 拼接错误或模型未按要求调用工具。", "Codex/Roo/Cline 类客户端依赖该能力，失败时不要直接作为主力入口。", AdvancedRiskLevel.High),
            AdvancedErrorKind.ReasoningProtocolIncompatible => new(kind, "Reasoning 协议不兼容", "reasoning 参数或 thinking 字段返回结构不符合常见 SDK。", "中转站对 Responses、DeepSeek/Qwen thinking 或 Anthropic thinking 适配不完整。", "按具体客户端选择支持的协议；不确定时关闭 reasoning 参数。", AdvancedRiskLevel.Medium),
            AdvancedErrorKind.ContextOverflow => new(kind, "上下文超限", "长上下文请求被拒绝或明显截断。", "模型实测上下文低于标称值或中转站设置了更小上限。", "按实测上限配置客户端 context window。", AdvancedRiskLevel.Medium),
            AdvancedErrorKind.UsageMissing => new(kind, "Usage 缺失", "响应缺少 token 用量字段。", "中转站未透传 usage 或上游接口不返回。", "成本透明度较低；如需计费核对，优先选择 usage 完整入口。", AdvancedRiskLevel.Low),
            AdvancedErrorKind.UsageSuspicious => new(kind, "Usage 可疑", "usage 数值和输出规模明显不匹配。", "用量被重写、估算或中转站统计异常。", "结合账单复核，不要单凭客户端 usage 判断成本。", AdvancedRiskLevel.Medium),
            AdvancedErrorKind.ModelMismatchSuspected => new(kind, "疑似模型不一致", "模型自报、能力指纹或输出风格存在异常。", "模型被别名映射、降级、偷换或中转站路由不透明。", "作为风险提示复核，不做绝对结论；建议和官方入口对照。", AdvancedRiskLevel.Medium),
            AdvancedErrorKind.Unknown => new(kind, "未知异常", "未分类异常。", "响应结构或本地异常不在已知分类中。", "查看原始请求/响应后复核。", AdvancedRiskLevel.Medium),
            _ => new(kind, "无异常", "未发现错误。", "无。", "继续观察长期稳定性。", AdvancedRiskLevel.Low)
        };
}
