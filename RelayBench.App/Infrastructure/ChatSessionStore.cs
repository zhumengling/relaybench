using System.IO;
using System.Text;
using System.Text.Json;

namespace RelayBench.App.Infrastructure;

public sealed class ChatSessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public ChatSessionsDocument LoadDocument()
    {
        try
        {
            var path = RelayBenchPaths.ChatSessionsPath;
            if (!File.Exists(path))
            {
                return new ChatSessionsDocument();
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var document = JsonSerializer.Deserialize<ChatSessionsDocument>(json, SerializerOptions);
            if (document?.Sessions.Count > 0 || document?.Presets.Count > 0)
            {
                return document;
            }

            var legacySession = JsonSerializer.Deserialize<ChatSessionSnapshot>(json, SerializerOptions);
            if (legacySession is not null)
            {
                legacySession.Title = string.IsNullOrWhiteSpace(legacySession.Title)
                    ? "\u5386\u53f2\u4f1a\u8bdd"
                    : legacySession.Title;
                legacySession.UpdatedAt = legacySession.UpdatedAt == default
                    ? DateTimeOffset.Now
                    : legacySession.UpdatedAt;
                return new ChatSessionsDocument
                {
                    ActiveSessionId = legacySession.SessionId,
                    Sessions = [legacySession]
                };
            }

            return new ChatSessionsDocument();
        }
        catch
        {
            return new ChatSessionsDocument();
        }
    }

    public void SaveDocument(ChatSessionsDocument document)
    {
        try
        {
            var path = RelayBenchPaths.ChatSessionsPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(document, SerializerOptions);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch
        {
        }
    }
}
