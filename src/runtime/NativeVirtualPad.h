#pragma once
#include <math.h>

// The on-screen pad always reports gamepad input, never keyboard input.
// Display modes are shared by the pre-game launcher row and the runtime pad.
enum NativeVirtualPadMode
{
    NativeVirtualPadAuto = 0,    // hide on real device input, show again on screen touch
    NativeVirtualPadAlways = 1,
    NativeVirtualPadNever = 2,
};

// One bit per pad control. The managed side mirrors these values, one for one.
enum NativeVirtualPadButton
{
    NativePadDpadUp = 1 << 0,
    NativePadDpadDown = 1 << 1,
    NativePadDpadLeft = 1 << 2,
    NativePadDpadRight = 1 << 3,
    NativePadSouth = 1 << 4,   // A
    NativePadEast = 1 << 5,    // B
    NativePadWest = 1 << 6,    // X
    NativePadNorth = 1 << 7,   // Y
    NativePadLeftShoulder = 1 << 8,
    NativePadRightShoulder = 1 << 9,
    NativePadLeftTrigger = 1 << 10,
    NativePadRightTrigger = 1 << 11,
    NativePadSelect = 1 << 12,
    NativePadStart = 1 << 13,
};

// Auto keeps the pad for players who touch the screen and removes it as soon as
// a real controller, keyboard or mouse reports input. Forced modes ignore real
// device input entirely, so "always" survives a controller being connected.
inline bool NativeVirtualPadVisible(int mode, bool realDeviceInput)
{
    if (mode == NativeVirtualPadAlways) return true;
    if (mode == NativeVirtualPadNever) return false;
    return !realDeviceInput;
}

// Radial dead zone so thumb jitter cannot leak into gameplay movement, with the
// remaining travel remapped to the full stick range and the magnitude clamped so
// diagonal input never exceeds unit length.
inline void NativeVirtualPadStick(float x, float y, float deadZone, float* outX, float* outY)
{
    float magnitude = sqrtf(x * x + y * y);
    if (magnitude <= deadZone) { *outX = 0; *outY = 0; return; }
    float limited = magnitude > 1 ? 1 : magnitude;
    float scale = (limited - deadZone) / (1 - deadZone);
    *outX = x / magnitude * scale;
    *outY = y / magnitude * scale;
}

// Mirror of the managed reader. Field order is the interop ABI; do not reorder.
struct NativeVirtualPadSnapshot
{
    int state;      // -1 pad disabled by its display mode, 0 hidden, 1 showing
    int buttons;    // NativeVirtualPadButton bits, including taps between frames
    float leftX, leftY;
};

// Keeps a dragged control of w x h inside the pad canvas. Offsets are stored in
// canvas units, so a layout the player arranged keeps its proportions on every
// iPad instead of drifting with the screen scale.
inline void NativeVirtualPadClampToCanvas(float x, float y, float w, float h,
                                          float canvasWidth, float canvasHeight,
                                          float* outX, float* outY)
{
    float maxX = canvasWidth - w; if (maxX < 0) maxX = 0;
    float maxY = canvasHeight - h; if (maxY < 0) maxY = 0;
    *outX = x < 0 ? 0 : (x > maxX ? maxX : x);
    *outY = y < 0 ? 0 : (y > maxY ? maxY : y);
}

#ifdef __cplusplus
extern "C" {
#endif
// 0 auto, 1 always on, 2 always off.
int NativeVirtualPad_Mode(void);
void NativeVirtualPad_SetMode(int mode);
// Called once gameplay starts and again whenever a launcher page appears.
void NativeVirtualPad_Attach(void);
void NativeVirtualPad_Detach(void);
// A real controller/keyboard/mouse produced input; auto hides the pad.
void NativeVirtualPad_RealDeviceInput(void);
void NativeVirtualPad_ReadState(NativeVirtualPadSnapshot* result);
// Launcher-driven layout editing: the pad shows itself over the launcher page
// even when its display mode is "always off", reports no gamepad while the
// player arranges controls, and persists the result on the way out. Captions
// are UTF-8 so the launcher can pass its own localised strings.
int NativeVirtualPad_Editing(void);
void NativeVirtualPad_BeginEditing(const char* done, const char* reset, const char* hint);
void NativeVirtualPad_EndEditing(void);
#ifdef __cplusplus
}
#endif
