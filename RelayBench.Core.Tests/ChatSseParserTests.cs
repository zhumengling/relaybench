using RelayBench.Core.Services;
using Xunit;

namespace RelayBench.Core.Tests;

public sealed class ChatSseParserTests
{
    [Theory]
    [InlineData("[DONE]")]
    [InlineData("""{"type":"message_stop"}""")]
    [InlineData("""{"type":"response.completed"}""")]
    public void IsDone_AcceptsOpenAiResponsesAndAnthropicStopEvents(string data)
    {
        Assert.True(ChatSseParser.IsDone(data));
    }

    [Theory]
    [InlineData("""{"choices":[{"delta":{"content":"chat-delta"}}]}""", "chat-delta")]
    [InlineData("""{"type":"response.output_text.delta","delta":"responses-delta"}""", "responses-delta")]
    [InlineData("""{"type":"content_block_delta","delta":{"text":"anthropic-delta"}}""", "anthropic-delta")]
    public void TryExtractDelta_ExtractsSupportedStreamingFormats(string data, string expected)
    {
        Assert.Equal(expected, ChatSseParser.TryExtractDelta(data));
    }
}
