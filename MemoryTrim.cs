using System.Runtime;
using System.Runtime.InteropServices;

namespace ZendeskWatcher;

/// <summary>
/// Gives memory back to Windows after something big goes away (the Settings window, the embedded browser):
/// a compacting garbage collection, then the working set is trimmed. A tray app spends most of its time idle,
/// so there is no reason to hold on to it. Must be called on the UI thread.
/// </summary>
public static class MemoryTrim
{
    private static System.Windows.Forms.Timer? _pending;

    /// <summary>Trims a few seconds from now (repeated calls in the meantime are merged).</summary>
    public static void Soon()
    {
        if (_pending is not null)
            return;
        _pending = new System.Windows.Forms.Timer { Interval = 3000 };
        _pending.Tick += (_, _) =>
        {
            _pending.Dispose();
            _pending = null;
            Now();
        };
        _pending.Start();
    }

    public static void Now()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
