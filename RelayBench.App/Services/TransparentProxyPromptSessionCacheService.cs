using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RelayBench.App.Services;

internal sealed class TransparentProxyPromptSessionCacheService
{
    private const int MaxEntries = 2048;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, TransparentProxyPromptSessionCacheEntry> _entries = new(StringComparer.Ordinal);
    private long _hits;
    private long _misses;

    public TransparentProxyPromptSessionMaterial Resolve(
        TransparentProxyRoute route,
        byte[] requestBody)
    {
        var model = TryReadStringProperty(requestBody, "model") ?? route.Model;
        var userScope = ResolveUserScope(requestBody);
        var credentialScope = ResolveCredentialScope(route);
        var key = BuildCacheKey(credentialScope, model, userScope);
        var now = DateTimeOffset.UtcNow;

        PruneExpired(now);
        if (_entries.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            _entries[key] = cached with { LastAccessedAt = now, ExpiresAt = now.Add(DefaultTtl) };
            Interlocked.Increment(ref _hits);
            return new TransparentProxyPromptSessionMaterial(cached.PromptCacheKey, cached.SessionId, model, userScope);
        }

        var promptCacheKey = BuildStableGuid("prompt-cache", key);
        var sessionId = BuildStableGuid("session", $"{credentialScope}\u001F{userScope}");
        _entries[key] = new TransparentProxyPromptSessionCacheEntry(
            promptCacheKey,
            sessionId,
            now,
            now,
            now.Add(DefaultTtl));
        Interlocked.Increment(ref _misses);
        EnforceCapacity();
        return new TransparentProxyPromptSessionMaterial(promptCacheKey, sessionId, model, userScope);
    }

    public TransparentProxyPromptSessionCacheStats Stats
        => new(_entries.Count, Interlocked.Read(ref _hits), Interlocked.Read(ref _misses));

    public void Clear()
    {
        _entries.Clear();
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private void EnforceCapacity()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (var key in _entries
                     .OrderBy(static pair => pair.Value.LastAccessedAt)
                     .Take(Math.Max(1, _entries.Count - MaxEntries))
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _entries.TryRemove(key, out _);
        }
    }

    private static string ResolveCredentialScope(TransparentProxyRoute route)
    {
        if (!string.IsNullOrWhiteSpace(route.ApiKey))
        {
            return "key:" + HashText(route.ApiKey.Trim());
        }

        return $"route:{route.Id}|{route.BaseUrl}";
    }

    private static string ResolveUserScope(byte[] requestBody)
    {
        var explicitUser =
            TryReadStringProperty(requestBody, "user") ??
            TryReadStringProperty(requestBody, "user_id") ??
            TryReadNestedStringProperty(requestBody, "metadata", "user_id") ??
            TryReadStringProperty(requestBody, "conversation_id") ??
            TryReadStringProperty(requestBody, "thread_id") ??
            TryReadStringProperty(requestBody, "session_id");
        if (!string.IsNullOrWhiteSpace(explicitUser))
        {
            return explicitUser.Trim();
        }

        if (TryBuildPromptPrefixFingerprint(requestBody, out var fingerprint))
        {
            return fingerprint;
        }

        return "default";
    }

    private static bool TryBuildPromptPrefixFingerprint(byte[] requestBody, out string fingerprint)
    {
        fingerprint = string.Empty;
        if (requestBody.Length == 0 || requestBody.Length > 512 * 1024)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            List<string> parts = [];
            if (document.RootElement.TryGetProperty("instructions", out var instructions) &&
                instructions.ValueKind == JsonValueKind.String)
            {
                parts.Add(Truncate(instructions.GetString()));
            }

            if (document.RootElement.TryGetProperty("system", out var system))
            {
                parts.Add(ExtractTextPreview(system));
            }

            if (document.RootElement.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray().Take(2))
                {
                    var role = TryReadStringProperty(message, "role") ?? "-";
                    var content = message.TryGetProperty("content", out var contentNode)
                        ? ExtractTextPreview(contentNode)
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        parts.Add($"{role}:{content}");
                    }
                }
            }
            else if (document.RootElement.TryGetProperty("input", out var input))
            {
                parts.Add(ExtractTextPreview(input));
            }

            var source = string.Join("\u001F", parts.Where(static item => !string.IsNullOrWhiteSpace(item)));
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            fingerprint = "prefix:" + HashText(source)[..20];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractTextPreview(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => Truncate(element.GetString()),
            JsonValueKind.Array => string.Join(
                " ",
                element.EnumerateArray()
                    .Take(4)
                    .Select(ExtractTextPreview)
                    .Where(static text => !string.IsNullOrWhiteSpace(text))),
            JsonValueKind.Object when element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                => Truncate(text.GetString()),
            JsonValueKind.Object when element.TryGetProperty("content", out var content)
                => ExtractTextPreview(content),
            _ => string.Empty
        };

    private static string Truncate(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= 240 ? text : text[..240];
    }

    private static string? TryReadStringProperty(byte[] requestBody, string propertyName)
    {
        if (requestBody.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return TryReadStringProperty(document.RootElement, propertyName);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadNestedStringProperty(byte[] requestBody, string parentPropertyName, string propertyName)
    {
        if (requestBody.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(parentPropertyName, out var parent)
                ? TryReadStringProperty(parent, propertyName)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadStringProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string BuildCacheKey(string credentialScope, string? model, string userScope)
        => $"{credentialScope}\u001F{model?.Trim() ?? string.Empty}\u001F{userScope.Trim()}";

    private static string BuildStableGuid(string prefix, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(prefix + "\u001F" + key));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes).ToString("D");
    }

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal sealed record TransparentProxyPromptSessionMaterial(
    string PromptCacheKey,
    string SessionId,
    string? Model,
    string UserScope);

internal sealed record TransparentProxyPromptSessionCacheStats(int Entries, long Hits, long Misses);

internal sealed record TransparentProxyPromptSessionCacheEntry(
    string PromptCacheKey,
    string SessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    DateTimeOffset ExpiresAt);
