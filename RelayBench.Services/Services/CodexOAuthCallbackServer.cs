using System.Net;
using System.Text;

namespace RelayBench.Services;

internal sealed class CodexOAuthCallbackServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<CodexOAuthCallbackResult, bool> _onCallback;
    private CancellationTokenSource? _cancellationSource;
    private Task? _listenTask;
    private bool _disposed;

    public CodexOAuthCallbackServer(Func<CodexOAuthCallbackResult, bool> onCallback)
    {
        _onCallback = onCallback;
    }

    public void Start()
    {
        if (_listener.IsListening)
        {
            return;
        }

        _listener.Prefixes.Add("http://127.0.0.1:1455/auth/callback/");
        _listener.Prefixes.Add("http://localhost:1455/auth/callback/");
        _listener.Start();
        _cancellationSource = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _listenTask = Task.Run(() => ListenAsync(_cancellationSource.Token));
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(StopQuietly);
        while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch when (cancellationToken.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            var result = ParseCallback(context.Request.Url?.ToString() ?? string.Empty);
            var accepted = _onCallback(result);
            await WriteResultPageAsync(context.Response, accepted, cancellationToken);
            if (accepted)
            {
                StopQuietly();
                return;
            }
        }
    }

    public static CodexOAuthCallbackResult ParseCallback(string callbackUrl)
    {
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri))
        {
            return new CodexOAuthCallbackResult(string.Empty, string.Empty, "invalid_callback", "Invalid callback URL.");
        }

        var query = ParseQuery(uri.Query);
        return new CodexOAuthCallbackResult(
            query.TryGetValue("code", out var code) ? code : string.Empty,
            query.TryGetValue("state", out var state) ? state : string.Empty,
            query.TryGetValue("error", out var error) ? error : string.Empty,
            query.TryGetValue("error_description", out var description) ? description : string.Empty);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (query ?? string.Empty).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = separator >= 0 ? part[..separator] : part;
            var value = separator >= 0 ? part[(separator + 1)..] : string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            values[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return values;
    }

    private static async Task WriteResultPageAsync(HttpListenerResponse response, bool accepted, CancellationToken cancellationToken)
    {
        var text = accepted
            ? "Codex OAuth login completed. You can return to RelayBench."
            : "Codex OAuth callback was not accepted. Please return to RelayBench and retry.";
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>RelayBench Codex OAuth</title></head>" +
                   $"<body style=\"font-family:Segoe UI,Arial,sans-serif;margin:40px;color:#0f172a\"><h2>{WebUtility.HtmlEncode(text)}</h2></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = accepted ? 200 : 400;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.LongLength;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.OutputStream.Close();
    }

    private void StopQuietly()
    {
        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cancellationSource?.Cancel();
        }
        catch
        {
        }

        StopQuietly();
        _listener.Close();
        _cancellationSource?.Dispose();
    }
}
