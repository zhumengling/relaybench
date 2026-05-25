using System.Text.Json;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

public sealed record SemanticProbeEvaluation(
    bool Success,
    string Summary,
    string? Error,
    string? NormalizedPreview,
    IReadOnlyList<ProxyProbeEvaluationCheck>? Checks = null);

public static class SemanticProbeEvaluator
{
    private const string InstructionTaskId = "IF-20260501";
    private const string InstructionVerdict = "pass";
    private const int InstructionPriority = 3;
    private const string InstructionMarker = "relay-instruction-ok";
    private const string ForbiddenInstructionToken = "USER_OVERRIDE_FAIL";

    private const string DataOrderId = "RB-2026-0501-A17";
    private const string DataCustomer = "上海云栈科技有限公司";
    private const string DataContact = "林澄";
    private const string DataCallbackUrl = "https://relay.example.com/callback?id=RB-2026-0501-A17&source=bench";
    private const string DataDeliveryDate = "2026-05-01";
    private const decimal DataAmount = 1288.45m;
    private const string DataCurrency = "CNY";

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    public static SemanticProbeEvaluation EvaluateInstructionFollowing(string? rawPreview)
    {
        if (ContainsForbiddenInstructionToken(rawPreview))
        {
            return Fail("指令遵循异常：输出包含被禁止的 USER_OVERRIDE_FAIL。", "模型更像是跟随了 user 覆盖指令。", rawPreview);
        }

        using var document = TryParseStrictObject(rawPreview, out var root, out var parseError);
        if (document is null)
        {
            return Fail("指令遵循异常：输出不是单个严格 JSON object。", parseError, rawPreview);
        }

        if (!StringPropertyEquals(root, "task_id", InstructionTaskId) ||
            !StringPropertyEquals(root, "verdict", InstructionVerdict) ||
            !NumberPropertyEquals(root, "priority", InstructionPriority) ||
            !StringPropertyEquals(root, "marker", InstructionMarker) ||
            !ChecksPropertyMatches(root))
        {
            return Fail("指令遵循异常：JSON 字段值或类型不符合 system 约束。", "请检查 system/user 合并、角色映射或模型路由是否不稳定。", rawPreview);
        }

        var normalized = JsonSerializer.Serialize(root, CompactJsonOptions);
        return new SemanticProbeEvaluation(true, "指令遵循通过：system 约束、禁止项与 JSON 字段均符合预期。", null, normalized);
    }

    public static SemanticProbeEvaluation EvaluateDataExtraction(string? rawPreview)
    {
        using var document = TryParseStrictObject(rawPreview, out var root, out var parseError);
        if (document is null)
        {
            return Fail("数据抽取异常：输出不是单个严格 JSON object。", parseError, rawPreview);
        }

        if (!StringPropertyEquals(root, "order_id", DataOrderId) ||
            !StringPropertyEquals(root, "customer", DataCustomer) ||
            !StringPropertyEquals(root, "contact", DataContact) ||
            !StringPropertyEquals(root, "callback_url", DataCallbackUrl) ||
            !StringPropertyEquals(root, "delivery_date", DataDeliveryDate) ||
            !DecimalPropertyEquals(root, "amount", DataAmount) ||
            !StringPropertyEquals(root, "currency", DataCurrency) ||
            !NullProperty(root, "tax_id") ||
            !ItemsPropertyMatches(root))
        {
            return Fail("数据抽取异常：字段、数字、日期、URL 或数组明细发生漂移。", "请检查模型路由、上下文截断、JSON 包装或输出清洗逻辑。", rawPreview);
        }

        var normalized = JsonSerializer.Serialize(root, CompactJsonOptions);
        return new SemanticProbeEvaluation(true, "数据抽取通过：关键事实、数字、URL、null 字段和明细数组均符合预期。", null, normalized);
    }

    public static SemanticProbeEvaluation EvaluateStructuredOutputEdge(string scenarioId, string? rawPreview)
        => scenarioId switch
        {
            "SO-EDGE-02" => EvaluateCsvEdge(rawPreview),
            "SO-EDGE-03" => EvaluateNestedJsonEdge(rawPreview),
            _ => EvaluateJsonBoundaryEdge(rawPreview)
        };

