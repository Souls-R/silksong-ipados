using System;

public static class NativeRenderScale
{
    public static readonly int[] Percentages = { 100, 85, 75, 67, 50 };
    public static int Index(int percent)
    {
        for (int i = 0; i < Percentages.Length; i++) if (Percentages[i] == percent) return i;
        return -1;
    }
    public static void Resolution(int nativeWidth, int nativeHeight, int percent, out int width, out int height)
    {
        if (Index(percent) < 0) throw new ArgumentOutOfRangeException("percent");
        width = Math.Max(2, (int)Math.Round(nativeWidth * percent / 200.0) * 2);
        height = Math.Max(2, (int)Math.Round(nativeHeight * percent / 200.0) * 2);
    }
}
