using System.Collections.Generic;
using UnityEngine;

public static class NativeBindingLabel
{
    static readonly Dictionary<MappableKey, Font> originals = new Dictionary<MappableKey, Font>();

    public static void ShowPrompt(MappableKey key)
    {
        var label = key.keymapText;
        if (!label || string.IsNullOrEmpty(label.text)) return;
        // Keycaps use Arial. Desktop OS font fallback is not portable to iOS.
        // Select the game's already-loaded CJK font only for the prompt.
        bool cjk = false;
        foreach (char c in label.text) if (c >= '\u3000' && c <= '\u9fff') cjk = true;
        if (!cjk) return;
        string preferred = FontManager.CurrentLanguage.ToString() == "ZH_TW" ? "NotoSerifTC" :
            FontManager.CurrentLanguage.ToString() == "JA" ? "HannariMincho" : "NotoSerifCJKsc";
        foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
        {
            if (!font.name.StartsWith(preferred, System.StringComparison.OrdinalIgnoreCase)) continue;
            bool complete = true;
            foreach (char c in label.text)
                if (!char.IsWhiteSpace(c) && !font.HasCharacter(c)) { complete = false; break; }
            if (!complete) continue;
            if (!originals.ContainsKey(key)) originals.Add(key, label.font);
            label.font = font;
            label.SetAllDirty();
            break;
        }
        if (NativePreferences.CompatibilityLogs)
            Debug.Log("NATIVE_INPUT: binding prompt=" + label.text + "; font=" + label.font.name);
    }

    public static void Restore(MappableKey key)
    {
        Font original;
        if (!originals.TryGetValue(key, out original)) return;
        if (key && key.keymapText) key.keymapText.font = original;
        originals.Remove(key);
    }
}
