using System.Text.Json;

namespace RelayBench.WinUI.Storage;

/// <summary>
/// Persists the last-used endpoint credentials (BaseUrl, ApiKey, Model) to a local JSON file.
/// All pages share this store so the API key auto-fills across SingleStation, DataSafety, Batch, ModelChat, etc.
/// The API key is stored as-is (not masked) since this is local-only storage.
/// </summary>
public sealed class SharedEndpointStore
{
    private static readonly string s_filePath = Path.Combine(StoragePaths.Root, "shared-endpoint.json");

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly object s_fileGate = new();

    /// <summary>
    /// Loads the last-used endpoint state. Returns null if no state has been saved yet.
    /// </summary>
    public static SharedEndpointState? Load()
    {
        lock (s_fileGate)
        {
            try
            {
                if (!File.Exists(s_filePath))
                    return null;

                var json = File.ReadAllText(s_filePath);
                return JsonSerializer.Deserialize<SharedEndpointState>(json, s_jsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Saves the current endpoint state. Called whenever a user starts a test or fetches models.
    /// </summary>
    public static Task SaveAsync(string baseUrl, string apiKey, string model, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            return Task.CompletedTask;

        ct.ThrowIfCancellationRequested();

        var state = new SharedEndpointState
        {
            BaseUrl = baseUrl.Trim(),
            ApiKey = apiKey.Trim(),
            Model = model?.Trim() ?? ""
        };

        lock (s_fileGate)
        {
            var dir = Path.GetDirectoryName(s_filePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(state, s_jsonOptions);
            File.WriteAllText(s_filePath, json);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The shared endpoint state persisted to disk.
/// </summary>
public sealed class SharedEndpointState
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
}
