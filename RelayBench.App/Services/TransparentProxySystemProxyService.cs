using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Services;

namespace RelayBench.App.Services;

internal sealed class TransparentProxySystemProxyService
{
    private const string InternetSettingsRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string AutoConfigUrlName = "AutoConfigURL";
    private const string ProxyEnableName = "ProxyEnable";
    private const string ProxyServerName = "ProxyServer";
    private const string ProxyOverrideName = "ProxyOverride";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _snapshotPath;

    public TransparentProxySystemProxyService()
        : this(Path.Combine(RelayBenchPaths.ConfigDirectory, "transparent-proxy-system-proxy-snapshot.json"))
    {
    }

    internal TransparentProxySystemProxyService(string snapshotPath)
    {
        _snapshotPath = string.IsNullOrWhiteSpace(snapshotPath)
            ? Path.Combine(RelayBenchPaths.ConfigDirectory, "transparent-proxy-system-proxy-snapshot.json")
            : Path.GetFullPath(snapshotPath);
    }

    public TransparentProxySystemProxyInspection Inspect(string relayBenchPacUrl)
    {
        try
        {
            var snapshot = ReadCurrentSnapshot();
            var isRelayBenchPac = IsRelayBenchPacUrl(snapshot.AutoConfigUrl) ||
                                  string.Equals(snapshot.AutoConfigUrl, relayBenchPacUrl, StringComparison.OrdinalIgnoreCase);
            var proxyState = snapshot.ProxyEnable == 1
                ? $"手动代理开启：{RedactProxyServer(snapshot.ProxyServer)}"
                : "手动代理关闭";
            var pacState = string.IsNullOrWhiteSpace(snapshot.AutoConfigUrl)
                ? "PAC 未设置"
                : $"PAC={ProbeTraceRedactor.RedactUrl(snapshot.AutoConfigUrl)}";
            var summary = isRelayBenchPac
                ? $"系统代理：PAC 正指向 RelayBench；{proxyState}。"
                : $"系统代理：未由 RelayBench 接管；{pacState}，{proxyState}。";

            return new TransparentProxySystemProxyInspection(
                true,
                isRelayBenchPac,
                snapshot.AutoConfigUrl ?? string.Empty,
                snapshot.ProxyEnable == 1,
                RedactProxyServer(snapshot.ProxyServer),
                snapshot.ProxyOverride ?? string.Empty,
                summary);
        }
        catch (Exception ex)
        {
            return new TransparentProxySystemProxyInspection(
                false,
                false,
                string.Empty,
                false,
                string.Empty,
                string.Empty,
                $"系统代理状态读取失败：{ex.Message}");
        }
    }

    public TransparentProxySystemProxyMutationResult RestoreLatestSnapshot()
    {
        try
        {
            var snapshot = ReadSavedSnapshot();
            if (snapshot is null)
            {
                return new TransparentProxySystemProxyMutationResult(false, "未发现系统代理备份，跳过系统代理恢复。", string.Empty);
            }

            var current = ReadCurrentSnapshot();
            if (!ShouldRestoreSnapshot(current, snapshot))
            {
                return new TransparentProxySystemProxyMutationResult(
                    false,
                    "系统代理当前未指向 RelayBench PAC，为避免覆盖用户后续改动，已跳过自动恢复。",
                    _snapshotPath);
            }

            using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsRegistryPath, writable: true);
            if (key is null)
            {
                return new TransparentProxySystemProxyMutationResult(false, "系统代理恢复失败：无法打开 Internet Settings 注册表。", _snapshotPath);
            }

            RestoreRegistryValue(key, AutoConfigUrlName, snapshot.AutoConfigUrl, RegistryValueKind.String);
            RestoreRegistryValue(key, ProxyEnableName, snapshot.ProxyEnable, RegistryValueKind.DWord);
            RestoreRegistryValue(key, ProxyServerName, snapshot.ProxyServer, RegistryValueKind.String);
            RestoreRegistryValue(key, ProxyOverrideName, snapshot.ProxyOverride, RegistryValueKind.String);
            NotifyProxySettingsChanged();
            TryDeleteSnapshot();

            return new TransparentProxySystemProxyMutationResult(true, "系统代理已恢复到写入 RelayBench PAC 之前的状态。", _snapshotPath);
        }
        catch (Exception ex)
        {
            return new TransparentProxySystemProxyMutationResult(false, $"系统代理恢复失败：{ex.Message}", _snapshotPath);
        }
    }

    internal static bool IsRelayBenchPacUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.EndsWith("/relaybench/pac", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldRestoreSnapshot(
        TransparentProxySystemProxySnapshot current,
        TransparentProxySystemProxySnapshot saved)
    {
        if (string.IsNullOrWhiteSpace(saved.AppliedPacUrl))
        {
            return false;
        }

        return string.Equals(current.AutoConfigUrl, saved.AppliedPacUrl, StringComparison.OrdinalIgnoreCase) ||
               IsRelayBenchPacUrl(current.AutoConfigUrl);
    }

    private static void RestoreRegistryValue(RegistryKey key, string name, string? value, RegistryValueKind kind)
    {
        if (string.IsNullOrEmpty(value))
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        key.SetValue(name, value, kind);
    }

    private static void RestoreRegistryValue(RegistryKey key, string name, int? value, RegistryValueKind kind)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        key.SetValue(name, value.Value, kind);
    }

    private static string RedactProxyServer(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : ProbeTraceRedactor.RedactUrl(value);

    private TransparentProxySystemProxySnapshot ReadCurrentSnapshot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsRegistryPath, writable: false);
        return new TransparentProxySystemProxySnapshot
        {
            AutoConfigUrl = key?.GetValue(AutoConfigUrlName) as string,
            ProxyEnable = key?.GetValue(ProxyEnableName) is int proxyEnable ? proxyEnable : null,
            ProxyServer = key?.GetValue(ProxyServerName) as string,
            ProxyOverride = key?.GetValue(ProxyOverrideName) as string
        };
    }

    private TransparentProxySystemProxySnapshot? ReadSavedSnapshot()
    {
        if (!File.Exists(_snapshotPath))
        {
            return null;
        }

        var json = File.ReadAllText(_snapshotPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<TransparentProxySystemProxySnapshot>(json, JsonOptions);
    }

    private void WriteSnapshot(TransparentProxySystemProxySnapshot snapshot)
    {
        var directory = Path.GetDirectoryName(_snapshotPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(_snapshotPath, json, new UTF8Encoding(false));
    }

    private void TryDeleteSnapshot()
    {
        try
        {
            if (File.Exists(_snapshotPath))
            {
                File.Delete(_snapshotPath);
            }
        }
        catch
        {
        }
    }

    private static void NotifyProxySettingsChanged()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        catch
        {
        }
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}

internal sealed record TransparentProxySystemProxyInspection(
    bool CanInspect,
    bool IsRelayBenchPacActive,
    string AutoConfigUrl,
    bool ProxyEnabled,
    string ProxyServer,
    string ProxyOverride,
    string Summary);

internal sealed record TransparentProxySystemProxyMutationResult(
    bool Succeeded,
    string Summary,
    string BackupPath);

internal sealed class TransparentProxySystemProxySnapshot
{
    public DateTimeOffset CreatedAt { get; set; }

    public string? AutoConfigUrl { get; set; }

    public int? ProxyEnable { get; set; }

    public string? ProxyServer { get; set; }

    public string? ProxyOverride { get; set; }

    public string AppliedPacUrl { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
}
