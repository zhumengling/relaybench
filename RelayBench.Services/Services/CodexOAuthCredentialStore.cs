using System.IO;
using System.Text;
using System.Text.Json;
using RelayBench.Services.Infrastructure;

namespace RelayBench.Services;

internal sealed class CodexOAuthCredentialStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;

    public CodexOAuthCredentialStore()
        : this(RelayBenchPaths.TransparentProxyCodexOAuthPath)
    {
    }

    public CodexOAuthCredentialStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
    }

    public IReadOnlyList<CodexOAuthCredential> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<CodexOAuthCredential>();
            }

            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var document = JsonSerializer.Deserialize<CodexOAuthCredentialDocument>(json, SerializerOptions);
            if (document?.Credentials is null)
            {
                return Array.Empty<CodexOAuthCredential>();
            }

            return document.Credentials
                .Select(Unprotect)
                .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
                .ToArray();
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("CodexOAuthCredentialStore.Load", ex);
            return Array.Empty<CodexOAuthCredential>();
        }
    }

    public void Save(IEnumerable<CodexOAuthCredential> credentials)
    {
        try
        {
            var document = new CodexOAuthCredentialDocument
            {
                Credentials = credentials
                    .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
                    .Select(static item => item.Clone())
                    .Select(Protect)
                    .ToList()
            };
            var json = JsonSerializer.Serialize(document, SerializerOptions);
            WriteAllTextAtomically(_filePath, json);
        }
        catch (Exception ex)
        {
            AppDiagnosticLog.Write("CodexOAuthCredentialStore.Save", ex);
            throw;
        }
    }

    private static CodexOAuthCredential Protect(CodexOAuthCredential credential)
    {
        credential.AccessToken = SecretProtector.Protect(credential.AccessToken);
        credential.RefreshToken = SecretProtector.Protect(credential.RefreshToken);
        credential.IdToken = SecretProtector.Protect(credential.IdToken);
        return credential;
    }

    private static CodexOAuthCredential Unprotect(CodexOAuthCredential credential)
    {
        credential.AccessToken = SecretProtector.Unprotect(credential.AccessToken);
        credential.RefreshToken = SecretProtector.Unprotect(credential.RefreshToken);
        credential.IdToken = SecretProtector.Unprotect(credential.IdToken);
        return credential;
    }

    private static void WriteAllTextAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, content, Encoding.UTF8);

        if (File.Exists(path))
        {
            File.Replace(temporaryPath, path, null);
            return;
        }

        File.Move(temporaryPath, path);
    }

    private sealed class CodexOAuthCredentialDocument
    {
        public int Version { get; set; } = 1;

        public List<CodexOAuthCredential> Credentials { get; set; } = [];
    }
}
