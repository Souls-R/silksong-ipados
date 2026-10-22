#import <UIKit/UIKit.h>
#import <CoreText/CoreText.h>
#import <GameController/GameController.h>
#import <QuartzCore/QuartzCore.h>
#include <math.h>
#include "NativeVirtualPad.h"
#include <stdlib.h>
#include <string.h>

extern "C" UIView* UnityGetGLView();

static NSDictionary* LaunchStrings;
static NSString* LaunchLanguage;
static int LaunchAction;
static NSString* const ProjectURL = @"https://github.com/Souls-R/silksong-ipados";

// The notice pages are authored on the same 1194x834 canvas as the on-screen
// pad. A phone safe area is a different shape, so every frame is mapped into it
// instead of the whole page being scaled into a 4:3 box and letterboxed.
static const CGFloat LaunchDesignWidth = 1194;
static const CGFloat LaunchDesignHeight = 834;

static NSString* AssetPath(NSString* name)
{
    return [[[NSBundle mainBundle] bundlePath] stringByAppendingPathComponent:
        [@"Data/Raw/native-launch" stringByAppendingPathComponent:name]];
}
static void LoadLaunchAssets()
{
    if (LaunchStrings) return;
    NSString* language = NSLocale.preferredLanguages.firstObject.lowercaseString ?: @"en";
    BOOL chinese = [language hasPrefix:@"zh"];
    BOOL traditional = ![language containsString:@"hans"] &&
        ([language containsString:@"hant"] || [language containsString:@"-tw"] ||
         [language containsString:@"-hk"] || [language containsString:@"-mo"]);
    LaunchLanguage = chinese ? (traditional ? @"zh-Hant" : @"zh-Hans") : @"en";
    NSData* data = [NSData dataWithContentsOfFile:AssetPath(@"strings.json")];
    NSDictionary* all = data ? [NSJSONSerialization JSONObjectWithData:data options:0 error:nil] : nil;
    LaunchStrings = all[LaunchLanguage] ?: @{};
    for (NSString* file in @[@"NotoSerifCJKsc-Regular.otf", @"Perpetua.otf"])
        CTFontManagerRegisterFontsForURL((__bridge CFURLRef)[NSURL fileURLWithPath:AssetPath(file)], kCTFontManagerScopeProcess, nil);
}
static NSString* Text(NSString* key) { return LaunchStrings[key] ?: key; }
static UIFont* MenuFont(CGFloat size)
{
    NSString* name = [LaunchLanguage isEqualToString:@"en"] ? @"Perpetua" : @"NotoSerifCJKsc-Regular";
    return [UIFont fontWithName:name size:size] ?: [UIFont systemFontOfSize:size];
}

@interface NativeLaunchView : UIView
@property(nonatomic, strong) UIView* canvas;
@property(nonatomic, strong) NSArray<UIButton*>* choices;
@property(nonatomic, strong) UIButton* nextLaunch;
@property(nonatomic, strong) UILabel* nextValue;
@property(nonatomic, strong) UIButton* padChoice;
@property(nonatomic, strong) UILabel* padValue;
@property(nonatomic, strong) UIButton* padEdit;
@property(nonatomic, strong) UIButton* hudChoice;
@property(nonatomic, strong) UILabel* hudValue;
@property(nonatomic, strong) UILabel* hudHint;
@property(nonatomic, strong) UIImageView* leftCursor;
@property(nonatomic, strong) UIImageView* rightCursor;
@property(nonatomic, strong) CADisplayLink* controllerPoll;
@property(nonatomic) NSInteger selection;
@property(nonatomic) int page;
@property(nonatomic, copy) NSString* pageDetail;
// The safe area the current page was laid out for, plus the design-to-safe-area
// ratios every frame is mapped with. Text takes the smaller ratio, so glyphs are
// never stretched and lines still fit their frames.
@property(nonatomic) CGSize footprint;
@property(nonatomic) CGFloat scaleX;
@property(nonatomic) CGFloat scaleY;
@property(nonatomic) CGFloat fontScale;
@property(nonatomic) unsigned previousButtons;
- (void)showPage:(int)page detail:(NSString*)detail;
- (void)stop;
@end

static NativeLaunchView* LaunchView;

