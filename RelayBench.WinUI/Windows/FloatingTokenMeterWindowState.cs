using System.Text.Json;
using RelayBench.Services.Infrastructure;

namespace RelayBench.WinUI.Desktop;

public sealed class FloatingTokenMeterWindowState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public int? Left { get; set; }

    public int? Top { get; set; }

    public bool WasRequested { get; set; }

    public bool HasVisibilityPreference { get; set; }

    public bool IsPositionLocked { get; set; }

    public static FloatingTokenMeterWindowState Load()
    {
        try
        {
            var path = RelayBenchPaths.TokenMeterWindowStatePath;
            if (!File.Exists(path))
            {
                return new FloatingTokenMeterWindowState();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<FloatingTokenMeterWindowState>(json, JsonOptions) ??
                   new FloatingTokenMeterWindowState();
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("FloatingTokenMeterWindowState.Load", ex);
            return new FloatingTokenMeterWindowState();
        }
    }

    public void Save()
    {
        try
        {
            var path = RelayBenchPaths.TokenMeterWindowStatePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("FloatingTokenMeterWindowState.Save", ex);
        }
    }
}
