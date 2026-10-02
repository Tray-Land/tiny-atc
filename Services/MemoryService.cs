using System.Runtime;
using Windows.Win32;

namespace TinyAtc.Services;

/// <summary>
/// Gives memory back to Windows once the app is idle, the state a tray app spends nearly all its
/// time in. An idle tray app allocates almost nothing, so the GC won't run on its own and a closed
/// window's memory would otherwise never be returned.
/// </summary>
internal static class MemoryService
{
    /// <summary>Default wait for WinUI to finish tearing a closed window down.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// After <paramref name="delay"/>, collects everything (running finalizers, which free native
    /// resources such as Direct3D devices) and trims the working set. Runs off the UI thread
    /// because some finalizers need to reach it.
    /// </summary>
    public static void ReleaseIdle(TimeSpan? delay = null) => _ = Task.Run(async () =>
    {
        // Collect too early and the window's objects are still referenced and just get promoted.
        await Task.Delay(delay ?? SettleDelay);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Aggressive mode compacts every generation, including the LOH, and decommits free space
        // instead of keeping it for reuse. The heap is a few MB, so this is cheap.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // Most of a WinUI app's working set is native (XAML, composition, font caches) that nothing
        // touches while hidden; it pages back in cheaply from the standby list on the next open.
        PInvoke.SetProcessWorkingSetSize(PInvoke.GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue);
    });
}
