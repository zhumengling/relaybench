using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RelayBench.Services;

internal sealed class TransparentProxyAnthropicToolUseStreamNormalizer
{
    private readonly Dictionary<string, ToolUseState> _states = new(StringComparer.OrdinalIgnoreCase);
    private int _nextContentBlockIndex;
    private int _nextStateSequence;

    public bool HasToolCalls { get; private set; }

    public IReadOnlyList<TransparentProxySseEvent> ExtractToolUseEvents(string? data)
    {
        if (string.IsNullOrWhiteSpace(data) ||
            string.Equals(data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<TransparentProxySseEvent>();
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            List<TransparentProxySseEvent> events = [];
            ExtractOpenAiChatToolCalls(document.RootElement, events);
            ExtractResponsesToolCalls(document.RootElement, events);
            return events;
        }
        catch
        {
            return Array.Empty<TransparentProxySseEvent>();
        }
    }

    public int AllocateContentBlockIndex()
        => _nextContentBlockIndex++;

    public IReadOnlyList<TransparentProxySseEvent> FlushOpenBlocks()
    {
        List<TransparentProxySseEvent> events = [];
        foreach (var state in _states.Values
                     .OrderBy(static item => item.Started ? item.ContentBlockIndex : item.SortOrder)
                     .ThenBy(static item => item.Sequence))
        {
            if (!state.Started && state.HasBelatedIdentity)
            {
                StartToolBlock(state, events);
            }

            StopToolBlock(state, events);
        }

        return events;
    }

    private void ExtractOpenAiChatToolCalls(JsonElement root, List<TransparentProxySseEvent> events)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out var delta) ||
                !delta.TryGetProperty("tool_calls", out var toolCalls) ||
                toolCalls.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var toolCall in toolCalls.EnumerateArray())
            {
                var key = ResolveChatToolKey(root, toolCall);
                var state = GetState(key, ResolveChatToolSortOrder(toolCall));
                UpdateIdentity(
                    state,
                    TryReadString(toolCall, "id"),
                    toolCall.TryGetProperty("function", out var function)
                        ? TryReadString(function, "name")
                        : null);

                if (state.HasAnnounceableIdentity)
                {
                    StartToolBlock(state, events);
                }

                if (toolCall.TryGetProperty("function", out function))
                {
                    AppendArgumentDelta(state, TryReadString(function, "arguments"), events);
                }
            }
        }
    }

    private void ExtractResponsesToolCalls(JsonElement root, List<TransparentProxySseEvent> events)
    {
        var type = TryReadString(root, "type");
        if (string.IsNullOrWhiteSpace(type) ||
            !type.StartsWith("response.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if ((type.Equals("response.output_item.added", StringComparison.OrdinalIgnoreCase) ||
             type.Equals("response.output_item.done", StringComparison.OrdinalIgnoreCase)) &&
            root.TryGetProperty("item", out var item) &&
            IsResponsesFunctionCall(item))
        {
            var state = GetState(ResolveResponsesToolKey(root, item), ResolveResponsesToolSortOrder(root));
            UpdateIdentity(
                state,
                TryReadString(item, "call_id") ?? TryReadString(item, "id"),
                TryReadString(item, "name"));
            StartToolBlock(state, events);

            if (type.Equals("response.output_item.done", StringComparison.OrdinalIgnoreCase))
            {
                CompleteArguments(state, TryReadString(item, "arguments"), events);
                StopToolBlock(state, events);
            }

            return;
        }

        if (type.Equals("response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase))
        {
            var state = GetState(ResolveResponsesToolKey(root), ResolveResponsesToolSortOrder(root));
            AppendArgumentDelta(state, TryReadString(root, "delta"), events);
            return;
        }

        if (type.Equals("response.function_call_arguments.done", StringComparison.OrdinalIgnoreCase))
        {
            var state = GetState(ResolveResponsesToolKey(root), ResolveResponsesToolSortOrder(root));
            CompleteArguments(state, TryReadString(root, "arguments"), events);
            StopToolBlock(state, events);
        }
    }

    private ToolUseState GetState(string key, int sortOrder)
    {
        if (_states.TryGetValue(key, out var state))
        {
            if (sortOrder < state.SortOrder)
            {
                state.SortOrder = sortOrder;
            }

            return state;
        }

        state = new ToolUseState(sortOrder, _nextStateSequence++);
        _states[key] = state;
        return state;
    }

    private static void UpdateIdentity(ToolUseState state, string? id, string? name)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            state.Id ??= id;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            state.Name ??= name;
        }
    }

    private void StartToolBlock(ToolUseState state, List<TransparentProxySseEvent> events)
    {
        if (state.Started)
        {
            return;
        }

        state.Name ??= "tool";
        if (state.ContentBlockIndex < 0)
        {
            state.ContentBlockIndex = AllocateContentBlockIndex();
        }

        state.Id = TransparentProxyClaudeToolUseId.Normalize(state.Id);
        events.Add(new TransparentProxySseEvent(
            "content_block_start",
            BuildToolUseStartEvent(state.ContentBlockIndex, state.Id, state.Name)));
        state.Started = true;
        HasToolCalls = true;

        if (state.PendingArguments.Length > 0)
        {
            var pendingArguments = state.PendingArguments.ToString();
            events.Add(new TransparentProxySseEvent(
                "content_block_delta",
                BuildToolUseInputDeltaEvent(state.ContentBlockIndex, pendingArguments)));
            state.PendingArguments.Clear();
            state.EmittedArgumentLength = state.Arguments.Length;
        }
    }

    private void AppendArgumentDelta(ToolUseState state, string? delta, List<TransparentProxySseEvent> events)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        state.Arguments.Append(delta);
        if (!state.Started)
        {
            state.PendingArguments.Append(delta);
            return;
        }

        events.Add(new TransparentProxySseEvent(
            "content_block_delta",
            BuildToolUseInputDeltaEvent(state.ContentBlockIndex, delta)));
        state.EmittedArgumentLength = state.Arguments.Length;
        HasToolCalls = true;
    }

    private void CompleteArguments(ToolUseState state, string? arguments, List<TransparentProxySseEvent> events)
    {
        if (!string.IsNullOrEmpty(arguments))
        {
            if (state.Arguments.Length == 0)
            {
                AppendArgumentDelta(state, arguments, events);
            }
            else if (arguments.Length > state.Arguments.Length &&
                     arguments.StartsWith(state.Arguments.ToString(), StringComparison.Ordinal))
            {
                AppendArgumentDelta(state, arguments[state.Arguments.Length..], events);
            }
        }

        StartToolBlock(state, events);
    }

    private static void StopToolBlock(ToolUseState state, List<TransparentProxySseEvent> events)
    {
        if (!state.Started || state.Stopped)
        {
            return;
        }

        events.Add(new TransparentProxySseEvent(
            "content_block_stop",
            BuildContentBlockStopEvent(state.ContentBlockIndex)));
        state.Stopped = true;
    }

    private static string BuildToolUseStartEvent(int index, string id, string name)
    {
        var root = new JsonObject
        {
            ["type"] = "content_block_start",
            ["index"] = index,
            ["content_block"] = new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = id,
                ["name"] = name,
                ["input"] = new JsonObject()
            }
        };
        return root.ToJsonString();
    }

    private static string BuildToolUseInputDeltaEvent(int index, string partialJson)
    {
        var root = new JsonObject
        {
            ["type"] = "content_block_delta",
            ["index"] = index,
            ["delta"] = new JsonObject
            {
                ["type"] = "input_json_delta",
                ["partial_json"] = partialJson
            }
        };
        return root.ToJsonString();
    }

    private static string BuildContentBlockStopEvent(int index)
    {
        var root = new JsonObject
        {
            ["type"] = "content_block_stop",
            ["index"] = index
        };
        return root.ToJsonString();
    }

    private static bool IsResponsesFunctionCall(JsonElement item)
        => string.Equals(TryReadString(item, "type"), "function_call", StringComparison.OrdinalIgnoreCase);

    private static string ResolveChatToolKey(JsonElement root, JsonElement toolCall)
    {
        if (TryReadInt(toolCall, "index") is { } index)
        {
            return $"chat:index:{index}";
        }

        return $"chat:{TryReadString(toolCall, "id") ?? root.GetRawText().GetHashCode().ToString("X")}";
    }

    private static int ResolveChatToolSortOrder(JsonElement toolCall)
        => TryReadInt(toolCall, "index") ?? int.MaxValue;

    private static string ResolveResponsesToolKey(JsonElement root, JsonElement? item = null)
    {
        var outputIndex = TryReadInt(root, "output_index");
        if (outputIndex is >= 0)
        {
            return $"responses:index:{outputIndex.Value}";
        }

        var itemId = TryReadString(root, "item_id");
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            return $"responses:item:{itemId}";
        }

        if (item is { } itemValue)
        {
            var callId = TryReadString(itemValue, "call_id") ?? TryReadString(itemValue, "id");
            if (!string.IsNullOrWhiteSpace(callId))
            {
                return $"responses:call:{callId}";
            }
        }

        return $"responses:{root.GetRawText().GetHashCode():X}";
    }

    private static int ResolveResponsesToolSortOrder(JsonElement root)
        => TryReadInt(root, "output_index") ?? int.MaxValue;

    private static string? TryReadString(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? TryReadInt(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.Number &&
           property.TryGetInt32(out var value)
            ? value
            : null;

    private sealed class ToolUseState(int sortOrder, int sequence)
    {
        public int ContentBlockIndex { get; set; } = -1;
        public int SortOrder { get; set; } = sortOrder;
        public int Sequence { get; } = sequence;
        public string? Id { get; set; }
        public string? Name { get; set; }
        public bool Started { get; set; }
        public bool Stopped { get; set; }
        public int EmittedArgumentLength { get; set; }
        public StringBuilder Arguments { get; } = new();
        public StringBuilder PendingArguments { get; } = new();
        public bool HasAnnounceableIdentity =>
            !string.IsNullOrWhiteSpace(Id) &&
            !string.IsNullOrWhiteSpace(Name);
        public bool HasBelatedIdentity => !string.IsNullOrWhiteSpace(Name);
    }
}
