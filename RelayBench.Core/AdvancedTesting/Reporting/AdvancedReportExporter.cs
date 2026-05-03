using System.Text;
using System.Text.Json;
using RelayBench.Core.AdvancedTesting.Models;

namespace RelayBench.Core.AdvancedTesting.Reporting;

public sealed class AdvancedReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string BuildMarkdown(AdvancedTestRunResult result)
    {
        StringBuilder builder = new();
        builder.AppendLine("# RelayBench Advanced Test Lab Report");
        builder.AppendLine();
        builder.AppendLine($"- Base URL: {result.Endpoint.BaseUrl}");
        builder.AppendLine($"- Model: {result.Endpoint.Model}");
        builder.AppendLine($"- Started: {result.StartedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- Completed: {result.CompletedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- Overall: {result.Scores.Overall:0.0}");
        builder.AppendLine($"- Codex Fit: {result.Scores.CodexFit:0.0}");
        builder.AppendLine($"- Agent Fit: {result.Scores.AgentFit:0.0}");
        builder.AppendLine($"- RAG Fit: {result.Scores.RagFit:0.0}");
        builder.AppendLine($"- Chat Experience: {result.Scores.ChatExperience:0.0}");
        builder.AppendLine();
        AppendRedTeamRisk(builder, result);
        builder.AppendLine();
        builder.AppendLine("## Results");
        builder.AppendLine();
        builder.AppendLine("| Test | Status | Score | Risk | Error | Summary |");
        builder.AppendLine("| --- | --- | ---: | --- | --- | --- |");
        foreach (var item in result.Results)
        {
            builder.AppendLine($"| {Escape(item.DisplayName)} | {item.Status} | {item.Score:0.0} | {item.RiskLevel} | {item.ErrorKind} | {Escape(item.ResponseSummary)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Suggestions");
        builder.AppendLine();
        foreach (var item in result.Results.Where(static item => item.Status is AdvancedTestStatus.Failed or AdvancedTestStatus.Partial))
        {
            builder.AppendLine($"### {item.DisplayName}");
            foreach (var suggestion in item.Suggestions)
            {
                builder.AppendLine($"- {suggestion}");
            }
        }

        return builder.ToString();
    }

    public string BuildJson(AdvancedTestRunResult result)
        => JsonSerializer.Serialize(result, JsonOptions);

    private static string Escape(string value)
        => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static void AppendRedTeamRisk(StringBuilder builder, AdvancedTestRunResult result)
    {
        var redTeamResults = result.Results
            .Where(static item => item.Category == AdvancedTestCategory.SecurityRedTeam)
            .ToArray();
        if (redTeamResults.Length == 0)
        {
            builder.AppendLine("## Data Security Risk");
            builder.AppendLine();
            builder.AppendLine("- Status: Not run");
            return;
        }

        var failed = redTeamResults.Where(static item => item.Status == AdvancedTestStatus.Failed).ToArray();
        var partial = redTeamResults.Where(static item => item.Status == AdvancedTestStatus.Partial).ToArray();
        var passed = redTeamResults.Count(static item => item.Status == AdvancedTestStatus.Passed);
        var status = failed.Any(static item => item.RiskLevel == AdvancedRiskLevel.Critical)
            ? "Critical"
            : failed.Length > 0
                ? "High"
                : partial.Length > 0
                    ? "Medium"
                    : "Low";

        builder.AppendLine("## Data Security Risk");
        builder.AppendLine();
        builder.AppendLine($"- Status: {status}");
        builder.AppendLine($"- Passed: {passed}");
        builder.AppendLine($"- Partial: {partial.Length}");
        builder.AppendLine($"- Failed: {failed.Length}");
        if (failed.Length > 0)
        {
            builder.AppendLine($"- Failures: {Escape(string.Join(", ", failed.Select(static item => item.DisplayName)))}");
        }
    }
}
