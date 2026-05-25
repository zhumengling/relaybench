using System.Text;
using System.Text.Json;

namespace RelayBench.Services;

internal sealed class TransparentProxyChatToolCallStreamNormalizer
{
    private readonly TransparentProxyResponseNormalizationService _responseNormalizer;
    private readonly string _model;
    private readonly string _wireApi;
    private readonly string _streamId;
    private readonly IReadOnlyDictionary<string, string>? _toolNameAliases;
    private readonly Dictionary<string, ToolCallState> _states = new(StringComparer.Ordinal);
    private int _nextIndex;

    public TransparentProxyChatToolCallStreamNormalizer(
        TransparentProxyResponseNormalizationService responseNormalizer,
        string model,
        string wireApi,
        string streamId,
        IReadOnlyDictionary<string, string>? toolNameAliases)
    {
        _responseNormalizer = responseNormalizer;
        _model = model;
        _wireApi = wireApi;
        _streamId = streamId;
        _toolNameAliases = toolNameAliases;
    }

    public bool HasToolCalls { get; private set; }

    public IReadOnlyList<TransparentProxyResponseNormalizationService.TransparentProxyNormalizedToolCall> BuildToolCalls()
        => _states.Values
            .Where(static state => state.HeaderEmitted && !string.IsNullOrWhiteSpace(state.Name))
            .OrderBy(static state => state.Index)
            .Select(static state => new TransparentProxyResponseNormalizationService.TransparentProxyNormalizedToolCall(
                string.IsNullOrWhiteSpace(state.Id) ? $"call_{state.Index + 1}" : state.Id!,
                state.Name!,
                state.Arguments.Length == 0 ? "{}" : state.Arguments.ToString()))
            .ToArray();

    public IReadOnlyList<string> BuildChunks(TransparentProxySseEvent sseEvent)
    {
        if (string.IsNullOrWhiteSpace(sseEvent.Data) ||
            string.Equals(sseEvent.Data.Trim(), "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(sseEvent.Data);
            List<string> chunks = [];
            ExtractOpenAiChatToolCalls(document.RootElement, chunks);
            ExtractResponsesToolCalls(document.RootElement, chunks);
            ExtractAnthropicToolCalls(document.RootElement, chunks);
            ExtractGeminiToolCalls(document.RootElement, chunks);
            return chunks;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void ExtractOpenAiChatToolCalls(JsonElement root, List<string> chunks)
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
                var index = TryReadInt(toolCall, "index");
                var key = index is null
                    ? ResolveKey("chat", root, toolCall)
                    : $"chat:index:{index.Value}";
                var state = GetState(key, index);
                UpdateIdentity(
                    state,
                    TryReadString(toolCall, "id"),
                    toolCall.TryGetProperty("function", out var function)
                        ? ResolveToolNameAlias(TryReadString(function, "name"))
                        : null);

                if (!state.HeaderEmitted && !string.IsNullOrWhiteSpace(state.Name))
                {
                    EmitHeader(state, string.Empty, chunks);
                }

                if (toolCall.TryGetProperty("function", out function))
                {
                    AppendArgumentDelta(state, TryReadString(function, "arguments"), chunks);
                }
            }
        }
    }

