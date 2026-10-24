using System;
using GlobalEnums;
using InControl;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem.iOS;
using InputDevice = UnityEngine.InputSystem.InputDevice;

// Keep the IGC state layout. Resolve identity before command controls are
// created, then reuse that style for the original game's binding setup.
public static class NativeControllerIdentity
{
    public static bool IsIOSController(InputDevice device)
    {
        return device != null && device.description.interfaceName == "iOS" &&
            device.description.deviceClass == "iOSGameController";
    }

    public static bool IsNintendoName(string product)
    {
        if (string.IsNullOrWhiteSpace(product)) return false;
        string name = product.Trim();
        // Exact descriptions only; containing "Switch" or "Pro" is insufficient.
        foreach (string known in new[] { "Pro Controller", "Switch Pro Controller", "Nintendo Switch Pro Controller",
            "Switch 2 Pro Controller", "Nintendo Switch 2 Pro Controller", "Joy-Con", "Joy-Con (L)", "Joy-Con (R)",
            "Joy-Con (L/R)", "Joy-Con 2", "Joy-Con 2 (L)", "Joy-Con 2 (R)", "Joy-Con 2 (L/R)" })
            if (string.Equals(name, known, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Negative retains the original detector for other interfaces/types.
    public static int DetectStyle(InputDevice device)
    {
        if (!IsIOSController(device)) return -1;
        if (device is XboxOneGampadiOS) return (int)InputDeviceStyle.XboxOne;
        // iOS DualSense derives from DualShockGamepad, not DualSenseGamepadHID.
        if (device is DualSenseGampadiOS) return (int)InputDeviceStyle.PlayStation5;
        if (device is DualShock4GampadiOS) return (int)InputDeviceStyle.PlayStation4;
        if (device is iOSGameController)
            return (int)(IsNintendoName(device.description.product) ? InputDeviceStyle.NintendoSwitch : InputDeviceStyle.Unknown);
        return -1;
    }

    public static int GamepadTypeForStyle(InputDeviceStyle style)
    {
        switch (style)
        {
            case InputDeviceStyle.XboxOne: return (int)GamepadType.XBOX_ONE;
            case InputDeviceStyle.PlayStation4: return (int)GamepadType.PS4;
            case InputDeviceStyle.PlayStation5: return (int)GamepadType.PS5;
            case InputDeviceStyle.NintendoSwitch: return (int)GamepadType.SWITCH_PRO_CONTROLLER;
            case InputDeviceStyle.Unknown: return (int)GamepadType.UNKNOWN;
            default: return -1;
        }
    }

    // Called after the detached-device guard, before the game's name fallback.
    public static int ResolveGamepadType(InControl.InputDevice device)
    {
        var adapter = device as NewUnityInputDevice;
        return adapter != null && IsIOSController(adapter.UnityGamepad)
            ? GamepadTypeForStyle(adapter.DeviceStyle) : -1;
    }

    public static string LegacyDualSenseMapping(string existing, string legacy)
    {
        if (existing != null || string.IsNullOrWhiteSpace(legacy)) return null;
        try
        {
            var mapping = JObject.Parse(legacy);
            foreach (string key in new[] { "jump", "attack", "dash", "cast", "superDash", "dreamNail", "quickMap", "quickCast", "taunt" })
                if (mapping[key] == null || mapping[key].Type != JTokenType.Integer) return null;
            mapping["gamepadType"] = (int)GamepadType.PS5;
            return mapping.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch (Exception) { return null; }
    }

    public static void PrepareBindings(int gamepadType)
    {
        if (gamepadType != (int)GamepadType.PS5 || Platform.Current == null) return;
        try
        {
            var storage = Platform.Current.LocalSharedData;
            if (storage == null || storage.HasKey("ControllerPS5") || !storage.HasKey("ControllerPS4")) return;
            string mapping = LegacyDualSenseMapping(null, storage.GetString("ControllerPS4", ""));
            if (mapping == null) return;
            storage.SetString("ControllerPS5", mapping);
            storage.Save();
            if (NativePreferences.CompatibilityLogs)
                Debug.Log("NATIVE_INPUT: Copied legacy PS4 bindings to missing PS5 profile; PS4 profile retained");
        }
        catch (Exception ex) { Debug.LogWarning("NATIVE_INPUT: Legacy PS5 binding copy skipped: " + ex.Message); }
    }
}
