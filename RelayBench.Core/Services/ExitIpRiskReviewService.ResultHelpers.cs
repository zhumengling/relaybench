using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class ExitIpRiskReviewService
{
    private static ExitIpRiskSourceResult BuildFailureSourceResult(string key, string displayName, Exception ex, string category = "风险源")
        => new(
            key,
            displayName,
            category,
            false,
            "失败",
            $"{displayName} 查询失败。",
            ex.Message,
            Error: ex.Message);

    internal static string BuildRiskVerdict(
        bool? isDatacenter,
        bool? isProxy,
        bool? isVpn,
        bool? isTor,
        bool? isAbuse,
        double? riskScore)
    {
        if (isTor == true || isAbuse == true || riskScore is >= 70d)
        {
            return "高风险";
        }

        if (isProxy == true || isVpn == true || isDatacenter == true || riskScore is >= 35d)
        {
            return "注意";
        }

        return "通过";
    }

    private static string? FormatAsn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.StartsWith("AS", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"AS{value}";
    }

    private static string FormatCoordinate(JsonElement root)
    {
        var latitude = GetDouble(root, "latitude");
        var longitude = GetDouble(root, "longitude");
        if (latitude is null || longitude is null)
        {
            return "--";
        }

        return $"{latitude.Value.ToString("F4", CultureInfo.InvariantCulture)}, {longitude.Value.ToString("F4", CultureInfo.InvariantCulture)}";
    }

    private static string FormatRiskScore(double? value)
        => value is null ? "--" : value.Value.ToString("F0", CultureInfo.InvariantCulture);

    private static string FormatYesNo(bool? value)
        => value switch
        {
            true => "是",
            false => "否",
            _ => "--"
        };

    private static bool IsYes(string? value)
        => string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static int? TryExtractAsnNumber(string? asnText)
    {
        if (string.IsNullOrWhiteSpace(asnText))
        {
            return null;
        }

        var digits = new string(asnText.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    internal static bool IsAddressInCidr(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var networkAddress) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefixLength))
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var networkBytes = networkAddress.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length)
        {
            return false;
        }

        var totalBits = addressBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > totalBits)
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var index = 0; index < fullBytes; index++)
        {
            if (addressBytes[index] != networkBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static string JoinNonEmpty(string separator, params string?[] values)
        => string.Join(separator, values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string? FirstNonEmpty(string? first, params string?[] rest)
        => new[] { first }
            .Concat(rest)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
}
