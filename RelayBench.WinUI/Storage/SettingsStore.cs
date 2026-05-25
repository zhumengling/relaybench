using System.Text.Json;

namespace RelayBench.WinUI.Storage;

/// <summary>
/// Manages loading and persisting application settings to a JSON file.
/// Writes are debounced (500 ms) and use a temp-file swap pattern for crash safety.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private AppSettings _current = AppSettings.Defaults;
    private Timer? _debounceTimer;
    private bool _writePending;
    private bool _disposed;

    /// <summary>
    /// Gets the current in-memory settings snapshot.
    /// </summary>
    public AppSettings Current => _current;

    /// <summary>
    /// Raised after every successful in-memory commit (before the debounced disk write).
    /// </summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>
    /// Loads settings from disk. If the file is missing, defaults are used.
    /// If the file contains invalid JSON, it is renamed to
    /// <c>settings.json.broken-yyyyMMddHHmmss</c> and defaults are returned.
    /// </summary>
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        var path = StoragePaths.SettingsJsonPath;

        if (!File.Exists(path))
        {
            _current = AppSettings.Defaults;
            await PersistImmediateAsync(ct).ConfigureAwait(false);
            return _current;
        }

        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions);

            if (settings is null)
            {
                await HandleCorruptFileAsync(path, ct).ConfigureAwait(false);
                return _current;
            }

            _current = settings;
            return _current;
        }
        catch (JsonException)
        {
            await HandleCorruptFileAsync(path, ct).ConfigureAwait(false);
            return _current;
        }
        catch (IOException)
        {
            // File might be locked or inaccessible — use defaults without renaming
            _current = AppSettings.Defaults;
            return _current;
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>
    /// Applies a mutation to the current settings, raises <see cref="Changed"/>,
    /// and schedules a debounced write to disk (500 ms).
    /// </summary>
    /// <param name="mutate">A function that transforms the current settings into the desired state.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _current = mutate(_current);
            _writePending = true;
            ScheduleDebounce();
        }
        finally
        {
            _mutex.Release();
        }

        Changed?.Invoke(this, _current);
    }

    /// <summary>
    /// Releases the debounce timer and flushes any pending write synchronously.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _debounceTimer?.Dispose();
        _debounceTimer = null;

        // Flush any pending write
        if (_writePending)
        {
            FlushToDiskAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        _mutex.Dispose();
    }

    private void ScheduleDebounce()
    {
        if (_debounceTimer is null)
        {
            _debounceTimer = new Timer(OnDebounceElapsed, null, 500, Timeout.Infinite);
        }
        else
        {
            // Reset the timer to 500 ms from now
            _debounceTimer.Change(500, Timeout.Infinite);
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        // Fire-and-forget the flush; errors are swallowed (best-effort persistence)
        _ = FlushToDiskAsync(CancellationToken.None);
    }

    private async Task FlushToDiskAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_writePending) return;

            await WriteToDiskAsync(_current, ct).ConfigureAwait(false);
            _writePending = false;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static async Task WriteToDiskAsync(AppSettings settings, CancellationToken ct)
    {
        var targetPath = StoragePaths.SettingsJsonPath;
        var newPath = targetPath + ".new";
        var bakPath = targetPath + ".bak";

        // Write to temp file first
        var json = JsonSerializer.Serialize(settings, s_jsonOptions);
        await File.WriteAllTextAsync(newPath, json, ct).ConfigureAwait(false);

        // Swap: use File.Replace if the target already exists (preserves .bak)
        if (File.Exists(targetPath))
        {
            try
            {
                File.Replace(newPath, targetPath, bakPath);
            }
            catch (IOException)
            {
                // Fallback: copy current to .bak, then move new to target
                try { File.Copy(targetPath, bakPath, overwrite: true); } catch { /* best effort */ }
                File.Move(newPath, targetPath, overwrite: true);
            }
        }
        else
        {
            // No existing file — just move the new file into place
            File.Move(newPath, targetPath, overwrite: true);
        }
    }

    private async Task HandleCorruptFileAsync(string path, CancellationToken ct)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var brokenPath = $"{path}.broken-{timestamp}";

        try
        {
            File.Move(path, brokenPath, overwrite: true);
        }
        catch
        {
            // If we can't rename, just proceed with defaults
        }

        _current = AppSettings.Defaults;
        await WriteToDiskAsync(_current, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Immediately persists the current settings to disk (bypasses debounce).
    /// Used during initial load when no file exists.
    /// </summary>
    private async Task PersistImmediateAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteToDiskAsync(_current, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }
}
