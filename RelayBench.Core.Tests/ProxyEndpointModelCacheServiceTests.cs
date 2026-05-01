using RelayBench.Core.Models;
using RelayBench.Core.Services;
using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class ProxyEndpointModelCacheServiceTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase(
            "endpoint model cache isolates models and api keys",
            RunModelAndApiKeyIsolationAsync,
            group: "cache",
            timeout: TimeSpan.FromSeconds(10));

        yield return new TestCase(
            "endpoint model cache merges catalog context with protocol support",
            RunCatalogAndProtocolMergeAsync,
            group: "cache",
            timeout: TimeSpan.FromSeconds(10));

        yield return new TestCase(
            "endpoint model cache replaces stale protocol support flags",
            RunProtocolSupportReplacementAsync,
            group: "cache",
            timeout: TimeSpan.FromSeconds(10));

        yield return new TestCase(
            "endpoint model cache handles concurrent model writes",
            RunConcurrentModelWritesAsync,
            group: "cache",
            timeout: TimeSpan.FromSeconds(10));
    }

    private static async Task RunModelAndApiKeyIsolationAsync()
    {
        await using var database = TemporaryCacheDatabase.Create();
        var service = new ProxyEndpointModelCacheService(database.Path);
        var baseUrl = "https://relay.example.com/v1";
        var apiKey = "sk-test";

        await service.SaveProtocolProbeAsync(
            BuildSettings(baseUrl, apiKey, "gpt-responses"),
            BuildProtocolProbe(baseUrl, "gpt-responses", responses: true, preferredWireApi: "responses"));
        await service.SaveProtocolProbeAsync(
            BuildSettings(baseUrl, apiKey, "claude-messages"),
            BuildProtocolProbe(baseUrl, "claude-messages", anthropic: true, preferredWireApi: "anthropic"));

        var gpt = await service.TryResolveAsync(baseUrl, apiKey, "gpt-responses");
        var claude = await service.TryResolveAsync(baseUrl, apiKey, "claude-messages");
        var wrongKey = await service.TryResolveAsync(baseUrl, "sk-other", "gpt-responses");

        AssertEqual(gpt?.PreferredWireApi ?? string.Empty, "responses");
        AssertTrue(gpt?.ResponsesSupported == true, "Responses support should stay on the responses model.");
        AssertEqual(claude?.PreferredWireApi ?? string.Empty, "anthropic");
        AssertTrue(claude?.AnthropicMessagesSupported == true, "Anthropic support should stay on the Anthropic model.");
        AssertTrue(wrongKey is null, "Endpoint cache must be partitioned by API key hash.");
    }

    private static async Task RunCatalogAndProtocolMergeAsync()
    {
        await using var database = TemporaryCacheDatabase.Create();
        var service = new ProxyEndpointModelCacheService(database.Path);
        var settings = BuildSettings("https://relay.example.com/v1", "sk-test", "gpt-responses");

        await service.SaveCatalogAsync(
            settings,
            new ProxyModelCatalogResult(
                DateTimeOffset.UtcNow,
                settings.BaseUrl,
                true,
                200,
                1,
                ["gpt-responses"],
                TimeSpan.FromMilliseconds(10),
                "ok",
                null,
                ModelItems: [new ProxyModelCatalogItem("gpt-responses", 123_456)]));
        await service.SaveProtocolProbeAsync(
            settings,
            BuildProtocolProbe(settings.BaseUrl, settings.Model, responses: true, preferredWireApi: "responses"));

        var cached = await service.TryResolveAsync(settings.BaseUrl, settings.ApiKey, settings.Model);

        AssertTrue(cached is not null, "Merged catalog/protocol cache should be resolvable.");
        AssertTrue(cached!.ContextWindow == 123_456, $"Expected catalog context window, got {cached.ContextWindow}.");
        AssertEqual(cached.PreferredWireApi ?? string.Empty, "responses");
        AssertTrue(cached.ResponsesSupported == true, "Protocol support should be merged with catalog context.");
    }

    private static async Task RunProtocolSupportReplacementAsync()
    {
        await using var database = TemporaryCacheDatabase.Create();
        var service = new ProxyEndpointModelCacheService(database.Path);
        var settings = BuildSettings("https://relay.example.com/v1", "sk-test", "switching-model");

        await service.SaveProtocolProbeAsync(
            settings,
            BuildProtocolProbe(settings.BaseUrl, settings.Model, chat: true, preferredWireApi: "chat"));
        await service.SaveProtocolProbeAsync(
            settings,
            BuildProtocolProbe(settings.BaseUrl, settings.Model, responses: true, preferredWireApi: "responses"));

        var cached = await service.TryResolveAsync(settings.BaseUrl, settings.ApiKey, settings.Model);

        AssertEqual(cached?.PreferredWireApi ?? string.Empty, "responses");
        AssertTrue(cached?.ResponsesSupported == true, "Latest probe should mark responses support.");
        AssertTrue(cached?.ChatCompletionsSupported == false, "Latest probe should replace earlier chat support.");
    }

    private static async Task RunConcurrentModelWritesAsync()
    {
        await using var database = TemporaryCacheDatabase.Create();
        var service = new ProxyEndpointModelCacheService(database.Path);
        var baseUrl = "https://relay.example.com/v1";
        var apiKey = "sk-test";
        var models = Enumerable.Range(1, 8).Select(index => $"model-{index}").ToArray();

        await Task.WhenAll(models.Select(model =>
            service.SaveProtocolProbeAsync(
                BuildSettings(baseUrl, apiKey, model),
                BuildProtocolProbe(baseUrl, model, chat: true, preferredWireApi: "chat"))));

        foreach (var model in models)
        {
            var cached = await service.TryResolveAsync(baseUrl, apiKey, model);
            AssertTrue(cached is not null, $"Expected cache entry for {model}.");
            AssertEqual(cached!.PreferredWireApi ?? string.Empty, "chat");
            AssertTrue(cached.ChatCompletionsSupported == true, $"{model} should be cached as chat-capable.");
        }
    }

    private static ProxyEndpointSettings BuildSettings(string baseUrl, string apiKey, string model)
        => new(baseUrl, apiKey, model, false, 10);

    private static ProxyEndpointProtocolProbeResult BuildProtocolProbe(
        string baseUrl,
        string model,
        bool chat = false,
        bool responses = false,
        bool anthropic = false,
        string? preferredWireApi = null)
        => new(
            DateTimeOffset.UtcNow,
            baseUrl,
            model,
            chat,
            responses,
            anthropic,
            preferredWireApi,
            "ok",
            null);
}

internal sealed class TemporaryCacheDatabase : IAsyncDisposable
{
    private TemporaryCacheDatabase(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TemporaryCacheDatabase Create()
        => new(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"relaybench-cache-{Guid.NewGuid():N}.sqlite"));

    public ValueTask DisposeAsync()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }

        return ValueTask.CompletedTask;
    }
}
