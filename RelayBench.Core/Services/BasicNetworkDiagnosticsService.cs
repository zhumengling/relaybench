using System.Net.NetworkInformation;
using System.Net.Sockets;
using RelayBench.Core.Models;
using RelayBench.Core.Support;

namespace RelayBench.Core.Services;

public sealed class BasicNetworkDiagnosticsService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    public async Task<NetworkSnapshot> RunAsync(CancellationToken cancellationToken = default)
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsUsefulAdapter)
            .Select(adapter => new NetworkAdapterInfo(
                adapter.Name,
                adapter.Description,
                adapter.NetworkInterfaceType.ToString(),
                adapter.Supports(NetworkInterfaceComponent.IPv4),
                adapter.Supports(NetworkInterfaceComponent.IPv6),
                adapter.GetIPProperties().UnicastAddresses
                    .Select(address => address.Address.ToString())
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray(),
                adapter.GetIPProperties().DnsAddresses
                    .Select(address => address.ToString())
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray()))
            .ToArray();

        var traceValues = await TryGetCloudflareTraceValuesAsync(cancellationToken);
        var pingTargets = new[] { "1.1.1.1", "8.8.8.8", "chatgpt.com" };
        var pingChecks = new List<PingCheck>(pingTargets.Length);

        foreach (var target in pingTargets)
        {
            pingChecks.Add(await PingAsync(target));
        }

        return new NetworkSnapshot(
            DateTimeOffset.Now,
            Environment.MachineName,
            traceValues.TryGetValue("ip", out var publicIp) ? publicIp : null,
            traceValues.TryGetValue("colo", out var colo) ? colo : null,
            adapters,
            pingChecks);
    }

    private static bool IsUsefulAdapter(NetworkInterface adapter)
    {
        if (adapter.OperationalStatus != OperationalStatus.Up)
        {
            return false;
        }

        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return false;
        }

        return adapter.GetIPProperties().UnicastAddresses.Count > 0;
    }

    private static async Task<PingCheck> PingAsync(string target)
    {
        using var ping = new Ping();

        try
        {
            var reply = await ping.SendPingAsync(target, 1500);
            return new PingCheck(
                target,
                reply.Status.ToString(),
                reply.Status == IPStatus.Success ? reply.RoundtripTime : null,
                reply.Address?.ToString(),
                reply.Status == IPStatus.Success ? null : $"Ping 状态：{TranslatePingStatus(reply.Status)}");
        }
        catch (PingException ex)
        {
            return new PingCheck(target, "Error", null, null, ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            return new PingCheck(target, "Error", null, null, ex.Message);
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> TryGetCloudflareTraceValuesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync("https://www.cloudflare.com/cdn-cgi/trace", cancellationToken);
            response.EnsureSuccessStatusCode();
            var rawText = await response.Content.ReadAsStringAsync(cancellationToken);
            return TraceDocumentParser.Parse(rawText);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    private static string TranslatePingStatus(IPStatus status)
        => status switch
        {
            IPStatus.Success => "成功",
            IPStatus.TimedOut => "超时",
            IPStatus.DestinationHostUnreachable => "目标主机不可达",
            IPStatus.DestinationNetworkUnreachable => "目标网络不可达",
            IPStatus.DestinationPortUnreachable => "目标端口不可达",
            IPStatus.DestinationProtocolUnreachable => "目标协议不可达",
            IPStatus.BadRoute => "路由异常",
            IPStatus.TtlExpired => "TTL 已过期",
            IPStatus.TimeExceeded => "超出时间限制",
            IPStatus.PacketTooBig => "数据包过大",
            _ => status.ToString()
        };
}
