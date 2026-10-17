#import <UIKit/UIKit.h>
#import <CoreText/CoreText.h>
#import <GameController/GameController.h>
#import <QuartzCore/QuartzCore.h>
#include <math.h>

extern "C" UIView* UnityGetGLView();

static NSDictionary* LaunchStrings;
static NSString* LaunchLanguage;
static int LaunchAction;
static NSString* const ProjectURL = @"https://github.com/Souls-R/silksong-ipados";

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
@property(nonatomic, strong) UIImageView* leftCursor;
@property(nonatomic, strong) UIImageView* rightCursor;
@property(nonatomic, strong) CADisplayLink* controllerPoll;
@property(nonatomic) NSInteger selection;
@property(nonatomic) int page;
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
        self.canvas = [[UIView alloc] initWithFrame:CGRectMake(0, 0, 1194, 834)];
        [self addSubview:self.canvas];
        self.accessibilityViewIsModal = YES;
    }
    return self;
}
- (void)layoutSubviews
{
    [super layoutSubviews];
    CGRect safe = UIEdgeInsetsInsetRect(self.bounds, self.safeAreaInsets);
    CGFloat scale = MIN(safe.size.width / 1194.0, safe.size.height / 834.0);
    self.canvas.bounds = CGRectMake(0, 0, 1194, 834);
    self.canvas.center = CGPointMake(CGRectGetMidX(safe), CGRectGetMidY(safe));
    self.canvas.transform = CGAffineTransformMakeScale(scale, scale);
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
- (UILabel*)label:(NSString*)text frame:(CGRect)frame size:(CGFloat)size color:(UIColor*)color
{
    UILabel* label = [[UILabel alloc] initWithFrame:frame];
    label.text = text;
    label.font = MenuFont(size);
    label.textColor = color;
    label.textAlignment = NSTextAlignmentCenter;
    label.numberOfLines = 0;
    [self.canvas addSubview:label];
    return label;
}
- (UIButton*)button:(NSString*)title y:(CGFloat)y action:(SEL)action
{
    UIButton* button = [UIButton buttonWithType:UIButtonTypeCustom];
    button.frame = CGRectMake(317, y, 560, 54);
    [button setTitle:title forState:UIControlStateNormal];
    button.titleLabel.font = MenuFont(24);
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
        ember.frame = CGRectMake(35 + (i * 173) % 1120, 40 + (i * 229) % 760, 1.5, 5 + i % 8);
        ember.backgroundColor = [UIColor colorWithRed:.87 green:.41 blue:.22 alpha:.20 + (i % 4) * .04].CGColor;
        ember.transform = CATransform3DMakeRotation(.4, 0, 0, 1);
        [self.canvas.layer addSublayer:ember];
    }
}
- (void)title:(NSString*)title y:(CGFloat)y
{
    [self label:title frame:CGRectMake(90, y, 1014, 52) size:34 color:UIColor.whiteColor];
    UIImageView* fleur = [[UIImageView alloc] initWithImage:[UIImage imageWithContentsOfFile:AssetPath(@"Warning_Fleur0008.png")]];
    fleur.contentMode = UIViewContentModeScaleAspectFit;
    fleur.frame = CGRectMake(402, y + 58, 390, 47);
    [self.canvas addSubview:fleur];
}
- (void)syncChoice
{
    BOOL show = [[NSUserDefaults standardUserDefaults] boolForKey:@"NativeShowNotice"];
    self.nextValue.text = Text(show ? @"showNotice" : @"skipNotice");
    self.nextLaunch.accessibilityLabel = Text(@"nextLaunch");
    self.nextLaunch.accessibilityValue = self.nextValue.text;
}
- (void)showPage:(int)page detail:(NSString*)detail
{
    [self stop];
    self.page = page;
    self.nextLaunch = nil;
    self.nextValue = nil;
    self.selection = 0;
    for (UIView* view in self.canvas.subviews) [view removeFromSuperview];
    for (CALayer* layer in [self.canvas.layer.sublayers copy]) [layer removeFromSuperlayer];
    self.choices = @[];
    UIColor* body = [UIColor colorWithRed:.88 green:.85 blue:.82 alpha:1];
    if (page == 3) {
        [self label:Text(@"loading") frame:CGRectMake(100, 723, 994, 40) size:18 color:body];
        return;
    }
    [self background];
    if (page == 1) {
        [self title:Text(@"noticeTitle") y:162];
        [self label:Text(@"noticeFree") frame:CGRectMake(157, 298, 880, 62) size:21 color:UIColor.whiteColor];
        [self label:Text(@"noticeResale") frame:CGRectMake(157, 369, 880, 76) size:21 color:body];
        UIButton* enter = [self button:Text(@"enterGame") y:463 action:@selector(enter:)];
        self.nextLaunch = [self button:@"" y:526 action:@selector(toggle:)];
        UILabel* label = [[UILabel alloc] initWithFrame:CGRectMake(25, 0, 250, 54)];
        label.font = MenuFont(22); label.textColor = body; label.text = Text(@"nextLaunch");
        [self.nextLaunch addSubview:label];
        self.nextValue = [[UILabel alloc] initWithFrame:CGRectMake(255, 0, 280, 54)];
        self.nextValue.font = MenuFont(22); self.nextValue.textColor = body;
        self.nextValue.textAlignment = NSTextAlignmentRight;
        [self.nextLaunch addSubview:self.nextValue];
        [self syncChoice];
        [self label:Text(@"noticeHelp") frame:CGRectMake(117, 598, 960, 40) size:15 color:[UIColor colorWithWhite:.65 alpha:1]];
        UIButton* home = [self button:Text(@"projectHome") y:650 action:@selector(home:)];
        home.titleLabel.font = MenuFont(16);
        self.choices = @[enter, self.nextLaunch, home];
    } else {
        [self title:Text(page == 2 ? @"missingTitle" : (page == 6 ? @"startupErrorTitle" : @"loadErrorTitle")) y:122];
        NSString* introduction = [NSString stringWithFormat:@"%@\n%@", Text(@"missingIntro"), Text(@"missingInstruction")];
        [self label:page == 5 ? Text(@"restartHelp") : (page == 6 ? Text(@"startupErrorHelp") : introduction) frame:CGRectMake(127, 257, 940, 78) size:21 color:body];
        NSString* path = [NSString stringWithFormat:@"%@\nSilksongResources-v1", Text(@"resourcePath")];
        [self label:path frame:CGRectMake(100, 343, 994, 66) size:22 color:UIColor.whiteColor];
        [self label:Text(@"missingKeep") frame:CGRectMake(100, 417, 994, 40) size:18 color:body];
        [self label:detail frame:CGRectMake(107, 469, 980, 70) size:16 color:[UIColor colorWithWhite:.65 alpha:1]];
        UIButton* retry = [self button:Text(@"retry") y:558 action:@selector(retry:)];
        UIButton* home = [self button:Text(@"projectHome") y:625 action:@selector(home:)];
        home.titleLabel.font = MenuFont(16);
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
    self.leftCursor.frame = CGRectMake(frame.origin.x - 30, CGRectGetMidY(frame) - 15, 26, 30);
    self.rightCursor.bounds = CGRectMake(0, 0, 26, 30);
    self.rightCursor.center = CGPointMake(CGRectGetMaxX(frame) + 17, CGRectGetMidY(frame));
}
- (void)key:(UIKeyCommand*)command
{
    if ([command.input isEqualToString:UIKeyInputUpArrow]) [self selectItemAtIndex:self.selection - 1];
    else if ([command.input isEqualToString:UIKeyInputDownArrow]) [self selectItemAtIndex:self.selection + 1];
    else if ([command.input isEqualToString:UIKeyInputLeftArrow] || [command.input isEqualToString:UIKeyInputRightArrow]) {
        if (self.choices.count && self.choices[self.selection] == self.nextLaunch) [self toggle:nil];
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
    unsigned pressed = current & ~self.previousButtons;
    self.previousButtons = current;
    if (pressed & 1) [self selectItemAtIndex:self.selection - 1];
    if (pressed & 2) [self selectItemAtIndex:self.selection + 1];
    if ((pressed & 4) && self.choices.count && self.choices[self.selection] == self.nextLaunch) [self toggle:nil];
    if ((pressed & 8) && self.choices.count) [self.choices[self.selection] sendActionsForControlEvents:UIControlEventTouchUpInside];
}
- (void)enter:(id)sender { LaunchAction = 1; }
- (void)retry:(id)sender { LaunchAction = 2; }
- (void)toggle:(id)sender
{
    NSUserDefaults* settings = NSUserDefaults.standardUserDefaults;
    [settings setBool:![settings boolForKey:@"NativeShowNotice"] forKey:@"NativeShowNotice"];
    [self syncChoice];
}
- (void)home:(id)sender { [UIApplication.sharedApplication openURL:[NSURL URLWithString:ProjectURL] options:@{} completionHandler:nil]; }
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
}
