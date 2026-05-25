using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelayBench.Core.Models;
using RelayBench.WinUI.ViewModels;

namespace RelayBench.WinUI.Storage;

/// <summary>
/// Persists chat sessions and their messages to a SQLite database.
/// Uses a dedicated database file separate from the history database.
/// </summary>
public sealed class ChatSessionSqliteStore
{
    private static readonly object _initLock = new();
    private static bool _schemaInitialized;

    private static string DbPath => Path.Combine(StoragePaths.Root, "chat-sessions.db");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string SchemaScript = """
        CREATE TABLE IF NOT EXISTS sessions (
            id              TEXT    PRIMARY KEY,
            title           TEXT    NOT NULL,
            created_at      TEXT    NOT NULL,
            last_message_at TEXT    NOT NULL,
            message_count   INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_sessions_last_msg ON sessions(last_message_at DESC);

        CREATE TABLE IF NOT EXISTS messages (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id      TEXT    NOT NULL,
            is_user         INTEGER NOT NULL,
            content         TEXT    NOT NULL,
            created_at      TEXT    NOT NULL,
            payload_json    TEXT    NULL,
            FOREIGN KEY (session_id) REFERENCES sessions(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_id, id);
        """;

    private SqliteConnection CreateConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        // Enable foreign keys
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        EnsureSchema(connection);
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        if (_schemaInitialized) return;

