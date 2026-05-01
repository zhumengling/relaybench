using RelayBench.Core.Services;
using RelayBench.Core.Models;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelayBench.App.Services;
using RelayBench.App.ViewModels;

namespace RelayBench.Core.Tests;

internal sealed class InMemoryClientApiConfigMutationEnvironment : IClientApiConfigMutationEnvironment
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryClientApiConfigMutationEnvironment()
    {
        UserProfilePath = Path.Combine(Path.GetTempPath(), "RelayBenchTests", Guid.NewGuid().ToString("N"));
        RoamingAppDataPath = Path.Combine(UserProfilePath, "AppData", "Roaming");
        LocalAppDataPath = Path.Combine(UserProfilePath, "AppData", "Local");
        EnsureDirectoryExists(UserProfilePath);
        EnsureDirectoryExists(RoamingAppDataPath);
        EnsureDirectoryExists(LocalAppDataPath);
    }

    public string UserProfilePath { get; }

    public string RoamingAppDataPath { get; }

    public string LocalAppDataPath { get; }

    public void EnsureDirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var current = NormalizePath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            _directories.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent ?? string.Empty;
        }
    }

    public bool FileExists(string path)
        => _files.ContainsKey(NormalizePath(path));

    public bool DirectoryExists(string path)
        => _directories.Contains(NormalizePath(path));

    public string? ReadFileText(string path)
        => _files.TryGetValue(NormalizePath(path), out var content) ? content : null;

    public void WriteFileText(string path, string content)
    {
        var normalizedPath = NormalizePath(path);
        var directory = Path.GetDirectoryName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            EnsureDirectoryExists(directory);
        }

        _files[normalizedPath] = content;
    }

    public void DeleteFile(string path)
        => _files.Remove(NormalizePath(path));

    public IReadOnlyList<string> EnumerateFiles(string directoryPath, string searchPattern)
    {
        var directory = NormalizePath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return _files.Keys
            .Where(path => string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase))
            .Where(path => MatchesSearchPattern(Path.GetFileName(path), searchPattern))
            .ToArray();
    }

    public IReadOnlyList<string> EnumerateFilesRecursive(string directoryPath, string searchPattern)
    {
        var directory = NormalizePath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return _files.Keys
            .Where(path => path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            .Where(path => MatchesSearchPattern(Path.GetFileName(path), searchPattern))
            .ToArray();
    }

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        var source = NormalizePath(sourcePath);
        var destination = NormalizePath(destinationPath);
        if (!_files.TryGetValue(source, out var content))
        {
            throw new FileNotFoundException("Source file was not found.", sourcePath);
        }

        if (!overwrite && _files.ContainsKey(destination))
        {
            throw new IOException("Destination file already exists.");
        }

        WriteFileText(destination, content);
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path);

    private static bool MatchesSearchPattern(string fileName, string searchPattern)
    {
        if (string.IsNullOrWhiteSpace(searchPattern) || searchPattern == "*")
        {
            return true;
        }

        var regex = "^" + Regex.Escape(searchPattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase);
    }
}
