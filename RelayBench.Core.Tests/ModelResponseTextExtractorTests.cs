using RelayBench.Core.Services;
using Xunit;

namespace RelayBench.Core.Tests;

public sealed class ModelResponseTextExtractorTests
{
    [Fact]
    public void TryExtractAssistantText_ReadsChatCompletionsContent()
    {
        const string json = """
        {
          "choices": [
            {
              "message": {
                "content": "chat-ok"
              }
            }
          ]
        }
        """;

        Assert.Equal("chat-ok", ModelResponseTextExtractor.TryExtractAssistantText(json));
    }

    [Fact]
    public void TryExtractAssistantText_ReadsAnthropicMessagesContent()
    {
        const string json = """
        {
          "type": "message",
          "content": [
            {
              "type": "text",
              "text": "anthropic-ok"
            }
          ]
        }
        """;

        Assert.Equal("anthropic-ok", ModelResponseTextExtractor.TryExtractAssistantText(json));
    }

    [Fact]
    public void TryExtractAssistantText_ReadsResponsesNestedOutputText()
    {
        const string json = """
        {
          "object": "response",
          "output": [
            {
              "type": "reasoning",
              "summary": []
            },
            {
              "type": "message",
              "content": [
                {
                  "type": "output_text",
                  "text": "responses-ok"
                }
              ]
            }
          ]
        }
        """;

        Assert.Equal("responses-ok", ModelResponseTextExtractor.TryExtractAssistantText(json));
    }

    [Fact]
    public void TryExtractAssistantText_ReadsResponsesTextNestedUnderOutputItem()
    {
        const string json = """
        {
          "object": "response",
          "output": [
            {
              "type": "message",
              "text": "direct-output-ok"
            }
          ]
        }
        """;

        Assert.Equal("direct-output-ok", ModelResponseTextExtractor.TryExtractAssistantText(json));
    }
}