        lock (_initLock)
        {
            if (_schemaInitialized) return;

            using var command = connection.CreateCommand();
            command.CommandText = SchemaScript;
            command.ExecuteNonQuery();

            TryAddColumn(connection, "ALTER TABLE messages ADD COLUMN payload_json TEXT NULL;");

            _schemaInitialized = true;
        }
    }

    private static void TryAddColumn(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (
            ex.SqliteErrorCode == 1 &&
            ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    /// <summary>
    /// Returns all sessions ordered by last message time descending.
    /// </summary>
    public List<ChatSession> GetAllSessions()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, created_at, last_message_at, message_count FROM sessions ORDER BY last_message_at DESC";

        var sessions = new List<ChatSession>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new ChatSession
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                CreatedAtUtc = DateTime.Parse(reader.GetString(2)),
                LastMessageAtUtc = DateTime.Parse(reader.GetString(3)),
                MessageCount = reader.GetInt32(4)
            });
        }
        return sessions;
    }

    /// <summary>
    /// Creates a new session record.
    /// </summary>
    public void CreateSession(ChatSession session)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (id, title, created_at, last_message_at, message_count)
            VALUES ($id, $title, $created_at, $last_message_at, $message_count)
            """;
        cmd.Parameters.AddWithValue("$id", session.Id);
        cmd.Parameters.AddWithValue("$title", session.Title);
        cmd.Parameters.AddWithValue("$created_at", session.CreatedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$last_message_at", session.LastMessageAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$message_count", session.MessageCount);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes a session and all its messages (cascade).
    /// </summary>
    public void DeleteSession(string sessionId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Renames a session.
    /// </summary>
    public void RenameSession(string sessionId, string newTitle)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET title = $title WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$title", newTitle);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Saves messages for a session, replacing any existing messages.
    /// Also updates the session's last_message_at and message_count.
    /// </summary>
    public void SaveMessages(string sessionId, IReadOnlyList<ChatMessageItem> messages)
    {
        using var conn = CreateConnection();
        using var transaction = conn.BeginTransaction();

        try
        {
            // Delete existing messages for this session
            using (var delCmd = conn.CreateCommand())
            {
                delCmd.CommandText = "DELETE FROM messages WHERE session_id = $sid";
                delCmd.Parameters.AddWithValue("$sid", sessionId);
                delCmd.ExecuteNonQuery();
            }

            // Insert all messages
            var now = DateTime.UtcNow;
            foreach (var msg in messages)
            {
                using var insCmd = conn.CreateCommand();
                insCmd.CommandText = """
                    INSERT INTO messages (session_id, is_user, content, created_at, payload_json)
                    VALUES ($sid, $is_user, $content, $created_at, $payload_json)
                    """;
                insCmd.Parameters.AddWithValue("$sid", sessionId);
                insCmd.Parameters.AddWithValue("$is_user", msg.IsUser ? 1 : 0);
                insCmd.Parameters.AddWithValue("$content", msg.Content ?? "");
                insCmd.Parameters.AddWithValue("$created_at", now.ToString("O"));
                insCmd.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(ToSnapshot(msg), JsonOptions));
                insCmd.ExecuteNonQuery();
            }

            // Update session metadata
            using (var updCmd = conn.CreateCommand())
            {
                updCmd.CommandText = """
                    UPDATE sessions
                    SET last_message_at = $last_msg, message_count = $count
                    WHERE id = $id
                    """;
                updCmd.Parameters.AddWithValue("$id", sessionId);
                updCmd.Parameters.AddWithValue("$last_msg", now.ToString("O"));
                updCmd.Parameters.AddWithValue("$count", messages.Count);
                updCmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Loads all messages for a given session, ordered by insertion order.
    /// </summary>
    public List<ChatMessageItem> LoadMessages(string sessionId)
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT is_user, content, payload_json FROM messages WHERE session_id = $sid ORDER BY id";
        cmd.Parameters.AddWithValue("$sid", sessionId);

        var messages = new List<ChatMessageItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var isUser = reader.GetInt32(0) == 1;
            var content = reader.GetString(1);
            var payloadJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            messages.Add(FromSnapshot(isUser, content, payloadJson));
        }
        return messages;
    }

    private static StoredChatMessage ToSnapshot(ChatMessageItem message)
    {
        var itemSnapshots = new List<StoredChatAttachment>();
        var attachmentItems = message.AttachmentItems;
        var attachments = message.Attachments;
        var count = Math.Max(attachmentItems.Count, attachments.Count);
        for (var i = 0; i < count; i++)
        {
            var item = i < attachmentItems.Count ? attachmentItems[i] : null;
            var attachment = i < attachments.Count ? attachments[i] : null;
            var id = FirstNonEmpty(item?.Id, attachment?.Id, Guid.NewGuid().ToString("N"));
            var fileName = FirstNonEmpty(item?.FileName, attachment?.FileName, "attachment");
            var filePath = item?.FilePath ?? string.Empty;
            var isImage = item?.IsImage ?? attachment?.Kind == ChatAttachmentKind.Image;
            var sizeBytes = item?.SizeBytes ?? attachment?.SizeBytes ?? 0;
            var cachedContent = FirstNonEmpty(item?.CachedContent, attachment?.Content);
            var mediaType = FirstNonEmpty(
                item?.MediaType,
                attachment?.MediaType,
                NormalizeMediaType(null, filePath, isImage));

            itemSnapshots.Add(new StoredChatAttachment(
                id,
                fileName,
                filePath,
                isImage,
                sizeBytes,
                cachedContent,
                mediaType));
        }

        return new StoredChatMessage(
            message.Content ?? string.Empty,
            message.Metrics,
            message.CodeBlock,
            itemSnapshots);
    }

    private static ChatMessageItem FromSnapshot(bool isUser, string fallbackContent, string? payloadJson)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<StoredChatMessage>(payloadJson, JsonOptions);
                if (snapshot is not null)
                {
                    var storedAttachments = snapshot.Attachments ?? Array.Empty<StoredChatAttachment>();
                    var attachmentItems = storedAttachments
                        .Select(static item => new ChatAttachmentItem(
                            item.FileName,
                            item.FilePath,
                            item.IsImage,
                            item.SizeBytes,
                            item.CachedContent,
                            item.MediaType,
                            item.Id))
                        .ToArray();
                    var attachments = attachmentItems
                        .Select(BuildCoreAttachment)
                        .ToArray();
                    return new ChatMessageItem(
                        isUser,
                        snapshot.Content,
                        snapshot.Metrics,
                        snapshot.CodeBlock,
                        attachments,
                        attachmentItems);
                }
            }
            catch
            {
                // Fall through to old-row compatibility.
            }
        }

        return new ChatMessageItem(isUser, fallbackContent, null);
    }

    private static ChatAttachment BuildCoreAttachment(ChatAttachmentItem item)
    {
        var mediaType = NormalizeMediaType(item.MediaType, item.FilePath, item.IsImage);
        if (item.IsImage)
        {
            var dataUrl = item.CachedContent;
            if (string.IsNullOrWhiteSpace(dataUrl) && File.Exists(item.FilePath))
            {
                var bytes = File.ReadAllBytes(item.FilePath);
                dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
            }

            return new ChatAttachment(
                item.Id,
                ChatAttachmentKind.Image,
                item.FileName,
                mediaType,
                item.SizeBytes,
                dataUrl ?? string.Empty);
        }

        var content = item.CachedContent;
        if (string.IsNullOrWhiteSpace(content) && File.Exists(item.FilePath))
        {
            content = File.ReadAllText(item.FilePath);
        }

        return new ChatAttachment(
            item.Id,
            ChatAttachmentKind.TextFile,
            item.FileName,
            mediaType,
            item.SizeBytes,
            content ?? string.Empty);
    }

    private static string NormalizeMediaType(string? mediaType, string filePath, bool isImage)
    {
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            return mediaType.Trim();
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ when isImage => "application/octet-stream",
            _ => "text/plain"
        };
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    /// <summary>
    /// Resets the schema-initialized flag. Intended for testing scenarios only.
    /// </summary>
    internal static void ResetInitialization()
    {
        lock (_initLock)
        {
            _schemaInitialized = false;
        }
    }

    private sealed record StoredChatMessage(
        string Content,
        string? Metrics,
        string? CodeBlock,
        IReadOnlyList<StoredChatAttachment> Attachments);

    private sealed record StoredChatAttachment(
        string Id,
        string FileName,
        string FilePath,
        bool IsImage,
        long SizeBytes,
        string? CachedContent,
        string? MediaType);
}
