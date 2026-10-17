#import <GameController/GameController.h>
#import <UIKit/UIKit.h>
#include "NativeMouseState.h"

extern "C" UIView* UnityGetGLView(void);
static std::atomic<bool> active{false};
static std::atomic<int> gcEvents{0};
static int uiEvents;
static NativeMouseButtons uiButtons;
static bool uiSeen, hasPosition;
static float pointerX, pointerY;

// Preserve any handler installed by the engine or another plugin.
@interface NativeMouseButtonSubscription : NSObject
@property(nonatomic, strong) GCControllerButtonInput* button;
@property(nonatomic, copy) GCControllerButtonValueChangedHandler previous;
@property(nonatomic, copy) GCControllerButtonValueChangedHandler installed;
@end
@implementation NativeMouseButtonSubscription
- (void)dealloc
{
    if (self.button.pressedChangedHandler == self.installed)
        self.button.pressedChangedHandler = self.previous;
}
@end

@interface NativeMouseDevice : NSObject {
@public NativeMouseButtons buttons;
}
@property(nonatomic, strong) GCMouse* mouse;
@property(nonatomic, strong) NSMutableArray<NativeMouseButtonSubscription*>* subscriptions;
- (instancetype)initWithMouse:(GCMouse*)mouse;
@end
@implementation NativeMouseDevice
- (instancetype)initWithMouse:(GCMouse*)mouse
{
    if (!(self = [super init])) return nil;
    self.mouse = mouse;
    self.subscriptions = [NSMutableArray array];
    GCMouseInput* input = mouse.mouseInput;
    NSMutableArray* inputs = [NSMutableArray arrayWithObjects:
        input.leftButton ?: NSNull.null, input.rightButton ?: NSNull.null,
        input.middleButton ?: NSNull.null, nil];
    if (input.auxiliaryButtons) [inputs addObjectsFromArray:input.auxiliaryButtons];
    for (NSUInteger i = 0; i < MIN(inputs.count, (NSUInteger)7); ++i) {
        if (inputs[i] == NSNull.null) continue;
        GCControllerButtonInput* button = inputs[i];
        int bit = NativeMouseButtonBit((int)i + 1);
        buttons.Set(bit, button.pressed);
        NativeMouseButtonSubscription* subscription = [NativeMouseButtonSubscription new];
        subscription.button = button;
        subscription.previous = button.pressedChangedHandler;
        GCControllerButtonValueChangedHandler previous = subscription.previous;
        __weak NativeMouseDevice* weakSelf = self;
        subscription.installed = ^(GCControllerButtonInput* changed, float value, BOOL pressed) {
            NativeMouseDevice* owner = weakSelf;
            if (owner && active.load()) {
                owner->buttons.Set(bit, pressed);
                gcEvents.fetch_add(1);
            }
            if (previous) previous(changed, value, pressed);
        };
        button.pressedChangedHandler = subscription.installed;
        [self.subscriptions addObject:subscription];
    }
    return self;
}
@end

static NSMutableArray<NativeMouseDevice*>* devices;
static void AttachMouse(GCMouse* mouse)
{
    if (!mouse.mouseInput) return;
    for (NativeMouseDevice* device in devices) if (device.mouse == mouse) return;
    [devices addObject:[[NativeMouseDevice alloc] initWithMouse:mouse]];
}
static void ClearPointer()
{
    uiButtons.Clear();
    uiSeen = hasPosition = false;
    for (NativeMouseDevice* device in devices) device->buttons.Clear();
}
static void Position(CGPoint point, UIView* view)
{
    CGSize size = view.bounds.size;
    if (size.width <= 0 || size.height <= 0) return;
    pointerX = point.x / size.width;
    pointerY = 1.0 - point.y / size.height;
    hasPosition = uiSeen = true;
}

@interface NativeMouseHover : NSObject
- (void)moved:(UIHoverGestureRecognizer*)recognizer;
@end
@implementation NativeMouseHover
- (void)moved:(UIHoverGestureRecognizer*)recognizer
{
    if (active.load()) Position([recognizer locationInView:recognizer.view], recognizer.view);
}
@end

