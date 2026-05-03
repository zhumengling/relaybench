using RelayBench.Core.AdvancedTesting.Models;
using RelayBench.Core.AdvancedTesting.Redaction;
using Xunit;

namespace RelayBench.Core.Tests.AdvancedTesting;

public sealed class SensitiveDataRedactorTests
{
    [Fact]
    public void Redact_RemovesAuthorizationAndJsonSecrets()
    {
        var redactor = new SensitiveDataRedactor();
        var input = """
        {
          "authorization": "Bearer sk-test-123",
          "nested": {
            "api_key": "abc",
            "safe": "visible"
          }
        }
        """;

        var redacted = redactor.Redact(input);

        Assert.DoesNotContain("sk-test-123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", redacted, StringComparison.Ordinal);
        Assert.Contains("visible", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_ExchangeMasksSensitiveHeaders()
    {
        var redactor = new SensitiveDataRedactor();
        var exchange = new AdvancedRawExchange(
            "POST",
            "https://example.test/v1/chat/completions?api_key=abc&safe=1",
            new Dictionary<string, string> { ["Authorization"] = "Bearer sk-live", ["Accept"] = "application/json" },
            "{\"model\":\"gpt\",\"token\":\"secret-value\"}",
            200,
            new Dictionary<string, string> { ["content-type"] = "application/json" },
            "{\"ok\":true}");

        var redacted = redactor.Redact(exchange);

        Assert.Equal("***", redacted.RequestHeaders["Authorization"]);
        Assert.DoesNotContain("sk-live", redacted.RequestBody + redacted.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", redacted.RequestBody, StringComparison.Ordinal);
        Assert.Contains("safe", redacted.Url, StringComparison.Ordinal);
    }
}
