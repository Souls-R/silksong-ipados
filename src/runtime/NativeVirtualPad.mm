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
// Player-arranged control positions, stored per control name as "dx,dy" in canvas
// units so an adjusted layout fits every iPad instead of one screen size.
static NSString* const PadLayoutKey = @"NativeVirtualPadLayout";

// Laid out on the same 1194x834 canvas as NativeLaunchView, then scaled inside
// the safe area, so every iPad keeps identical proportions.
static const CGFloat PadCanvasWidth = 1194;
static const CGFloat PadCanvasHeight = 834;
static const float PadDeadZone = 0.18f;

static NativeVirtualPadView* PadView;
static BOOL PadAttached;
static BOOL PadEditing;
static int PadHeld, PadPending;
static float PadLeftX, PadLeftY;
static UILongPressGestureRecognizer* PadTouch;
static id PadTouchTarget;

static UIColor* PadFill(void) { return [UIColor colorWithWhite:1 alpha:.13]; }
static UIColor* PadActive(void) { return [UIColor colorWithWhite:1 alpha:.32]; }
static UIColor* PadLine(void) { return [UIColor colorWithWhite:1 alpha:.35]; }
static UIColor* PadEditLine(void) { return [UIColor colorWithRed:1 green:.72 blue:.28 alpha:.95]; }
static UIColor* PadText(void) { return [UIColor colorWithWhite:1 alpha:.86]; }

// One row per placeable control. The name doubles as the stored layout key, so
// renaming a control silently drops the saved position it used to have.
struct PadButtonSpec
{
    const char* name;
    const char* title;
    float x, y, w, h;
    int bit;
    CGFloat size;
};

static const PadButtonSpec PadButtons[] = {
    {"dpadUp", "▲", 414, 543, 62, 62, NativePadDpadUp, 20},
    {"dpadDown", "▼", 414, 675, 62, 62, NativePadDpadDown, 20},
    {"dpadLeft", "◀", 348, 609, 62, 62, NativePadDpadLeft, 20},
    {"dpadRight", "▶", 480, 609, 62, 62, NativePadDpadRight, 20},
    {"north", "Y", 982, 478, 66, 66, NativePadNorth, 24},
    {"south", "A", 982, 626, 66, 66, NativePadSouth, 24},
    {"west", "X", 908, 552, 66, 66, NativePadWest, 24},
    {"east", "B", 1056, 552, 66, 66, NativePadEast, 24},
    {"leftShoulder", "LB", 810, 372, 128, 54, NativePadLeftShoulder, 20},
    {"rightShoulder", "RB", 1046, 372, 128, 54, NativePadRightShoulder, 20},
    {"leftTrigger", "LT", 810, 302, 128, 54, NativePadLeftTrigger, 20},
    {"rightTrigger", "RT", 1046, 302, 128, 54, NativePadRightTrigger, 20},
    {"select", "SELECT", 898, 726, 112, 46, NativePadSelect, 15},
    {"start", "START", 1040, 726, 112, 46, NativePadStart, 15},
};

// Both the buttons and the stick are placeable, so the view can hold them in one
// list and address each one by the name that keys its stored position.
@protocol NativePadPlaceable
@property(nonatomic, copy) NSString* name;
@end

@interface NativePadButton : UIControl <NativePadPlaceable>
- (instancetype)initWithTitle:(NSString*)title bit:(int)bit size:(CGFloat)size;
@property(nonatomic, copy) NSString* name;
@property(nonatomic) int bit;
@property(nonatomic, strong) UILabel* captionLabel;
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
        self.captionLabel = caption;
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

