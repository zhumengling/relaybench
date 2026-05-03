using RelayBench.Core.AdvancedTesting.TestCases;
using Xunit;

namespace RelayBench.Core.Tests.AdvancedTesting;

public sealed class ToolCallingAdvancedTestCaseTests
{
    [Fact]
    public void InspectToolCall_AcceptsResponsesFunctionCallShape()
    {
        const string body = """
            {
              "output": [
                {
                  "type": "function_call",
                  "name": "search_docs",
                  "arguments": "{\"query\":\"relay cache isolation\",\"limit\":5}"
                }
              ]
            }
            """;

        var inspection = ToolCallingBasicTestCase.InspectToolCall(body, "search_docs");

        Assert.True(inspection.HasValidToolCall);
    }

    [Fact]
    public void InspectToolCall_AcceptsAnthropicToolUseShape()
    {
        const string body = """
            {
              "content": [
                {
                  "type": "tool_use",
                  "name": "search_docs",
                  "input": {
                    "query": "relay cache isolation",
                    "limit": 5
                  }
                }
              ]
            }
            """;

        var inspection = ToolCallingBasicTestCase.InspectToolCall(body, "search_docs");

        Assert.True(inspection.HasValidToolCall);
    }

    [Fact]
    public void InspectStreamToolCall_AcceptsResponsesFunctionCallEvents()
    {
        string[] dataLines =
        [
            """{"type":"response.output_item.added","item":{"type":"function_call","name":"search_docs","arguments":""}}""",
            """{"type":"response.function_call_arguments.delta","delta":"{\"query\":\"relay cache isolation\","}""",
            """{"type":"response.function_call_arguments.delta","delta":"\"limit\":5}"}""",
            """{"type":"response.completed"}"""
        ];

        var inspection = ToolCallingStreamTestCase.InspectStreamToolCall(dataLines);

        Assert.True(inspection.HasToolDelta);
        Assert.True(inspection.NameMatches);
        Assert.True(inspection.ArgumentsUsable);
        Assert.True(inspection.SawDone);
    }

    [Fact]
    public void InspectStreamToolCall_AcceptsAnthropicToolUseEvents()
    {
        string[] dataLines =
        [
            """{"type":"content_block_start","content_block":{"type":"tool_use","name":"search_docs","input":{}}}""",
            """{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{\"query\":\"relay cache isolation\","}}""",
            """{"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"\"limit\":5}"}}""",
            """{"type":"message_stop"}"""
        ];

        var inspection = ToolCallingStreamTestCase.InspectStreamToolCall(dataLines);

        Assert.True(inspection.HasToolDelta);
        Assert.True(inspection.NameMatches);
        Assert.True(inspection.ArgumentsUsable);
        Assert.True(inspection.SawDone);
    }
}
