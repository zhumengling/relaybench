using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class PortScanDiagnosticsService
{
    private static HttpClient CreatePublicDnsHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelayBenchSuite/1.0 (Windows desktop diagnostics)");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private static string NormalizeCustomPortsText(string? customPortsText)
        => string.IsNullOrWhiteSpace(customPortsText)
            ? string.Empty
            : customPortsText.Trim();

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    private static bool IsLikelyDnsName(string target)
        => !IPAddress.TryParse(target, out _) && target.Contains('.', StringComparison.Ordinal);

    private static string BuildPseudoCommandLine(string target, PortScanProfile profile, string effectivePortsText)
        => $"builtin://port-scan --target {target} --ports {effectivePortsText} --timeout {profile.ConnectTimeoutMilliseconds} --concurrency {profile.MaxConcurrency} --protocols {(profile.EnableUdpProbe ? "tcp+udp" : "tcp")} --probes {profile.ProbeSummaryText.Replace(" / ", "+", StringComparison.Ordinal)}";

    private static string BuildRawOutput(
        string target,
        PortScanProfile profile,
        string customPortsText,
        string effectivePortsText,
        IReadOnlyList<string> resolvedAddresses,
        IReadOnlyList<string> systemResolvedAddresses,
        string resolutionSource,
        string resolutionSummary,
        IReadOnlyList<PortScanFinding> findings,
        int attemptedEndpointCount,
        string summary,
        IReadOnlyList<int> udpPorts)
    {
        StringBuilder builder = new();
        builder.AppendLine($"Engine: {EngineName} {EngineVersion}");
        builder.AppendLine($"Target: {target}");
        builder.AppendLine($"Resolution source: {resolutionSource}");
        builder.AppendLine($"Resolved addresses: {(resolvedAddresses.Count == 0 ? "none" : string.Join(", ", resolvedAddresses))}");
        builder.AppendLine($"System-resolved addresses: {(systemResolvedAddresses.Count == 0 ? "none" : string.Join(", ", systemResolvedAddresses))}");
        builder.AppendLine($"Resolution note: {resolutionSummary}");
        builder.AppendLine($"Profile: {profile.DisplayName} ({profile.Key})");
        builder.AppendLine($"Custom ports: {(string.IsNullOrWhiteSpace(customPortsText) ? "none" : customPortsText)}");
        builder.AppendLine($"Effective ports: {effectivePortsText}");
        builder.AppendLine($"UDP probe ports: {(udpPorts.Count == 0 ? "none" : string.Join(", ", udpPorts))}");
        builder.AppendLine($"Concurrency: {profile.MaxConcurrency}");
        builder.AppendLine($"Connect timeout: {profile.ConnectTimeoutMilliseconds} ms");
        builder.AppendLine($"Probes: {profile.ProbeSummaryText}");
        builder.AppendLine($"Attempted endpoints: {attemptedEndpointCount}");
        builder.AppendLine();

        if (findings.Count == 0)
        {
            builder.AppendLine("No open TCP/UDP endpoints detected.");
        }
        else
        {
            foreach (var finding in findings)
            {
                builder.Append($"OPEN {finding.Endpoint}/{finding.Protocol}");
                builder.Append($" latency={finding.ConnectLatencyMilliseconds}ms");
                builder.Append($" service={finding.ServiceHint}");

                if (!string.IsNullOrWhiteSpace(finding.Banner))
                {
                    builder.Append($" banner=\"{finding.Banner}\"");
                }

                if (!string.IsNullOrWhiteSpace(finding.TlsSummary))
                {
                    builder.Append($" tls=\"{finding.TlsSummary}\"");
                }

                if (!string.IsNullOrWhiteSpace(finding.HttpSummary))
                {
                    builder.Append($" app=\"{finding.HttpSummary}\"");
                }

                if (!string.IsNullOrWhiteSpace(finding.ProbeNotes))
                {
                    builder.Append($" notes=\"{finding.ProbeNotes}\"");
                }

                builder.AppendLine();
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Summary: {summary}");
        return builder.ToString();
    }

    private static string FormatEndpoint(string address, int port)
        => address.Contains(':', StringComparison.Ordinal) && !address.StartsWith("[", StringComparison.Ordinal)
            ? $"[{address}]:{port}"
            : $"{address}:{port}";

    private static string ShortenText(string value, int maxLength)
    {
        var normalized = SanitizeText(value);
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..(maxLength - 1)] + "…";
    }

    private static string SanitizeText(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (var character in value)
        {
            if (character == '\r' || character == '\n' || character == '\t' || !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder
            .ToString()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal)
            .Trim();
    }
}
