#include "../src/runtime/NativeVirtualPad.h"
#include <cassert>
#include <cmath>
#include <iostream>

int main()
{
    static_assert(sizeof(NativeVirtualPadSnapshot) == 16, "C# interop layout");
    assert(NativeVirtualPadAuto == 0 && NativeVirtualPadAlways == 1 && NativeVirtualPadNever == 2);
    // Every control owns its own bit, and the whole table fits the low 16 bits.
    int combined = 0;
    for (int bit : {NativePadDpadUp, NativePadDpadDown, NativePadDpadLeft, NativePadDpadRight,
                    NativePadSouth, NativePadEast, NativePadWest, NativePadNorth,
                    NativePadLeftShoulder, NativePadRightShoulder, NativePadLeftTrigger,
                    NativePadRightTrigger, NativePadSelect, NativePadStart})
    {
        assert(bit != 0 && (combined & bit) == 0);
        combined |= bit;
    }
    assert(combined == 0x3FFF);
    // Auto follows the player: real device input hides the pad and the next
    // screen touch brings it back; both forced modes ignore device input.
    assert(NativeVirtualPadVisible(NativeVirtualPadAuto, false));
    assert(!NativeVirtualPadVisible(NativeVirtualPadAuto, true));
    assert(NativeVirtualPadVisible(NativeVirtualPadAlways, false));
    assert(NativeVirtualPadVisible(NativeVirtualPadAlways, true));
    assert(!NativeVirtualPadVisible(NativeVirtualPadNever, false));
    assert(!NativeVirtualPadVisible(NativeVirtualPadNever, true));
    // Thumb jitter inside the dead zone must read as a released stick.
    float x = 1, y = 1;
    NativeVirtualPadStick(0, 0, 0.18f, &x, &y);
    assert(x == 0 && y == 0);
    NativeVirtualPadStick(0.1f, -0.1f, 0.18f, &x, &y);
    assert(x == 0 && y == 0);
    // Outside the dead zone the remaining travel is remapped to the full range.
    NativeVirtualPadStick(0.59f, 0, 0.18f, &x, &y);
    assert(fabsf(x - 0.5f) < 1e-5f && y == 0);
    NativeVirtualPadStick(1, 0, 0.18f, &x, &y);
    assert(fabsf(x - 1) < 1e-5f && y == 0);
    NativeVirtualPadStick(0, 1, 0.18f, &x, &y);
    assert(x == 0 && fabsf(y - 1) < 1e-5f);
    // Dragging past the edge clamps to unit length, diagonals included.
    NativeVirtualPadStick(4, 0, 0.18f, &x, &y);
    assert(fabsf(x - 1) < 1e-5f && y == 0);
    NativeVirtualPadStick(3, 4, 0, &x, &y);
    assert(fabsf(x - 0.6f) < 1e-4f && fabsf(y - 0.8f) < 1e-4f);
    NativeVirtualPadStick(0.6f, 0.8f, 0.18f, &x, &y);
    assert(fabsf(x * x + y * y - 1) < 1e-4f);
    std::cout << "PASS: gamepad bit table, three display modes, stick dead zone and remapping\n";
}
