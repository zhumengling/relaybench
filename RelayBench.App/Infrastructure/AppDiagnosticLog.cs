using System.IO;
using System.Text;

namespace RelayBench.App.Infrastructure;

public static class AppDiagnosticLog
{
    private static readonly object SyncRoot = new();

    public static void Write(string source, Exception exception)
    {
        try
        {
            var logPath = RelayBenchPaths.StartupLogPath;
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var message =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}";

            lock (SyncRoot)
            {
                File.AppendAllText(logPath, message, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
