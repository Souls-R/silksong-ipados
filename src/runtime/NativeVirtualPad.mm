#import <UIKit/UIKit.h>
#import <QuartzCore/QuartzCore.h>
#include "NativeVirtualPad.h"

extern "C" UIView* UnityGetGLView(void);

@class NativeVirtualPadView;

// The pad is a touch surface only: every control is opaque to touches, the rest
// of the overlay is transparent so the game keeps receiving its own touches.
// A synthetic iOS gamepad reports the resulting state; the launcher chooses the
// display mode (see NativeVirtualPad.h).
static NSString* const PadModeKey = @"NativeVirtualPadMode";

// Laid out on the same 1194x834 canvas as NativeLaunchView, then scaled inside
// the safe area, so every iPad keeps identical proportions.
static const CGFloat PadCanvasWidth = 1194;
static const CGFloat PadCanvasHeight = 834;
static const float PadDeadZone = 0.18f;

static NativeVirtualPadView* PadView;
static BOOL PadAttached;
static int PadHeld, PadPending;
static float PadLeftX, PadLeftY;
static UILongPressGestureRecognizer* PadTouch;
static id PadTouchTarget;

static UIColor* PadFill(void) { return [UIColor colorWithWhite:1 alpha:.13]; }
static UIColor* PadActive(void) { return [UIColor colorWithWhite:1 alpha:.32]; }
static UIColor* PadLine(void) { return [UIColor colorWithWhite:1 alpha:.35]; }
static UIColor* PadText(void) { return [UIColor colorWithWhite:1 alpha:.86]; }

@interface NativePadButton : UIControl
- (instancetype)initWithTitle:(NSString*)title bit:(int)bit size:(CGFloat)size;
@property(nonatomic) int bit;
@end

@implementation NativePadButton
- (instancetype)initWithTitle:(NSString*)title bit:(int)bit size:(CGFloat)size
{
    if ((self = [super initWithFrame:CGRectZero])) {
        self.bit = bit;
        self.backgroundColor = PadFill();
        self.layer.borderWidth = 1;
        self.layer.borderColor = PadLine().CGColor;
        UILabel* caption = [UILabel new];
        caption.text = title;
        caption.textColor = PadText();
        caption.font = [UIFont systemFontOfSize:size weight:UIFontWeightSemibold];
        caption.textAlignment = NSTextAlignmentCenter;
        caption.translatesAutoresizingMaskIntoConstraints = NO;
        caption.userInteractionEnabled = NO;
        [self addSubview:caption];
        [NSLayoutConstraint activateConstraints:@[
            [caption.centerXAnchor constraintEqualToAnchor:self.centerXAnchor],
            [caption.centerYAnchor constraintEqualToAnchor:self.centerYAnchor]]];
    }
    return self;
}
- (void)layoutSubviews
{
    [super layoutSubviews];
    self.layer.cornerRadius = CGRectGetHeight(self.bounds) / 2;
}
// The pad keeps its own pressed look; the game's own feedback stays on the stick.
- (void)setHighlighted:(BOOL)highlighted
{
    [super setHighlighted:highlighted];
    self.backgroundColor = highlighted ? PadActive() : PadFill();
}
@end

@interface NativePadStick : UIControl
- (void)releaseStick;
@property(nonatomic, strong) UIView* knob;
@property(nonatomic, copy) void (^changed)(float x, float y);
@end

@implementation NativePadStick
- (instancetype)initWithFrame:(CGRect)frame
{
    if ((self = [super initWithFrame:frame])) {
        self.backgroundColor = PadFill();
        self.layer.borderWidth = 2;
        self.layer.borderColor = PadLine().CGColor;
        self.knob = [UIView new];
        self.knob.backgroundColor = PadActive();
        self.knob.userInteractionEnabled = NO;
        [self addSubview:self.knob];
    }
    return self;
}
- (void)layoutSubviews
{
    [super layoutSubviews];
    self.layer.cornerRadius = CGRectGetWidth(self.bounds) / 2;
    CGFloat size = CGRectGetWidth(self.bounds) * .42;
    self.knob.bounds = CGRectMake(0, 0, size, size);
    self.knob.layer.cornerRadius = size / 2;
    if (!self.tracking) [self releaseStick];
}
- (BOOL)beginTrackingWithTouch:(UITouch*)touch withEvent:(UIEvent*)event { [self track:touch]; return YES; }
- (BOOL)continueTrackingWithTouch:(UITouch*)touch withEvent:(UIEvent*)event { [self track:touch]; return YES; }
- (void)endTrackingWithTouch:(UITouch*)touch withEvent:(UIEvent*)event { [self releaseStick]; }
- (void)cancelTrackingWithEvent:(UIEvent*)event { [self releaseStick]; }
- (void)track:(UITouch*)touch
{
    CGFloat radius = CGRectGetWidth(self.bounds) / 2;
    if (radius <= 0) return;
    CGPoint center = CGPointMake(CGRectGetMidX(self.bounds), CGRectGetMidY(self.bounds));
    CGPoint point = [touch locationInView:self];
    float x, y;
    // UIKit is y-down; a gamepad stick is y-up.
    NativeVirtualPadStick((float)((point.x - center.x) / radius), (float)((center.y - point.y) / radius),
                          PadDeadZone, &x, &y);
    self.knob.center = CGPointMake(center.x + x * radius * .55, center.y - y * radius * .55);
    if (self.changed) self.changed(x, y);
}
- (void)releaseStick
{
    self.knob.center = CGPointMake(CGRectGetMidX(self.bounds), CGRectGetMidY(self.bounds));
    if (self.changed) self.changed(0, 0);
}
@end

