using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed partial class CodexHistorySyncService
{
    private static async Task<IReadOnlyList<ThreadCwdStat>> ReadThreadCwdStatsAsync(string codexHome)
    {
        var dbPath = StateDbPath(codexHome);
        if (!File.Exists(dbPath))
        {
            return [];
        }

        try
        {
            await using var connection = OpenConnection(dbPath, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync();
            var columns = await ReadThreadTableColumnsAsync(connection);
            if (!columns.Contains("cwd"))
            {
                return [];
            }

            var updatedAtExpression = columns.Contains("updated_at_ms")
                ? (columns.Contains("updated_at")
                    ? "COALESCE(MAX(updated_at_ms), MAX(updated_at) * 1000, 0)"
                    : "COALESCE(MAX(updated_at_ms), 0)")
                : (columns.Contains("updated_at")
                    ? "COALESCE(MAX(updated_at) * 1000, 0)"
                    : "0");

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT
                  cwd,
                  COUNT(*) AS count,
                  {updatedAtExpression} AS updated_at_ms
                FROM threads
                WHERE cwd IS NOT NULL AND cwd <> ''
                GROUP BY cwd
                ORDER BY count DESC, updated_at_ms DESC, cwd
                """;

            List<ThreadCwdStat> rows = [];
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var cwd = reader.GetString(0);
                var normalized = NormalizeComparablePath(cwd);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                rows.Add(new ThreadCwdStat(
                    cwd,
                    normalized,
                    reader.GetInt64(1),
                    reader.GetInt64(2)));
            }

            return rows;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<WorkspaceRootSyncResult> SyncWorkspaceRootsAsync(
        string codexHome,
        IReadOnlyList<ThreadCwdStat>? cwdStats = null)
    {
        var statePath = GlobalStatePath(codexHome);
        if (!File.Exists(statePath))
        {
            return new WorkspaceRootSyncResult(false, false, 0, 0);
        }

        JsonObject state;
        try
        {
            state = JsonNode.Parse(await File.ReadAllTextAsync(statePath)) as JsonObject
                    ?? throw new InvalidOperationException("global state json root is not an object");
        }
        catch
        {
            return new WorkspaceRootSyncResult(true, false, 0, 0);
        }

        var effectiveCwdStats = cwdStats ?? await ReadThreadCwdStatsAsync(codexHome);
        var existingSavedRoots = ToPathList(state["electron-saved-workspace-roots"]);
        var existingProjectOrder = ToPathList(state["project-order"]);
        var existingActiveRoots = ToPathList(state["active-workspace-roots"]);
        var originalActiveRoots = state["active-workspace-roots"]?.DeepClone();
        var originalLabels = state["electron-workspace-root-labels"]?.DeepClone();
        var originalOpenTargets = state["open-in-target-preferences"]?.DeepClone();

        var savedRootCandidates = existingProjectOrder.Count > 0
            ? existingProjectOrder.Concat(existingSavedRoots).Concat(existingActiveRoots)
            : existingSavedRoots.Concat(existingActiveRoots);
        var nextSavedRoots = DedupePaths(savedRootCandidates.Select(value => ResolveStoredPath(value, effectiveCwdStats)));
        var projectOrderCandidates = existingProjectOrder.Count > 0
            ? existingProjectOrder.Concat(existingSavedRoots)
            : nextSavedRoots;
        var nextProjectOrder = DedupePaths(projectOrderCandidates.Select(value => ResolveStoredPath(value, effectiveCwdStats)));
        var nextActiveRoots = DedupePaths(existingActiveRoots.Select(value => ResolveStoredPath(value, effectiveCwdStats)));

        JsonNode? nextActiveRootsNode = state["active-workspace-roots"] is JsonArray
            ? ToJsonArray(nextActiveRoots)
            : (nextActiveRoots.Count > 0 ? JsonValue.Create(nextActiveRoots[0]) : null);
        var nextLabels = CopyResolvedObjectKeys(state["electron-workspace-root-labels"] as JsonObject, effectiveCwdStats);
        JsonObject? nextOpenTargets = state["open-in-target-preferences"] as JsonObject;
        if (nextOpenTargets is not null)
        {
            nextOpenTargets = (JsonObject)nextOpenTargets.DeepClone();
            if (nextOpenTargets["perPath"] is JsonObject perPath)
            {
                nextOpenTargets["perPath"] = CopyResolvedObjectKeys(perPath, effectiveCwdStats);
            }
        }

        var savedRootsChanged = !existingSavedRoots.SequenceEqual(nextSavedRoots, StringComparer.Ordinal);
        var projectOrderChanged = !existingProjectOrder.SequenceEqual(nextProjectOrder, StringComparer.Ordinal);
        var activeRootsChanged = !JsonNode.DeepEquals(originalActiveRoots, nextActiveRootsNode);
        var labelsChanged = !JsonNode.DeepEquals(originalLabels, nextLabels);
        var openTargetsChanged = !JsonNode.DeepEquals(originalOpenTargets, nextOpenTargets);
        var backupMissing = !File.Exists(GlobalStateBackupPath(codexHome));

        state["electron-saved-workspace-roots"] = ToJsonArray(nextSavedRoots);
        state["project-order"] = ToJsonArray(nextProjectOrder);
        state["active-workspace-roots"] = nextActiveRootsNode;
        if (nextLabels is not null)
        {
            state["electron-workspace-root-labels"] = nextLabels;
        }

        if (nextOpenTargets is not null)
        {
            state["open-in-target-preferences"] = nextOpenTargets;
        }

        var updated = savedRootsChanged || projectOrderChanged || activeRootsChanged || labelsChanged || openTargetsChanged || backupMissing;
        if (updated)
        {
            var json = state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            await File.WriteAllTextAsync(statePath, json);
            await File.WriteAllTextAsync(GlobalStateBackupPath(codexHome), json);
        }

        return new WorkspaceRootSyncResult(
            true,
            updated,
            CountArrayChanges(existingSavedRoots, nextSavedRoots),
            nextSavedRoots.Count);
    }

    private static async Task<IReadOnlyList<CodexProjectThreadVisibility>> ReadProjectThreadVisibilityAsync(
        string codexHome,
        int pageSize = 50)
    {
        var statePath = GlobalStatePath(codexHome);
        if (!File.Exists(statePath))
        {
            return [];
        }

        JsonObject state;
        try
        {
            state = JsonNode.Parse(await File.ReadAllTextAsync(statePath)) as JsonObject
                    ?? throw new InvalidOperationException("global state json root is not an object");
        }
        catch
        {
            return [];
        }

        var roots = ReadWorkspaceRootsFromState(state);
        if (roots.Count == 0)
        {
            return [];
        }

        var dbPath = StateDbPath(codexHome);
        if (!File.Exists(dbPath))
        {
            return roots
                .Select(static root => new CodexProjectThreadVisibility(
                    root,
                    0,
                    0,
                    0,
                    0,
                    [],
                    string.Empty,
                    new Dictionary<string, int>(StringComparer.Ordinal)))
                .ToArray();
        }

        try
        {
            await using var connection = OpenConnection(dbPath, SqliteOpenMode.ReadOnly);
            await connection.OpenAsync();
            var columns = await ReadThreadTableColumnsAsync(connection);
            if (!columns.Contains("cwd"))
            {
                return [];
            }

            var sourceFilter = columns.Contains("source") ? "AND source IN ('cli', 'vscode')" : string.Empty;
            var archivedFilter = columns.Contains("archived") ? "AND archived = 0" : string.Empty;
            var firstUserFilter = columns.Contains("first_user_message") ? "AND first_user_message <> ''" : string.Empty;
            var providerExpression = columns.Contains("model_provider") ? "model_provider" : "'' AS model_provider";
            var timeExpression = BuildTimeExpression(columns);

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT
                  id,
                  cwd,
                  {providerExpression},
                  {timeExpression} AS sort_ts
                FROM threads
                WHERE cwd IS NOT NULL AND cwd <> ''
                  {archivedFilter}
                  {firstUserFilter}
                  {sourceFilter}
                ORDER BY sort_ts DESC, id DESC
                """;

            List<(string Cwd, string DesktopCwd, string? NormalizedCwd, string Provider, int Rank)> rows = [];
            await using var reader = await command.ExecuteReaderAsync();
            var rank = 1;
            while (await reader.ReadAsync())
            {
                var cwd = reader.GetString(1);
                var provider = reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2))
                    ? "(missing)"
                    : reader.GetString(2);
                rows.Add((cwd, ToDesktopWorkspacePath(cwd), NormalizeComparablePath(cwd), provider, rank));
                rank++;
            }

            List<CodexProjectThreadVisibility> result = [];
            foreach (var root in roots)
            {
                var exactRoot = ToDesktopWorkspacePath(root);
                var normalizedRoot = NormalizeComparablePath(root);
                var matchingRows = rows
                    .Where(row => string.Equals(row.NormalizedCwd, normalizedRoot, StringComparison.Ordinal))
                    .ToList();
                var ranks = matchingRows.Select(static row => row.Rank).ToArray();
                Dictionary<string, int> providerCounts = new(StringComparer.Ordinal);
                foreach (var row in matchingRows)
                {
                    providerCounts[row.Provider] = providerCounts.GetValueOrDefault(row.Provider) + 1;
                }

                result.Add(new CodexProjectThreadVisibility(
                    exactRoot,
                    matchingRows.Count,
                    ranks.Count(value => value <= pageSize),
                    matchingRows.Count(row => string.Equals(row.Cwd, exactRoot, StringComparison.Ordinal)),
                    matchingRows.Count(row => row.Cwd.StartsWith(@"\\?\", StringComparison.Ordinal)),
                    ranks,
                    FormatRankPreview(ranks),
                    SortCounts(providerCounts)));
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<HashSet<string>> ReadThreadTableColumnsAsync(SqliteConnection connection)
    {
        HashSet<string> columns = new(StringComparer.Ordinal);
        if (!await TableExistsAsync(connection, "threads"))
        {
            return columns;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"threads\")";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static string BuildTimeExpression(HashSet<string> columns)
    {
        List<string> expressions = [];
        if (columns.Contains("updated_at_ms"))
        {
            expressions.Add("updated_at_ms");
        }

        if (columns.Contains("updated_at"))
        {
            expressions.Add("updated_at * 1000");
        }

        if (columns.Contains("created_at_ms"))
        {
            expressions.Add("created_at_ms");
        }

        if (columns.Contains("created_at"))
        {
            expressions.Add("created_at * 1000");
        }

        expressions.Add("0");
        return $"COALESCE({string.Join(", ", expressions)})";
    }

    private static List<string> ReadWorkspaceRootsFromState(JsonObject state)
    {
        var savedRoots = ToPathList(state["electron-saved-workspace-roots"]);
        var projectOrder = ToPathList(state["project-order"]);
        var activeRoots = ToPathList(state["active-workspace-roots"]);
        var candidates = projectOrder.Count > 0
            ? projectOrder.Concat(savedRoots).Concat(activeRoots)
            : savedRoots.Concat(activeRoots);
        return DedupePaths(candidates.Select(ToDesktopWorkspacePath));
    }

    private static string FormatRankPreview(IReadOnlyList<int> ranks, int maxCount = 12)
    {
        var preview = string.Join(", ", ranks.Take(maxCount));
        var remaining = ranks.Count - Math.Min(ranks.Count, maxCount);
        return remaining > 0 ? $"{preview}（另有 {remaining} 个）" : preview;
    }

    private static string ResolveStoredPath(string value, IReadOnlyList<ThreadCwdStat> cwdStats)
    {
        var comparable = NormalizeComparablePath(value);
        if (string.IsNullOrWhiteSpace(comparable))
        {
            return value;
        }

        var match = cwdStats
            .Where(entry => string.Equals(entry.NormalizedCwd, comparable, StringComparison.Ordinal))
            .OrderByDescending(static entry => entry.Count)
            .ThenByDescending(static entry => entry.UpdatedAtMs)
            .ThenBy(static entry => entry.Cwd, StringComparer.Ordinal)
            .FirstOrDefault();

        return ToDesktopWorkspacePath(match?.Cwd ?? value);
    }

    private static List<string> ToPathList(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array
                .Select(static entry => GetString(entry))
                .Where(static entry => !string.IsNullOrWhiteSpace(entry))
                .Cast<string>()
                .ToList();
        }

        if (node is JsonValue value)
        {
            var text = GetString(value);
            return string.IsNullOrWhiteSpace(text) ? [] : [text];
        }

        return [];
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        JsonArray array = [];
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static List<string> DedupePaths(IEnumerable<string> values)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> result = [];
        foreach (var value in values)
        {
            var comparable = NormalizeComparablePath(value);
            if (string.IsNullOrWhiteSpace(comparable) || !seen.Add(comparable))
            {
                continue;
            }

            result.Add(value);
        }

        return result;
    }

    private static JsonObject? CopyResolvedObjectKeys(JsonObject? source, IReadOnlyList<ThreadCwdStat> cwdStats)
    {
        if (source is null)
        {
            return null;
        }

        JsonObject result = [];
        foreach (var (key, value) in source)
        {
            var resolved = ResolveStoredPath(key, cwdStats);
            if (!result.ContainsKey(resolved) || string.Equals(resolved, key, StringComparison.Ordinal))
            {
                result[resolved] = value?.DeepClone();
            }
        }

        return result;
    }

    private static string? NormalizeComparablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = @"\\" + normalized[8..];
        }
        else if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            normalized = normalized[4..];
        }

        normalized = normalized.Replace('/', '\\').TrimEnd('\\');
        if (normalized.Length == 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
        {
            normalized += "\\";
        }

        return normalized.ToLowerInvariant();
    }

    private static int CountArrayChanges(IReadOnlyList<string> previous, IReadOnlyList<string> next)
    {
        var compared = Math.Max(previous.Count, next.Count);
        var changed = 0;
        for (var index = 0; index < compared; index++)
        {
            var left = index < previous.Count ? previous[index] : null;
            var right = index < next.Count ? next[index] : null;
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                changed++;
            }
        }

        return changed;
    }
}
