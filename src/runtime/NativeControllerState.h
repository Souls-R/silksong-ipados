#pragma once
#include <atomic>

// A complete press/release between Unity input updates still produces one
// pressed frame. Each physical controller owns a separate instance.
class NativeControllerButtonState
{
    std::atomic<bool> held{false}, pending{false};
public:
    void Set(bool pressed)
    {
        bool previous = held.exchange(pressed);
        if (pressed && !previous) pending.store(true);
    }
    bool Read() { return pending.exchange(false) | held.load(); }
    void Clear() { held.store(false); pending.store(false); }
};
