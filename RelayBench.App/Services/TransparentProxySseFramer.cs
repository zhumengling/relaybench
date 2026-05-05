using System.Runtime.CompilerServices;
using System.Text;
using System.Net.Http;
using System.IO;

namespace RelayBench.App.Services;

internal sealed class TransparentProxySseFramer
{
    public async IAsyncEnumerable<TransparentProxySseEvent> ReadEventsAsync(
        HttpContent content,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        StringBuilder dataBuilder = new();
        string eventName = string.Empty;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (dataBuilder.Length > 0)
                {
                    yield return new TransparentProxySseEvent(eventName, dataBuilder.ToString());
                    dataBuilder.Clear();
                    eventName = string.Empty;
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (dataBuilder.Length > 0)
            {
                dataBuilder.Append('\n');
            }

            dataBuilder.Append(line[5..].TrimStart());
        }

        if (dataBuilder.Length > 0)
        {
            yield return new TransparentProxySseEvent(eventName, dataBuilder.ToString());
        }
    }
}

internal sealed record TransparentProxySseEvent(string EventName, string Data);