@implementation NativeLaunchView
- (instancetype)initWithFrame:(CGRect)frame
{
    if ((self = [super initWithFrame:frame])) {
        self.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
        self.backgroundColor = UIColor.blackColor;
        self.canvas = [[UIView alloc] initWithFrame:CGRectMake(0, 0, LaunchDesignWidth, LaunchDesignHeight)];
        self.scaleX = self.scaleY = self.fontScale = 1;
        [self addSubview:self.canvas];
        self.accessibilityViewIsModal = YES;
    }
    return self;
}
- (void)layoutSubviews
{
    [super layoutSubviews];
    CGRect safe = UIEdgeInsetsInsetRect(self.bounds, self.safeAreaInsets);
    if (safe.size.width < 1 || safe.size.height < 1) return;
    // The canvas is the safe area itself, so the page spreads over the whole
    // screen instead of being scaled into a 4:3 box with bars on both sides.
    self.scaleX = safe.size.width / LaunchDesignWidth;
    self.scaleY = safe.size.height / LaunchDesignHeight;
    self.fontScale = MIN(self.scaleX, self.scaleY);
    self.canvas.bounds = CGRectMake(0, 0, safe.size.width, safe.size.height);
    self.canvas.center = CGPointMake(CGRectGetMidX(safe), CGRectGetMidY(safe));
    self.canvas.transform = CGAffineTransformIdentity;
    if (!CGSizeEqualToSize(safe.size, self.footprint)) {
        // The page was drawn before the safe area was known; lay it out again.
        self.footprint = safe.size;
        if (self.page) [self showPage:self.page detail:self.pageDetail];
    }
}
// Design frame in, canvas frame out: each axis follows its own ratio, so the page
// fills the safe area while the rendered text stays one unstretched size.
- (CGRect)scaledX:(CGFloat)x y:(CGFloat)y w:(CGFloat)w h:(CGFloat)h
{
    return CGRectMake(x * self.scaleX, y * self.scaleY, w * self.scaleX, h * self.scaleY);
}
- (BOOL)canBecomeFirstResponder { return YES; }
- (NSArray<UIKeyCommand*>*)keyCommands
{
    NSMutableArray* keys = [NSMutableArray array];
    for (NSString* input in @[UIKeyInputUpArrow, UIKeyInputDownArrow, UIKeyInputLeftArrow, UIKeyInputRightArrow, @"\r", @" "])
    {
        UIKeyCommand* command = [UIKeyCommand keyCommandWithInput:input modifierFlags:0 action:@selector(key:)];
        command.wantsPriorityOverSystemBehavior = YES;
        [keys addObject:command];
    }
    return keys;
}
- (UILabel*)label:(NSString*)text x:(CGFloat)x y:(CGFloat)y w:(CGFloat)w h:(CGFloat)h size:(CGFloat)size color:(UIColor*)color
{
    UILabel* label = [[UILabel alloc] initWithFrame:[self scaledX:x y:y w:w h:h]];
    label.text = text;
    label.font = MenuFont(size * self.fontScale);
    label.textColor = color;
    label.textAlignment = NSTextAlignmentCenter;
    label.numberOfLines = 0;
    [self.canvas addSubview:label];
    return label;
}
- (UIButton*)button:(NSString*)title y:(CGFloat)y action:(SEL)action
{
    UIButton* button = [UIButton buttonWithType:UIButtonTypeCustom];
    button.frame = [self scaledX:317 y:y w:560 h:54];
    [button setTitle:title forState:UIControlStateNormal];
    button.titleLabel.font = MenuFont(24 * self.fontScale);
    [button setTitleColor:[UIColor colorWithWhite:.85 alpha:1] forState:UIControlStateNormal];
    [button setTitleColor:UIColor.whiteColor forState:UIControlStateHighlighted];
    [button addTarget:self action:action forControlEvents:UIControlEventTouchUpInside];
    [self.canvas addSubview:button];
    return button;
}
- (void)background
{
    CAGradientLayer* glow = [CAGradientLayer layer];
    glow.frame = self.canvas.bounds;
    glow.type = kCAGradientLayerRadial;
    glow.startPoint = CGPointMake(.5, 1.12);
    glow.endPoint = CGPointMake(1.1, .35);
    glow.colors = @[(id)[UIColor colorWithRed:.40 green:.10 blue:.07 alpha:1].CGColor,
                    (id)[UIColor colorWithRed:.07 green:.02 blue:.02 alpha:1].CGColor,
                    (id)UIColor.blackColor.CGColor];
    glow.locations = @[@0, @.6, @1];
    [self.canvas.layer insertSublayer:glow atIndex:0];
    // Static accents, no particle renderer or animation loop after handoff.
    for (NSInteger i = 0; i < 24; i++) {
        CALayer* ember = [CALayer layer];
        ember.frame = [self scaledX:35 + (i * 173) % 1120 y:40 + (i * 229) % 760 w:1.5 h:5 + i % 8];
        ember.backgroundColor = [UIColor colorWithRed:.87 green:.41 blue:.22 alpha:.20 + (i % 4) * .04].CGColor;
        ember.transform = CATransform3DMakeRotation(.4, 0, 0, 1);
        [self.canvas.layer addSublayer:ember];
    }
}
- (void)title:(NSString*)title y:(CGFloat)y
{
    [self label:title x:90 y:y w:1014 h:52 size:34 color:UIColor.whiteColor];
    UIImageView* fleur = [[UIImageView alloc] initWithImage:[UIImage imageWithContentsOfFile:AssetPath(@"Warning_Fleur0008.png")]];
    fleur.contentMode = UIViewContentModeScaleAspectFit;
    fleur.frame = [self scaledX:402 y:y + 58 w:390 h:47];
    [self.canvas addSubview:fleur];
}
- (void)syncChoice
{
    BOOL show = [[NSUserDefaults standardUserDefaults] boolForKey:@"NativeShowNotice"];
    self.nextValue.text = Text(show ? @"showNotice" : @"skipNotice");
    self.nextLaunch.accessibilityLabel = Text(@"nextLaunch");
    self.nextLaunch.accessibilityValue = self.nextValue.text;
}
- (void)syncPad
{
    int mode = NativeVirtualPad_Mode();
    self.padValue.text = Text(mode == NativeVirtualPadAlways ? @"settingPadAlways" :
        (mode == NativeVirtualPadNever ? @"settingPadNever" : @"settingPadAuto"));
    self.padChoice.accessibilityLabel = Text(@"settingVirtualPad");
    self.padChoice.accessibilityValue = self.padValue.text;
}
- (void)syncHud
{
    BOOL enabled = [[NSUserDefaults standardUserDefaults] boolForKey:@"NativeMetalHUD"];
    self.hudValue.text = Text(enabled ? @"hudOn" : @"hudOff");
    self.hudChoice.accessibilityLabel = Text(@"settingHud");
    self.hudChoice.accessibilityValue = self.hudValue.text;
    // The overlay reads its environment when Metal starts, so a change made here
    // only shows up after the next start; the hint says so until then.
    const char* value = getenv("MTL_HUD_ENABLED");
    BOOL running = value && strcmp(value, "1") == 0;
    self.hudHint.text = Text(running == enabled ? @"noticeHelp" : @"hudRestart");
}
- (void)showPage:(int)page detail:(NSString*)detail
{
    [self stop];
    NativeVirtualPad_Detach();
    self.page = page;
    self.pageDetail = detail;
    self.nextLaunch = nil;
    self.nextValue = nil;
    self.padChoice = nil;
    self.padValue = nil;
    self.padEdit = nil;
    self.hudChoice = nil;
    self.hudValue = nil;
    self.hudHint = nil;
    self.selection = 0;
    for (UIView* view in self.canvas.subviews) [view removeFromSuperview];
    for (CALayer* layer in [self.canvas.layer.sublayers copy]) [layer removeFromSuperlayer];
    self.choices = @[];
    UIColor* body = [UIColor colorWithRed:.88 green:.85 blue:.82 alpha:1];
    if (page == 3) {
        [self label:Text(@"loading") x:100 y:723 w:994 h:40 size:18 color:body];
        return;
    }
    [self background];
    if (page == 1) {
        [self title:Text(@"noticeTitle") y:132];
        [self label:Text(@"noticeFree") x:157 y:250 w:880 h:62 size:21 color:UIColor.whiteColor];
        [self label:Text(@"noticeResale") x:157 y:316 w:880 h:76 size:21 color:body];
        UIButton* enter = [self button:Text(@"enterGame") y:398 action:@selector(enter:)];
        self.nextLaunch = [self button:@"" y:461 action:@selector(toggle:)];
        UILabel* label = [[UILabel alloc] initWithFrame:[self scaledX:25 y:0 w:250 h:54]];
        label.font = MenuFont(22 * self.fontScale); label.textColor = body; label.text = Text(@"nextLaunch");
        [self.nextLaunch addSubview:label];
        self.nextValue = [[UILabel alloc] initWithFrame:[self scaledX:255 y:0 w:280 h:54]];
        self.nextValue.font = MenuFont(22 * self.fontScale); self.nextValue.textColor = body;
        self.nextValue.textAlignment = NSTextAlignmentRight;
        [self.nextLaunch addSubview:self.nextValue];
        [self syncChoice];
        self.padChoice = [self button:@"" y:524 action:@selector(pad:)];
        UILabel* padLabel = [[UILabel alloc] initWithFrame:[self scaledX:25 y:0 w:250 h:54]];
        padLabel.font = MenuFont(22 * self.fontScale); padLabel.textColor = body; padLabel.text = Text(@"settingVirtualPad");
        [self.padChoice addSubview:padLabel];
        self.padValue = [[UILabel alloc] initWithFrame:[self scaledX:255 y:0 w:280 h:54]];
        self.padValue.font = MenuFont(22 * self.fontScale); self.padValue.textColor = body;
        self.padValue.textAlignment = NSTextAlignmentRight;
        [self.padChoice addSubview:self.padValue];
        [self syncPad];
        self.padEdit = [self button:Text(@"padEdit") y:587 action:@selector(editPad:)];
        self.hudChoice = [self button:@"" y:650 action:@selector(hud:)];
        UILabel* hudLabel = [[UILabel alloc] initWithFrame:[self scaledX:25 y:0 w:250 h:54]];
        hudLabel.font = MenuFont(22 * self.fontScale); hudLabel.textColor = body; hudLabel.text = Text(@"settingHud");
        [self.hudChoice addSubview:hudLabel];
        self.hudValue = [[UILabel alloc] initWithFrame:[self scaledX:255 y:0 w:280 h:54]];
        self.hudValue.font = MenuFont(22 * self.fontScale); self.hudValue.textColor = body;
        self.hudValue.textAlignment = NSTextAlignmentRight;
        [self.hudChoice addSubview:self.hudValue];
        // One shared hint line: the Settings pointer normally, the restart note
        // while the HUD choice has not been picked up yet.
        self.hudHint = [self label:Text(@"noticeHelp") x:117 y:710 w:960 h:34 size:15 color:[UIColor colorWithWhite:.65 alpha:1]];
        [self syncHud];
        UIButton* home = [self button:Text(@"projectHome") y:750 action:@selector(home:)];
        home.titleLabel.font = MenuFont(16 * self.fontScale);
        self.choices = @[enter, self.nextLaunch, self.padChoice, self.padEdit, self.hudChoice, home];
    } else {
        [self title:Text(page == 2 ? @"missingTitle" : (page == 6 ? @"startupErrorTitle" : @"loadErrorTitle")) y:122];
        NSString* introduction = [NSString stringWithFormat:@"%@\n%@", Text(@"missingIntro"), Text(@"missingInstruction")];
        [self label:page == 5 ? Text(@"restartHelp") : (page == 6 ? Text(@"startupErrorHelp") : introduction) x:127 y:257 w:940 h:78 size:21 color:body];
        NSString* path = [NSString stringWithFormat:@"%@\nSilksongResources-v1", Text(@"resourcePath")];
        [self label:path x:100 y:343 w:994 h:66 size:22 color:UIColor.whiteColor];
        [self label:Text(@"missingKeep") x:100 y:417 w:994 h:40 size:18 color:body];
        [self label:detail x:107 y:469 w:980 h:70 size:16 color:[UIColor colorWithWhite:.65 alpha:1]];
        UIButton* retry = [self button:Text(@"retry") y:558 action:@selector(retry:)];
        UIButton* home = [self button:Text(@"projectHome") y:625 action:@selector(home:)];
        home.titleLabel.font = MenuFont(16 * self.fontScale);
        retry.hidden = page == 6;
        self.choices = page == 6 ? @[home] : @[retry, home];
    }
    UIImage* cursor = [UIImage imageWithContentsOfFile:AssetPath(@"main_menu_cursor_small0010.png")];
    self.leftCursor = [[UIImageView alloc] initWithImage:cursor];
    self.rightCursor = [[UIImageView alloc] initWithImage:cursor];
    self.rightCursor.transform = CGAffineTransformMakeScale(-1, 1);
    [self.canvas addSubview:self.leftCursor]; [self.canvas addSubview:self.rightCursor];
    [self selectItemAtIndex:0];
    // Read controller state without replacing Unity's GameController handlers.
    self.previousButtons = [self controllerButtons];
    self.controllerPoll = [CADisplayLink displayLinkWithTarget:self selector:@selector(pollController:)];
    self.controllerPoll.preferredFrameRateRange = CAFrameRateRangeMake(30, 60, 60);
    [self.controllerPoll addToRunLoop:NSRunLoop.mainRunLoop forMode:NSRunLoopCommonModes];
    [self becomeFirstResponder];
    UIAccessibilityPostNotification(UIAccessibilityScreenChangedNotification, self.choices.firstObject);
}
- (void)selectItemAtIndex:(NSInteger)index
{
    if (!self.choices.count) return;
    self.selection = (index + self.choices.count) % self.choices.count;
    CGRect frame = self.choices[self.selection].frame;
    // The cursors keep their design distance from the row they mark.
    CGFloat cursorWidth = 26 * self.fontScale;
    CGFloat cursorHeight = 30 * self.fontScale;
    self.leftCursor.bounds = CGRectMake(0, 0, cursorWidth, cursorHeight);
    self.leftCursor.center = CGPointMake(frame.origin.x - 17 * self.fontScale, CGRectGetMidY(frame));
    self.rightCursor.bounds = CGRectMake(0, 0, cursorWidth, cursorHeight);
    self.rightCursor.center = CGPointMake(CGRectGetMaxX(frame) + 17 * self.fontScale, CGRectGetMidY(frame));
}
- (void)key:(UIKeyCommand*)command
{
    // The pad editor covers the page, so nothing behind it may react to keys.
    if (NativeVirtualPad_Editing()) return;
    if ([command.input isEqualToString:UIKeyInputUpArrow]) [self selectItemAtIndex:self.selection - 1];
    else if ([command.input isEqualToString:UIKeyInputDownArrow]) [self selectItemAtIndex:self.selection + 1];
    else if ([command.input isEqualToString:UIKeyInputLeftArrow] || [command.input isEqualToString:UIKeyInputRightArrow]) {
        if (self.choices.count && self.choices[self.selection] == self.nextLaunch) [self toggle:nil];
        else if (self.choices.count && self.choices[self.selection] == self.padChoice) [self pad:nil];
        else if (self.choices.count && self.choices[self.selection] == self.hudChoice) [self hud:nil];
    } else if (self.choices.count) [self.choices[self.selection] sendActionsForControlEvents:UIControlEventTouchUpInside];
}
- (unsigned)controllerButtons
{
    unsigned result = 0;
    for (GCController* controller in GCController.controllers) {
        GCExtendedGamepad* pad = controller.extendedGamepad;
        if (!pad) continue;
        if (pad.dpad.up.isPressed || pad.leftThumbstick.yAxis.value > .6) result |= 1;
        if (pad.dpad.down.isPressed || pad.leftThumbstick.yAxis.value < -.6) result |= 2;
        if (pad.dpad.left.isPressed || pad.dpad.right.isPressed || fabsf(pad.leftThumbstick.xAxis.value) > .6) result |= 4;
        if (pad.buttonA.isPressed || pad.buttonMenu.isPressed) result |= 8;
    }
    return result;
}
- (void)pollController:(CADisplayLink*)sender
{
    unsigned current = [self controllerButtons];
    // The pad editor covers the page; inputs it swallows must not queue up and
    // fire the moment the player leaves it.
    unsigned pressed = NativeVirtualPad_Editing() ? 0 : current & ~self.previousButtons;
    self.previousButtons = current;
    if (pressed & 1) [self selectItemAtIndex:self.selection - 1];
    if (pressed & 2) [self selectItemAtIndex:self.selection + 1];
    if (pressed & 4) {
        if (self.choices.count && self.choices[self.selection] == self.nextLaunch) [self toggle:nil];
        else if (self.choices.count && self.choices[self.selection] == self.padChoice) [self pad:nil];
        else if (self.choices.count && self.choices[self.selection] == self.hudChoice) [self hud:nil];
    }
    if ((pressed & 8) && self.choices.count) [self.choices[self.selection] sendActionsForControlEvents:UIControlEventTouchUpInside];
}
- (void)enter:(id)sender { if (NativeVirtualPad_Editing()) return; LaunchAction = 1; }
- (void)retry:(id)sender { if (NativeVirtualPad_Editing()) return; LaunchAction = 2; }
- (void)toggle:(id)sender
{
    if (NativeVirtualPad_Editing()) return;
    NSUserDefaults* settings = NSUserDefaults.standardUserDefaults;
    [settings setBool:![settings boolForKey:@"NativeShowNotice"] forKey:@"NativeShowNotice"];
    [self syncChoice];
}
- (void)pad:(id)sender
{
    if (NativeVirtualPad_Editing()) return;
    int mode = NativeVirtualPad_Mode();
    NativeVirtualPad_SetMode(mode >= NativeVirtualPadNever ? NativeVirtualPadAuto : mode + 1);
    [self syncPad];
}
// Hands the screen to the pad's own layout editor, which positions every control
// by dragging and remembers the result.
- (void)editPad:(id)sender
{
    if (NativeVirtualPad_Editing()) return;
    NativeVirtualPad_BeginEditing(Text(@"padEditDone").UTF8String, Text(@"padEditReset").UTF8String,
                                  Text(@"padEditHint").UTF8String);
}
- (void)hud:(id)sender
{
    if (NativeVirtualPad_Editing()) return;
    // Same preference the generated startup code reads before Unity loads Metal,
    // so the row and the system Settings switch stay in sync.
    NSUserDefaults* settings = NSUserDefaults.standardUserDefaults;
    [settings setBool:![settings boolForKey:@"NativeMetalHUD"] forKey:@"NativeMetalHUD"];
    [self syncHud];
}
- (void)home:(id)sender
{
    if (NativeVirtualPad_Editing()) return;
    [UIApplication.sharedApplication openURL:[NSURL URLWithString:ProjectURL] options:@{} completionHandler:nil];
}
- (void)stop { [self.controllerPoll invalidate]; self.controllerPoll = nil; }
@end

