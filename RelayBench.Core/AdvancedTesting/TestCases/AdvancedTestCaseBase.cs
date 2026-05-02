using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.Services;

namespace RelayBench.Core.AdvancedTesting.TestCases;

public abstract class AdvancedTestCaseBase : IAdvancedTestCase
{
    protected AdvancedTestCaseBase(AdvancedTestCaseDefinition definition)
    {
        Definition = definition;
    }

    public AdvancedTestCaseDefinition Definition { get; }

    public abstract Task<AdvancedTestCaseResult> RunAsync(
        AdvancedTestRunContext context,
        IModelClient client,
        ISensitiveDataRedactor redactor,
        CancellationToken cancellationToken);

    protected static AdvancedRawExchange ToRawExchange(AdvancedModelExchange exchange)
        => new(
            exchange.RequestMethod,
            exchange.RequestUrl,
            exchange.RequestHeaders,
            exchange.RequestBody,
            exchange.StatusCode,
            exchange.ResponseHeaders,
            exchange.ResponseBody);

    protected AdvancedTestCaseResult BuildResult(
        AdvancedModelExchange exchange,
        ISensitiveDataRedactor redactor,
        AdvancedTestStatus status,
        double score,
        string requestSummary,
        string responseSummary,
        IReadOnlyList<AdvancedCheckResult> checks,
        AdvancedErrorKind errorKind = AdvancedErrorKind.None,
        string errorCode = "",
        string errorMessage = "",
        AdvancedRiskLevel? riskLevel = null,
        IReadOnlyList<string>? suggestions = null)
    {
        var redacted = redactor.Redact(ToRawExchange(exchange));
        var descriptor = AdvancedErrorCatalog.Describe(errorKind);
        return new AdvancedTestCaseResult(
            Definition.TestId,
            Definition.DisplayName,
            Definition.Category,
            status,
            score,
            Definition.Weight,
            exchange.Duration,
            requestSummary,
            responseSummary,
            BuildRequestDocument(redacted),
            BuildResponseDocument(redacted),
            errorKind,
            errorCode,
            string.IsNullOrWhiteSpace(errorMessage) ? descriptor.UserMessage : errorMessage,
            riskLevel ?? descriptor.RiskLevel,
            suggestions ?? new[] { descriptor.Suggestion },
            checks);
    }

    protected AdvancedTestCaseResult BuildExceptionResult(
        Exception exception,
        TimeSpan duration,
        ISensitiveDataRedactor redactor)
    {
        var kind = ClassifyException(exception);
        var descriptor = AdvancedErrorCatalog.Describe(kind);
        return new AdvancedTestCaseResult(
            Definition.TestId,
            Definition.DisplayName,
            Definition.Category,
            AdvancedTestStatus.Failed,
            0,
            Definition.Weight,
            duration,
            "本地执行异常",
            descriptor.UserMessage,
            null,
            redactor.Redact(exception.ToString()),
            kind,
            exception.GetType().Name,
            descriptor.UserMessage,
            descriptor.RiskLevel,
            new[] { descriptor.Suggestion },
            Array.Empty<AdvancedCheckResult>());
    }

    protected static AdvancedErrorKind ClassifyExchange(AdvancedModelExchange exchange)
    {
        if (exchange.StatusCode is null)
        {
            var body = exchange.ResponseBody ?? string.Empty;
            if (body.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                return AdvancedErrorKind.NetworkTimeout;
            }

            if (body.Contains("NameResolution", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("DNS", StringComparison.OrdinalIgnoreCase))
            {
                return AdvancedErrorKind.DnsFailure;
            }

            if (body.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            {
                return AdvancedErrorKind.TlsFailure;
            }

            return AdvancedErrorKind.Unknown;
        }

        return exchange.StatusCode.Value switch
        {
            401 or 403 => AdvancedErrorKind.Unauthorized,
            400 or 404 or 422 => AdvancedErrorKind.InvalidRequest,
            429 => AdvancedErrorKind.RateLimited,
            502 or 503 or 504 or 520 or 524 => AdvancedErrorKind.BadGateway,
            >= 500 => AdvancedErrorKind.ServerError,
            _ => AdvancedErrorKind.None
        };
    }

    protected static AdvancedErrorKind ClassifyException(Exception exception)
        => exception switch
        {
            OperationCanceledException => AdvancedErrorKind.NetworkTimeout,
            TimeoutException => AdvancedErrorKind.NetworkTimeout,
            _ => AdvancedErrorKind.Unknown
        };

    protected static bool TryParseJson(string? value, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            document = JsonDocument.Parse(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected static string ExtractOpenAiText(string? responseBody)
        => ModelResponseTextExtractor.TryExtractAssistantText(responseBody) ?? string.Empty;

    protected static bool HasUsage(string? responseBody)
    {
        if (!TryParseJson(responseBody, out var document) || document is null)
        {
            return false;
        }

        using (document)
        {
            return document.RootElement.TryGetProperty("usage", out var usage) &&
                   usage.ValueKind == JsonValueKind.Object;
        }
    }

    protected static string BuildChatPayload(string model, string system, string user, bool stream = false)
        => JsonSerializer.Serialize(new
        {
            model,
            stream,
            temperature = 0,
            max_tokens = 512,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        });

    protected static string BuildRequestDocument(AdvancedRawExchange exchange)
    {
        StringBuilder builder = new();
        builder.AppendLine($"{exchange.Method} {exchange.Url}");
        builder.AppendLine();
        foreach (var header in exchange.RequestHeaders)
        {
            builder.AppendLine($"{header.Key}: {header.Value}");
        }

        if (!string.IsNullOrWhiteSpace(exchange.RequestBody))
        {
            builder.AppendLine();
            builder.AppendLine(exchange.RequestBody);
        }

        return builder.ToString().TrimEnd();
    }

    protected static string BuildResponseDocument(AdvancedRawExchange exchange)
    {
        StringBuilder builder = new();
        builder.AppendLine(exchange.StatusCode is null ? "Status: -" : $"Status: {exchange.StatusCode}");
        builder.AppendLine();
        foreach (var header in exchange.ResponseHeaders)
        {
            builder.AppendLine($"{header.Key}: {header.Value}");
        }

        if (!string.IsNullOrWhiteSpace(exchange.ResponseBody))
        {
            builder.AppendLine();
            builder.AppendLine(exchange.ResponseBody);
        }

        return builder.ToString().TrimEnd();
    }

    protected static async Task<AdvancedTestCaseResult> RunMeasuredAsync(
        Func<Task<AdvancedTestCaseResult>> action,
        Func<Exception, TimeSpan, AdvancedTestCaseResult> onException)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return onException(ex, stopwatch.Elapsed);
        }
    }
}
