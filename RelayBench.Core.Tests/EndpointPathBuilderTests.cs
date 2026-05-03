using RelayBench.Core.Services;
using Xunit;

namespace RelayBench.Core.Tests;

public sealed class EndpointPathBuilderTests
{
    [Theory]
    [InlineData("https://relay.example.com/", "models", "https://relay.example.com/v1/models")]
    [InlineData("https://relay.example.com", "/chat/completions", "https://relay.example.com/v1/chat/completions")]
    [InlineData("https://relay.example.com/v1", "models", "https://relay.example.com/v1/models")]
    [InlineData("https://relay.example.com/v1/", "/responses", "https://relay.example.com/v1/responses")]
    [InlineData("https://relay.example.com/anthropic", "messages", "https://relay.example.com/anthropic/v1/messages")]
    [InlineData("https://relay.example.com/anthropic/v1", "messages", "https://relay.example.com/anthropic/v1/messages")]
    public void CombineOpenAiCompatibleUrl_AddsV1OnlyWhenMissing(string baseUrl, string endpoint, string expected)
    {
        var actual = EndpointPathBuilder.CombineOpenAiCompatibleUrl(baseUrl, endpoint);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CombineOpenAiCompatibleUrl_KeepsCompleteModelStylePathsIndependentFromModelNames()
    {
        var url = EndpointPathBuilder.CombineOpenAiCompatibleUrl(
            "https://relay.example.com/",
            "chat/completions");

        Assert.Equal("https://relay.example.com/v1/chat/completions", url);
    }
}