@interface NativePadStick : UIControl <NativePadPlaceable>
- (void)releaseStick;
@property(nonatomic, copy) NSString* name;
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
// While the player arranges controls the stick must not steer the game, so it
// declines tracking and lets the drag gesture take the touch instead.
- (BOOL)beginTrackingWithTouch:(UITouch*)touch withEvent:(UIEvent*)event
{
    if (PadEditing) return NO;
    [self track:touch];
    return YES;
}
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
@property(nonatomic, strong) UIView* scrim;
@property(nonatomic, strong) NativePadStick* stick;
@property(nonatomic, strong) UILabel* hint;
@property(nonatomic, strong) NativePadButton* done;
@property(nonatomic, strong) NativePadButton* reset;
@property(nonatomic, strong) NSMutableArray<UIView<NativePadPlaceable>*>* draggables;
@property(nonatomic, strong) NSMutableDictionary<NSString*, NSValue*>* baseFrames;
@property(nonatomic, strong) NSMutableDictionary<NSString*, NSValue*>* offsets;
@property(nonatomic, strong) NSMutableDictionary<NSString*, UIPanGestureRecognizer*>* drags;
@property(nonatomic) CGPoint grab;
@property(nonatomic) BOOL realDeviceInput;
- (void)applyVisibility;
- (void)releaseAll;
- (void)setEditingState:(BOOL)editing;
- (void)prepareEditorDone:(NSString*)done reset:(NSString*)reset hint:(NSString*)hint;
- (void)applyLayout;
- (void)saveLayout;
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
- (NativePadButton*)buttonWithTitle:(NSString*)title bit:(int)bit size:(CGFloat)size
{
    NativePadButton* control = [[NativePadButton alloc] initWithTitle:title bit:bit size:size];
    [control addTarget:self action:@selector(press:) forControlEvents:UIControlEventTouchDown | UIControlEventTouchDragEnter];
    [control addTarget:self action:@selector(release:) forControlEvents:UIControlEventTouchUpInside |
        UIControlEventTouchUpOutside | UIControlEventTouchCancel | UIControlEventTouchDragExit];
    return control;
}
// Every control is placed from its default frame plus the stored offset, and can
// be dragged once the launcher opens the layout editor.
- (void)makeDraggable:(UIView<NativePadPlaceable>*)control name:(NSString*)name frame:(CGRect)frame
{
    control.name = name;
    self.baseFrames[name] = [NSValue valueWithCGRect:frame];
    UIPanGestureRecognizer* drag = [[UIPanGestureRecognizer alloc] initWithTarget:self action:@selector(drag:)];
    drag.enabled = NO;
    [control addGestureRecognizer:drag];
    self.drags[name] = drag;
    [self.draggables addObject:control];
}
- (void)buildControls
{
    self.draggables = [NSMutableArray array];
    self.baseFrames = [NSMutableDictionary dictionary];
    self.offsets = [NSMutableDictionary dictionary];
    self.drags = [NSMutableDictionary dictionary];

    // The stick is added first so it stays behind the buttons where they overlap.
    self.stick = [[NativePadStick alloc] initWithFrame:CGRectZero];
    self.stick.changed = ^(float x, float y) { PadLeftX = x; PadLeftY = y; };
    [self.canvas addSubview:self.stick];
    [self makeDraggable:self.stick name:@"stick" frame:CGRectMake(100, 445, 200, 200)];

    for (size_t i = 0; i < sizeof(PadButtons) / sizeof(PadButtons[0]); i++) {
        const PadButtonSpec& spec = PadButtons[i];
        NativePadButton* control = [self buttonWithTitle:[NSString stringWithUTF8String:spec.title]
                                                    bit:spec.bit size:spec.size];
        [self.canvas addSubview:control];
        [self makeDraggable:control name:[NSString stringWithUTF8String:spec.name]
                      frame:CGRectMake(spec.x, spec.y, spec.w, spec.h)];
    }
    [self buildEditor];
    [self loadLayout];
    [self applyLayout];
}
// The layout editor adds its own chrome: a scrim that dims the launcher page
// without swallowing touches, a hint line and the two editor buttons.
- (void)buildEditor
{
    self.scrim = [[UIView alloc] initWithFrame:self.bounds];
    self.scrim.backgroundColor = [UIColor colorWithWhite:0 alpha:.55];
    self.scrim.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
    self.scrim.userInteractionEnabled = NO;
    self.scrim.hidden = YES;
    // Behind the canvas, so the controls stay crisp above the dimmed page.
    [self insertSubview:self.scrim belowSubview:self.canvas];

    self.hint = [[UILabel alloc] initWithFrame:CGRectMake(0, 96, PadCanvasWidth, 40)];
    self.hint.textColor = [UIColor colorWithWhite:1 alpha:.8];
    self.hint.font = [UIFont systemFontOfSize:20];
    self.hint.textAlignment = NSTextAlignmentCenter;
    self.hint.numberOfLines = 0;
    self.hint.hidden = YES;
    [self.canvas addSubview:self.hint];

    self.reset = [[NativePadButton alloc] initWithTitle:@"" bit:0 size:20];
    self.reset.frame = CGRectMake(347, 20, 240, 56);
    [self.reset addTarget:self action:@selector(resetLayout:) forControlEvents:UIControlEventTouchUpInside];
    [self.canvas addSubview:self.reset];
    self.done = [[NativePadButton alloc] initWithTitle:@"" bit:0 size:20];
    self.done.frame = CGRectMake(607, 20, 240, 56);
    [self.done addTarget:self action:@selector(finishEditing:) forControlEvents:UIControlEventTouchUpInside];
    [self.canvas addSubview:self.done];
    self.reset.hidden = YES;
    self.done.hidden = YES;
}
- (void)press:(NativePadButton*)sender
{
    if (PadEditing) return;
    int previous = PadHeld;
    PadHeld |= sender.bit;
    PadPending |= sender.bit & ~previous;   // keep a tap that starts and ends between reads
}
- (void)release:(NativePadButton*)sender
{
    if (PadEditing) return;
    PadHeld &= ~sender.bit;
}
- (void)releaseAll
{
    PadHeld = 0;
    PadPending = 0;
    [self.stick releaseStick];
}
- (void)applyVisibility
{
    // Arranging controls counts as visible even when the display mode is off.
    BOOL visible = PadAttached && (PadEditing || NativeVirtualPadVisible(NativeVirtualPad_Mode(), self.realDeviceInput));
    self.hidden = !visible;
    // A hidden pad must not keep a pressed button alive.
    if (!visible) [self releaseAll];
}
- (void)prepareEditorDone:(NSString*)done reset:(NSString*)reset hint:(NSString*)hint
{
    self.done.captionLabel.text = done;
    self.reset.captionLabel.text = reset;
    self.hint.text = hint;
}
- (void)setEditingState:(BOOL)editing
{
    self.scrim.hidden = !editing;
    self.hint.hidden = !editing;
    self.done.hidden = !editing;
    self.reset.hidden = !editing;
    for (UIPanGestureRecognizer* drag in self.drags.allValues) drag.enabled = editing;
    for (UIView<NativePadPlaceable>* control in self.draggables) {
        control.layer.borderColor = (editing ? PadEditLine() : PadLine()).CGColor;
        control.layer.borderWidth = editing ? 2 : (control == self.stick ? 2 : 1);
    }
    // Arranging is not playing: drop anything still held.
    if (editing) [self releaseAll];
}
- (void)applyLayout
{
    for (UIView<NativePadPlaceable>* control in self.draggables) {
        CGRect base = [self.baseFrames[control.name] CGRectValue];
        NSValue* stored = self.offsets[control.name];
        CGPoint offset = stored ? stored.CGPointValue : CGPointZero;
        float x, y;
        NativeVirtualPadClampToCanvas(base.origin.x + offset.x, base.origin.y + offset.y,
                                      base.size.width, base.size.height,
                                      PadCanvasWidth, PadCanvasHeight, &x, &y);
        control.frame = CGRectMake(x, y, base.size.width, base.size.height);
    }
}
- (void)loadLayout
{
    NSDictionary* stored = [NSUserDefaults.standardUserDefaults dictionaryForKey:PadLayoutKey];
    for (NSString* name in stored) {
        NSArray<NSString*>* parts = [stored[name] componentsSeparatedByString:@","];
        if (parts.count != 2) continue;
        self.offsets[name] = [NSValue valueWithCGPoint:CGPointMake(parts[0].floatValue, parts[1].floatValue)];
    }
}
- (void)saveLayout
{
    NSMutableDictionary<NSString*, NSString*>* stored = [NSMutableDictionary dictionary];
    for (NSString* name in self.offsets) {
        CGPoint offset = [self.offsets[name] CGPointValue];
        if (offset.x == 0 && offset.y == 0) continue;
        stored[name] = [NSString stringWithFormat:@"%.1f,%.1f", offset.x, offset.y];
    }
    [NSUserDefaults.standardUserDefaults setObject:stored forKey:PadLayoutKey];
}
- (void)storeOffset:(NSString*)name frame:(CGRect)frame
{
    CGRect base = [self.baseFrames[name] CGRectValue];
    CGPoint offset = CGPointMake(frame.origin.x - base.origin.x, frame.origin.y - base.origin.y);
    if (offset.x == 0 && offset.y == 0) [self.offsets removeObjectForKey:name];
    else self.offsets[name] = [NSValue valueWithCGPoint:offset];
}
- (void)drag:(UIPanGestureRecognizer*)pan
{
    UIView<NativePadPlaceable>* control = (UIView<NativePadPlaceable>*)pan.view;
    NSString* name = control.name;
    if (!name) return;
    CGPoint point = [pan locationInView:self.canvas];
    if (pan.state == UIGestureRecognizerStateBegan) {
        // Remember where inside the control the finger landed, so it keeps that
        // grip instead of snapping its centre to the finger.
        self.grab = CGPointMake(point.x - control.frame.origin.x, point.y - control.frame.origin.y);
        return;
    }
    if (pan.state == UIGestureRecognizerStateChanged) {
        float x, y;
        NativeVirtualPadClampToCanvas(point.x - self.grab.x, point.y - self.grab.y,
                                      CGRectGetWidth(control.frame), CGRectGetHeight(control.frame),
                                      PadCanvasWidth, PadCanvasHeight, &x, &y);
        control.frame = CGRectMake(x, y, CGRectGetWidth(control.frame), CGRectGetHeight(control.frame));
        return;
    }
    // Ended, cancelled or failed: keep the position on screen and remember it.
    [self storeOffset:name frame:control.frame];
    [self saveLayout];
}
- (void)resetLayout:(id)sender
{
    [self.offsets removeAllObjects];
    [self applyLayout];
    [self saveLayout];
}
- (void)finishEditing:(id)sender { NativeVirtualPad_EndEditing(); }
@end

