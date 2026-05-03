using System.IO;
using System.IO.Pipes;
using System.Text;

namespace RelayBench.App.Infrastructure;

public sealed class SingleInstanceActivationService : IDisposable
{
    public const string MutexName = @"Local\RelayBench.App.SingleInstance";
    private const string PipeName = "RelayBench.App.SingleInstance.Activate";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(120);

    private readonly Action _activate;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task? _listenTask;

    public SingleInstanceActivationService(Action activate)
    {
        _activate = activate;
    }

    public void Start()
    {
        if (_listenTask is not null)
        {
            return;
        }

        _listenTask = Task.Run(() => ListenAsync(_cancellationTokenSource.Token));
    }

    public static async Task<bool> TrySendActivationRequestAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using CancellationTokenSource cancellationTokenSource = new(deadline - DateTimeOffset.UtcNow);
                await using NamedPipeClientStream client = new(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                await client.ConnectAsync(250, cancellationTokenSource.Token).ConfigureAwait(false);
                await using StreamWriter writer = new(client, new UTF8Encoding(false), leaveOpen: false)
                {
                    AutoFlush = true
                };
                await writer.WriteLineAsync("activate").ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            await Task.Delay(RetryDelay).ConfigureAwait(false);
        }

        return false;
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream server = new(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using StreamReader reader = new(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                _activate();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppDiagnosticLog.Write("SingleInstanceActivationService.Listen", ex);
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
