using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyPortInspectorService
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public TransparentProxyPortInspectionResult Inspect(int port)
    {
        if (port is < 1 or > 65535)
        {
            return TransparentProxyPortInspectionResult.NotListening(port);
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p tcp",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2500);
            return ParseNetstatOutput(port, output);
        }
        catch (Exception ex)
        {
            return new TransparentProxyPortInspectionResult(
                port,
                false,
                null,
                string.Empty,
                string.Empty,
                $"端口 {port} 占用检查失败：{ex.Message}");
        }
    }

    internal static TransparentProxyPortInspectionResult ParseNetstatOutput(int port, string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = WhitespaceRegex.Split(trimmed);
            if (parts.Length < 5 ||
                !IsMatchingLocalPort(parts[1], port) ||
                !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(parts[^1], out var processId))
            {
                continue;
            }

            var processName = ResolveProcessName(processId);
            var summary = string.IsNullOrWhiteSpace(processName)
                ? $"端口 {port} 已被 PID {processId} 占用，监听 {parts[1]}。"
                : $"端口 {port} 已被 {processName} (PID {processId}) 占用，监听 {parts[1]}。";
            return new TransparentProxyPortInspectionResult(
                port,
                true,
                processId,
                processName,
                parts[1],
                summary);
        }

        return TransparentProxyPortInspectionResult.NotListening(port);
    }

    private static bool IsMatchingLocalPort(string localAddress, int port)
        => localAddress.EndsWith($":{port}", StringComparison.OrdinalIgnoreCase) ||
           localAddress.EndsWith($".{port}", StringComparison.OrdinalIgnoreCase) ||
           localAddress.EndsWith($"]:{port}", StringComparison.OrdinalIgnoreCase);

    private static string ResolveProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }
}

internal sealed record TransparentProxyPortInspectionResult(
    int Port,
    bool IsListening,
    int? ProcessId,
    string ProcessName,
    string LocalAddress,
    string Summary)
{
    public static TransparentProxyPortInspectionResult NotListening(int port)
        => new(port, false, null, string.Empty, string.Empty, $"端口 {port} 当前未发现监听进程。");
}
