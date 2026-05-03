using RelayBench.Core.Services;
using Xunit;

namespace RelayBench.Core.Tests;

public sealed class ProbeTraceRedactorTests
{
    [Fact]
    public void RedactUrlMasksSensitiveQueryValues()
    {
        var redacted = ProbeTraceRedactor.RedactUrl("/v1/chat/completions?api_key=sk-live&password=pw123&model=gpt-test");

        Assert.Contains("api_key=***", redacted);
        Assert.Contains("password=***", redacted);
        Assert.Contains("model=gpt-test", redacted);
        Assert.DoesNotContain("sk-live", redacted);
        Assert.DoesNotContain("pw123", redacted);
    }

    [Fact]
    public void RedactTextMasksInlineSecrets()
    {
        var redacted = ProbeTraceRedactor.RedactText("failed token=abc123 password=pw123 client_secret=secret123");

        Assert.Contains("token=***", redacted);
        Assert.Contains("password=***", redacted);
        Assert.Contains("client_secret=***", redacted);
        Assert.DoesNotContain("abc123", redacted);
        Assert.DoesNotContain("pw123", redacted);
        Assert.DoesNotContain("secret123", redacted);
    }
}
