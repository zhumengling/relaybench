using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;

namespace RelayBench.WinUI.Charts;

internal static class LiveChartsInitializer
{
    private static readonly object SyncRoot = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            LiveCharts.Configure(static settings => settings.UseDefaults());
            _initialized = true;
        }
    }
}