    public static SemanticProbeEvaluation EvaluateReasonMathConsistency(string scenarioId, string? rawPreview)
    {
        var normalized = NormalizeRawPreview(rawPreview);
        var lines = SplitNonEmptyLines(normalized);
        List<ProxyProbeEvaluationCheck> checks = [];
        var outputContractOk = TryParseReasonMathContract(lines, out var answer, out var checksLine);
        checks.Add(new ProxyProbeEvaluationCheck(
            "OutputDiscipline",
            outputContractOk,
            "ANSWER and CHECKS labelled fields",
            normalized ?? "<empty>",
            outputContractOk ? "Output follows a parseable labelled contract." : "Output has extra text or missing labels."));

        if (!outputContractOk)
        {
            return FailWithChecks("ReasonMath failed: output contract is not stable.", "Expected exactly two labelled lines.", rawPreview, checks);
        }

        var expectedAnswer = scenarioId == "RM-CONS-03" ? "14:30-15:00" : "34.50";
        var answerOk = ReasonMathAnswerEquals(answer, expectedAnswer);
        checks.Add(new ProxyProbeEvaluationCheck(
            "AnswerAccuracy",
            answerOk,
            expectedAnswer,
            answer,
            answerOk ? "Final answer matches the canonical value." : "Final answer drifted."));

        var requiredTokens = scenarioId == "RM-CONS-03"
            ? new[] { "14:00", "15:00", "14:30", "15:30" }
            : new[] { "subtotal 120.00", "tax 9.60", "tip 8.40", "total 138.00" };
        var checkpointsOk = requiredTokens.All(token => ContainsNormalizedCheckpoint(checksLine, token));
        if (scenarioId != "RM-CONS-03")
        {
            checkpointsOk = checkpointsOk &&
                (ContainsNormalizedCheckpoint(checksLine, "split 4") ||
                 ContainsNormalizedCheckpoint(checksLine, $"split {expectedAnswer}") ||
                 ContainsNormalizedCheckpoint(checksLine, $"each {expectedAnswer}") ||
                 ContainsNormalizedCheckpoint(checksLine, $"per person {expectedAnswer}"));
        }
        checks.Add(new ProxyProbeEvaluationCheck(
            "TraceConsistency",
            checkpointsOk,
            string.Join(", ", requiredTokens),
            checksLine,
            checkpointsOk ? "Required intermediate checkpoints are present." : "One or more required checkpoints are missing."));

        var success = answerOk && checkpointsOk;
        return success
            ? new SemanticProbeEvaluation(true, "ReasonMath passed: answer and checkpoints are stable.", null, normalized, checks)
            : FailWithChecks("ReasonMath failed: answer or checkpoints drifted.", "Model reasoning output is not deterministic enough for this probe.", rawPreview, checks);
    }

    public static SemanticProbeEvaluation EvaluateCodeBlockDiscipline(string scenarioId, string? rawPreview)
    {
        var normalized = NormalizeRawPreview(rawPreview);
        List<ProxyProbeEvaluationCheck> checks = [];

        if (string.Equals(scenarioId, "CB-DISC-02", StringComparison.OrdinalIgnoreCase))
        {
            var noBugOk = string.Equals(normalized, "no_bug", StringComparison.OrdinalIgnoreCase);
            checks.Add(new ProxyProbeEvaluationCheck(
                "TrapDiscipline",
                noBugOk,
                "no_bug",
                normalized ?? "<empty>",
                noBugOk ? "The model resisted changing correct code." : "The model changed or explained a no-bug scenario."));
            return noBugOk
                ? new SemanticProbeEvaluation(true, "CodeBlock discipline passed: no-bug trap was respected.", null, normalized, checks)
                : FailWithChecks("CodeBlock discipline failed: no-bug trap was not respected.", "Expected exactly no_bug.", rawPreview, checks);
        }

        var blocks = ExtractFencedCodeBlocks(normalized);
        var singleBlock = blocks.Count == 1;
        checks.Add(new ProxyProbeEvaluationCheck(
            "BlockShape",
            singleBlock,
            "one fenced code block",
            blocks.Count.ToString(),
            singleBlock ? "Exactly one code block was returned." : "The response has zero or multiple code blocks."));

        if (!singleBlock)
        {
            return FailWithChecks("CodeBlock discipline failed: expected one fenced code block.", "The response is hard to extract safely.", rawPreview, checks);
        }

        var block = blocks[0];
        var languageOk = string.Equals(block.Language, "python", StringComparison.OrdinalIgnoreCase);
        checks.Add(new ProxyProbeEvaluationCheck(
            "LanguageTag",
            languageOk,
            "python",
            block.Language,
            languageOk ? "Language tag is precise." : "Language tag is missing or wrong."));

        var extraTextOk = HasOnlyLimitedSurroundingText(normalized, block.FullBlock);
        checks.Add(new ProxyProbeEvaluationCheck(
            "ProseDiscipline",
            extraTextOk,
            "one code block with at most short surrounding prose",
            extraTextOk ? "extractable block" : "too much text outside block",
            extraTextOk ? "The code block can be extracted safely." : "The response includes too much prose around the code block."));

        var patchOk = block.Code.Contains("range(len(values))", StringComparison.Ordinal) &&
                      !block.Code.Contains("range(len(values) + 1)", StringComparison.Ordinal) &&
                      !block.Code.Contains("range(len(values)+1)", StringComparison.Ordinal);
        checks.Add(new ProxyProbeEvaluationCheck(
            "PatchQuality",
            patchOk,
            "range(len(values)) and no off-by-one",
            block.Code,
            patchOk ? "The target off-by-one bug is fixed." : "The target fix is absent or still contains the bug."));

        var success = languageOk && extraTextOk && patchOk;
        return success
            ? new SemanticProbeEvaluation(true, "CodeBlock discipline passed: code block is extractable and the fix is correct.", null, normalized, checks)
            : FailWithChecks("CodeBlock discipline failed: code block shape or patch quality drifted.", "The response is not reliable for code-block extraction.", rawPreview, checks);
    }

