#include "../src/runtime/NativeMouseState.h"
#include <cassert>
#include <iostream>

int main()
{
    static_assert(sizeof(NativeMouseSnapshot) == 36, "C# interop layout");
    NativeMouseButtons buttons;
    const int left = 2, right = 4, side = 1024;
    assert(NativeMouseUIKitMask(1) == left);
    assert(NativeMouseUIKitMask(2) == right);
    assert(NativeMouseUIKitMask(4) == 8);
    assert(NativeMouseUIKitMask(8 | 16) == (1024 | 2048));
    assert(NativeMouseUIKitMask(1UL << 30) == 0);
    buttons.SetMask(NativeMouseUIKitState(4, false));
    assert(buttons.Read() == 8);
    buttons.SetMask(NativeMouseUIKitState(4, true));
    assert(buttons.Read() == 0); // Ended event may still carry its middle-button mask.
    buttons.SetMask(NativeMouseUIKitState(8, false));
    buttons.SetMask(NativeMouseUIKitState(8, true));
    assert(buttons.Read() == side); // Short UIKit click is still delivered once.
    assert(buttons.Read() == 0);
    buttons.Set(left, true);
    buttons.Set(left, false);
    assert(buttons.Read() == left); // Complete click between Unity frames.
    assert(buttons.Read() == 0);
    buttons.Set(left, true);
    buttons.Set(side, true);
    assert(buttons.Read() == (left | side));
    assert(buttons.Read() == (left | side));
    buttons.Set(left, false);
    assert(buttons.Read() == side);
    buttons.Clear(); // Disconnect / background must discard queued presses.
    assert(buttons.Read() == 0);
    buttons.SetMask(left | right);
    assert(buttons.Read() == (left | right));
    buttons.SetMask(right);
    assert(buttons.Read() == right);
    buttons.SetMask(0);
    assert(buttons.Read() == 0);
    // 0.7.7 device regression: GC released to zero while UIKit retained 8 or
    // 1024 indefinitely. UIKit must not override hardware release or neutral.
    for (int button : {left, right, 8, side, 2048}) {
        assert(NativeMouseMerge(0, button, true) == 0);
        assert(NativeMouseMerge(button, button, true) == button);
        assert(NativeMouseMerge(button, 0, true) == button);
        assert(NativeMouseMerge(0, button, true) == 0);
    }
    assert(NativeMouseMerge(side | right, left, true) == (side | right));
    assert(NativeMouseMerge(0, left, false) == left);
    assert(NativeMouseMerge(0, 0, false) == 0);
    std::cout << "PASS: short clicks, held/simultaneous buttons, lifecycle, UIKit mapping and hardware release precedence\n";
}
