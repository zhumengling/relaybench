using System.Text.Json;
using NetTest.Core.Models;

namespace NetTest.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    public async Task<IReadOnlyList<ProxyMultiModelSpeedTestResult>> RunMultiModelSpeedTestAsync(
        ProxyEndpointSettings settings,
        IReadOnlyList<string> requestedModels,
        CancellationToken cancellationToken = default)
    {
        var models = requestedModels
            .Where(static model => !string.IsNullOrWhiteSpace(model))
            .Select(static model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (models.Length == 0)
        {
            return Array.Empty<ProxyMultiModelSpeedTestResult>();
        }

        if (!TryValidateSettings(settings, out var normalizedSettings, out var baseUri, out var error))
        {
            return models
                .Select(model => new ProxyMultiModelSpeedTestResult(
                    model,
                    false,
                    null,
                    null,
                    false,
                    "\u591A\u6A21\u578B tok/s \u6D4B\u901F\u53C2\u6570\u6821\u9A8C\u5931\u8D25\u3002",
                    null,
                    error))
                .ToArray();
        }

        using var client = CreateClient(baseUri, normalizedSettings);
        var chatPath = BuildApiPath(baseUri, "chat/completions");
        List<ProxyMultiModelSpeedTestResult> results = new(models.Length);

        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probe = await ProbeStreamingScenarioAsync(
                client,
                chatPath,
                BuildMultiModelSpeedPayload(model),
                ProxyProbeScenarioKind.ChatCompletionsStream,
                model,
                TryParseChatStreamContent,
                static preview => !string.IsNullOrWhiteSpace(preview),
                cancellationToken);

            results.Add(new ProxyMultiModelSpeedTestResult(
                model,
                probe.Success,
                probe.StatusCode,
                probe.OutputTokensPerSecond,
                probe.OutputTokenCountEstimated,
                probe.Summary,
                probe.Preview,
                probe.Error));
        }

        return results;
    }

    private static string BuildMultiModelSpeedPayload(string model)
    {
        var payload = new
        {
            model,
            max_tokens = 128,
            temperature = 0,
            stream = true,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are a speed probe. Reply with plain text only."
                },
                new
                {
                    role = "user",
                    content = "Output the numbers 1 to 80 separated by spaces. Do not add any extra words."
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }
}
