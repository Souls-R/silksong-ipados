using System;
using InControl;
using UnityEngine;

// Minimal host API surface; exercise the production provider without iOS.
namespace UnityEngine
{
    public static class Application { public static bool isFocused = true; }
    public struct Vector2 {
        public float x, y;
        public Vector2(float x, float y) { this.x=x; this.y=y; }
        public static Vector2 zero { get { return new Vector2(); } }
        public static Vector2 operator -(Vector2 a, Vector2 b) { return new Vector2 { x = a.x-b.x, y=a.y-b.y }; }
    }
    public static class Input { public static Vector2 mousePosition, mouseScrollDelta; public static int touchCount; }
    public static class Screen { public static int width=2388, height=1668; }
    public static class Cursor { public static int lockState; }
    public static class Debug { public static void Log(string text) { } }
}
public static class NativePreferences { public static bool CompatibilityLogs; }
public static class NativeMouseUI { public static void BeforeInputUpdate() { } }
namespace InControl
{
    public enum Mouse { None, LeftButton, RightButton, MiddleButton, NegativeX, PositiveX,
        NegativeY, PositiveY, PositiveScrollWheel, NegativeScrollWheel, Button4, Button5, Button6, Button7 }
    public interface IMouseProvider
    {
        void Setup(); void Reset(); void Update();
        Vector2 GetPosition(); float GetDeltaX(); float GetDeltaY(); float GetDeltaScroll();
        bool GetButtonIsPressed(Mouse control); bool GetButtonWasPressed(Mouse control);
        bool GetButtonWasReleased(Mouse control); bool HasMousePresent();
    }
}
sealed class Fallback : IMouseProvider
{
    public bool present;
    public int buttons, updates;
    public void Setup() { }
    public void Reset() { buttons = 0; }
    public void Update() { updates++; }
    public Vector2 GetPosition() { return new Vector2 { x = 321, y = 123 }; }
    public float GetDeltaX() { return 2; }
    public float GetDeltaY() { return -3; }
    public float GetDeltaScroll() { return 4; }
    public bool GetButtonIsPressed(Mouse control) { return (buttons & (1 << (int)control)) != 0; }
    public bool GetButtonWasPressed(Mouse control) { throw new Exception("Edges must use one consistent snapshot"); }
    public bool GetButtonWasReleased(Mouse control) { throw new Exception("Edges must use one consistent snapshot"); }
    public bool HasMousePresent() { return present; }
}
static class MouseProviderTests
{
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
    static int Bit(Mouse button) { return 1 << (int)button; }
    static void Main()
    {
        int native = 0, reads = 0;
        var fallback = new Fallback();
        bool pointer = false;
        var mouse = new NativeMouseProvider(fallback, () => { reads++; return new NativeMouseProvider.Snapshot {
            buttons=native, hasPosition=pointer ? 1 : 0, x=0.25f, y=0.75f }; });
        mouse.Setup();
        mouse.Update();
        Check(mouse.HasMousePresent() && !mouse.GetButtonIsPressed(Mouse.LeftButton), "Connected idle mouse");
        Input.mousePosition = new Vector2 { x=125, y=250 };
        mouse.Update();
        Check(mouse.GetPosition().x == 125 && mouse.GetDeltaY() == 250, "Legacy pointer remains usable without InputSystem.Mouse");
        mouse.Update();
        Check(mouse.GetDeltaX() == 0 && mouse.GetDeltaY() == 0, "Stationary cursor must not keep waking the mouse");
        native = Bit(Mouse.LeftButton) | Bit(Mouse.RightButton) | Bit(Mouse.MiddleButton) |
            Bit(Mouse.Button4) | Bit(Mouse.Button5) | Bit(Mouse.Button6) | Bit(Mouse.Button7);
        mouse.Update();
        foreach (var key in new[] { Mouse.LeftButton, Mouse.RightButton, Mouse.MiddleButton,
            Mouse.Button4, Mouse.Button5, Mouse.Button6, Mouse.Button7 })
            Check(mouse.GetButtonIsPressed(key) && mouse.GetButtonWasPressed(key), "Simultaneous button " + key);
        Check(!mouse.GetButtonIsPressed(Mouse.NegativeX) && !mouse.GetButtonIsPressed(Mouse.PositiveScrollWheel)
            && !mouse.GetButtonIsPressed(Mouse.None) && !mouse.GetButtonIsPressed((Mouse)33), "Buttons must not alias axes/invalid values");
        mouse.Update();
        Check(mouse.GetButtonIsPressed(Mouse.LeftButton) && !mouse.GetButtonWasPressed(Mouse.LeftButton), "Held button repeats no edge");
        native &= ~Bit(Mouse.LeftButton);
        mouse.Update();
        Check(mouse.GetButtonWasReleased(Mouse.LeftButton) && mouse.GetButtonIsPressed(Mouse.RightButton), "Independent release");
        mouse.Update();
        Check(!mouse.GetButtonWasReleased(Mouse.LeftButton), "Release lasts one update");
        native = -1;
        mouse.Update();
        Check(!mouse.HasMousePresent() && mouse.GetButtonWasReleased(Mouse.RightButton), "Disconnect releases held buttons");
        mouse.Update();
        Check(!mouse.GetButtonWasReleased(Mouse.RightButton), "Disconnected state cannot repeat release");
        native = Bit(Mouse.Button4);
        mouse.Update();
        Check(mouse.GetButtonWasPressed(Mouse.Button4), "Hot reconnect");
        int beforeBackground = reads;
        Application.isFocused = false;
        Check(!mouse.GetButtonIsPressed(Mouse.Button4), "Focus loss gates stale snapshot immediately");
        mouse.Update();
        Check(reads == beforeBackground && mouse.GetButtonWasReleased(Mouse.Button4) && !mouse.HasMousePresent(), "Focus loss clears input");
        Application.isFocused = true;
        native = 0;
        mouse.Update();
        Check(!mouse.GetButtonWasPressed(Mouse.Button4), "Resume has no stale button");
        fallback.present = true;
        fallback.buttons = Bit(Mouse.LeftButton);
        native = -1;
        mouse.Update();
        Check(mouse.GetButtonWasPressed(Mouse.LeftButton), "Unity fallback remains available");
        Check(mouse.GetPosition().x == 321 && mouse.GetDeltaX() == 2 && mouse.GetDeltaY() == -3 && mouse.GetDeltaScroll() == 4,
            "Existing cursor and scroll values are preserved");
        native = 0;
        mouse.Update();
        Check(!mouse.GetButtonIsPressed(Mouse.LeftButton), "Physical snapshot overrides stale Unity buttons");
        pointer = true;
        mouse.Update();
        Check(mouse.GetPosition().x == 597 && mouse.GetPosition().y == 1251,
            "UIKit pointer uses Unity render dimensions and bottom-left coordinates");
        mouse.Update();
        Check(mouse.GetDeltaX() == 0 && mouse.GetDeltaY() == 0,
            "Native stationary pointer overrides a stale Unity delta");
        pointer = false;
        native = -2;
        mouse.Update();
        Check(!mouse.GetButtonIsPressed(Mouse.LeftButton) && !mouse.HasMousePresent(), "Native suspension cannot fall back to stale Unity input");
        native = Bit(Mouse.LeftButton);
        mouse.Update();
        mouse.Reset();
        Check(!mouse.GetButtonIsPressed(Mouse.LeftButton) && !mouse.GetButtonWasReleased(Mouse.LeftButton), "Reset clears both snapshots");
        var noFallback = new NativeMouseProvider(null, () => new NativeMouseProvider.Snapshot { buttons=-1 });
        noFallback.Setup(); noFallback.Update();
        Check(!noFallback.HasMousePresent() && noFallback.GetDeltaScroll() == 0, "No device and no fallback");
        Console.WriteLine("PASS: mouse edges, simultaneous buttons, mapping, disconnect/focus, fallback and reset");
    }
}
