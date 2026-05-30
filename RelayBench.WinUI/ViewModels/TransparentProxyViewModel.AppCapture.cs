using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using Microsoft.UI.Xaml;
using RelayBench.Core.Models;
using RelayBench.Services.Infrastructure;
using RelayBench.Services;
using RelayBench.Core.Services;
using RelayBench.WinUI.Charts;
using RelayBench.WinUI.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace RelayBench.WinUI.ViewModels;

public sealed partial class TransparentProxyViewModel
{    // Phase 6: Detect Applications
    [RelayCommand]
    private void DetectApps()
    {
        DetectedApps.Clear();
        try
        {
            var port = ParseListenPort();
            var apps = _appDetector.Detect();
            foreach (var app in apps)
            {
                var (isTakeoverEnabled, hasLiveBackup, backupDisplay) = ResolveCaptureState(app.ConfigPath, port);
                DetectedApps.Add(new DetectedAppInfo(
                    app.Id,
                    app.DisplayName,
                    app.ExecutablePath ?? "--",
                    app.IsDetected,
                    app.RecommendedMode,
                    app.Status,
                    isTakeoverEnabled,
                    hasLiveBackup,
                    backupDisplay));
            }

            var detectedCount = DetectedApps.Count(static app => app.IsConfigured);
            AppCaptureStatusText = $"\u68C0\u6D4B\u5230 {detectedCount}/{DetectedApps.Count} \u4E2A\u5E94\u7528";
            // Also build CLI environment snippets
            var env = _cliEnvironmentService.Build(port);
            CliEnvPowerShellText = env.PowerShell;
            CliEnvCmdText = env.Cmd;
            RefreshPolicyStatus();
        }
        catch (Exception ex)
        {
            StatusText = $"应用检测错误: {ex.Message}";
        }
    }

