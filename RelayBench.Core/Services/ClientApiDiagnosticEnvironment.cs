using System.Diagnostics;

namespace RelayBench.Core.Services;

public sealed class ClientApiDiagnosticEnvironment : IClientApiDiagnosticEnvironment, IClientApiConfigMutationEnvironment
{
    public string UserProfilePath => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string RoamingAppDataPath => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string LocalAppDataPath => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string? GetEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name);

    public void EnsureDirectoryExists(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    public bool FileExists(string path)
        => File.Exists(path);

    public bool DirectoryExists(string path)
        => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.GetDirectories(path)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public string? ReadFileText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public void WriteFileText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content);
    }

    public void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    public IReadOnlyList<string> EnumerateFiles(string directoryPath, string searchPattern)
    {
        try
        {
            return Directory.Exists(directoryPath)
                ? Directory.GetFiles(directoryPath, searchPattern)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> EnumerateFilesRecursive(string directoryPath, string searchPattern)
    {
        try
        {
            return Directory.Exists(directoryPath)
                ? Directory.GetFiles(directoryPath, searchPattern, SearchOption.AllDirectories)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(sourcePath, destinationPath, overwrite);
    }

    public IReadOnlyList<string> ResolveCommandPaths(string commandName)
    {
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = commandName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            return output.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> GetRunningProcessNames()
    {
        try
        {
            return Process.GetProcesses()
                .Select(process =>
                {
                    try
                    {
                        return process.ProcessName;
                    }
                    catch
                    {
                        return string.Empty;
                    }
                })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
