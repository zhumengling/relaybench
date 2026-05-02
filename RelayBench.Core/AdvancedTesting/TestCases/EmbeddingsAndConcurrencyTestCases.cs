using System.Diagnostics;
using System.Text.Json;
using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting.TestCases;

public sealed class EmbeddingsEndpointTestCase : AdvancedTestCaseBase
{
    public EmbeddingsEndpointTestCase()
        : base(new AdvancedTestCaseDefinition(
            "embeddings_basic",
            "Embeddings 基础",
            AdvancedTestCategory.Rag,
            1.2d,
            "检查 /embeddings 是否可用、向量维度是否合理、batch input 是否支持。"))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var body = JsonSerializer.Serialize(new
            {
                model = context.Endpoint.Model,
                input = new[] { "RelayBench interface stability", "中转站接口稳定性" }
            });
            var exchange = await client.PostJsonAsync("embeddings", body, cancellationToken).ConfigureAwait(false);
            var hasVector = TryGetEmbeddingDimension(exchange.ResponseBody, out var dimension);
            var dimensionOk = dimension > 0;
            var checks = new[]
            {
                new AdvancedCheckResult("HttpStatus", exchange.IsSuccessStatusCode, "2xx", exchange.StatusCode?.ToString() ?? "-", exchange.IsSuccessStatusCode ? "Embeddings 请求成功。" : "Embeddings 请求失败。"),
                new AdvancedCheckResult("Vector", hasVector, "data[0].embedding[]", hasVector ? $"dimension={dimension}" : "-", hasVector ? "返回了向量数组。" : "没有返回标准向量结构。"),
                new AdvancedCheckResult("Dimension", dimensionOk, ">0", dimension.ToString(), dimensionOk ? "向量维度有效。" : "向量维度无效。")
            };

            if (exchange.IsSuccessStatusCode && hasVector && dimensionOk)
            {
                return BuildResult(exchange, redactor, AdvancedTestStatus.Passed, 100, "POST /embeddings", $"Embeddings 可用，维度 {dimension}。", checks, suggestions: new[] { "该入口可进入 RAG 套件进一步检查相似度和长文本输入。" });
            }

            return BuildResult(exchange, redactor, AdvancedTestStatus.Failed, 0, "POST /embeddings", "Embeddings 不可用或返回结构异常。", checks, ClassifyExchange(exchange));
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));

    private static bool TryGetEmbeddingDimension(string? responseBody, out int dimension)
    {
        dimension = 0;
        if (!TryParseJson(responseBody, out var document) || document is null)
        {
            return false;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
            {
                return false;
            }

            var first = data[0];
            if (!first.TryGetProperty("embedding", out var embedding) ||
                embedding.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            dimension = embedding.GetArrayLength();
            return dimension > 0;
        }
    }
}

public sealed class ConcurrencyLimitTestCase : AdvancedTestCaseBase
{
    public ConcurrencyLimitTestCase()
        : base(new AdvancedTestCaseDefinition(
            "concurrency_mini",
            "并发限流轻测",
            AdvancedTestCategory.Concurrency,
            1.3d,
            "以 1 / 2 两档轻量请求观察成功率、平均延迟和限流信号。"))
    {
    }

    public override Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken)
        => RunMeasuredAsync(async () =>
        {
            var body = BuildChatPayload(
                context.Endpoint.Model,
                "You are a concurrency probe. Answer only OK.",
                "Return OK.",
                stream: false);

            List<AdvancedModelExchange> exchanges = [];
            foreach (var concurrency in new[] { 1, 2 })
            {
                var tasks = Enumerable.Range(0, concurrency)
                    .Select(_ => client.PostJsonAsync("chat/completions", body, cancellationToken))
                    .ToArray();
                exchanges.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
            }

            var total = exchanges.Count;
            var success = exchanges.Count(static item => item.IsSuccessStatusCode);
            var rateLimited = exchanges.Count(static item => item.StatusCode == 429);
            var avgLatency = total == 0 ? 0 : exchanges.Average(static item => item.Duration.TotalMilliseconds);
            var representative = exchanges.LastOrDefault() ?? new AdvancedModelExchange(null, string.Empty, "POST", context.Endpoint.BaseUrl, new Dictionary<string, string>(), body, new Dictionary<string, string>(), "No exchange.", TimeSpan.Zero, null, Array.Empty<string>());
            var successRate = total == 0 ? 0 : (double)success / total;
            var checks = new[]
            {
                new AdvancedCheckResult("SuccessRate", successRate >= 0.9d, ">=90%", $"{success}/{total}", successRate >= 0.9d ? "轻量并发成功率达标。" : "轻量并发成功率偏低。"),
                new AdvancedCheckResult("RateLimit", rateLimited == 0, "0", rateLimited.ToString(), rateLimited == 0 ? "未观察到 429。" : "轻量并发下已出现限流。"),
                new AdvancedCheckResult("AverageLatency", avgLatency > 0, ">0 ms", $"{avgLatency:0} ms", "记录轻量并发平均延迟。")
            };

            if (successRate >= 0.9d && rateLimited == 0)
            {
                return BuildResult(representative, redactor, AdvancedTestStatus.Passed, 100, "POST /chat/completions x 1/2 concurrency", $"轻量并发成功率 {success}/{total}，平均 {avgLatency:0} ms。", checks, suggestions: new[] { "轻量并发表现正常；需要容量判断时再运行完整并发阶梯。" });
            }

            var partial = successRate >= 0.5d;
            return BuildResult(
                representative,
                redactor,
                partial ? AdvancedTestStatus.Partial : AdvancedTestStatus.Failed,
                partial ? 55 : 0,
                "POST /chat/completions x 1/2 concurrency",
                $"轻量并发成功率 {success}/{total}，429={rateLimited}。",
                checks,
                rateLimited > 0 ? AdvancedErrorKind.RateLimited : AdvancedErrorKind.ServerError,
                suggestions: new[] { "如果轻量并发也不稳定，不建议配置给多 Agent 或批量任务。" });
        }, (ex, duration) => BuildExceptionResult(ex, duration, redactor));
}