    private async Task PersistDiscoveredRouteProtocolsAsync(IReadOnlyList<TransparentProxyRoute> hydratedRoutes)
    {
        foreach (var hydrated in hydratedRoutes)
        {
            if (string.IsNullOrWhiteSpace(hydrated.PreferredWireApi) ||
                hydrated.Id.StartsWith("codex-oauth-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var index = -1;
            for (var i = 0; i < Routes.Count; i++)
            {
                if (string.Equals(Routes[i].Id, hydrated.Id, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                continue;
            }

            var current = Routes[index];
            if (string.Equals(current.PreferredWireApi, hydrated.PreferredWireApi, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var updated = current with
            {
                PreferredWireApi = hydrated.PreferredWireApi,
                UpdatedAtUtc = DateTime.UtcNow
            };
            Routes[index] = updated;
            if (_routeRepository is not null)
            {
                await _routeRepository.UpsertAsync(updated);
            }
        }
    }

    private static (bool IsTakeoverEnabled, bool HasLiveBackup, string BackupDisplay) ResolveCaptureState(
        string? configPath,
        int listenPort)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return (false, false, "--");
        }

        var hasLiveBackup = TryFindLatestCaptureBackup(configPath, out var latestBackupPath, out var latestBackupAt);
        var backupDisplay = hasLiveBackup && latestBackupAt is not null
            ? latestBackupAt.Value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "--";

        if (!System.IO.File.Exists(configPath))
        {
            return (false, hasLiveBackup, backupDisplay);
        }

        try
        {
            var content = System.IO.File.ReadAllText(configPath);
            var localPort = listenPort.ToString(CultureInfo.InvariantCulture);
            var takeoverEnabled =
                content.Contains($"127.0.0.1:{localPort}", StringComparison.OrdinalIgnoreCase) ||
                content.Contains($"localhost:{localPort}", StringComparison.OrdinalIgnoreCase) ||
                content.Contains($"http://127.0.0.1:{localPort}", StringComparison.OrdinalIgnoreCase) ||
                content.Contains($"http://localhost:{localPort}", StringComparison.OrdinalIgnoreCase);
            return (takeoverEnabled, hasLiveBackup, backupDisplay);
        }
        catch
        {
            return (false, hasLiveBackup, backupDisplay);
        }
    }

    private static bool TryFindLatestCaptureBackup(
        string configPath,
        out string? latestBackupPath,
        out DateTimeOffset? latestBackupAt)
    {
        latestBackupPath = null;
        latestBackupAt = null;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(configPath);
            var fileName = System.IO.Path.GetFileName(configPath);
            if (string.IsNullOrWhiteSpace(directory) ||
                string.IsNullOrWhiteSpace(fileName) ||
                !System.IO.Directory.Exists(directory))
            {
                return false;
            }

            latestBackupPath = System.IO.Directory
                .GetFiles(directory, $"{fileName}.relaybench-app-capture-backup-*")
                .OrderByDescending(static item => item, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (latestBackupPath is null)
            {
                return false;
            }

            latestBackupAt = System.IO.File.GetLastWriteTime(latestBackupPath);
            return true;
        }
        catch
        {
            latestBackupPath = null;
            latestBackupAt = null;
            return false;
        }
    }

    // Phase 6.3: Preview/Apply/Restore per detected application
    [RelayCommand]
    private async Task PreviewAppCaptureAsync(DetectedAppInfo? app)
    {
        if (app is null) return;
        var port = ParseListenPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var (model, wireApi) = ResolveAppCaptureEndpoint();
        try
        {
            var previewText = app.Id switch
            {
                "codex-cli" or "codex-desktop" => _codexConfigService.Preview(baseUrl, model, wireApi).PreviewText,
                "claude-cli" => _claudeConfigService.Preview(baseUrl, model).PreviewText,
                "vs-codex" => _vsCodeSettingsService.Preview(baseUrl).PreviewText,
                "antigravity" => BuildAntigravityCapturePreview(baseUrl, model),
                _ => $"\u6682\u4E0D\u652F\u6301\u9884\u89C8 {app.Name} \u7684\u914D\u7F6E"
            };
            AppCaptureStatusText = previewText;
        }
        catch (Exception ex)
        {
            AppCaptureStatusText = $"\u9884\u89C8\u5931\u8D25: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ApplyAppCaptureAsync(DetectedAppInfo? app)
    {
        if (app is null) return;
        var port = ParseListenPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var (model, wireApi) = ResolveAppCaptureEndpoint();
        try
        {
            var result = app.Id switch
            {
                "codex-cli" or "codex-desktop" => await Task.Run(() =>
                {
                    var r = _codexConfigService.Apply(baseUrl, model, wireApi);
                    return (Succeeded: r.Succeeded, Summary: r.Summary);
                }),
                "claude-cli" => await Task.Run(() =>
                {
                    var r = _claudeConfigService.Apply(baseUrl, model);
                    return (Succeeded: r.Succeeded, Summary: r.Summary);
                }),
                "vs-codex" => await Task.Run(() =>
                {
                    var r = _vsCodeSettingsService.Apply(baseUrl);
                    return (Succeeded: r.Succeeded, Summary: r.Summary);
                }),
                "antigravity" => await ApplyAntigravityCaptureAsync(baseUrl, model, wireApi),
                _ => (Succeeded: false, Summary: $"\u6682\u4E0D\u652F\u6301\u63A5\u7BA1 {app.Name}")
            };

            AppCaptureStatusText = result.Succeeded
                ? $"\u2705 {result.Summary}"
                : $"\u274C {result.Summary}";
            var statusText = AppCaptureStatusText;
            DetectApps();
            AppCaptureStatusText = statusText;
        }
        catch (Exception ex)
        {
            AppCaptureStatusText = $"\u63A5\u7BA1\u5931\u8D25: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RestoreAppCaptureAsync(DetectedAppInfo? app)
    {
        if (app is null) return;
        try
        {
            var result = app.Id switch
            {
                "codex-cli" or "codex-desktop" => await Task.Run(() =>
                {
                    var r = _codexConfigService.RestoreLatestBackup();
                    return (Succeeded: r.Succeeded, Summary: r.Summary);
                }),
                "claude-cli" => await Task.Run(() =>
                {
                    var r = _claudeConfigService.RestoreLatestBackup();
                    return (Succeeded: r.Succeeded, Summary: r.Summary);
                }),
                "vs-codex" => await Task.Run(() =>
                {
                    var r = _vsCodeSettingsService.RestoreLatestBackups();
                    return (Succeeded: r.Succeeded, Summary: r.Summary);
                }),
                "antigravity" => await Task.Run(async () =>
                {
                    var r = await _clientApiConfigRestoreService.RestoreAsync("Antigravity");
                    return (r.Succeeded, r.Summary);
                }),
                _ => (Succeeded: false, Summary: $"\u6682\u4E0D\u652F\u6301\u6062\u590D {app.Name}")
            };

            AppCaptureStatusText = result.Succeeded
                ? $"\u2705 {result.Summary}"
                : $"\u274C {result.Summary}";
            var statusText = AppCaptureStatusText;
            DetectApps();
            AppCaptureStatusText = statusText;
        }
        catch (Exception ex)
        {
            AppCaptureStatusText = $"\u6062\u590D\u5931\u8D25: {ex.Message}";
        }
    }

    private static string BuildAntigravityCapturePreview(string baseUrl, string model)
        => string.Join(
            Environment.NewLine,
            [
                "目标文件：%USERPROFILE%\\.gemini\\.env + %USERPROFILE%\\.gemini\\settings.json",
                "动作：写入 Gemini 环境变量，并将 Antigravity / Gemini 认证方式切到 gemini-api-key。",
                $"GOOGLE_GEMINI_BASE_URL = {baseUrl}",
                "GEMINI_API_KEY = relaybench-local",
                $"GEMINI_MODEL = {model}"
            ]);

    private async Task<(bool Succeeded, string Summary)> ApplyAntigravityCaptureAsync(
        string baseUrl,
        string model,
        string wireApi)
    {
        var result = await _clientAppConfigApplyService.ApplyAsync(
            new ClientApplyEndpoint(
                baseUrl,
                "relaybench-local",
                model,
                "RelayBench",
                ContextWindow: null,
                PreferredWireApi: wireApi),
            [
                new ClientApplyTargetSelection(
                    "antigravity",
                    ClientApplyProtocolKind.Gemini)
            ]);
        return (result.Succeeded, result.Summary);
    }

    // Phase 6.4: Launch Wrapper Script Generation
    [RelayCommand]
    private void GenerateLaunchWrapper(DetectedAppInfo? app)
    {
        if (app is null) return;
        var (id, displayName, command) = ResolveLaunchWrapperTarget(app);
        WriteTransparentProxyLauncher(id, displayName, command);
    }

    [RelayCommand]
    private void PreviewTransparentProxyCodexLauncher()
        => PreviewTransparentProxyLauncher("codex-cli", "Codex CLI", "codex");

    [RelayCommand]
    private void WriteTransparentProxyCodexLauncher()
        => WriteTransparentProxyLauncher("codex-cli", "Codex CLI", "codex");

    [RelayCommand]
    private void PreviewTransparentProxyClaudeLauncher()
        => PreviewTransparentProxyLauncher("claude-cli", "Claude CLI", "claude");

    [RelayCommand]
    private void WriteTransparentProxyClaudeLauncher()
        => WriteTransparentProxyLauncher("claude-cli", "Claude CLI", "claude");

    private void PreviewTransparentProxyLauncher(string id, string displayName, string command)
    {
        try
        {
            var preview = _launchWrapperService.Preview(id, displayName, command, ParseListenPort());
            ApplyTransparentProxyLaunchWrapperPreview(preview);
            LaunchWrapperStatusText = $"{preview.DisplayName} 启动器预览已生成；预览不会写入文件。";
            StatusText = LaunchWrapperStatusText;
        }
        catch (Exception ex)
        {
            LaunchWrapperStatusText = $"启动器预览失败: {ex.Message}";
            StatusText = LaunchWrapperStatusText;
        }
    }

    private void WriteTransparentProxyLauncher(string id, string displayName, string command)
    {
        try
        {
            var result = _launchWrapperService.Write(id, displayName, command, ParseListenPort());
            var preview = _launchWrapperService.Preview(id, displayName, command, ParseListenPort());
            ApplyTransparentProxyLaunchWrapperPreview(preview);
            if (result.Succeeded)
            {
                LaunchWrapperStatusText = result.Summary;
                LaunchWrapperPathText = $"PS: {result.PowerShellPath}\nCMD: {result.CmdPath}";
                StatusText = result.Summary;
            }
            else
            {
                LaunchWrapperStatusText = "\u542F\u52A8\u5668\u751F\u6210\u5931\u8D25";
                StatusText = LaunchWrapperStatusText;
            }
        }
        catch (Exception ex)
        {
            LaunchWrapperStatusText = $"\u751F\u6210\u5931\u8D25: {ex.Message}";
            StatusText = LaunchWrapperStatusText;
        }
    }

    private static (string Id, string DisplayName, string Command) ResolveLaunchWrapperTarget(DetectedAppInfo app)
    {
        var command = app.Id switch
        {
            "codex-cli" or "codex-desktop" => "codex",
            "claude-cli" => "claude",
            _ => app.Name.ToLowerInvariant().Replace(" ", "-")
        };
        return (app.Id, app.Name, command);
    }

    private void ApplyTransparentProxyLaunchWrapperPreview(TransparentProxyLaunchWrapperPreview preview)
    {
        LaunchWrapperPathText = $"PS: {preview.PowerShellPath}\nCMD: {preview.CmdPath}";
        LaunchWrapperPreviewText = string.Join(
            Environment.NewLine,
            [
                $"# {preview.DisplayName} PowerShell",
                preview.PowerShellScript.TrimEnd(),
                string.Empty,
                $"# {preview.DisplayName} CMD",
                preview.CmdScript.TrimEnd()
            ]);
    }

    // Phase 6.5: CLI Environment Variable Copy Commands
    [RelayCommand]
    private void CopyPowerShellEnv()
    {
        var port = ParseListenPort();
        var env = _cliEnvironmentService.Build(port);
        CliEnvPowerShellText = env.PowerShell;
        var dataPackage = new DataPackage();
        dataPackage.SetText(env.PowerShell);
        Clipboard.SetContent(dataPackage);
        AppCaptureStatusText = "\u5DF2\u590D\u5236 PowerShell \u73AF\u5883\u53D8\u91CF\u5230\u526A\u8D34\u677F";
    }

    [RelayCommand]
    private void CopyTransparentProxyPowerShellEnv()
        => CopyPowerShellEnv();

    [RelayCommand]
    private void CopyCmdEnv()
    {
        var port = ParseListenPort();
        var env = _cliEnvironmentService.Build(port);
        CliEnvCmdText = env.Cmd;
        var dataPackage = new DataPackage();
        dataPackage.SetText(env.Cmd);
        Clipboard.SetContent(dataPackage);
        AppCaptureStatusText = "\u5DF2\u590D\u5236 CMD \u73AF\u5883\u53D8\u91CF\u5230\u526A\u8D34\u677F";
    }

    [RelayCommand]
    private void CopyTransparentProxyCmdEnv()
        => CopyCmdEnv();

    [RelayCommand]
    private void CopyTransparentProxyEndpoint()
    {
        var endpoint = _cliEnvironmentService.Build(ParseListenPort()).OpenAiBaseUrl;
        var dataPackage = new DataPackage();
        dataPackage.SetText(endpoint);
        Clipboard.SetContent(dataPackage);
        StatusText = $"\u5DF2\u590D\u5236\u900F\u660E\u4EE3\u7406\u5165\u53E3\uFF1A{endpoint}";
    }

    [RelayCommand]
    private void ClearTransparentProxyCache()
    {
        var cleared = _proxyService.ClearCache();
        StatusText = cleared <= 0
            ? "\u900F\u660E\u4EE3\u7406\u7F13\u5B58\u5DF2\u4E3A\u7A7A"
            : $"\u5DF2\u6E05\u7406\u900F\u660E\u4EE3\u7406\u7F13\u5B58\uFF1A{cleared} \u9879";
    }
}
