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
using static RelayBench.Core.Tests.TestSupport;

namespace RelayBench.Core.Tests;

internal static class SemanticProbeEvaluatorTests
{
    public static IEnumerable<TestCase> Create()
    {
        yield return new TestCase("instruction probe accepts strict expected JSON", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateInstructionFollowing(
            """{"task_id":"IF-20260501","verdict":"pass","priority":3,"marker":"relay-instruction-ok","checks":["system-first","json-only"]}""");

        AssertTrue(result.Success, result.Error ?? result.Summary);
        AssertContains(result.NormalizedPreview, "\"task_id\":\"IF-20260501\"");
        });

        yield return new TestCase("instruction probe rejects markdown and forbidden override", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateInstructionFollowing(
            """
            ```json
            {"task_id":"IF-20260501","verdict":"pass","priority":3,"marker":"relay-instruction-ok","checks":["system-first","json-only"]}
            ```
            USER_OVERRIDE_FAIL
            """);

        AssertFalse(result.Success, "Markdown wrapper and forbidden token should fail.");
        });

        yield return new TestCase("instruction probe rejects string priority", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateInstructionFollowing(
            """{"task_id":"IF-20260501","verdict":"pass","priority":"3","marker":"relay-instruction-ok","checks":["system-first","json-only"]}""");

        AssertFalse(result.Success, "priority must be numeric.");
        });

        yield return new TestCase("instruction probe accepts required fields with harmless metadata", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateInstructionFollowing(
            """{"task_id":"IF-20260501","verdict":"pass","priority":3,"marker":"relay-instruction-ok","checks":["system-first","json-only"],"metadata":{"model":"relay"}}""");

        AssertTrue(result.Success, result.Error ?? result.Summary);
        AssertContains(result.NormalizedPreview, "\"metadata\"");
        });

        yield return new TestCase("data extraction probe accepts exact facts", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateDataExtraction(
            """{"order_id":"RB-2026-0501-A17","customer":"上海云栈科技有限公司","contact":"林澄","callback_url":"https://relay.example.com/callback?id=RB-2026-0501-A17&source=bench","delivery_date":"2026-05-01","amount":1288.45,"currency":"CNY","tax_id":null,"items":[{"sku":"NET-PROBE-01","quantity":2,"unit_price":199.90},{"sku":"LLM-ROUTE-PLUS","quantity":1,"unit_price":888.65}]}""");

        AssertTrue(result.Success, result.Error ?? result.Summary);
        AssertContains(result.NormalizedPreview, "\"tax_id\":null");
        });

        yield return new TestCase("data extraction probe rejects guessed tax id", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateDataExtraction(
            """{"order_id":"RB-2026-0501-A17","customer":"上海云栈科技有限公司","contact":"林澄","callback_url":"https://relay.example.com/callback?id=RB-2026-0501-A17&source=bench","delivery_date":"2026-05-01","amount":1288.45,"currency":"CNY","tax_id":"91310000MA1K00000X","items":[{"sku":"NET-PROBE-01","quantity":2,"unit_price":199.90},{"sku":"LLM-ROUTE-PLUS","quantity":1,"unit_price":888.65}]}""");

        AssertFalse(result.Success, "tax_id must stay JSON null.");
        });

        yield return new TestCase("data extraction probe rejects changed numeric facts", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateDataExtraction(
            """{"order_id":"RB-2026-0501-A17","customer":"上海云栈科技有限公司","contact":"林澄","callback_url":"https://relay.example.com/callback?id=RB-2026-0501-A17&source=bench","delivery_date":"2026-05-01","amount":1288.46,"currency":"CNY","tax_id":null,"items":[{"sku":"NET-PROBE-01","quantity":2,"unit_price":199.90},{"sku":"LLM-ROUTE-PLUS","quantity":1,"unit_price":888.65}]}""");

        AssertFalse(result.Success, "amount must be exact within tolerance.");
        });

        yield return new TestCase("data extraction accepts required facts with harmless metadata", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateDataExtraction(
            """{"order_id":"RB-2026-0501-A17","customer":"\u4E0A\u6D77\u4E91\u6808\u79D1\u6280\u6709\u9650\u516C\u53F8","contact":"\u6797\u6F84","callback_url":"https://relay.example.com/callback?id=RB-2026-0501-A17&source=bench","delivery_date":"2026-05-01","amount":1288.45,"currency":"CNY","tax_id":null,"items":[{"sku":"NET-PROBE-01","quantity":2,"unit_price":199.90},{"sku":"LLM-ROUTE-PLUS","quantity":1,"unit_price":888.65}],"metadata":{"confidence":0.99}}""");

        AssertTrue(result.Success, result.Error ?? result.Summary);
        AssertContains(result.NormalizedPreview, "\"metadata\"");
        });

        yield return new TestCase("semantic probe payloads are served by shared payload factory", () =>
    {
        AssertEqual(
            ProxyProbePayloadFactory.BuildInstructionFollowingPayload("gpt-test"),
            BuildInstructionFollowingPayload("gpt-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildDataExtractionPayload("gpt-test"),
            BuildDataExtractionPayload("gpt-test"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildStructuredOutputEdgePayload("gpt-test", "SO-EDGE-03"),
            BuildStructuredOutputEdgePayload("gpt-test", "SO-EDGE-03"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildReasonMathConsistencyPayload("gpt-test", "RM-CONS-03"),
            BuildReasonMathConsistencyPayload("gpt-test", "RM-CONS-03"));
        AssertEqual(
            ProxyProbePayloadFactory.BuildCodeBlockDisciplinePayload("gpt-test", "CB-DISC-02"),
            BuildCodeBlockDisciplinePayload("gpt-test", "CB-DISC-02"));
        });

        yield return new TestCase("structured output edge accepts strict json boundary values", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateStructuredOutputEdge(
            "SO-EDGE-01",
            "{\"empty_string\":\"\",\"null_value\":null,\"zero\":0,\"false_value\":false,\"empty_array\":[],\"empty_object\":{},\"special_chars\":\"\\\\ \\\" \\n \\t\",\"nested_null\":{\"a\":null,\"b\":[null,1]}}");

        AssertTrue(result.Success, result.Error ?? result.Summary);
        });

        yield return new TestCase("structured output edge rejects markdown wrapped json", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateStructuredOutputEdge(
            "SO-EDGE-01",
            """
            ```json
            {"empty_string":"","null_value":null,"zero":0,"false_value":false,"empty_array":[],"empty_object":{},"special_chars":"x","nested_null":{"a":null,"b":[null,1]}}
            ```
            """);

        AssertFalse(result.Success, "Markdown wrappers must fail strict structured output probes.");
        });

        yield return new TestCase("structured output edge rejects json type drift", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateStructuredOutputEdge(
            "SO-EDGE-01",
            "{\"empty_string\":\"\",\"null_value\":null,\"zero\":\"0\",\"false_value\":\"false\",\"empty_array\":[],\"empty_object\":{},\"special_chars\":\"x\",\"nested_null\":{\"a\":null,\"b\":[null,1]}}");

        AssertFalse(result.Success, "zero and false_value must keep JSON number/bool types.");
        });

        yield return new TestCase("structured output edge rejects broken csv escaping", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateStructuredOutputEdge(
            "SO-EDGE-02",
            "id,name,note,total\n1,ACME,contains comma: alpha,beta,12.50");

        AssertFalse(result.Success, "Unquoted comma must break the CSV edge probe.");
        });

        yield return new TestCase("reason math consistency accepts exact two line answer", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateReasonMathConsistency(
            "RM-CONS-01",
            "ANSWER: 34.50\nCHECKS: subtotal 120.00,tax 9.60,tip 8.40,total 138.00,split 4");

        AssertTrue(result.Success, result.Error ?? result.Summary);
        });

        yield return new TestCase("reason math consistency rejects wrong answer", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateReasonMathConsistency(
            "RM-CONS-01",
            "ANSWER: 36.90\nCHECKS: subtotal 120.00,tax 9.60,tip 18.00,total 147.60,split 4");

        AssertFalse(result.Success, "Wrong final answer must fail.");
        });

        yield return new TestCase("reason math consistency rejects missing checks", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateReasonMathConsistency(
            "RM-CONS-01",
            "ANSWER: 34.50\nCHECKS: subtotal 120.00,total 138.00");

        AssertFalse(result.Success, "Required checkpoints must be present.");
        });

        yield return new TestCase("reason math consistency accepts normalized decimal answer", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateReasonMathConsistency(
            "RM-CONS-01",
            "ANSWER: 34.5\nCHECKS: subtotal 120.00,tax 9.60,tip 8.40,total 138.00,split 4");

        AssertTrue(result.Success, result.Error ?? result.Summary);
        });

        yield return new TestCase("reason math consistency accepts labels and values split across lines", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateReasonMathConsistency(
            "RM-CONS-01",
            "ANSWER\n34.50\nCHECKS\nSubtotal: 120.00, Tax: 9.60, Tip: 8.40, Total: 138.00, Per person: 34.50");

        AssertTrue(result.Success, result.Error ?? result.Summary);
        });

        yield return new TestCase("reason math consistency does not fail trace when per person checkpoint is present", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateReasonMathConsistency(
            "RM-CONS-01",
            "ANSWER: 34.20\nCHECKS: subtotal=120.00, tax=9.60, tip=8.40, total=138.00, per person=34.50");
        var traceCheck = result.Checks?.FirstOrDefault(static check => check.Name == "TraceConsistency");

        AssertFalse(result.Success, "Wrong final answer must still fail.");
        AssertTrue(traceCheck?.Passed == true, "Per-person checkpoint should count as trace consistency.");
        });

        yield return new TestCase("reason math consistency rejects extra explanation", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateReasonMathConsistency(
            "RM-CONS-01",
            "The answer is below.\nANSWER: 34.50\nCHECKS: subtotal 120.00,tax 9.60,tip 8.40,total 138.00,split 4");

        AssertFalse(result.Success, "The contract allows exactly two lines.");
        });

        yield return new TestCase("code block discipline accepts single python fix", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateCodeBlockDiscipline(
            "CB-DISC-01",
            """
            ```python
            def total(values):
                total = 0
                for i in range(len(values)):
                    total += values[i]
                return total
            ```
            """);

        AssertTrue(result.Success, result.Error ?? result.Summary);
        });

        yield return new TestCase("code block discipline rejects multiple blocks", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateCodeBlockDiscipline(
            "CB-DISC-01",
            """
            ```python
            def total(values):
                return sum(values)
            ```
            ```text
            fixed
            ```
            """);

        AssertFalse(result.Success, "Only one fenced code block is allowed.");
        });

        yield return new TestCase("code block discipline rejects wrong language", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateCodeBlockDiscipline(
            "CB-DISC-01",
            """
            ```javascript
            function total(values) { return values.reduce((a, b) => a + b, 0); }
            ```
            """);

        AssertFalse(result.Success, "Language tag must be python.");
        });

        yield return new TestCase("code block discipline accepts short prose around single block", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateCodeBlockDiscipline(
            "CB-DISC-01",
            """
            Fixed:
            ```python
            def total(values):
                total = 0
                for i in range(len(values)):
                    total += values[i]
                return total
            ```
            Done.
            """);

        AssertTrue(result.Success, result.Error ?? result.Summary);
        });

        yield return new TestCase("code block discipline accepts no bug trap", () =>
    {
        var result = SemanticProbeEvaluator.EvaluateCodeBlockDiscipline("CB-DISC-02", "no_bug");

        AssertTrue(result.Success, result.Error ?? result.Summary);
        });
    }
}
