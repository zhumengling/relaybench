using System.IO;
using System.Text;
using RelayBench.App.Infrastructure;
using RelayBench.Core.Models;

namespace RelayBench.App.Services;

public sealed class ModelChatExportService
{
    private readonly string _exportDirectory;

    public ModelChatExportService()
        : this(Path.Combine(RelayBenchPaths.ExportsDirectory, "model-chat"))
    {
    }

    public ModelChatExportService(string exportDirectory)
    {
        _exportDirectory = string.IsNullOrWhiteSpace(exportDirectory)
            ? Path.Combine(RelayBenchPaths.ExportsDirectory, "model-chat")
            : exportDirectory;
    }

    public string ExportMarkdown(string title, IReadOnlyList<ChatMessage> messages)
    {
        var path = BuildFilePath(title, ".md");
        File.WriteAllText(path, BuildMarkdown(title, messages), new UTF8Encoding(false));
        return path;
    }

    public string ExportText(string title, IReadOnlyList<ChatMessage> messages)
    {
        var path = BuildFilePath(title, ".txt");
        File.WriteAllText(path, BuildText(title, messages), new UTF8Encoding(false));
        return path;
    }

    private string BuildFilePath(string title, string extension)
    {
        Directory.CreateDirectory(_exportDirectory);
        var stamp = DateTimeOffset.Now.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        return Path.Combine(_exportDirectory, $"model-chat-{stamp}-{SanitizeFileName(title)}{extension}");
    }

    private static string BuildMarkdown(string title, IReadOnlyList<ChatMessage> messages)
    {
        StringBuilder builder = new();
        builder.AppendLine($"# {NormalizeTitle(title)}");
        builder.AppendLine();
        builder.AppendLine($"> 导出时间：{DateTimeOffset.Now.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();

        foreach (var message in messages)
        {
            builder.AppendLine($"## {ToRoleLabel(message.Role)}");
            builder.AppendLine();
            builder.AppendLine($"- 时间：{message.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
            if (message.Metrics is not null)
            {
                builder.AppendLine($"- 指标：{BuildMetricsSummary(message.Metrics)}");
            }

            if (message.Attachments.Count > 0)
            {
                builder.AppendLine("- 附件：");
                foreach (var attachment in message.Attachments)
                {
                    builder.AppendLine($"  - {ToAttachmentKindLabel(attachment.Kind)}：{attachment.FileName} ({FormatBytes(attachment.SizeBytes)})");
                }
            }

            if (!string.IsNullOrWhiteSpace(message.Error))
            {
                builder.AppendLine($"- 错误：{message.Error}");
            }

            builder.AppendLine();
            builder.AppendLine(string.IsNullOrWhiteSpace(message.Content) ? "（空）" : message.Content.TrimEnd());
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildText(string title, IReadOnlyList<ChatMessage> messages)
    {
        StringBuilder builder = new();
        builder.AppendLine(NormalizeTitle(title));
        builder.AppendLine($"导出时间：{DateTimeOffset.Now.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine(new string('=', 48));
        builder.AppendLine();

        foreach (var message in messages)
        {
            builder.AppendLine($"[{ToRoleLabel(message.Role)}] {message.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            if (message.Metrics is not null)
            {
                builder.AppendLine(BuildMetricsSummary(message.Metrics));
            }

            if (message.Attachments.Count > 0)
            {
                builder.AppendLine("附件：" + string.Join("，", message.Attachments.Select(static attachment => attachment.FileName)));
            }

            if (!string.IsNullOrWhiteSpace(message.Error))
            {
                builder.AppendLine($"错误：{message.Error}");
            }

            builder.AppendLine(string.IsNullOrWhiteSpace(message.Content) ? "（空）" : message.Content.TrimEnd());
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildMetricsSummary(ChatMessageMetrics metrics)
    {
        var ttft = metrics.FirstTokenLatency is null
            ? "TTFT --"
            : $"TTFT {metrics.FirstTokenLatency.Value.TotalMilliseconds:F0} ms";
        var speed = metrics.CharactersPerSecond is null
            ? "-- chars/s"
            : $"{metrics.CharactersPerSecond.Value:F1} chars/s";
        return $"{metrics.WireApi} | {metrics.Elapsed.TotalMilliseconds:F0} ms | {ttft} | {speed}";
    }

    private static string ToRoleLabel(string role)
        => role switch
        {
            "user" => "用户",
            "assistant" => "助手",
            "assistant-group" => "多模型回答",
            _ => role
        };

    private static string ToAttachmentKindLabel(ChatAttachmentKind kind)
        => kind == ChatAttachmentKind.Image ? "图片" : "文本文件";

    private static string NormalizeTitle(string title)
        => string.IsNullOrWhiteSpace(title) ? "大模型对话" : title.Trim();

    private static string SanitizeFileName(string title)
    {
        var normalized = NormalizeTitle(title);
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            normalized = normalized.Replace(invalid, '-');
        }

        normalized = string.Join('-', normalized.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 48 ? normalized : normalized[..48];
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:F1} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:F1} KB" : $"{bytes} B";
    }
}
