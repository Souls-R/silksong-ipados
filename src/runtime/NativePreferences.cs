using System;
using System.Runtime.InteropServices;

// Read once per process. Settings explicitly take effect on the next launch.
// No preference reads, UI polling or diagnostics timers in the gameplay loop.
public static class NativePreferences
{
    public static bool ResourceLogs { get; private set; }
    public static bool CompatibilityLogs { get; private set; }
    public static bool StartupDetails { get; private set; }
    public static bool ShowNotice { get; private set; }
    public static bool RecheckResources { get; private set; }
    public static bool Diagnostics { get { return ResourceLogs || CompatibilityLogs || StartupDetails; } }
    static bool initialized;
#if UNITY_IOS && !UNITY_EDITOR
    [DllImport("__Internal")] static extern int NativeSettings_Flags();
    [DllImport("__Internal")] static extern void NativeSettings_ConsumeRecheck();
    [DllImport("__Internal")] static extern void NativeLaunch_Show(int page, string detail);
    [DllImport("__Internal")] static extern int NativeLaunch_Poll();
    [DllImport("__Internal")] static extern void NativeLaunch_Hide();
#endif
    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        int flags = 8;
#if UNITY_IOS && !UNITY_EDITOR
        flags = NativeSettings_Flags();
#endif
        ResourceLogs = (flags & 1) != 0;
        CompatibilityLogs = (flags & 2) != 0;
        StartupDetails = (flags & 4) != 0;
        ShowNotice = (flags & 8) != 0;
        RecheckResources = (flags & 16) != 0;
    }
    public static void ConsumeRecheck()
    {
        RecheckResources = false;
#if UNITY_IOS && !UNITY_EDITOR
        NativeSettings_ConsumeRecheck();
#endif
    }
    // 1 notice; 2 missing pack; 3 preparing; 4 load failure; 5 repaired/relaunch.
    public static void Show(int page, string detail = "")
    {
#if UNITY_IOS && !UNITY_EDITOR
        NativeLaunch_Show(page, detail ?? "");
#endif
    }
    public static int Poll()
    {
#if UNITY_IOS && !UNITY_EDITOR
        return NativeLaunch_Poll();
#else
        return 1;
#endif
    }
    public static void Hide()
    {
#if UNITY_IOS && !UNITY_EDITOR
        NativeLaunch_Hide();
#endif
    }
}
