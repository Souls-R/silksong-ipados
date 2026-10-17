#import <GameController/GameController.h>
#import <UIKit/UIKit.h>
#include <cstring>

// Called once per InControl update; copy every physical key independently.
// There is no text-input/repeat event or single-current-key state here.
extern "C" int NativeKeyboard_CopyState(unsigned char* destination, int length)
{
    if (!destination || length < 256) return 0;
    std::memset(destination, 0, 256);
    if (UIApplication.sharedApplication.applicationState != UIApplicationStateActive) return 0;
    GCKeyboardInput* input = [GCKeyboard coalescedKeyboard].keyboardInput;
    if (!input) return 0;
    for (int code = 4; code < 232; ++code)
        destination[code] = [input buttonForKeyCode:static_cast<GCKeyCode>(code)].pressed ? 1 : 0;
    return 1;
}