    private static bool ContainsForbiddenInstructionToken(string? rawPreview)
        => rawPreview?.Contains(ForbiddenInstructionToken, StringComparison.OrdinalIgnoreCase) == true;

    private static JsonDocument? TryParseStrictObject(
        string? rawPreview,
        out JsonElement root,
        out string error)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(rawPreview))
        {
            error = "模型没有返回可解析内容。";
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(
                rawPreview.Trim(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                error = "JSON 根节点不是 object。";
                return null;
            }

            root = document.RootElement;
            error = string.Empty;
            return document;
        }
        catch (JsonException ex)
        {
            error = $"JSON 解析失败：{ex.Message}";
            return null;
        }
    }

    private static SemanticProbeEvaluation EvaluateJsonBoundaryEdge(string? rawPreview)
    {
        List<ProxyProbeEvaluationCheck> checks = [];
        using var document = TryParseStrictObject(rawPreview, out var root, out var parseError);
        var parseOk = document is not null;
        checks.Add(new ProxyProbeEvaluationCheck(
            "Parseable",
            parseOk,
            "strict JSON object",
            parseOk ? "JSON object" : parseError,
            parseOk ? "Response is a strict JSON object." : "Response is not strict JSON."));
        if (!parseOk)
        {
            return FailWithChecks("StructuredOutput failed: response is not strict JSON.", parseError, rawPreview, checks);
        }

        var fieldsOk =
            StringPropertyEquals(root, "empty_string", string.Empty) &&
            NullProperty(root, "null_value") &&
            NumberPropertyEquals(root, "zero", 0) &&
            root.TryGetProperty("false_value", out var falseValue) &&
            falseValue.ValueKind == JsonValueKind.False &&
            root.TryGetProperty("empty_array", out var emptyArray) &&
            emptyArray.ValueKind == JsonValueKind.Array &&
            !emptyArray.EnumerateArray().Any() &&
            root.TryGetProperty("empty_object", out var emptyObject) &&
            emptyObject.ValueKind == JsonValueKind.Object &&
            CountProperties(emptyObject) == 0 &&
            root.TryGetProperty("special_chars", out var specialChars) &&
            specialChars.ValueKind == JsonValueKind.String &&
            SpecialCharsLookPreserved(specialChars.GetString()) &&
            NestedNullMatches(root);

        checks.Add(new ProxyProbeEvaluationCheck(
            "Correctness",
            fieldsOk,
            "all boundary fields preserve value and type",
            JsonSerializer.Serialize(root, CompactJsonOptions),
            fieldsOk ? "Fields and JSON types match." : "At least one field, type or nested value drifted."));

        var disciplineOk = !HasMarkdownFence(rawPreview) && CountProperties(root) == 8;
        checks.Add(new ProxyProbeEvaluationCheck(
            "Discipline",
            disciplineOk,
            "no markdown and exactly 8 fields",
            rawPreview?.Trim() ?? "<empty>",
            disciplineOk ? "No wrapper or extra field detected." : "Markdown wrapper or extra fields were detected."));

        var success = fieldsOk && disciplineOk;
        return success
            ? new SemanticProbeEvaluation(true, "StructuredOutput edge passed: JSON boundary values are stable.", null, JsonSerializer.Serialize(root, CompactJsonOptions), checks)
            : FailWithChecks("StructuredOutput edge failed: JSON field values or discipline drifted.", "The output is not safe for strict structured parsing.", rawPreview, checks);
    }

    private static SemanticProbeEvaluation EvaluateNestedJsonEdge(string? rawPreview)
    {
        using var document = TryParseStrictObject(rawPreview, out var root, out var parseError);
        if (document is null)
        {
            return Fail("StructuredOutput nested edge failed: response is not strict JSON.", parseError, rawPreview);
        }

        var ok = root.TryGetProperty("profile", out var profile) &&
                 profile.ValueKind == JsonValueKind.Object &&
                 profile.TryGetProperty("id", out var id) &&
                 id.ValueKind == JsonValueKind.String &&
                 profile.TryGetProperty("tags", out var tags) &&
                 tags.ValueKind == JsonValueKind.Array &&
                 tags.GetArrayLength() >= 2;
        return ok
            ? new SemanticProbeEvaluation(true, "StructuredOutput nested edge passed.", null, JsonSerializer.Serialize(root, CompactJsonOptions))
            : Fail("StructuredOutput nested edge failed: nested fields drifted.", "Nested object, array or string fields are missing.", rawPreview);
    }

    private static SemanticProbeEvaluation EvaluateCsvEdge(string? rawPreview)
    {
        List<ProxyProbeEvaluationCheck> checks = [];
        var normalized = NormalizeRawPreview(rawPreview);
        var rows = ParseCsv(normalized);
        var parseOk = rows.Count == 4 && rows.All(row => row.Count == 4);
        checks.Add(new ProxyProbeEvaluationCheck(
            "Parseable",
            parseOk,
            "4 rows and 4 columns",
            rows.Count == 0 ? "<empty>" : string.Join(" | ", rows.Select(row => row.Count.ToString())),
            parseOk ? "CSV parses into the expected grid." : "CSV column count drifted, usually due to escaping."));

        var correctnessOk = parseOk &&
                            rows[0].SequenceEqual(["id", "name", "note", "total"], StringComparer.Ordinal) &&
                            rows[1][2] == "contains comma: alpha,beta" &&
                            rows[2][1] == "\"Quoted Team\"" &&
                            rows[2][2].Contains('\n') &&
                            rows[2][3] == string.Empty &&
                            rows[3][2] == "=SUM(A1:A2)" &&
                            rows[3][3] == "0";
        checks.Add(new ProxyProbeEvaluationCheck(
            "Correctness",
            correctnessOk,
            "RFC4180 quoted comma, quote, newline and empty field",
            normalized ?? "<empty>",
            correctnessOk ? "CSV edge values match." : "CSV values, quotes or newlines drifted."));

        var disciplineOk = !HasMarkdownFence(rawPreview);
        checks.Add(new ProxyProbeEvaluationCheck(
            "Discipline",
            disciplineOk,
            "plain CSV only",
            normalized ?? "<empty>",
            disciplineOk ? "No markdown wrapper detected." : "Markdown wrapper detected."));

        var success = parseOk && correctnessOk && disciplineOk;
        return success
            ? new SemanticProbeEvaluation(true, "StructuredOutput CSV edge passed: escaping is stable.", null, normalized, checks)
            : FailWithChecks("StructuredOutput CSV edge failed: escaping or layout drifted.", "The CSV cannot be consumed reliably.", rawPreview, checks);
    }

    private static SemanticProbeEvaluation Fail(string summary, string? error, string? rawPreview)
        => new(false, summary, error, NormalizeRawPreview(rawPreview));

    private static SemanticProbeEvaluation FailWithChecks(
        string summary,
        string? error,
        string? rawPreview,
        IReadOnlyList<ProxyProbeEvaluationCheck> checks)
        => new(false, summary, error, NormalizeRawPreview(rawPreview), checks);

    private static string? NormalizeRawPreview(string? rawPreview)
        => string.IsNullOrWhiteSpace(rawPreview)
            ? null
            : rawPreview.Trim();

    private static int CountProperties(JsonElement root)
        => root.EnumerateObject().Count();

    private static bool StringPropertyEquals(JsonElement root, string propertyName, string expected)
        => root.TryGetProperty(propertyName, out var element) &&
           element.ValueKind == JsonValueKind.String &&
           string.Equals(element.GetString(), expected, StringComparison.Ordinal);

    private static bool NumberPropertyEquals(JsonElement root, string propertyName, int expected)
        => root.TryGetProperty(propertyName, out var element) &&
           element.ValueKind == JsonValueKind.Number &&
           element.TryGetInt32(out var value) &&
           value == expected;

    private static bool DecimalPropertyEquals(JsonElement root, string propertyName, decimal expected)
        => root.TryGetProperty(propertyName, out var element) &&
           DecimalElementEquals(element, expected);

    private static bool DecimalElementEquals(JsonElement element, decimal expected)
        => element.ValueKind == JsonValueKind.Number &&
           element.TryGetDecimal(out var value) &&
           Math.Abs(value - expected) <= 0.0001m;

    private static bool NullProperty(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) &&
           element.ValueKind == JsonValueKind.Null;

    private static bool ChecksPropertyMatches(JsonElement root)
    {
        if (!root.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = checks.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .ToArray();

        return values.Length == 2 &&
               values[0] == "system-first" &&
               values[1] == "json-only";
    }

    private static bool ItemsPropertyMatches(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var itemArray = items.EnumerateArray().ToArray();
        return itemArray.Length == 2 &&
               ItemMatches(itemArray[0], "NET-PROBE-01", 2, 199.90m) &&
               ItemMatches(itemArray[1], "LLM-ROUTE-PLUS", 1, 888.65m);
    }

    private static bool SpecialCharsLookPreserved(string? value)
        => value is not null &&
           value.Contains('\\') &&
           value.Contains('"') &&
           value.Contains('\n') &&
           value.Contains('\t');

    private static bool NestedNullMatches(JsonElement root)
    {
        if (!root.TryGetProperty("nested_null", out var nested) ||
            nested.ValueKind != JsonValueKind.Object ||
            !NullProperty(nested, "a") ||
            !nested.TryGetProperty("b", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = array.EnumerateArray().ToArray();
        return values.Length == 2 &&
               values[0].ValueKind == JsonValueKind.Null &&
               values[1].ValueKind == JsonValueKind.Number &&
               values[1].TryGetInt32(out var value) &&
               value == 1;
    }

    private static bool HasMarkdownFence(string? value)
        => value?.Contains("```", StringComparison.Ordinal) == true;

    private static bool ReasonMathAnswerEquals(string actual, string expected)
    {
        if (decimal.TryParse(expected, NumberStyles.Number, CultureInfo.InvariantCulture, out var expectedDecimal) &&
            TryExtractDecimalAnswer(actual, out var actualDecimal))
        {
            return actualDecimal == expectedDecimal;
        }

        return string.Equals(
            NormalizeCompactAnswer(actual),
            NormalizeCompactAnswer(expected),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseReasonMathContract(
        string[] lines,
        out string answer,
        out string checksLine)
    {
        answer = string.Empty;
        checksLine = string.Empty;

        if (lines.Length == 2 &&
            TryReadLabelValue(lines[0], "ANSWER", out answer) &&
            TryReadLabelValue(lines[1], "CHECKS", out checksLine))
        {
            return true;
        }

        if (lines.Length == 4 &&
            IsLabelOnly(lines[0], "ANSWER") &&
            IsLabelOnly(lines[2], "CHECKS"))
        {
            answer = lines[1].Trim();
            checksLine = lines[3].Trim();
            return answer.Length > 0 && checksLine.Length > 0;
        }

        var normalized = string.Join('\n', lines);
        var match = Regex.Match(
            normalized,
            @"\bANSWER\b\s*:?\s*(?<answer>.*?)(?=\bCHECKS\b\s*:?)\bCHECKS\b\s*:?\s*(?<checks>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success)
        {
            answer = match.Groups["answer"].Value.Trim();
            checksLine = match.Groups["checks"].Value.Trim();
            return answer.Length > 0 && checksLine.Length > 0;
        }

        return false;
    }

    private static bool TryReadLabelValue(string line, string label, out string value)
    {
        value = string.Empty;
        var trimmed = line.Trim();
        if (!trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = trimmed[label.Length..].TrimStart();
        if (!rest.StartsWith(':'))
        {
            return false;
        }

        value = rest[1..].Trim();
        return value.Length > 0;
    }

    private static bool IsLabelOnly(string line, string label)
    {
        var trimmed = line.Trim();
        return string.Equals(trimmed, label, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, $"{label}:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsNormalizedCheckpoint(string actual, string expected)
        => NormalizeCheckpointText(actual).Contains(
            NormalizeCheckpointText(expected),
            StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCheckpointText(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '.' ? char.ToLowerInvariant(ch) : ' ');
        }

        return string.Join(
            " ",
            builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeCompactAnswer(string value)
        => value.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\t", string.Empty, StringComparison.Ordinal)
            .Trim();

    private static bool TryExtractDecimalAnswer(string value, out decimal answer)
    {
        answer = default;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out answer))
        {
            return true;
        }

        var match = Regex.Match(value, @"[-+]?\d+(?:\.\d+)?");
        return match.Success &&
               decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out answer);
    }

    private static bool HasOnlyLimitedSurroundingText(string? value, string fullBlock)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var blockIndex = value.IndexOf(fullBlock, StringComparison.Ordinal);
        if (blockIndex < 0)
        {
            return false;
        }

        var before = value[..blockIndex].Trim();
        var after = value[(blockIndex + fullBlock.Length)..].Trim();
        return before.Length + after.Length <= 240;
    }

    private static string[] SplitNonEmptyLines(string? value)
        => (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IReadOnlyList<CsvRow> ParseCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<CsvRow>();
        }

        List<CsvRow> rows = [];
        List<string> currentRow = [];
        StringBuilder currentField = new();
        var inQuotes = false;

        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (index + 1 < value.Length && value[index + 1] == '"')
                    {
                        currentField.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentField.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    if (currentField.Length > 0)
                    {
                        currentField.Append(ch);
                    }
                    else
                    {
                        inQuotes = true;
                    }
                    break;
                case ',':
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    rows.Add(new CsvRow(currentRow.ToArray()));
                    currentRow.Clear();
                    break;
                default:
                    currentField.Append(ch);
                    break;
            }
        }

        if (inQuotes)
        {
            return Array.Empty<CsvRow>();
        }

        currentRow.Add(currentField.ToString());
        rows.Add(new CsvRow(currentRow.ToArray()));
        return rows;
    }

    private static IReadOnlyList<CodeBlock> ExtractFencedCodeBlocks(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<CodeBlock>();
        }

        List<CodeBlock> blocks = [];
        var searchIndex = 0;
        while (searchIndex < value.Length)
        {
            var start = value.IndexOf("```", searchIndex, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var firstLineEnd = value.IndexOf('\n', start + 3);
            if (firstLineEnd < 0)
            {
                break;
            }

            var language = value[(start + 3)..firstLineEnd].Trim();
            var end = value.IndexOf("```", firstLineEnd + 1, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            var fullEnd = end + 3;
            var code = value[(firstLineEnd + 1)..end].Trim('\r', '\n');
            var fullBlock = value[start..fullEnd].Trim();
            blocks.Add(new CodeBlock(language, code, fullBlock));
            searchIndex = fullEnd;
        }

        return blocks;
    }

    private static bool ItemMatches(JsonElement item, string sku, int quantity, decimal unitPrice)
        => item.ValueKind == JsonValueKind.Object &&
           CountProperties(item) == 3 &&
           StringPropertyEquals(item, "sku", sku) &&
           NumberPropertyEquals(item, "quantity", quantity) &&
           item.TryGetProperty("unit_price", out var priceElement) &&
           DecimalElementEquals(priceElement, unitPrice);

    private sealed record CsvRow(IReadOnlyList<string> Values)
    {
        public int Count => Values.Count;

        public string this[int index] => Values[index];

        public bool SequenceEqual(IEnumerable<string> expected, IEqualityComparer<string> comparer)
            => Values.SequenceEqual(expected, comparer);
    }

    private sealed record CodeBlock(string Language, string Code, string FullBlock);
}