// A bare finger anywhere on the game screen means the player is back on touch
// input. The recognizer never cancels touches, so gameplay input is unaffected.
@interface NativePadTouchObserver : NSObject
- (void)touch:(UILongPressGestureRecognizer*)recognizer;
@end

@implementation NativePadTouchObserver
- (void)touch:(UILongPressGestureRecognizer*)recognizer
{
    if (recognizer.state != UIGestureRecognizerStateBegan || !PadAttached || !PadView || PadEditing) return;
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

// The launcher page is added to the host after the pad, so the pad has to be put
// back on top whenever it needs to be seen again.
static void PadEnsureView(void)
{
    UIView* host = UnityGetGLView();
    if (!host) return;
    if (!PadView) {
        PadView = [[NativeVirtualPadView alloc] initWithFrame:host.bounds];
        // Backgrounding releases everything, exactly like a disconnected pad.
        [NSNotificationCenter.defaultCenter addObserverForName:UIApplicationWillResignActiveNotification
            object:nil queue:NSOperationQueue.mainQueue
            usingBlock:^(NSNotification* note) { PadView.realDeviceInput = YES; [PadView applyVisibility]; }];
    }
    if (host.subviews.lastObject != PadView) [host addSubview:PadView];
    PadObserve(host);
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
    PadEnsureView();
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
extern "C" int NativeVirtualPad_Editing(void) { return PadEditing ? 1 : 0; }
extern "C" void NativeVirtualPad_BeginEditing(const char* done, const char* reset, const char* hint)
{
    PadAttached = YES;
    PadEnsureView();
    PadEditing = YES;
    [PadView prepareEditorDone:done ? [NSString stringWithUTF8String:done] : @"Done"
                         reset:reset ? [NSString stringWithUTF8String:reset] : @"Reset"
                          hint:hint ? [NSString stringWithUTF8String:hint] : @""];
    [PadView setEditingState:YES];
    [PadView applyVisibility];
}
extern "C" void NativeVirtualPad_EndEditing(void)
{
    PadEditing = NO;
    [PadView setEditingState:NO];
    [PadView saveLayout];
    NativeVirtualPad_Detach();
}
extern "C" void NativeVirtualPad_ReadState(NativeVirtualPadSnapshot* result)
{
    if (!result) return;
    *result = {};
    // The editor is on-screen only: while the player arranges controls the game
    // must not see a pad, or a stray press would leak into the launcher page.
    if (PadEditing) { result->state = 0; return; }
    if (NativeVirtualPad_Mode() == NativeVirtualPadNever) { result->state = -1; return; }
    if (!PadAttached || !PadView || PadView.hidden) { result->state = 0; return; }
    result->state = 1;
    result->buttons = PadPending | PadHeld;
    PadPending = 0;
    result->leftX = PadLeftX;
    result->leftY = PadLeftY;
}
