using System.IO;
using System.Runtime.InteropServices;

namespace RelayBench.WinUI;

/// <summary>
/// Prevents multiple instances of the application from running simultaneously.
/// Second launches signal the first instance to restore its main window.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Global\RelayBench.WinUI.SingleInstance";
    private const string ActivationEventName = @"Global\RelayBench.WinUI.ActivateMainWindow";
    private const string WindowTitle = "RelayBench";

    private Mutex? _mutex;
    private EventWaitHandle? _activationEvent;
    private ManualResetEvent? _stopActivationListener;
    private Thread? _activationListenerThread;
    private bool _owned;

    /// <summary>
    /// Attempts to acquire the single-instance mutex. If this is the first
    /// instance, also creates the named activation event used by later launches.
    /// </summary>
    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName);

        try
        {
            _owned = _mutex.WaitOne(TimeSpan.Zero);
            if (_owned)
            {
                _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            }

            return _owned;
        }
        catch (AbandonedMutexException)
        {
            _owned = true;
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            return true;
        }
    }

    public void StartActivationListener(Action activateMainWindow)
    {
        if (!_owned || _activationEvent is null || _activationListenerThread is not null)
        {
            return;
        }

        var activationEvent = _activationEvent;
        var stopEvent = new ManualResetEvent(false);
        _stopActivationListener = stopEvent;
        _activationListenerThread = new Thread(() =>
        {
            WaitHandle[] waitHandles = [activationEvent, stopEvent];
            while (true)
            {
                var signaled = WaitHandle.WaitAny(waitHandles);
                if (signaled != 0)
                {
                    return;
                }

                try
                {
                    activateMainWindow();
                }
                catch
                {
                    // A failed foreground attempt should not stop future attempts.
                }
            }
        })
        {
            IsBackground = true,
            Name = "RelayBench single-instance activation"
        };
        _activationListenerThread.Start();
    }

    /// <summary>
    /// Requests that the existing instance restore its main window. Falls back to
    /// direct title-based activation for older instances.
    /// </summary>
    public static void ActivateExistingInstance()
    {
        if (SignalExistingInstance())
        {
            return;
        }

        nint hwnd = FindWindow(null, WindowTitle);
        if (hwnd == 0)
        {
            return;
        }

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }

        ShowWindow(hwnd, SwShow);
        SetForegroundWindow(hwnd);
    }

    private static bool SignalExistingInstance()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            return activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stopActivationListener?.Set();
        _activationListenerThread?.Join(TimeSpan.FromSeconds(1));
        _activationListenerThread = null;
        _stopActivationListener?.Dispose();
        _stopActivationListener = null;
        _activationEvent?.Dispose();
        _activationEvent = null;

        if (_mutex is not null)
        {
            if (_owned)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // The mutex was not owned by this thread.
                }
            }

            _mutex.Dispose();
            _mutex = null;
            _owned = false;
        }
    }

    private const int SwShow = 5;
    private const int SwRestore = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hWnd);
}
