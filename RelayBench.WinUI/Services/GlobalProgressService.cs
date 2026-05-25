namespace RelayBench.WinUI.Services;

/// <summary>
/// A static service that any ViewModel can use to report progress to the shell's progress bar.
/// </summary>
public static class GlobalProgressService
{
    /// <summary>
    /// Raised when progress changes. Tuple contains (Percent 0-100, Step description).
    /// </summary>
    public static event EventHandler<(double Percent, string Step)>? ProgressChanged;

    /// <summary>
    /// Reports progress to all subscribers.
    /// </summary>
    /// <param name="percent">Progress percentage (0-100).</param>
    /// <param name="step">Description of the current step.</param>
    public static void Report(double percent, string step)
    {
        ProgressChanged?.Invoke(null, (Math.Clamp(percent, 0, 100), step));
    }

    /// <summary>
    /// Signals that the operation is complete and the progress bar should hide.
    /// </summary>
    public static void Complete()
    {
        ProgressChanged?.Invoke(null, (100, string.Empty));
    }
}
