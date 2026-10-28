using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace MilesFontExtractor.Core;

/// <summary>Hands memory back to Windows after the heavy phases (scan, copy) so the app idles small.</summary>
public static class MemoryTrim
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
    }
}
