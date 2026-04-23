namespace RelayBench.Core.Services;

public interface IClientApiDiagnosticEnvironment
{
    string UserProfilePath { get; }

    string RoamingAppDataPath { get; }

    string LocalAppDataPath { get; }

    string? GetEnvironmentVariable(string name);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    IReadOnlyList<string> EnumerateDirectories(string path);

    string? ReadFileText(string path);

    IReadOnlyList<string> ResolveCommandPaths(string commandName);

    IReadOnlyList<string> GetRunningProcessNames();
}