    private void ExtractResponsesToolCalls(JsonElement root, List<string> chunks)
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
            IsToolType(item, "function_call"))
        {
            var key = ResolveKey("responses", root, item);
            var state = GetState(key, TryReadInt(root, "output_index"));
            UpdateIdentity(
                state,
                TryReadString(item, "call_id") ?? TryReadString(item, "id"),
                ResolveToolNameAlias(TryReadString(item, "name")));

            var arguments = TryReadString(item, "arguments");
            if (type.Equals("response.output_item.added", StringComparison.OrdinalIgnoreCase))
            {
                EmitHeader(state, string.Empty, chunks);
            }
            else
            {
                CompleteArguments(state, arguments, chunks);
            }

            return;
        }

        if (type.Equals("response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase))
        {
            var state = GetState(ResolveKey("responses", root), TryReadInt(root, "output_index"));
            AppendArgumentDelta(state, TryReadString(root, "delta"), chunks);
            return;
        }

        if (type.Equals("response.function_call_arguments.done", StringComparison.OrdinalIgnoreCase))
        {
            var state = GetState(ResolveKey("responses", root), TryReadInt(root, "output_index"));
            CompleteArguments(state, TryReadString(root, "arguments"), chunks);
        }
    }

    private void ExtractAnthropicToolCalls(JsonElement root, List<string> chunks)
    {
        var type = TryReadString(root, "type");
        if (type is null)
        {
            return;
        }

        if (type.Equals("content_block_start", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("content_block", out var contentBlock) &&
            IsToolType(contentBlock, "tool_use"))
        {
            var index = TryReadInt(root, "index");
            var state = GetState(index is null ? ResolveKey("anthropic", root, contentBlock) : $"anthropic:index:{index.Value}", index);
            UpdateIdentity(
                state,
                TryReadString(contentBlock, "id"),
                ResolveToolNameAlias(TryReadString(contentBlock, "name")));

            var arguments = contentBlock.TryGetProperty("input", out var input) &&
                            input.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? input.GetRawText()
                : string.Empty;
            EmitHeader(state, IsEmptyJsonObject(arguments) ? string.Empty : arguments, chunks);
            return;
        }

        if (type.Equals("content_block_delta", StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("delta", out var delta) &&
            TryReadString(delta, "type")?.Equals("input_json_delta", StringComparison.OrdinalIgnoreCase) == true)
        {
            var index = TryReadInt(root, "index");
            var state = GetState(index is null ? ResolveKey("anthropic", root) : $"anthropic:index:{index.Value}", index);
            AppendArgumentDelta(state, TryReadString(delta, "partial_json"), chunks);
        }
    }

    private void ExtractGeminiToolCalls(JsonElement root, List<string> chunks)
    {
        if (root.TryGetProperty("response", out var response) &&
            response.ValueKind == JsonValueKind.Object)
        {
            ExtractGeminiToolCalls(response, chunks);
            return;
        }

        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var candidateIndex = 0;
        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                candidateIndex++;
                continue;
            }

            var partIndex = 0;
            foreach (var part in parts.EnumerateArray())
            {
                if (!part.TryGetProperty("functionCall", out var functionCall))
                {
                    partIndex++;
                    continue;
                }

                var state = GetState($"gemini:{candidateIndex}:{partIndex}", null);
                UpdateIdentity(
                    state,
                    null,
                    ResolveToolNameAlias(TryReadString(functionCall, "name")));
                var arguments = functionCall.TryGetProperty("args", out var args)
                    ? args.GetRawText()
                    : "{}";
                CompleteArguments(state, arguments, chunks);
                partIndex++;
            }

            candidateIndex++;
        }
    }

    private ToolCallState GetState(string key, int? preferredIndex)
    {
        if (_states.TryGetValue(key, out var state))
        {
            return state;
        }

        if (preferredIndex is >= 0)
        {
            state = new ToolCallState(preferredIndex.Value);
            _nextIndex = Math.Max(_nextIndex, preferredIndex.Value + 1);
        }
        else
        {
            state = new ToolCallState(_nextIndex++);
        }

        _states[key] = state;
        return state;
    }

    private void UpdateIdentity(ToolCallState state, string? id, string? name)
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

    private void EmitHeader(ToolCallState state, string? initialArguments, List<string> chunks)
    {
        if (state.HeaderEmitted || string.IsNullOrWhiteSpace(state.Name))
        {
            if (!string.IsNullOrEmpty(initialArguments))
            {
                AppendArgumentDelta(state, initialArguments, chunks);
            }

            return;
        }

        var arguments = initialArguments ?? string.Empty;
        chunks.Add(_responseNormalizer.BuildOpenAiChatToolCallChunk(
            state.Index,
            string.IsNullOrWhiteSpace(state.Id) ? $"call_{state.Index + 1}" : state.Id,
            state.Name,
            arguments,
            _model,
            _wireApi,
            _streamId));
        state.HeaderEmitted = true;
        HasToolCalls = true;
        if (!string.IsNullOrEmpty(arguments))
        {
            state.Arguments.Append(arguments);
            state.EmittedArgumentLength = state.Arguments.Length;
        }
    }

    private void AppendArgumentDelta(ToolCallState state, string? delta, List<string> chunks)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        state.Arguments.Append(delta);
        if (!state.HeaderEmitted)
        {
            return;
        }

        chunks.Add(_responseNormalizer.BuildOpenAiChatToolCallChunk(
            state.Index,
            null,
            null,
            delta,
            _model,
            _wireApi,
            _streamId));
        state.EmittedArgumentLength = state.Arguments.Length;
        HasToolCalls = true;
    }

    private void CompleteArguments(ToolCallState state, string? arguments, List<string> chunks)
    {
        var fullArguments = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments;
        if (!state.HeaderEmitted)
        {
            EmitHeader(state, fullArguments, chunks);
            return;
        }

        var current = state.Arguments.ToString();
        if (current.Length == 0)
        {
            AppendArgumentDelta(state, fullArguments, chunks);
            return;
        }

        if (fullArguments.StartsWith(current, StringComparison.Ordinal) &&
            fullArguments.Length > current.Length)
        {
            AppendArgumentDelta(state, fullArguments[current.Length..], chunks);
        }
    }

    private string? ResolveToolNameAlias(string? name)
        => !string.IsNullOrWhiteSpace(name) &&
           _toolNameAliases is not null &&
           _toolNameAliases.TryGetValue(name, out var original)
            ? original
            : name;

    private static bool IsToolType(JsonElement element, string expected)
        => TryReadString(element, "type")?.Equals(expected, StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsEmptyJsonObject(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Trim().Equals("{}", StringComparison.Ordinal);

    private static string ResolveKey(string prefix, JsonElement root)
        => ResolveKey(prefix, root, default, hasItem: false);

    private static string ResolveKey(string prefix, JsonElement root, JsonElement item)
        => ResolveKey(prefix, root, item, hasItem: true);

    private static string ResolveKey(string prefix, JsonElement root, JsonElement item, bool hasItem)
    {
        var rootItemId = TryReadString(root, "item_id");
        if (!string.IsNullOrWhiteSpace(rootItemId))
        {
            return $"{prefix}:item:{rootItemId}";
        }

        var rootCallId = TryReadString(root, "call_id");
        if (!string.IsNullOrWhiteSpace(rootCallId))
        {
            return $"{prefix}:call:{rootCallId}";
        }

        if (TryReadInt(root, "output_index") is { } outputIndex)
        {
            return $"{prefix}:output:{outputIndex}";
        }

        if (TryReadInt(root, "index") is { } index)
        {
            return $"{prefix}:index:{index}";
        }

        if (hasItem)
        {
            var itemCallId = TryReadString(item, "call_id");
            if (!string.IsNullOrWhiteSpace(itemCallId))
            {
                return $"{prefix}:call:{itemCallId}";
            }

            var itemId = TryReadString(item, "id");
            if (!string.IsNullOrWhiteSpace(itemId))
            {
                return $"{prefix}:item:{itemId}";
            }
        }

        return $"{prefix}:unknown";
    }

    private static string? TryReadString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? TryReadInt(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => Math.Max(0, value),
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => Math.Max(0, value),
            _ => null
        };
    }

    private sealed class ToolCallState
    {
        public ToolCallState(int index)
        {
            Index = index;
        }

        public int Index { get; }

        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();

        public int EmittedArgumentLength { get; set; }

        public bool HeaderEmitted { get; set; }
    }
}
