using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ProxyDiagnosticsService
{
    public async Task<ProxyCapabilityMatrixResult> ProbeCapabilityMatrixAsync(
        ProxyEndpointSettings settings,
        IProgress<ProxyProbeScenarioResult>? scenarioProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateSettings(settings, out var normalizedSettings, out var baseUri, out var error))
        {
            return new ProxyCapabilityMatrixResult(
                Array.Empty<ProxyProbeScenarioResult>(),
                $"\u975E\u804A\u5929 API \u80FD\u529B\u77E9\u9635\u53C2\u6570\u6821\u9A8C\u5931\u8D25\uFF1A{error}");
        }

        var configuredCapabilityCount = CountConfiguredCapabilityModels(normalizedSettings);
        if (configuredCapabilityCount == 0)
        {
            return new ProxyCapabilityMatrixResult(
                Array.Empty<ProxyProbeScenarioResult>(),
                "\u975E\u804A\u5929 API \u80FD\u529B\u77E9\u9635\uFF1A\u672C\u8F6E\u672A\u914D\u7F6E\u4EFB\u4F55\u80FD\u529B\u6A21\u578B\uFF0C\u5DF2\u8DF3\u8FC7\u3002");
        }

        using var client = CreateClient(baseUri, normalizedSettings);
        var embeddingsPath = BuildApiPath(baseUri, "embeddings");
        var imagesPath = BuildApiPath(baseUri, "images/generations");
        var transcriptionPath = BuildApiPath(baseUri, "audio/transcriptions");
        var speechPath = BuildApiPath(baseUri, "audio/speech");
        var moderationPath = BuildApiPath(baseUri, "moderations");

        List<ProxyProbeScenarioResult> scenarios = new(5);

        async Task AddScenarioAsync(Task<ProxyProbeScenarioResult> scenarioTask)
        {
            var scenario = await scenarioTask;
            scenarios.Add(scenario);
            scenarioProgress?.Report(scenario);
        }

        if (!string.IsNullOrWhiteSpace(normalizedSettings.EmbeddingsModel))
        {
            await AddScenarioAsync(ProbeEmbeddingsCapabilityScenarioAsync(
                client,
                embeddingsPath,
                normalizedSettings.EmbeddingsModel,
                cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(normalizedSettings.ImagesModel))
        {
            await AddScenarioAsync(ProbeImagesCapabilityScenarioAsync(
                client,
                imagesPath,
                normalizedSettings.ImagesModel,
                cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(normalizedSettings.AudioTranscriptionModel))
        {
            await AddScenarioAsync(ProbeAudioTranscriptionCapabilityScenarioAsync(
                client,
                transcriptionPath,
                normalizedSettings.AudioTranscriptionModel,
                cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(normalizedSettings.AudioSpeechModel))
        {
            await AddScenarioAsync(ProbeAudioSpeechCapabilityScenarioAsync(
                client,
                speechPath,
                normalizedSettings.AudioSpeechModel,
                cancellationToken));
        }

        if (!string.IsNullOrWhiteSpace(normalizedSettings.ModerationModel))
        {
            await AddScenarioAsync(ProbeModerationCapabilityScenarioAsync(
                client,
                moderationPath,
                normalizedSettings.ModerationModel,
                cancellationToken));
        }

        return new ProxyCapabilityMatrixResult(
            scenarios,
            BuildCapabilityMatrixSummary(scenarios, configuredCapabilityCount));
    }

    public async Task<ProxyDiagnosticsResult> RunNonChatCapabilityMatrixAsync(
        ProxyEndpointSettings settings,
        ProxyDiagnosticsResult baselineResult,
        IProgress<ProxyDiagnosticsLiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateSettings(settings, out var normalizedSettings, out var baseUri, out _))
        {
            return baselineResult;
        }

        var effectiveSettings = string.IsNullOrWhiteSpace(baselineResult.EffectiveModel)
            ? normalizedSettings
            : normalizedSettings with { Model = baselineResult.EffectiveModel.Trim() };
        var configuredCapabilityCount = CountConfiguredCapabilityModels(effectiveSettings);
        if (configuredCapabilityCount == 0)
        {
            return baselineResult;
        }

        List<ProxyProbeScenarioResult> mergedScenarioResults = (baselineResult.ScenarioResults ?? Array.Empty<ProxyProbeScenarioResult>())
            .Where(result => !IsNonChatCapabilityScenario(result.Scenario))
            .ToList();
        var totalScenarioCount = mergedScenarioResults.Count + configuredCapabilityCount;

        var scenarioProgress = new Progress<ProxyProbeScenarioResult>(scenarioResult =>
        {
            mergedScenarioResults.Add(scenarioResult);
            ReportSingleProgress(
                progress,
                baseUri,
                effectiveSettings,
                baselineResult.EffectiveModel ?? effectiveSettings.Model,
                baselineResult.ModelCount,
                baselineResult.SampleModels,
                scenarioResult,
                mergedScenarioResults,
                totalScenarioCount);
        });

        await ProbeCapabilityMatrixAsync(effectiveSettings, scenarioProgress, cancellationToken);
        return RebuildDiagnosticsResult(baselineResult, mergedScenarioResults);
    }

    private static async Task<ProxyProbeScenarioResult> ProbeEmbeddingsCapabilityScenarioAsync(
        HttpClient client,
        string path,
        string? model,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return CreateCapabilityMatrixUnconfiguredScenario(
                ProxyProbeScenarioKind.Embeddings,
                "\u5411\u91CF / Embeddings",
                "\u672A\u914D\u7F6E Embeddings \u6A21\u578B\uFF0C\u672C\u8F6E\u4E0D\u53C2\u4E0E\u6D4B\u8BD5\u3002");
        }

        var outcome = await ProbeJsonScenarioAsync(
            client,
            path,
            BuildEmbeddingsPayload(model),
            ProxyProbeScenarioKind.Embeddings,
            "\u5411\u91CF / Embeddings",
            ParseEmbeddingsPreview,
            cancellationToken);

        return outcome.ScenarioResult;
    }

    private static async Task<ProxyProbeScenarioResult> ProbeImagesCapabilityScenarioAsync(
        HttpClient client,
        string path,
        string? model,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return CreateCapabilityMatrixUnconfiguredScenario(
                ProxyProbeScenarioKind.Images,
                "\u751F\u56FE / Images",
                "\u672A\u914D\u7F6E Images \u6A21\u578B\uFF0C\u672C\u8F6E\u4E0D\u53C2\u4E0E\u6D4B\u8BD5\u3002");
        }

        var outcome = await ProbeJsonScenarioAsync(
            client,
            path,
            BuildImagesPayload(model),
            ProxyProbeScenarioKind.Images,
            "\u751F\u56FE / Images",
            ParseImagesPreview,
            cancellationToken);

        return outcome.ScenarioResult;
    }

    private static async Task<ProxyProbeScenarioResult> ProbeModerationCapabilityScenarioAsync(
        HttpClient client,
        string path,
        string? model,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return CreateCapabilityMatrixUnconfiguredScenario(
                ProxyProbeScenarioKind.Moderation,
                "\u5BA1\u6838 / Moderation",
                "\u672A\u914D\u7F6E Moderation \u6A21\u578B\uFF0C\u672C\u8F6E\u4E0D\u53C2\u4E0E\u6D4B\u8BD5\u3002");
        }

        var outcome = await ProbeJsonScenarioAsync(
            client,
            path,
            BuildModerationPayload(model),
            ProxyProbeScenarioKind.Moderation,
            "\u5BA1\u6838 / Moderation",
            ParseModerationPreview,
            cancellationToken);

        return outcome.ScenarioResult;
    }

    private static async Task<ProxyProbeScenarioResult> ProbeAudioTranscriptionCapabilityScenarioAsync(
        HttpClient client,
        string path,
        string? model,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return CreateCapabilityMatrixUnconfiguredScenario(
                ProxyProbeScenarioKind.AudioTranscription,
                "\u8BED\u97F3\u8F6C\u5199",
                "\u672A\u914D\u7F6E Audio Transcription \u6A21\u578B\uFF0C\u672C\u8F6E\u4E0D\u53C2\u4E0E\u6D4B\u8BD5\u3002");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = BuildAudioTranscriptionContent(model)
            };

            using var response = await client.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            var headers = ExtractInterestingHeaders(response);
            var requestId = ExtractRequestId(headers);
            var traceId = ExtractTraceId(headers);

            if (!response.IsSuccessStatusCode)
            {
                var failureKind = ClassifyResponseFailure(ProxyProbeScenarioKind.AudioTranscription, statusCode, body);
                return BuildSupplementalFailureResult(
                    ProxyProbeScenarioKind.AudioTranscription,
                    "\u8BED\u97F3\u8F6C\u5199",
                    statusCode,
                    stopwatch.Elapsed,
                    ExtractBodySample(body),
                    $"\u8BED\u97F3\u8F6C\u5199\u5931\u8D25\uFF0C\u72B6\u6001\u7801 {statusCode}\u3002",
                    $"POST {path} \u8FD4\u56DE {statusCode} {response.ReasonPhrase}\u3002 {ExtractBodySample(body)}",
                    headers,
                    failureKind,
                    "\u8BED\u97F3\u8F6C\u5199",
                    requestId,
                    traceId);
            }

            string? preview;
            try
            {
                preview = ParseAudioTranscriptionPreview(body);
            }
            catch (Exception ex)
            {
                preview = BuildLooseSuccessPreview(body);
                if (string.IsNullOrWhiteSpace(preview))
                {
                    return new ProxyProbeScenarioResult(
                        ProxyProbeScenarioKind.AudioTranscription,
                        "\u8BED\u97F3\u8F6C\u5199",
                        "\u5F02\u5E38",
                        false,
                        statusCode,
                        stopwatch.Elapsed,
                        null,
                        null,
                        false,
                        0,
                        null,
                        "\u8BED\u97F3\u8F6C\u5199\u8FD4\u56DE 200\uFF0C\u4F46\u7ED3\u679C\u7ED3\u6784\u65E0\u6CD5\u89E3\u6790\u3002",
                        ExtractBodySample(body),
                        ProxyFailureKind.ProtocolMismatch,
                        "\u8BED\u97F3\u8F6C\u5199",
                        ex.Message,
                        headers,
                        RequestId: requestId,
                        TraceId: traceId);
                }
            }

            var outputMetrics = BuildOutputMetrics(preview, TryExtractOutputTokenCount(body), stopwatch.Elapsed);
            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.AudioTranscription,
                "\u8BED\u97F3\u8F6C\u5199",
                "\u652F\u6301",
                true,
                statusCode,
                stopwatch.Elapsed,
                null,
                null,
                false,
                0,
                null,
                "\u8BED\u97F3\u8F6C\u5199\u8BF7\u6C42\u6210\u529F\u3002",
                preview,
                null,
                "\u8BED\u97F3\u8F6C\u5199",
                null,
                headers,
                OutputTokenCount: outputMetrics.OutputTokenCount,
                OutputTokenCountEstimated: outputMetrics.OutputTokenCountEstimated,
                OutputCharacterCount: outputMetrics.OutputCharacterCount,
                GenerationDuration: outputMetrics.GenerationDuration,
                OutputTokensPerSecond: outputMetrics.OutputTokensPerSecond,
                EndToEndTokensPerSecond: outputMetrics.EndToEndTokensPerSecond,
                RequestId: requestId,
                TraceId: traceId);
        }
        catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
        {
            stopwatch.Stop();
            var failureKind = ClassifyException(ex);
            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.AudioTranscription,
                "\u8BED\u97F3\u8F6C\u5199",
                DescribeCapability(failureKind, false),
                false,
                null,
                stopwatch.Elapsed,
                null,
                null,
                false,
                0,
                null,
                $"\u8BED\u97F3\u8F6C\u5199\u8BF7\u6C42\u5931\u8D25\uFF1A{DescribeFailureKind(failureKind)}\u3002",
                null,
                failureKind,
                "\u8BED\u97F3\u8F6C\u5199",
                ex.Message,
                RequestId: null,
                TraceId: null);
        }
    }

    private static async Task<ProxyProbeScenarioResult> ProbeAudioSpeechCapabilityScenarioAsync(
        HttpClient client,
        string path,
        string? model,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return CreateCapabilityMatrixUnconfiguredScenario(
                ProxyProbeScenarioKind.AudioSpeech,
                "\u6587\u672C\u8F6C\u8BED\u97F3",
                "\u672A\u914D\u7F6E Audio Speech \u6A21\u578B\uFF0C\u672C\u8F6E\u4E0D\u53C2\u4E0E\u6D4B\u8BD5\u3002");
        }

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(BuildAudioSpeechPayload(model), Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            stopwatch.Stop();
            var headers = ExtractInterestingHeaders(response);
            var requestId = ExtractRequestId(headers);
            var traceId = ExtractTraceId(headers);

            if (!response.IsSuccessStatusCode)
            {
                var body = bytes.Length == 0 ? string.Empty : SafeDecodeUtf8(bytes);
                var failureKind = ClassifyResponseFailure(ProxyProbeScenarioKind.AudioSpeech, statusCode, body);
                return BuildSupplementalFailureResult(
                    ProxyProbeScenarioKind.AudioSpeech,
                    "\u6587\u672C\u8F6C\u8BED\u97F3",
                    statusCode,
                    stopwatch.Elapsed,
                    ExtractBodySample(body),
                    $"\u6587\u672C\u8F6C\u8BED\u97F3\u5931\u8D25\uFF0C\u72B6\u6001\u7801 {statusCode}\u3002",
                    $"POST {path} \u8FD4\u56DE {statusCode} {response.ReasonPhrase}\u3002 {ExtractBodySample(body)}",
                    headers,
                    failureKind,
                    "\u6587\u672C\u8F6C\u8BED\u97F3",
                    requestId,
                    traceId);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var preview = $"{contentType} / {bytes.Length} bytes";
            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.AudioSpeech,
                "\u6587\u672C\u8F6C\u8BED\u97F3",
                "\u652F\u6301",
                bytes.Length > 0,
                statusCode,
                stopwatch.Elapsed,
                null,
                null,
                false,
                0,
                null,
                bytes.Length > 0
                    ? "\u6587\u672C\u8F6C\u8BED\u97F3\u8BF7\u6C42\u6210\u529F\u3002"
                    : "\u6587\u672C\u8F6C\u8BED\u97F3\u8FD4\u56DE 200\uFF0C\u4F46\u54CD\u5E94\u4F53\u4E3A\u7A7A\u3002",
                preview,
                bytes.Length > 0 ? null : ProxyFailureKind.ProtocolMismatch,
                "\u6587\u672C\u8F6C\u8BED\u97F3",
                bytes.Length > 0 ? null : "\u8BED\u97F3\u8F93\u51FA\u4E3A\u7A7A\u3002",
                headers,
                RequestId: requestId,
                TraceId: traceId);
        }
        catch (Exception ex) when (!IsCancellationRequestedException(ex, cancellationToken))
        {
            stopwatch.Stop();
            var failureKind = ClassifyException(ex);
            return new ProxyProbeScenarioResult(
                ProxyProbeScenarioKind.AudioSpeech,
                "\u6587\u672C\u8F6C\u8BED\u97F3",
                DescribeCapability(failureKind, false),
                false,
                null,
                stopwatch.Elapsed,
                null,
                null,
                false,
                0,
                null,
                $"\u6587\u672C\u8F6C\u8BED\u97F3\u8BF7\u6C42\u5931\u8D25\uFF1A{DescribeFailureKind(failureKind)}\u3002",
                null,
                failureKind,
                "\u6587\u672C\u8F6C\u8BED\u97F3",
                ex.Message,
                RequestId: null,
                TraceId: null);
        }
    }

    private static ProxyProbeScenarioResult CreateCapabilityMatrixUnconfiguredScenario(
        ProxyProbeScenarioKind scenario,
        string displayName,
        string summary)
        => CreateInformationalSupplementalScenario(
            scenario,
            displayName,
            "\u672A\u914D\u7F6E",
            summary,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

    private static string BuildCapabilityMatrixSummary(IReadOnlyList<ProxyProbeScenarioResult> scenarios)
        => BuildCapabilityMatrixSummary(scenarios, scenarios.Count);

    private static string BuildCapabilityMatrixSummary(
        IReadOnlyList<ProxyProbeScenarioResult> scenarios,
        int configuredCapabilityCount)
    {
        if (configuredCapabilityCount <= 0)
        {
            return "\u975E\u804A\u5929 API \u80FD\u529B\u77E9\u9635\uFF1A\u672C\u8F6E\u672A\u914D\u7F6E\u4EFB\u4F55\u80FD\u529B\u6A21\u578B\uFF0C\u5DF2\u8DF3\u8FC7\u3002";
        }

        var passedCount = scenarios.Count(static scenario => scenario.Success);
        var unconfiguredCount = scenarios.Count(static scenario =>
            string.Equals(scenario.CapabilityStatus, "\u672A\u914D\u7F6E", StringComparison.Ordinal));
        var failedCount = Math.Max(0, configuredCapabilityCount - passedCount - unconfiguredCount);
        return
            $"\u975E\u804A\u5929 API \u80FD\u529B\u77E9\u9635\uFF1A\u901A\u8FC7 {passedCount}/{configuredCapabilityCount}\uFF1B" +
            $"\u672A\u914D\u7F6E {unconfiguredCount} \u9879\uFF1B" +
            $"\u5176\u4F59 {failedCount} \u9879\u9700\u8981\u590D\u6838\u3002";
    }

    private static int CountConfiguredCapabilityModels(ProxyEndpointSettings settings)
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(settings.EmbeddingsModel))
        {
            count++;
        }

        if (!string.IsNullOrWhiteSpace(settings.ImagesModel))
        {
            count++;
        }

        if (!string.IsNullOrWhiteSpace(settings.AudioTranscriptionModel))
        {
            count++;
        }

        if (!string.IsNullOrWhiteSpace(settings.AudioSpeechModel))
        {
            count++;
        }

        if (!string.IsNullOrWhiteSpace(settings.ModerationModel))
        {
            count++;
        }

        return count;
    }

    private static string BuildEmbeddingsPayload(string model)
        => JsonSerializer.Serialize(new
        {
            model,
            input = "RelayBench embeddings capability probe"
        });

    private static string BuildImagesPayload(string model)
        => JsonSerializer.Serialize(new
        {
            model,
            prompt = "Generate a simple flat test image with a single colored square and no text.",
            size = "256x256"
        });

    private static string BuildModerationPayload(string model)
        => JsonSerializer.Serialize(new
        {
            model,
            input = "RelayBench moderation capability probe."
        });

    private static string BuildAudioSpeechPayload(string model)
        => JsonSerializer.Serialize(new
        {
            model,
            voice = "alloy",
            response_format = "mp3",
            input = "RelayBench audio speech probe."
        });

    private static MultipartFormDataContent BuildAudioTranscriptionContent(string model)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(BuildAudioProbeWavBytes());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "relaybench-probe.wav");
        content.Add(new StringContent(model, Encoding.UTF8), "model");
        content.Add(new StringContent("json", Encoding.UTF8), "response_format");
        return content;
    }

    private static byte[] BuildAudioProbeWavBytes()
    {
        const int sampleRate = 8000;
        const short bitsPerSample = 16;
        const short channels = 1;
        var samples = sampleRate / 2;
        var dataLength = samples * channels * (bitsPerSample / 8);
        var bytes = new byte[44 + dataLength];
        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (var index = 0; index < samples; index++)
        {
            var sample = (short)(Math.Sin((index / (double)sampleRate) * Math.PI * 2d * 440d) * short.MaxValue * 0.12d);
            writer.Write(sample);
        }

        writer.Flush();
        return bytes;
    }

    private static string? ParseEmbeddingsPreview(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            return $"dim={embedding.GetArrayLength()}";
        }

        return null;
    }

    private static string? ParseImagesPreview(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var base64Element) && base64Element.ValueKind == JsonValueKind.String)
            {
                var value = base64Element.GetString();
                return string.IsNullOrWhiteSpace(value)
                    ? "b64_json"
                    : $"b64_json ({value!.Length} chars)";
            }

            if (item.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String)
            {
                return urlElement.GetString();
            }
        }

        return null;
    }

    private static string? ParseModerationPreview(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in results.EnumerateArray())
        {
            if (item.TryGetProperty("flagged", out var flagged) &&
                (flagged.ValueKind == JsonValueKind.True || flagged.ValueKind == JsonValueKind.False))
            {
                return $"flagged={flagged.GetBoolean().ToString().ToLowerInvariant()}";
            }
        }

        return null;
    }

    private static string? ParseAudioTranscriptionPreview(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            var value = text.GetString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(value) ? "[empty transcript]" : value;
        }

        if (document.RootElement.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
        {
            return $"segments={segments.GetArrayLength()}";
        }

        return null;
    }

    private static string SafeDecodeUtf8(byte[] bytes)
    {
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
