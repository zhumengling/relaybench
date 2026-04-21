using System.Text.Json;

namespace NetTest.Core.Services;

internal static class CodexRestoreStateStorage
{
    private const string StateFileName = "codex-live-restore-state.json";

    public static string GetStatePath(IClientApiConfigMutationEnvironment environment)
        => Path.Combine(
            environment.LocalAppDataPath,
            "RelayBench",
            "ClientApiRestore",
            StateFileName);

    public static void EnsureOriginalStateCaptured(
        IClientApiConfigMutationEnvironment environment,
        params string[] filePaths)
    {
        var statePath = GetStatePath(environment);
        if (environment.FileExists(statePath))
        {
            return;
        }

        CodexRestoreState state = new()
        {
            Files = filePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                {
                    var snapshot = CodexOfficialConfigTools.CreateBaselineSnapshot(environment, path);
                    return new CodexRestoreStateFile
                    {
                        Path = path,
                        Existed = snapshot.Existed,
                        Content = snapshot.Content
                    };
                })
                .ToList()
        };

        environment.WriteFileText(
            statePath,
            JsonSerializer.Serialize(
                state,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
    }

    public static CodexRestoreState? TryLoad(IClientApiConfigMutationEnvironment environment)
    {
        var statePath = GetStatePath(environment);
        if (!environment.FileExists(statePath))
        {
            return null;
        }

        try
        {
            var text = environment.ReadFileText(statePath);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return JsonSerializer.Deserialize<CodexRestoreState>(text!);
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(IClientApiConfigMutationEnvironment environment)
    {
        var statePath = GetStatePath(environment);
        if (environment.FileExists(statePath))
        {
            environment.DeleteFile(statePath);
        }
    }

    public sealed class CodexRestoreState
    {
        public List<CodexRestoreStateFile> Files { get; set; } = [];
    }

    public sealed class CodexRestoreStateFile
    {
        public string Path { get; set; } = string.Empty;

        public bool Existed { get; set; }

        public string? Content { get; set; }
    }
}
