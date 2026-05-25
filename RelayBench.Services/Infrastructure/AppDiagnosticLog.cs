using System.IO;
using System.Text;

namespace RelayBench.Services.Infrastructure;

public static class AppDiagnosticLog
{
    private static readonly object SyncRoot = new();

    public static void Write(string source, Exception exception)
    {
        Write(source, exception.ToString());
    }

    public static void Write(string source, string message)
    {
        try
        {
            var logPath = RelayBenchPaths.StartupLogPath;
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}" +
                $"{message}{Environment.NewLine}{Environment.NewLine}";

            lock (SyncRoot)
            {
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
