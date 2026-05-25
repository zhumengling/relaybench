using RelayBench.Core.Models;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class SingleStationViewModel
{
    private ProxyEndpointSettings BuildSettings()
        => new(
            NormalizeEndpointUrl(BaseUrl),
            ApiKey.Trim(),
            Model.Trim(),
            IgnoreTlsErrors,
            Math.Clamp(TimeoutSeconds, 5, 120),
            NullIfWhiteSpace(CapabilityEmbeddingsModel),
            NullIfWhiteSpace(CapabilityImagesModel),
            NullIfWhiteSpace(CapabilityAudioTranscriptionModel),
            NullIfWhiteSpace(CapabilityAudioSpeechModel),
            NullIfWhiteSpace(CapabilityModerationModel));

    private string NormalizeEndpointUrl(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return ProtocolPrefix + trimmed;
    }

    private static string? NullIfWhiteSpace(string value)
    {
        var trimmed = value.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0d;
        }

        var sorted = values.OrderBy(static value => value).ToArray();
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var clamped = Math.Clamp(percentile, 0d, 1d);
        var position = (sorted.Length - 1) * clamped;
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
        {
            return sorted[lowerIndex];
        }

        var weight = position - lowerIndex;
        return sorted[lowerIndex] + ((sorted[upperIndex] - sorted[lowerIndex]) * weight);
    }
}
