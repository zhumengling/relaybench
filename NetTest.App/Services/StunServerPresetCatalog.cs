using NetTest.App.Infrastructure;
using NetTest.Core.Models;

namespace NetTest.App.Services;

public sealed record StunServerPreset(string Host, StunTransportProtocol TransportProtocol);

public static class StunServerPresetCatalog
{
    private const string UdpKey = "udp";
    private const string TcpKey = "tcp";

    private static readonly StunServerPreset[] Presets =
    [
        new("fwa.lifesizecloud.com", StunTransportProtocol.Tcp),
        new("stun.isp.net.au", StunTransportProtocol.Tcp),
        new("stun.freeswitch.org", StunTransportProtocol.Tcp),
        new("stun.voip.blackberry.com", StunTransportProtocol.Tcp),
        new("stun.nextcloud.com", StunTransportProtocol.Tcp),
        new("stun.stunprotocol.org", StunTransportProtocol.Tcp),
        new("stun.sipnet.com", StunTransportProtocol.Tcp),
        new("stun.radiojar.com", StunTransportProtocol.Tcp),
        new("stun.sonetel.com", StunTransportProtocol.Tcp),
        new("stun.voipgate.com", StunTransportProtocol.Tcp),
        new("turn.cloudflare.com", StunTransportProtocol.Tcp),
        new("stun.miwifi.com", StunTransportProtocol.Udp),
        new("stun.chat.bilibili.com", StunTransportProtocol.Udp),
        new("stun.cloudflare.com", StunTransportProtocol.Udp)
    ];

    public static IReadOnlyList<SelectionOption> BuildTransportOptions()
        =>
        [
            new(UdpKey, "UDP"),
            new(TcpKey, "TCP")
        ];

    public static IReadOnlyList<SelectionOption> BuildServerOptions(StunTransportProtocol transportProtocol)
        => Presets
            .Where(item => item.TransportProtocol == transportProtocol)
            .Select(item => new SelectionOption(item.Host, item.Host))
            .ToArray();

    public static string ResolveTransportKey(string? transportKey, string? host)
    {
        var normalizedKey = NormalizeNullable(transportKey)?.ToLowerInvariant();
        if (normalizedKey is UdpKey or TcpKey)
        {
            return normalizedKey;
        }

        var preset = Presets.FirstOrDefault(item =>
            string.Equals(item.Host, NormalizeNullable(host), StringComparison.OrdinalIgnoreCase));
        return preset?.TransportProtocol == StunTransportProtocol.Tcp
            ? TcpKey
            : UdpKey;
    }

    public static StunTransportProtocol ParseTransportKey(string? transportKey)
        => string.Equals(ResolveTransportKey(transportKey, null), TcpKey, StringComparison.Ordinal)
            ? StunTransportProtocol.Tcp
            : StunTransportProtocol.Udp;

    public static string ResolveDefaultHost(StunTransportProtocol transportProtocol)
        => transportProtocol == StunTransportProtocol.Tcp
            ? "turn.cloudflare.com"
            : "stun.cloudflare.com";

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
