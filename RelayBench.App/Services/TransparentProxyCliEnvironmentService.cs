namespace RelayBench.App.Services;

internal sealed class TransparentProxyCliEnvironmentService
{
    private const string LocalToken = "relaybench-local";

    public TransparentProxyCliEnvironmentSnapshot Build(int port)
    {
        var normalizedPort = port is >= 1 and <= 65535 ? port : 17880;
        var baseUrl = $"http://127.0.0.1:{normalizedPort}";
        var openAiBaseUrl = $"{baseUrl}/v1";
        var anthropicBaseUrl = baseUrl;
        var powershell = string.Join(
            Environment.NewLine,
            [
                $"$env:OPENAI_BASE_URL = '{openAiBaseUrl}'",
                $"$env:OPENAI_API_KEY = '{LocalToken}'",
                $"$env:ANTHROPIC_BASE_URL = '{anthropicBaseUrl}'",
                $"$env:ANTHROPIC_AUTH_TOKEN = '{LocalToken}'",
                $"$env:RELAYBENCH_BASE_URL = '{openAiBaseUrl}'"
            ]);
        var cmd = string.Join(
            Environment.NewLine,
            [
                $"set OPENAI_BASE_URL={openAiBaseUrl}",
                $"set OPENAI_API_KEY={LocalToken}",
                $"set ANTHROPIC_BASE_URL={anthropicBaseUrl}",
                $"set ANTHROPIC_AUTH_TOKEN={LocalToken}",
                $"set RELAYBENCH_BASE_URL={openAiBaseUrl}"
            ]);
        return new TransparentProxyCliEnvironmentSnapshot(
            openAiBaseUrl,
            anthropicBaseUrl,
            LocalToken,
            powershell,
            cmd,
            "These snippets only affect the current terminal session and do not change system proxy settings.");
    }
}

internal sealed record TransparentProxyCliEnvironmentSnapshot(
    string OpenAiBaseUrl,
    string AnthropicBaseUrl,
    string LocalToken,
    string PowerShell,
    string Cmd,
    string Notes);
