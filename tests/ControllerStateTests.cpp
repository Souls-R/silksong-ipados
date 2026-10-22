#include "../src/runtime/NativeControllerState.h"
#include <cassert>
#include <iostream>

int main()
{
    NativeControllerButtonState first, second;
    assert(!first.Read());
    first.Set(true);
    first.Set(false);
    assert(first.Read()); // A complete click between input updates is retained.
    assert(!first.Read());
    first.Set(true);
    assert(first.Read());
    assert(first.Read()); // Held state survives multiple input updates.
    second.Set(true);
    first.Set(false);
    assert(!first.Read() && second.Read());
    first.Set(true);
    first.Clear(); // Background/disconnect must discard both held and queued state.
    assert(!first.Read());
    assert(second.Read()); // Clearing one controller does not release another.
    second.Set(false);
    assert(!second.Read());
    std::cout << "PASS controller short clicks, holds, independent devices and lifecycle reset\n";
}
