#import <UIKit/UIKit.h>
#import <GameController/GameController.h>
#include "NativeControllerState.h"

static std::atomic<bool> ControllerActive{false};

@interface NativeTouchpadDevice : NSObject {
@public NativeControllerButtonState state;
}
@property(nonatomic, strong) GCController* controller;
@property(nonatomic, strong) GCControllerButtonInput* button;
@property(nonatomic, copy) GCControllerButtonValueChangedHandler previous;
@property(nonatomic, copy) GCControllerButtonValueChangedHandler installed;
@end

@implementation NativeTouchpadDevice
- (void)dealloc
{
    // Do not remove another consumer's handler if it replaced ours.
    if (self.button.pressedChangedHandler == self.installed)
        self.button.pressedChangedHandler = self.previous;
}
@end

static NSMutableArray<NativeTouchpadDevice*>* Touchpads;

static void AttachTouchpad(GCController* controller)
{
    for (NativeTouchpadDevice* device in Touchpads)
        if (device.controller == controller) return;
    GCExtendedGamepad* profile = controller.extendedGamepad;
    GCControllerButtonInput* button = nil;
    if ([profile isKindOfClass:GCDualSenseGamepad.class])
        button = ((GCDualSenseGamepad*)profile).touchpadButton;
    else if ([profile isKindOfClass:GCDualShockGamepad.class])
        button = ((GCDualShockGamepad*)profile).touchpadButton;
    if (!button) return;
    NativeTouchpadDevice* device = [NativeTouchpadDevice new];
    device.controller = controller;
    device.button = button;
    if (ControllerActive.load()) device->state.Set(button.pressed);
    device.previous = button.pressedChangedHandler;
    GCControllerButtonValueChangedHandler previous = device.previous;
    __weak NativeTouchpadDevice* weakDevice = device;
    device.installed = ^(GCControllerButtonInput* changed, float value, BOOL pressed) {
        NativeTouchpadDevice* live = weakDevice;
        if (live && ControllerActive.load()) live->state.Set(pressed);
        if (previous) previous(changed, value, pressed);
    };
    button.pressedChangedHandler = device.installed;
    [Touchpads addObject:device];
}

static void ClearTouchpads()
{
    for (NativeTouchpadDevice* device in Touchpads) device->state.Clear();
}

static void InitializeTouchpads()
{
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        ControllerActive.store(UIApplication.sharedApplication.applicationState == UIApplicationStateActive);
        Touchpads = [NSMutableArray array];
        NSNotificationCenter* center = NSNotificationCenter.defaultCenter;
        [center addObserverForName:GCControllerDidConnectNotification object:nil queue:NSOperationQueue.mainQueue
            usingBlock:^(NSNotification* note) { AttachTouchpad(note.object); }];
        [center addObserverForName:GCControllerDidDisconnectNotification object:nil queue:NSOperationQueue.mainQueue
            usingBlock:^(NSNotification* note) {
                for (NSInteger i = (NSInteger)Touchpads.count - 1; i >= 0; --i)
                    if (Touchpads[(NSUInteger)i].controller == note.object)
                        [Touchpads removeObjectAtIndex:(NSUInteger)i];
            }];
        [center addObserverForName:UIApplicationWillResignActiveNotification object:nil queue:NSOperationQueue.mainQueue
            usingBlock:^(NSNotification* note) { ControllerActive.store(false); ClearTouchpads(); }];
        [center addObserverForName:UIApplicationDidBecomeActiveNotification object:nil queue:NSOperationQueue.mainQueue
            usingBlock:^(NSNotification* note) { ClearTouchpads(); ControllerActive.store(true); }];
        for (GCController* controller in GCController.controllers) AttachTouchpad(controller);
    });
}

static int ReadTouchpad(NSString* product)
{
    InitializeTouchpads();
    if (!ControllerActive.load()) return -2;
    NativeTouchpadDevice* match = nil;
    for (NativeTouchpadDevice* device in Touchpads) {
        NSString* vendor = device.controller.vendorName;
        if (!product.length || !vendor.length || [vendor caseInsensitiveCompare:product] != NSOrderedSame) continue;
        // Unity device IDs are not Apple's controller indices. Identical names
        // cannot establish a safe association between multiple physical pads.
        if (match) {
            for (NativeTouchpadDevice* candidate in Touchpads) {
                NSString* name = candidate.controller.vendorName;
                if (name.length && [name caseInsensitiveCompare:product] == NSOrderedSame)
                    candidate->state.Clear();
            }
            return -1;
        }
        match = device;
    }
    // Some iOS versions expose different product/vendor strings. A single
    // physical controller with a touchpad is still unambiguous.
    if (!match && GCController.controllers.count == 1 && Touchpads.count == 1)
        match = Touchpads.firstObject;
    return match ? (match->state.Read() ? 1 : 0) : -1;
}

extern "C" int NativeController_ReadTouchpad(int deviceId, const char* product)
{
    // deviceId is retained in the managed diagnostic path, never treated as
    // an index into GCController.controllers.
    (void)deviceId;
    NSString* name = product ? [NSString stringWithUTF8String:product] : @"";
    if (NSThread.isMainThread) return ReadTouchpad(name ?: @"");
    __block int result = -1;
    dispatch_sync(dispatch_get_main_queue(), ^{ result = ReadTouchpad(name ?: @""); });
    return result;
}
