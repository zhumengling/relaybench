using System.Diagnostics;
using System.Runtime.InteropServices;

var launcherPath = Environment.ProcessPath;
var launcherDirectory = Path.GetDirectoryName(launcherPath) ?? AppContext.BaseDirectory;
var appDirectory = Path.Combine(launcherDirectory, "app");
var appPath = Path.Combine(appDirectory, "RelayBench.WinUI.exe");

if (!File.Exists(appPath))
{
    ShowError($"RelayBench runtime was not found.\n\nExpected:\n{appPath}");
    return 2;
}

try
{
    Process.Start(new ProcessStartInfo
    {
        FileName = appPath,
        WorkingDirectory = appDirectory,
        UseShellExecute = true
    });

    return 0;
}
catch (Exception ex)
{
    ShowError($"RelayBench could not be started.\n\n{ex.Message}");
    return 1;
}

static void ShowError(string message)
{
    _ = MessageBox(IntPtr.Zero, message, "RelayBench", 0x00000010);
}

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