@interface NativeVirtualPadView : UIView
@property(nonatomic, strong) UIView* canvas;
@property(nonatomic, strong) NativePadStick* stick;
@property(nonatomic) BOOL realDeviceInput;
- (void)applyVisibility;
- (void)releaseAll;
@end

@implementation NativeVirtualPadView
- (instancetype)initWithFrame:(CGRect)frame
{
    if ((self = [super initWithFrame:frame])) {
        self.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
        self.backgroundColor = UIColor.clearColor;
        self.canvas = [[UIView alloc] initWithFrame:CGRectMake(0, 0, PadCanvasWidth, PadCanvasHeight)];
        self.canvas.backgroundColor = UIColor.clearColor;
        [self addSubview:self.canvas];
        [self buildControls];
    }
    return self;
}
- (void)layoutSubviews
{
    [super layoutSubviews];
    CGRect safe = UIEdgeInsetsInsetRect(self.bounds, self.safeAreaInsets);
    CGFloat scale = MIN(safe.size.width / PadCanvasWidth, safe.size.height / PadCanvasHeight);
    self.canvas.bounds = CGRectMake(0, 0, PadCanvasWidth, PadCanvasHeight);
    self.canvas.center = CGPointMake(CGRectGetMidX(safe), CGRectGetMidY(safe));
    self.canvas.transform = CGAffineTransformMakeScale(scale, scale);
}
// Touches on a control belong to the pad; everything else reaches the game.
- (UIView*)hitTest:(CGPoint)point withEvent:(UIEvent*)event
{
    UIView* hit = [super hitTest:point withEvent:event];
    return hit == self || hit == self.canvas ? nil : hit;
}
- (NativePadButton*)addControl:(NSString*)title frame:(CGRect)frame bit:(int)bit size:(CGFloat)size
{
    NativePadButton* control = [[NativePadButton alloc] initWithTitle:title bit:bit size:size];
    control.frame = frame;
    [control addTarget:self action:@selector(press:) forControlEvents:UIControlEventTouchDown | UIControlEventTouchDragEnter];
    [control addTarget:self action:@selector(release:) forControlEvents:UIControlEventTouchUpInside |
        UIControlEventTouchUpOutside | UIControlEventTouchCancel | UIControlEventTouchDragExit];
    [self.canvas addSubview:control];
    return control;
}
- (void)buildControls
{
    self.stick = [[NativePadStick alloc] initWithFrame:CGRectMake(100, 445, 200, 200)];
    self.stick.changed = ^(float x, float y) { PadLeftX = x; PadLeftY = y; };
    [self.canvas addSubview:self.stick];
    [self addControl:@"▲" frame:CGRectMake(414, 543, 62, 62) bit:NativePadDpadUp size:20];
    [self addControl:@"▼" frame:CGRectMake(414, 675, 62, 62) bit:NativePadDpadDown size:20];
    [self addControl:@"◀" frame:CGRectMake(348, 609, 62, 62) bit:NativePadDpadLeft size:20];
    [self addControl:@"▶" frame:CGRectMake(480, 609, 62, 62) bit:NativePadDpadRight size:20];
    [self addControl:@"Y" frame:CGRectMake(982, 478, 66, 66) bit:NativePadNorth size:24];
    [self addControl:@"A" frame:CGRectMake(982, 626, 66, 66) bit:NativePadSouth size:24];
    [self addControl:@"X" frame:CGRectMake(908, 552, 66, 66) bit:NativePadWest size:24];
    [self addControl:@"B" frame:CGRectMake(1056, 552, 66, 66) bit:NativePadEast size:24];
    [self addControl:@"LB" frame:CGRectMake(810, 372, 128, 54) bit:NativePadLeftShoulder size:20];
    [self addControl:@"RB" frame:CGRectMake(1046, 372, 128, 54) bit:NativePadRightShoulder size:20];
    [self addControl:@"LT" frame:CGRectMake(810, 302, 128, 54) bit:NativePadLeftTrigger size:20];
    [self addControl:@"RT" frame:CGRectMake(1046, 302, 128, 54) bit:NativePadRightTrigger size:20];
    [self addControl:@"SELECT" frame:CGRectMake(898, 726, 112, 46) bit:NativePadSelect size:15];
    [self addControl:@"START" frame:CGRectMake(1040, 726, 112, 46) bit:NativePadStart size:15];
}
- (void)press:(NativePadButton*)sender
{
    int previous = PadHeld;
    PadHeld |= sender.bit;
    PadPending |= sender.bit & ~previous;   // keep a tap that starts and ends between reads
}
- (void)release:(NativePadButton*)sender { PadHeld &= ~sender.bit; }
- (void)releaseAll
{
    PadHeld = 0;
    PadPending = 0;
    [self.stick releaseStick];
}
- (void)applyVisibility
{
    BOOL visible = PadAttached && NativeVirtualPadVisible(NativeVirtualPad_Mode(), self.realDeviceInput);
    self.hidden = !visible;
    // A hidden pad must not keep a pressed button alive.
    if (!visible) [self releaseAll];
}
@end

