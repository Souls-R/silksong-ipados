#pragma once
#include <atomic>

// InControl: left/right/middle = 1/2/3, auxiliary buttons = 10..13.
inline int NativeMouseButtonBit(int number)
{
    return number >= 1 && number <= 7 ? 1 << (number <= 3 ? number : number + 6) : 0;
}
inline int NativeMouseUIKitMask(unsigned long mask)
{
    int result = 0;
    for (int number = 1; number <= 7; ++number)
        if (mask & (1UL << (number - 1))) result |= NativeMouseButtonBit(number);
    return result;
}
inline int NativeMouseUIKitState(unsigned long mask, bool ended)
{
    // An ended/cancelled pointer sequence cannot keep a button held, even if
    // its UIEvent still describes the button involved in the click.
    return ended ? 0 : NativeMouseUIKitMask(mask);
}
inline int NativeMouseMerge(int gameController, int uiKit, bool hardwareConnected)
{
    // GCMouse has explicit down/up events. UIKit is only a fallback for a
    // pointer with no hardware profile; stale UI state must not block release.
    return hardwareConnected ? gameController : uiKit;
}

// Keep a press that begins and ends between two Unity updates. A held button
// remains immediate; a short click produces one down frame then a release.
class NativeMouseButtons
{
    std::atomic<int> held{0}, pending{0};
public:
    void Set(int bit, bool pressed)
    {
        if (pressed) {
            int old = held.fetch_or(bit);
            pending.fetch_or(bit & ~old);
        } else held.fetch_and(~bit);
    }
    void SetMask(int mask)
    {
        int old = held.exchange(mask);
        pending.fetch_or(mask & ~old);
    }
    int Read() { return pending.exchange(0) | held.load(); }
    void Clear() { held.store(0); pending.store(0); }
};

struct NativeMouseSnapshot
{
    int buttons, devices, gcButtons, uiButtons, gcEvents, uiEvents, hasPosition;
    float x, y;
};
