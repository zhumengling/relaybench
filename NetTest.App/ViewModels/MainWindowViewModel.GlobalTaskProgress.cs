namespace NetTest.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string GlobalTaskProgressStateIdle = "idle";
    private const string GlobalTaskProgressStateRunning = "running";
    private const string GlobalTaskProgressStateCompleted = "completed";
    private const string GlobalTaskProgressStateStopped = "stopped";
    private const string GlobalTaskProgressStateFailed = "failed";
    private const int GlobalTaskProgressDotsDelayMilliseconds = 420;
    private const int GlobalTaskProgressHideDelayMilliseconds = 1500;

    private bool _isGlobalTaskProgressVisible;
    private string _globalTaskProgressTitle = string.Empty;
    private string _globalTaskProgressShortStatus = "\u51C6\u5907\u4E2D";
    private double _globalTaskProgressPercent;
    private string _globalTaskProgressStateKey = GlobalTaskProgressStateIdle;
    private int _globalTaskProgressDotCount = 1;
    private CancellationTokenSource? _globalTaskProgressDotsCancellationSource;
    private CancellationTokenSource? _globalTaskProgressHideCancellationSource;
    private int _globalTaskProgressSessionId;

    public bool IsGlobalTaskProgressVisible
    {
        get => _isGlobalTaskProgressVisible;
        private set => SetProperty(ref _isGlobalTaskProgressVisible, value);
    }

    public string GlobalTaskProgressTitle
    {
        get => _globalTaskProgressTitle;
        private set => SetProperty(ref _globalTaskProgressTitle, value);
    }

    public string GlobalTaskProgressShortStatus
    {
        get => _globalTaskProgressShortStatus;
        private set
        {
            if (SetProperty(ref _globalTaskProgressShortStatus, NormalizeGlobalTaskProgressText(value, "\u6267\u884C\u4E2D")))
            {
                OnPropertyChanged(nameof(GlobalTaskProgressStatusText));
            }
        }
    }

    public double GlobalTaskProgressPercent
    {
        get => _globalTaskProgressPercent;
        private set
        {
            var normalized = Math.Clamp(value, 0d, 100d);
            if (SetProperty(ref _globalTaskProgressPercent, normalized))
            {
                OnPropertyChanged(nameof(GlobalTaskProgressFraction));
                OnPropertyChanged(nameof(GlobalTaskProgressPercentText));
                OnPropertyChanged(nameof(GlobalTaskProgressStatusText));
            }
        }
    }

    public double GlobalTaskProgressFraction
        => Math.Clamp(GlobalTaskProgressPercent / 100d, 0d, 1d);

    public string GlobalTaskProgressPercentText
        => $"{Math.Round(GlobalTaskProgressPercent, MidpointRounding.AwayFromZero):0}%";

    public string GlobalTaskProgressAnimatedDots
        => _globalTaskProgressDotCount switch
        {
            2 => "\u00B7\u00B7",
            3 => "\u00B7\u00B7\u00B7",
            _ => "\u00B7"
        };

    public string GlobalTaskProgressStatusText
        => IsGlobalTaskProgressRunning
            ? $"{GlobalTaskProgressShortStatus} {GlobalTaskProgressAnimatedDots} {GlobalTaskProgressPercentText}"
            : $"{GlobalTaskProgressShortStatus} {GlobalTaskProgressPercentText}";

    public string GlobalTaskProgressStateKey
    {
        get => _globalTaskProgressStateKey;
        private set
        {
            if (SetProperty(ref _globalTaskProgressStateKey, value))
            {
                OnPropertyChanged(nameof(IsGlobalTaskProgressRunning));
                OnPropertyChanged(nameof(IsGlobalTaskProgressCompleted));
                OnPropertyChanged(nameof(IsGlobalTaskProgressStopped));
                OnPropertyChanged(nameof(IsGlobalTaskProgressFailed));
                OnPropertyChanged(nameof(GlobalTaskProgressStatusText));
            }
        }
    }

    public bool IsGlobalTaskProgressRunning
        => string.Equals(GlobalTaskProgressStateKey, GlobalTaskProgressStateRunning, StringComparison.Ordinal);

    public bool IsGlobalTaskProgressCompleted
        => string.Equals(GlobalTaskProgressStateKey, GlobalTaskProgressStateCompleted, StringComparison.Ordinal);

    public bool IsGlobalTaskProgressStopped
        => string.Equals(GlobalTaskProgressStateKey, GlobalTaskProgressStateStopped, StringComparison.Ordinal);

    public bool IsGlobalTaskProgressFailed
        => string.Equals(GlobalTaskProgressStateKey, GlobalTaskProgressStateFailed, StringComparison.Ordinal);

    private void ShowGlobalTaskProgress(string title, string shortStatus, double percent = 0d)
    {
        CancelGlobalTaskProgressHide();
        _globalTaskProgressSessionId++;
        GlobalTaskProgressTitle = NormalizeGlobalTaskProgressText(title, "\u4EFB\u52A1\u8FDB\u5EA6");
        GlobalTaskProgressShortStatus = shortStatus;
        GlobalTaskProgressPercent = percent;
        GlobalTaskProgressStateKey = GlobalTaskProgressStateRunning;
        IsGlobalTaskProgressVisible = true;
        StartGlobalTaskProgressDots();
    }

    private void UpdateGlobalTaskProgress(string shortStatus, double percent, string? title = null)
    {
        CancelGlobalTaskProgressHide();
        if (!string.IsNullOrWhiteSpace(title))
        {
            GlobalTaskProgressTitle = NormalizeGlobalTaskProgressText(title, "\u4EFB\u52A1\u8FDB\u5EA6");
        }

        if (!IsGlobalTaskProgressVisible)
        {
            IsGlobalTaskProgressVisible = true;
        }

        if (!IsGlobalTaskProgressRunning)
        {
            GlobalTaskProgressStateKey = GlobalTaskProgressStateRunning;
            StartGlobalTaskProgressDots();
        }

        GlobalTaskProgressShortStatus = shortStatus;
        GlobalTaskProgressPercent = percent;
    }

    private void UpdateGlobalTaskProgress(int completed, int total, string shortStatus, string? title = null)
    {
        var percent = total <= 0
            ? 0d
            : Math.Clamp((double)completed / total * 100d, 0d, 100d);
        UpdateGlobalTaskProgress(shortStatus, percent, title);
    }

    private void CompleteGlobalTaskProgress(string? shortStatus = null)
        => FinalizeGlobalTaskProgress(
            GlobalTaskProgressStateCompleted,
            shortStatus ?? "\u5DF2\u5B8C\u6210",
            100d);

    private void StopGlobalTaskProgress(string? shortStatus = null, double? percent = null)
        => FinalizeGlobalTaskProgress(
            GlobalTaskProgressStateStopped,
            shortStatus ?? "\u5DF2\u505C\u6B62",
            percent ?? GlobalTaskProgressPercent);

    private void FailGlobalTaskProgress(string? shortStatus = null, double? percent = null)
        => FinalizeGlobalTaskProgress(
            GlobalTaskProgressStateFailed,
            shortStatus ?? "\u5DF2\u5931\u8D25",
            percent ?? GlobalTaskProgressPercent);

    private void FinalizeGlobalTaskProgress(string stateKey, string shortStatus, double percent)
    {
        CancelGlobalTaskProgressHide();
        StopGlobalTaskProgressDots();
        if (!IsGlobalTaskProgressVisible)
        {
            IsGlobalTaskProgressVisible = true;
        }

        GlobalTaskProgressShortStatus = shortStatus;
        GlobalTaskProgressPercent = percent;
        GlobalTaskProgressStateKey = stateKey;
        ScheduleGlobalTaskProgressHide();
    }

    private void StartGlobalTaskProgressDots()
    {
        StopGlobalTaskProgressDots();
        _globalTaskProgressDotCount = 1;
        OnPropertyChanged(nameof(GlobalTaskProgressAnimatedDots));
        OnPropertyChanged(nameof(GlobalTaskProgressStatusText));

        var cancellationSource = new CancellationTokenSource();
        _globalTaskProgressDotsCancellationSource = cancellationSource;
        _ = RunGlobalTaskProgressDotsLoopAsync(cancellationSource.Token);
    }

    private void StopGlobalTaskProgressDots()
    {
        _globalTaskProgressDotsCancellationSource?.Cancel();
        _globalTaskProgressDotsCancellationSource?.Dispose();
        _globalTaskProgressDotsCancellationSource = null;

        if (_globalTaskProgressDotCount != 1)
        {
            _globalTaskProgressDotCount = 1;
            OnPropertyChanged(nameof(GlobalTaskProgressAnimatedDots));
            OnPropertyChanged(nameof(GlobalTaskProgressStatusText));
        }
    }

    private async Task RunGlobalTaskProgressDotsLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(GlobalTaskProgressDotsDelayMilliseconds, cancellationToken);
                if (cancellationToken.IsCancellationRequested || !IsGlobalTaskProgressRunning)
                {
                    break;
                }

                _globalTaskProgressDotCount = _globalTaskProgressDotCount >= 3
                    ? 1
                    : _globalTaskProgressDotCount + 1;
                OnPropertyChanged(nameof(GlobalTaskProgressAnimatedDots));
                OnPropertyChanged(nameof(GlobalTaskProgressStatusText));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ScheduleGlobalTaskProgressHide()
    {
        CancelGlobalTaskProgressHide();
        var sessionId = _globalTaskProgressSessionId;
        var cancellationSource = new CancellationTokenSource();
        _globalTaskProgressHideCancellationSource = cancellationSource;
        _ = HideGlobalTaskProgressAsync(sessionId, cancellationSource.Token);
    }

    private void CancelGlobalTaskProgressHide()
    {
        _globalTaskProgressHideCancellationSource?.Cancel();
        _globalTaskProgressHideCancellationSource?.Dispose();
        _globalTaskProgressHideCancellationSource = null;
    }

    private async Task HideGlobalTaskProgressAsync(int sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(GlobalTaskProgressHideDelayMilliseconds, cancellationToken);
            if (cancellationToken.IsCancellationRequested ||
                sessionId != _globalTaskProgressSessionId ||
                IsGlobalTaskProgressRunning)
            {
                return;
            }

            IsGlobalTaskProgressVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string NormalizeGlobalTaskProgressText(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();
}