extern "C" int NativeSettings_Flags()
{
    NSUserDefaults* settings = NSUserDefaults.standardUserDefaults;
    int flags = [settings boolForKey:@"NativeDiagnosticLogs"] ? 3 : 0;
    int bit = 4;
    for (NSString* key in @[@"NativeStartupDetails", @"NativeShowNotice", @"NativeRecheckResources"]) {
        if ([settings boolForKey:key]) flags |= bit;
        bit <<= 1;
    }
    return flags;
}
extern "C" void NativeSettings_ConsumeRecheck()
{
    [NSUserDefaults.standardUserDefaults setBool:NO forKey:@"NativeRecheckResources"];
}
extern "C" void NativeLaunch_Show(int page, const char* detail)
{
    LoadLaunchAssets();
    if (!LaunchView) {
        UIView* host = UnityGetGLView();
        LaunchView = [[NativeLaunchView alloc] initWithFrame:host.bounds];
        [host addSubview:LaunchView];
    }
    LaunchAction = 0;
    [LaunchView showPage:page detail:detail ? [NSString stringWithUTF8String:detail] : @""];
}
extern "C" int NativeLaunch_Poll() { int action = LaunchAction; LaunchAction = 0; return action; }
extern "C" void NativeLaunch_Hide()
{
    [LaunchView stop];
    [LaunchView resignFirstResponder];
    [LaunchView removeFromSuperview];
    LaunchView = nil;
    LaunchAction = 0;
    // Gameplay is about to start: the launcher yields to the on-screen pad.
    NativeVirtualPad_Attach();
}
