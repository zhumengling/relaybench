namespace NetTest.Core.Services;

public interface IClientApiConfigMutationEnvironment
{
    string UserProfilePath { get; }

    string RoamingAppDataPath { get; }

    string LocalAppDataPath { get; }

    void EnsureDirectoryExists(string path);

    bool FileExists(string path);

    string? ReadFileText(string path);

    void WriteFileText(string path, string content);
}