static void InitializeMouse()
{
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        active.store(UIApplication.sharedApplication.applicationState == UIApplicationStateActive);
        devices = [NSMutableArray array];
        NSNotificationCenter* center = NSNotificationCenter.defaultCenter;
        [center addObserverForName:GCMouseDidConnectNotification object:nil queue:NSOperationQueue.mainQueue
            usingBlock:^(NSNotification* note) { AttachMouse(note.object); }];
        [center addObserverForName:GCMouseDidDisconnectNotification object:nil queue:NSOperationQueue.mainQueue
            usingBlock:^(NSNotification* note) {
                for (NSInteger i = (NSInteger)devices.count - 1; i >= 0; --i)
                    if (devices[(NSUInteger)i].mouse == note.object) [devices removeObjectAtIndex:(NSUInteger)i];
                if (!devices.count) ClearPointer();
            }];
        [center addObserverForName:UIApplicationWillResignActiveNotification object:nil queue:NSOperationQueue.mainQueue
            usingBlock:^(NSNotification* note) { active.store(false); ClearPointer(); }];
        [center addObserverForName:UIApplicationDidBecomeActiveNotification object:nil queue:NSOperationQueue.mainQueue
            usingBlock:^(NSNotification* note) { ClearPointer(); active.store(true); }];
        for (GCMouse* mouse in GCMouse.mice) AttachMouse(mouse);
    });
    // Unity may replace its view during startup. Attach once to each live view.
    static __weak UIView* observedView;
    static NativeMouseHover* target;
    static UIHoverGestureRecognizer* hover;
    UIView* view = UnityGetGLView();
    if (view && observedView != view) {
        if (observedView) [observedView removeGestureRecognizer:hover];
        if (!target) target = [NativeMouseHover new];
        hover = [[UIHoverGestureRecognizer alloc] initWithTarget:target action:@selector(moved:)];
        hover.allowedTouchTypes = @[@(UITouchTypeIndirectPointer)];
        hover.cancelsTouchesInView = NO;
        [view addGestureRecognizer:hover];
        observedView = view;
        hasPosition = false;
    }
}

// UnityView's four touch entry points pass through here. Finger/pencil events
// remain touches. Pointer events become mouse input, never a second touch
// click on a binding row after InControl has already accepted that button.
extern "C" NSSet* NativeMouse_FilterTouches(NSSet* touches, UIEvent* event, UIView* view, bool cancelled)
{
    NSMutableSet* direct = nil;
    bool pointer = false;
    bool allPointerEnded = true;
    for (UITouch* touch in touches) {
        if (touch.type == UITouchTypeIndirectPointer) {
            if (!direct) direct = [touches mutableCopy];
            [direct removeObject:touch];
            pointer = true;
            if (touch.phase != UITouchPhaseEnded && touch.phase != UITouchPhaseCancelled)
                allPointerEnded = false;
            if (active.load()) Position([touch locationInView:view], view);
        }
    }
    if (pointer && active.load()) {
        int mask = NativeMouseUIKitState((unsigned long)event.buttonMask, cancelled || allPointerEnded);
        uiButtons.SetMask(mask);
        uiSeen = true;
        ++uiEvents;
    }
    return direct ?: touches;
}

extern "C" void NativeMouse_ReadState(NativeMouseSnapshot* result)
{
    if (!result) return;
    InitializeMouse();
    *result = {};
    result->devices = (int)devices.count;
    result->gcEvents = gcEvents.load();
    result->uiEvents = uiEvents;
    if (!active.load()) { result->buttons = -2; return; }
    for (NativeMouseDevice* device in devices) result->gcButtons |= device->buttons.Read();
    result->uiButtons = uiButtons.Read();
    result->buttons = devices.count || uiSeen
        ? NativeMouseMerge(result->gcButtons, result->uiButtons, devices.count > 0) : -1;
    result->hasPosition = hasPosition ? 1 : 0;
    result->x = pointerX;
    result->y = pointerY;
}
