using System;
using UnityEngine;

// iOS display operations used by the ORIGINAL menus. No UI, scene callback,
// polling override, or independent render-scale preference.
public sealed class NativeDisplaySettings : MonoBehaviour
{
    static int nativeWidth, nativeHeight, renderWidth, renderHeight;
    static int requestedFrameRate = 60;
    static bool loaded, preview;
    static Resolution[] resolutions;
    float elapsed;
    int frames;
    public static float FramesPerSecond { get; private set; }

    static void Initialize()
    {
        if (resolutions != null) return;
        int a = Display.main.systemWidth, b = Display.main.systemHeight;
        if (a <= 0 || b <= 0) { a = Screen.width; b = Screen.height; }
        nativeWidth = Screen.width >= Screen.height ? Math.Max(a, b) : Math.Min(a, b);
        nativeHeight = Screen.width >= Screen.height ? Math.Min(a, b) : Math.Max(a, b);
        renderWidth = nativeWidth;
        renderHeight = nativeHeight;
        resolutions = new Resolution[NativeRenderScale.Percentages.Length];
        RefreshRate refresh = Screen.currentResolution.refreshRateRatio;
        if (refresh.denominator == 0 || refresh.numerator == 0)
            refresh = new RefreshRate { numerator = 60, denominator = 1 };
        for (int i = 0; i < resolutions.Length; i++)
        {
            int width, height;
            NativeRenderScale.Resolution(nativeWidth, nativeHeight,
                NativeRenderScale.Percentages[resolutions.Length - 1 - i], out width, out height);
            resolutions[i] = new Resolution { width = width, height = height, refreshRateRatio = refresh };
        }
    }
    public static Resolution[] GetResolutions() { Initialize(); return resolutions; }
    public static Resolution GetCurrentResolution()
    {
        Initialize();
        return new Resolution { width = renderWidth, height = renderHeight,
            refreshRateRatio = resolutions[resolutions.Length - 1].refreshRateRatio };
    }
    static Resolution MatchResolution(int width, int height)
    {
        Initialize();
        // Keep native aspect even for old desktop/cinematic requests.
        Resolution best = resolutions[resolutions.Length - 1];
        long distance = long.MaxValue;
        foreach (Resolution candidate in resolutions)
        {
            long delta = Math.Abs((long)candidate.width * candidate.height - (long)width * height);
            if (delta < distance) { best = candidate; distance = delta; }
        }
        return best;
    }
    public static void SetResolution(int width, int height, bool fullscreen)
    {
        Resolution resolution = MatchResolution(width, height);
        renderWidth = resolution.width;
        renderHeight = resolution.height;
        Display.main.SetRenderingResolution(renderWidth, renderHeight);
        if (NativePreferences.CompatibilityLogs) Debug.Log("NATIVE_DISPLAY: original-menu render=" + renderWidth + "x" + renderHeight
            + "; native=" + nativeWidth + "x" + nativeHeight + "; preview=" + preview);
    }
    public static void SetResolutionWithRate(int width, int height, bool fullscreen, int refresh)
    { SetResolution(width, height, fullscreen); }
    public static void SetResolutionWithMode(int width, int height, FullScreenMode mode, RefreshRate refresh)
    { SetResolution(width, height, true); }
    public static void LoadResolution()
    {
        if (loaded) return;
        Initialize();
        loaded = true;
        var data = Platform.Current.LocalSharedData;
        SetResolution(data.GetInt("VidWidth", nativeWidth), data.GetInt("VidHeight", nativeHeight), true);
    }
    public static void BeginResolutionPreview() { preview = true; }
    public static void RollbackResolutionPreview() { preview = false; }
    public static void ReturnToVideoMenu(bool rollback)
    {
        if (!preview || rollback) return;
        preview = false;
        SaveResolution();
    }
    static void SaveResolution()
    {
        var data = Platform.Current.LocalSharedData;
        data.SetInt("VidWidth", renderWidth);
        data.SetInt("VidHeight", renderHeight);
        data.Save();
        if (NativePreferences.CompatibilityLogs) Debug.Log("NATIVE_DISPLAY: confirmed=" + renderWidth + "x" + renderHeight);
    }
    public static void ResetResolution(int width, int height, bool fullscreen)
    {
        Initialize();
        preview = false;
        SetResolution(nativeWidth, nativeHeight, true);
        SaveResolution();
    }
    public static int GetTargetFrameRate() { return requestedFrameRate; }
    public static void SetTargetFrameRate(int value)
    {
        requestedFrameRate = value;
        Initialize();
        // iOS -1 means 30; the original menu means uncapped/VSync. Preserve
        // its logical value and send positive choices (including 30) unchanged.
        int actual = value > 0 ? value : (int)Math.Round(Screen.currentResolution.refreshRateRatio.value);
        if (actual <= 0) actual = 60;
        Application.targetFrameRate = actual;
        if (NativePreferences.CompatibilityLogs) Debug.Log("NATIVE_DISPLAY: original-menu fps=" + value + "; targetFPS=" + actual);
    }
}
