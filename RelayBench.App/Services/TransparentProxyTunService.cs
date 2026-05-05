using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RelayBench.App.Infrastructure;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyTunService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SessionJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _dataDirectory;
    private Process? _process;

    public TransparentProxyTunService()
        : this(RelayBenchPaths.DataDirectory)
    {
    }

    internal TransparentProxyTunService(string dataDirectory)
    {
        _dataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? RelayBenchPaths.DataDirectory
            : Path.GetFullPath(dataDirectory);
    }

    public bool IsRunning => IsProcessRunning(_process);

    public string? CurrentConfigPath { get; private set; }

    public string? CurrentSidecarPath { get; private set; }

    internal string ResidualSessionPath
        => Path.Combine(GetWorkDirectory(), "relaybench-tun-session.json");

    public string BuildMihomoConfig(TransparentProxyTunConfigOptions options)
    {
        var unifiedPort = NormalizePort(options.UnifiedEndpointPort, 17880);
        var forwardProxyPort = NormalizePort(options.ForwardProxyPort, 17881);
        var mixedPort = NormalizePort(options.MixedPort, 17882);
        var controllerPort = NormalizePort(options.ControllerPort, 17883);
        var dnsPort = NormalizePort(options.DnsPort, 17884);

        return $$"""
            # RelayBench generated mihomo TUN config.
            # This advanced mode is tunnel-only: HTTPS payloads stay encrypted unless a future TLS inspection mode is explicitly enabled.
            # Local agents should prefer the unified endpoint: http://127.0.0.1:{{unifiedPort}}/v1

            mixed-port: {{mixedPort.ToString(CultureInfo.InvariantCulture)}}
            allow-lan: false
            bind-address: 127.0.0.1
            mode: rule
            log-level: warning
            ipv6: false
            external-controller: 127.0.0.1:{{controllerPort.ToString(CultureInfo.InvariantCulture)}}

            dns:
              enable: true
              listen: 127.0.0.1:{{dnsPort.ToString(CultureInfo.InvariantCulture)}}
              enhanced-mode: fake-ip
              fake-ip-range: 198.18.0.1/16
              nameserver:
                - system

            tun:
              enable: true
              stack: mixed
              auto-route: true
              auto-detect-interface: true
              strict-route: true
              dns-hijack:
                - any:53
                - tcp://any:53
              route-exclude-address:
                - 127.0.0.0/8
                - 10.0.0.0/8
                - 172.16.0.0/12
                - 192.168.0.0/16

            proxies:
              - name: RelayBench
                type: http
                server: 127.0.0.1
                port: {{forwardProxyPort.ToString(CultureInfo.InvariantCulture)}}
                skip-cert-verify: true

            rules:
              - DOMAIN,localhost,DIRECT
              - IP-CIDR,127.0.0.0/8,DIRECT,no-resolve
              - IP-CIDR,10.0.0.0/8,DIRECT,no-resolve
              - IP-CIDR,172.16.0.0/12,DIRECT,no-resolve
              - IP-CIDR,192.168.0.0/16,DIRECT,no-resolve
              - DOMAIN,github.com,DIRECT
              - DOMAIN,api.github.com,DIRECT
              - DOMAIN,raw.githubusercontent.com,DIRECT
              - DOMAIN,objects.githubusercontent.com,DIRECT
              - DOMAIN-SUFFIX,github.com,DIRECT
              - DOMAIN-SUFFIX,githubusercontent.com,DIRECT
              - DOMAIN,registry.npmjs.org,DIRECT
              - DOMAIN-SUFFIX,npmjs.org,DIRECT
              - DOMAIN-SUFFIX,npmjs.com,DIRECT
              - DOMAIN,marketplace.visualstudio.com,DIRECT
              - DOMAIN-SUFFIX,gallerycdn.vsassets.io,DIRECT
              - DOMAIN-SUFFIX,vo.msecnd.net,DIRECT
              - DOMAIN,openai.com,DIRECT
              - DOMAIN,www.openai.com,DIRECT
              - DOMAIN,chat.openai.com,DIRECT
              - DOMAIN,auth.openai.com,DIRECT
              - DOMAIN,platform.openai.com,DIRECT
              - DOMAIN,chatgpt.com,DIRECT
              - DOMAIN-SUFFIX,chatgpt.com,DIRECT
              - DOMAIN-SUFFIX,oaistatic.com,DIRECT
              - DOMAIN-SUFFIX,oaiusercontent.com,DIRECT
              - DOMAIN,api.openai.com,RelayBench
              - DOMAIN-SUFFIX,api.openai.com,RelayBench
              - DOMAIN,api.anthropic.com,RelayBench
              - DOMAIN-SUFFIX,api.anthropic.com,RelayBench
              - MATCH,DIRECT
            """;
    }

    public async Task<TransparentProxyTunStartResult> StartAsync(
        string sidecarPath,
        TransparentProxyTunConfigOptions options,
        CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return new TransparentProxyTunStartResult(true, "TUN sidecar 已在运行。", CurrentConfigPath ?? string.Empty, CurrentSidecarPath ?? sidecarPath);
        }

        if (string.IsNullOrWhiteSpace(sidecarPath) || !File.Exists(sidecarPath))
        {
            return new TransparentProxyTunStartResult(false, "未找到 mihomo sidecar，已保持系统网络不变。", string.Empty, sidecarPath);
        }

        var workDirectory = GetWorkDirectory();
        Directory.CreateDirectory(workDirectory);
        var configPath = Path.Combine(workDirectory, "mihomo-relaybench.yaml");
        await File.WriteAllTextAsync(configPath, BuildMihomoConfig(options), new UTF8Encoding(false), cancellationToken);

        ProcessStartInfo startInfo = new()
        {
            FileName = sidecarPath,
            Arguments = $"-f \"{configPath}\" -d \"{workDirectory}\"",
            WorkingDirectory = workDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo);
        if (process is null)
        {
            return new TransparentProxyTunStartResult(false, "mihomo sidecar 启动失败，未返回进程句柄。", configPath, sidecarPath);
        }

        _process = process;
        CurrentConfigPath = configPath;
        CurrentSidecarPath = sidecarPath;
        WriteSession(new TransparentProxyTunSessionSnapshot
        {
            ProcessId = process.Id,
            StartedAt = DateTimeOffset.Now,
            SidecarPath = sidecarPath,
            ConfigPath = configPath,
            MixedPort = options.MixedPort,
            ControllerPort = options.ControllerPort,
            DnsPort = options.DnsPort,
            ForwardProxyPort = options.ForwardProxyPort
        });

        await Task.Delay(700, cancellationToken);
        if (!IsRunning)
        {
            DeleteSessionFile();
            return new TransparentProxyTunStartResult(false, "mihomo sidecar 已退出，请检查配置预览和权限。", configPath, sidecarPath);
        }

        return new TransparentProxyTunStartResult(true, $"TUN 已启动：{Path.GetFileName(sidecarPath)}", configPath, sidecarPath);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            await StopResidualSessionAsync(cancellationToken);
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
            CurrentConfigPath = null;
            CurrentSidecarPath = null;
            DeleteSessionFile();
        }
    }

    public TransparentProxyTunResidualSession InspectResidualSession()
    {
        var snapshot = ReadSession();
        if (snapshot is null)
        {
            return new TransparentProxyTunResidualSession(
                false,
                false,
                null,
                string.Empty,
                string.Empty,
                null,
                "未发现上次 TUN 会话残留。");
        }

        var isRunning = IsProcessRunning(snapshot.ProcessId, snapshot.SidecarPath);
        var summary = isRunning
            ? $"发现上次 TUN sidecar 可能仍在运行：PID {snapshot.ProcessId}，配置 {snapshot.ConfigPath}"
            : $"发现上次 TUN 会话记录未清理：PID {snapshot.ProcessId} 已不在运行。";
        return new TransparentProxyTunResidualSession(
            true,
            isRunning,
            snapshot.ProcessId,
            snapshot.ConfigPath ?? string.Empty,
            snapshot.SidecarPath ?? string.Empty,
            snapshot.StartedAt,
            summary);
    }

    public async Task<TransparentProxyTunResidualCleanupResult> StopResidualSessionAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = ReadSession();
        if (snapshot is null)
        {
            return new TransparentProxyTunResidualCleanupResult(false, "未发现 TUN 残留会话。");
        }

        var process = TryGetProcess(snapshot.ProcessId);
        if (process is not null && IsConfirmedSidecarProcess(process, snapshot.SidecarPath))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                return new TransparentProxyTunResidualCleanupResult(
                    false,
                    $"发现 TUN 残留 PID {snapshot.ProcessId}，但停止失败：{ex.Message}");
            }
            finally
            {
                process.Dispose();
            }

            DeleteSessionFile();
            return new TransparentProxyTunResidualCleanupResult(
                true,
                $"已停止上次残留的 TUN sidecar：PID {snapshot.ProcessId}");
        }

        process?.Dispose();
        if (process is null)
        {
            DeleteSessionFile();
            return new TransparentProxyTunResidualCleanupResult(
                true,
                $"已清理过期 TUN 会话记录：PID {snapshot.ProcessId} 已不在运行。");
        }

        return new TransparentProxyTunResidualCleanupResult(
            false,
            $"发现 PID {snapshot.ProcessId} 仍在运行，但无法确认是 RelayBench 启动的 mihomo，已保留会话记录以避免误杀。");
    }

    public async ValueTask DisposeAsync()
        => await StopAsync();

    private string GetWorkDirectory()
        => Path.Combine(_dataDirectory, "transparent-proxy-tun");

    private static int NormalizePort(int port, int fallback)
        => port is >= 1 and <= 65535 ? port : fallback;

    private static bool IsProcessRunning(Process? process)
    {
        if (process is null)
        {
            return false;
        }

        try
        {
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessRunning(int processId, string? sidecarPath)
    {
        var process = TryGetProcess(processId);
        if (process is null)
        {
            return false;
        }

        using (process)
        {
            return IsConfirmedSidecarProcess(process, sidecarPath);
        }
    }

    private static Process? TryGetProcess(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return process.HasExited ? null : process;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsConfirmedSidecarProcess(Process process, string? sidecarPath)
    {
        if (process.HasExited)
        {
            return false;
        }

        var expectedName = Path.GetFileNameWithoutExtension(sidecarPath ?? string.Empty);
        try
        {
            var actualPath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(actualPath) &&
                !string.IsNullOrWhiteSpace(sidecarPath) &&
                string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(sidecarPath), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
        }

        return !string.IsNullOrWhiteSpace(expectedName) &&
               string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase);
    }

    private TransparentProxyTunSessionSnapshot? ReadSession()
    {
        try
        {
            var path = ResidualSessionPath;
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<TransparentProxyTunSessionSnapshot>(json, SessionJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void WriteSession(TransparentProxyTunSessionSnapshot snapshot)
    {
        try
        {
            var path = ResidualSessionPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(snapshot, SessionJsonOptions);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private void DeleteSessionFile()
    {
        try
        {
            var path = ResidualSessionPath;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

internal sealed record TransparentProxyTunConfigOptions(
    int UnifiedEndpointPort,
    int ForwardProxyPort,
    int MixedPort,
    int ControllerPort,
    int DnsPort);

internal sealed record TransparentProxyTunStartResult(
    bool Started,
    string StatusText,
    string ConfigPath,
    string SidecarPath);

internal sealed record TransparentProxyTunResidualSession(
    bool HasSession,
    bool IsProcessRunning,
    int? ProcessId,
    string ConfigPath,
    string SidecarPath,
    DateTimeOffset? StartedAt,
    string Summary);

internal sealed record TransparentProxyTunResidualCleanupResult(
    bool Cleared,
    string Summary);

internal sealed class TransparentProxyTunSessionSnapshot
{
    public int ProcessId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public string SidecarPath { get; set; } = string.Empty;

    public string ConfigPath { get; set; } = string.Empty;

    public int MixedPort { get; set; }

    public int ControllerPort { get; set; }

    public int DnsPort { get; set; }

    public int ForwardProxyPort { get; set; }
}
