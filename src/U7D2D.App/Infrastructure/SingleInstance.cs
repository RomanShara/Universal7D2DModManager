using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace U7D2D.App.Infrastructure;

/// <summary>
/// One manager per user session. A second launch brings the existing window to the front instead of
/// starting a parallel copy that could write the same database and the same Mods folder.
/// </summary>
internal static class SingleInstance
{
    private const string Name = @"Local\Universal7D2DModManager";

    // Kept for the lifetime of the process; the OS releases it when the process exits.
    private static Mutex? _mutex;

    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, Name, out var createdNew);

        if (createdNew) return true;

        ActivateExisting();
        return false;
    }

    private static void ActivateExisting()
    {
        try
        {
            using var current = Process.GetCurrentProcess();

            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                using (process)
                {
                    if (process.Id == current.Id) continue;
                    if (process.MainWindowHandle == IntPtr.Zero) continue;

                    SetForegroundWindow(process.MainWindowHandle);
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Activation is a convenience; the important part is that the second instance exits.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