// A bare finger anywhere on the game screen means the player is back on touch
// input. The recognizer never cancels touches, so gameplay input is unaffected.
@interface NativePadTouchObserver : NSObject
- (void)touch:(UILongPressGestureRecognizer*)recognizer;
@end

@implementation NativePadTouchObserver
- (void)touch:(UILongPressGestureRecognizer*)recognizer
{
    if (recognizer.state != UIGestureRecognizerStateBegan || !PadAttached || !PadView) return;
    PadView.realDeviceInput = NO;
    [PadView applyVisibility];
}
@end

static void PadObserve(UIView* host)
{
    if (!PadTouchTarget) PadTouchTarget = [NativePadTouchObserver new];
    if (PadTouch.view == host) return;
    if (PadTouch) [PadTouch.view removeGestureRecognizer:PadTouch];
    PadTouch = [[UILongPressGestureRecognizer alloc] initWithTarget:PadTouchTarget action:@selector(touch:)];
    PadTouch.minimumPressDuration = 0;
    PadTouch.cancelsTouchesInView = NO;
    PadTouch.delaysTouchesBegan = NO;
    PadTouch.delaysTouchesEnded = NO;
    PadTouch.allowedTouchTypes = @[@(UITouchTypeDirect)];
    [host addGestureRecognizer:PadTouch];
}

extern "C" int NativeVirtualPad_Mode(void)
{
    return (int)[NSUserDefaults.standardUserDefaults integerForKey:PadModeKey];
}
extern "C" void NativeVirtualPad_SetMode(int mode)
{
    if (mode < NativeVirtualPadAuto || mode > NativeVirtualPadNever) mode = NativeVirtualPadAuto;
    [NSUserDefaults.standardUserDefaults setInteger:mode forKey:PadModeKey];
    [PadView applyVisibility];
}
extern "C" void NativeVirtualPad_Attach(void)
{
    PadAttached = YES;
    UIView* host = UnityGetGLView();
    if (!host) return;
    if (!PadView) {
        PadView = [[NativeVirtualPadView alloc] initWithFrame:host.bounds];
        // Backgrounding releases everything, exactly like a disconnected pad.
        [NSNotificationCenter.defaultCenter addObserverForName:UIApplicationWillResignActiveNotification
            object:nil queue:NSOperationQueue.mainQueue
            usingBlock:^(NSNotification* note) { PadView.realDeviceInput = YES; [PadView applyVisibility]; }];
    }
    if (PadView.superview != host) [host addSubview:PadView];
    PadObserve(host);
    [PadView applyVisibility];
}
extern "C" void NativeVirtualPad_Detach(void)
{
    PadAttached = NO;
    PadView.hidden = YES;
    [PadView releaseAll];
}
extern "C" void NativeVirtualPad_RealDeviceInput(void)
{
    if (!PadAttached || !PadView || PadView.realDeviceInput) return;
    PadView.realDeviceInput = YES;
    [PadView applyVisibility];
}
extern "C" void NativeVirtualPad_ReadState(NativeVirtualPadSnapshot* result)
{
    if (!result) return;
    *result = {};
    if (NativeVirtualPad_Mode() == NativeVirtualPadNever) { result->state = -1; return; }
    if (!PadAttached || !PadView || PadView.hidden) { result->state = 0; return; }
    result->state = 1;
    result->buttons = PadPending | PadHeld;
    PadPending = 0;
    result->leftX = PadLeftX;
    result->leftY = PadLeftY;
}
