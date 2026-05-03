using System.Text.Json.Nodes;
using RelayBench.Core.AdvancedTesting;
using RelayBench.Core.Services;
using Xunit;

namespace RelayBench.Core.Tests.AdvancedTesting;

public sealed class AdvancedWireRequestBuilderTests
{
    [Fact]
    public void PreparePostJson_MapsChatPayloadToResponsesWhenPreferred()
    {
        var payload = BuildChatPayload("mimo-v2.5-pro OpenAI", stream: false);

        var prepared = AdvancedWireRequestBuilder.PreparePostJson(
            "chat/completions",
            payload,
            ProxyWireApiProbeService.ResponsesWireApi,
            stream: false);

        Assert.Equal("responses", prepared.RelativePath);
        Assert.Equal(ProxyWireApiProbeService.ResponsesWireApi, prepared.WireApi);

        var root = JsonNode.Parse(prepared.RequestBody)!.AsObject();
        Assert.Equal("mimo-v2.5-pro OpenAI", root["model"]!.GetValue<string>());
        Assert.Equal(256, root["max_output_tokens"]!.GetValue<int>());
        Assert.Null(root["messages"]);
        Assert.NotNull(root["input"]);
        Assert.Contains("Keep the model id intact.", root["instructions"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void PreparePostJson_MapsChatPayloadToAnthropicMessagesWhenPreferred()
    {
        var payload = BuildChatPayload("mimo-v2.5-pro claude", stream: true);

        var prepared = AdvancedWireRequestBuilder.PreparePostJson(
            "chat/completions",
            payload,
            ProxyWireApiProbeService.AnthropicMessagesWireApi,
            stream: true);

        Assert.Equal("messages", prepared.RelativePath);
        Assert.Equal(ProxyWireApiProbeService.AnthropicMessagesWireApi, prepared.WireApi);
        Assert.Contains("anthropic-version", prepared.ExtraHeaders.Keys);

        var root = JsonNode.Parse(prepared.RequestBody)!.AsObject();
        Assert.Equal("mimo-v2.5-pro claude", root["model"]!.GetValue<string>());
        Assert.True(root["stream"]!.GetValue<bool>());
        Assert.True(root["max_tokens"]!.GetValue<int>() >= 512);
        Assert.Equal("Keep the model id intact.", root["system"]!.GetValue<string>());
        Assert.NotNull(root["messages"]);
    }

    [Fact]
    public void PreparePostJson_KeepsChatCompletionsWhenPreferredIsUnknown()
    {
        var payload = BuildChatPayload("deepseek-v4-flash OpenAI", stream: false);

        var prepared = AdvancedWireRequestBuilder.PreparePostJson(
            "chat/completions",
            payload,
            preferredWireApi: null,
            stream: false);

        Assert.Equal("chat/completions", prepared.RelativePath);
        Assert.Equal(ProxyWireApiProbeService.ChatCompletionsWireApi, prepared.WireApi);
        Assert.Equal(payload, prepared.RequestBody);
        Assert.Empty(prepared.ExtraHeaders);
    }

    private static string BuildChatPayload(string model, bool stream)
        => $$"""
           {
             "model": "{{model}}",
             "stream": {{stream.ToString().ToLowerInvariant()}},
             "temperature": 0,
             "max_tokens": 256,
             "messages": [
               {
                 "role": "system",
                 "content": "Keep the model id intact."
               },
               {
                 "role": "user",
                 "content": "Say pong."
               }
             ]
           }
           """;
}
