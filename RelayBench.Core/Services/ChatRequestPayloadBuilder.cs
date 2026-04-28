using System.Text;
using System.Text.Json;
using RelayBench.Core.Models;

namespace RelayBench.Core.Services;

internal static class ChatRequestPayloadBuilder
{
    public static string BuildChatCompletionsPayload(
        ChatRequestOptions options,
        IReadOnlyList<ChatMessage> messages)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", options.Model.Trim());
            writer.WriteBoolean("stream", true);
            writer.WriteNumber("temperature", Math.Clamp(options.Temperature, 0d, 2d));
            if (options.MaxTokens > 0)
            {
                writer.WriteNumber("max_tokens", Math.Clamp(options.MaxTokens, 1, 200_000));
            }

            writer.WriteStartArray("messages");
            if (!string.IsNullOrWhiteSpace(options.SystemPrompt))
            {
                writer.WriteStartObject();
                writer.WriteString("role", "system");
                writer.WriteString("content", options.SystemPrompt.Trim());
                writer.WriteEndObject();
            }

            foreach (var message in messages)
            {
                WriteChatMessage(writer, message);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string BuildResponsesPayload(
        ChatRequestOptions options,
        IReadOnlyList<ChatMessage> messages)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", options.Model.Trim());
            writer.WriteBoolean("stream", true);
            if (options.MaxTokens > 0)
            {
                writer.WriteNumber("max_output_tokens", Math.Clamp(options.MaxTokens, 1, 200_000));
            }

            if (options.ReasoningEffort is not ChatReasoningEffort.Auto)
            {
                writer.WriteStartObject("reasoning");
                writer.WriteString("effort", ToWireReasoningEffort(options.ReasoningEffort));
                writer.WriteEndObject();
            }

            writer.WriteString("input", BuildResponsesInput(options, messages));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteChatMessage(Utf8JsonWriter writer, ChatMessage message)
    {
        var role = NormalizeRole(message.Role);
        writer.WriteStartObject();
        writer.WriteString("role", role);

        if (role == "user" && message.Attachments.Count > 0)
        {
            writer.WriteStartArray("content");
            var textContent = BuildUserTextContent(message);
            if (!string.IsNullOrWhiteSpace(textContent))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", textContent);
                writer.WriteEndObject();
            }

            foreach (var attachment in message.Attachments.Where(static item => item.Kind == ChatAttachmentKind.Image))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WriteStartObject("image_url");
                writer.WriteString("url", attachment.Content);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            if (string.IsNullOrWhiteSpace(message.Content) &&
                message.Attachments.All(static item => item.Kind != ChatAttachmentKind.TextFile))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", "Please inspect the attached image.");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
        else
        {
            writer.WriteString("content", message.Content);
        }

        writer.WriteEndObject();
    }

    private static string BuildUserTextContent(ChatMessage message)
    {
        StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            builder.AppendLine(message.Content.Trim());
        }

        foreach (var attachment in message.Attachments.Where(static item => item.Kind == ChatAttachmentKind.TextFile))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine("Attached text file:");
            builder.AppendLine($"File: {attachment.FileName}");
            builder.AppendLine($"```{InferFenceLanguage(attachment.FileName)}");
            builder.AppendLine(attachment.Content);
            builder.AppendLine("```");
        }

        return builder.ToString().Trim();
    }

    private static string BuildResponsesInput(ChatRequestOptions options, IReadOnlyList<ChatMessage> messages)
    {
        StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(options.SystemPrompt))
        {
            builder.AppendLine("[system]");
            builder.AppendLine(options.SystemPrompt.Trim());
            builder.AppendLine();
        }

        foreach (var message in messages)
        {
            builder.AppendLine($"[{NormalizeRole(message.Role)}]");
            builder.AppendLine(BuildUserTextContent(message));
            builder.AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static string NormalizeRole(string role)
        => role.Trim().ToLowerInvariant() switch
        {
            "assistant" => "assistant",
            "system" => "system",
            _ => "user"
        };

    private static string ToWireReasoningEffort(ChatReasoningEffort effort)
        => effort switch
        {
            ChatReasoningEffort.Low => "low",
            ChatReasoningEffort.High => "high",
            _ => "medium"
        };

    private static string InferFenceLanguage(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".xaml" => "xml",
            ".xml" => "xml",
            ".json" => "json",
            ".md" => "markdown",
            ".ps1" => "powershell",
            ".yaml" or ".yml" => "yaml",
            ".csv" => "csv",
            ".log" => "text",
            _ => "text"
        };
}
