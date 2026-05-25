using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelayBench.WinUI.Storage;

/// <summary>
/// Persists chat presets to a JSON file. Supports built-in (read-only) and user-defined presets.
/// </summary>
public sealed class ChatPresetStore
{
    private static readonly string FilePath = Path.Combine(StoragePaths.Root, "chat-presets.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly List<ChatPreset> BuiltInPresets =
    [
        new("General Assistant", "You are a helpful assistant.", true),
        new("Code Helper", "You are a senior software engineer. Help the user write, review, and debug code. Provide clear explanations and follow best practices.", true),
        new("Translator", "You are a professional translator. Translate text between languages accurately while preserving tone and meaning. Ask for the target language if not specified.", true)
    ];

    /// <summary>
    /// Loads all presets (built-in + user-defined) from disk.
    /// </summary>
    public List<ChatPreset> LoadAll()
    {
        var userPresets = LoadUserPresets();
        var result = new List<ChatPreset>(BuiltInPresets.Count + userPresets.Count);
        result.AddRange(BuiltInPresets);
        result.AddRange(userPresets);
        return result;
    }

    /// <summary>
    /// Saves a new user-defined preset.
    /// </summary>
    public void Save(ChatPreset preset)
    {
        var userPresets = LoadUserPresets();
        // Remove existing with same name (overwrite)
        userPresets.RemoveAll(p => p.Name == preset.Name);
        userPresets.Add(preset with { IsBuiltIn = false });
        WriteUserPresets(userPresets);
    }

    /// <summary>
    /// Deletes a user-defined preset by name. Built-in presets cannot be deleted.
    /// </summary>
    public bool Delete(string name)
    {
        var userPresets = LoadUserPresets();
        var removed = userPresets.RemoveAll(p => p.Name == name);
        if (removed > 0)
        {
            WriteUserPresets(userPresets);
            return true;
        }
        return false;
    }

    private List<ChatPreset> LoadUserPresets()
    {
        try
        {
            if (!File.Exists(FilePath))
                return [];

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<ChatPreset>>(json, JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void WriteUserPresets(List<ChatPreset> presets)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(presets, JsonOptions);
        File.WriteAllText(FilePath, json);
    }
}

/// <summary>
/// Represents a chat preset with a name and system prompt.
/// </summary>
public sealed record ChatPreset(
    string Name,
    string SystemPrompt,
    bool IsBuiltIn);
