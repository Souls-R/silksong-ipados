#import <UIKit/UIKit.h>
#import <GameController/GameController.h>
#import <CoreHaptics/CoreHaptics.h>
#include <cmath>

// All state and Core Haptics operations live on the main thread. We observe
// notifications; Unity retains ownership of every controller input handler.
@interface NativeMotor : NSObject
@property(nonatomic, strong) CHHapticEngine* engine;
@property(nonatomic, strong) id<CHHapticPatternPlayer> player;
@property(nonatomic) BOOL engineStarted;
@property(nonatomic) BOOL playing;
@property(nonatomic) float intensity;
- (BOOL)setSpeed:(float)speed;
- (void)stop;
@end

@implementation NativeMotor
- (BOOL)setSpeed:(float)speed
{
    if (speed == 0) { [self stop]; return YES; }
    if (self.playing && self.engineStarted && self.intensity == speed) return YES;
    NSError* error = nil;
    if (!self.engineStarted) {
        if (![self.engine startAndReturnError:&error]) return NO;
        self.engineStarted = YES;
    }
    if (!self.player) {
        CHHapticEventParameter* amplitude = [[CHHapticEventParameter alloc]
            initWithParameterID:CHHapticEventParameterIDHapticIntensity value:1];
        CHHapticEvent* event = [[CHHapticEvent alloc] initWithEventType:CHHapticEventTypeHapticContinuous
            parameters:@[amplitude] relativeTime:0 duration:GCHapticDurationInfinite];
        CHHapticPattern* pattern = [[CHHapticPattern alloc] initWithEvents:@[event] parameters:@[] error:&error];
        if (!pattern) return NO;
        self.player = [self.engine createPlayerWithPattern:pattern error:&error];
        if (!self.player) return NO;
    }
    CHHapticDynamicParameter* parameter = [[CHHapticDynamicParameter alloc]
        initWithParameterID:CHHapticDynamicParameterIDHapticIntensityControl value:speed relativeTime:0];
    if (![self.player sendParameters:@[parameter] atTime:CHHapticTimeImmediate error:&error]) return NO;
    if (!self.playing && ![self.player startAtTime:CHHapticTimeImmediate error:&error]) return NO;
    self.playing = YES;
    self.intensity = speed;
    return YES;
}
- (void)stop
{
    if (self.playing) [self.player stopAtTime:CHHapticTimeImmediate error:nil];
    self.playing = NO;
    self.intensity = 0;
}
@end

static NativeMotor* MakeMotor(GCDeviceHaptics* haptics, GCHapticsLocality locality)
{
    CHHapticEngine* engine = [haptics createEngineWithLocality:locality];
    if (!engine) return nil;
    NativeMotor* motor = [NativeMotor new];
    motor.engine = engine;
    engine.autoShutdownEnabled = YES;
    __weak NativeMotor* weakMotor = motor;
    engine.resetHandler = ^{
        dispatch_async(dispatch_get_main_queue(), ^{
            NativeMotor* live = weakMotor;
            // Core Haptics invalidates players on reset. Recreate on the next
            // game command, rather than replaying an obsolete impact forever.
            live.player = nil;
            live.engineStarted = NO;
            live.playing = NO;
        });
    };
    engine.stoppedHandler = ^(CHHapticEngineStoppedReason reason) {
        dispatch_async(dispatch_get_main_queue(), ^{
            NativeMotor* live = weakMotor;
            live.engineStarted = NO;
            live.playing = NO;
            live.player = nil;
        });
    };
    return motor;
}

@interface NativeRumble : NSObject
@property(nonatomic, strong) NSMutableDictionary<NSNumber*, NSString*>* products;
@property(nonatomic, strong) GCController* controller;
@property(nonatomic, strong) NativeMotor* lowMotor;
@property(nonatomic, strong) NativeMotor* highMotor;
@property(nonatomic) int deviceId;
@property(nonatomic) int output;
@property(nonatomic) NSTimeInterval retryAfter;
- (void)stop;
- (int)setDevice:(int)deviceId low:(float)low high:(float)high;
@end

@implementation NativeRumble
- (instancetype)init
{
    if ((self = [super init])) {
        self.products = [NSMutableDictionary new];
        NSNotificationCenter* center = NSNotificationCenter.defaultCenter;
        [center addObserver:self selector:@selector(background:) name:UIApplicationWillResignActiveNotification object:nil];
        [center addObserver:self selector:@selector(disconnected:) name:GCControllerDidDisconnectNotification object:nil];
        [center addObserver:self selector:@selector(currentChanged:) name:GCControllerDidBecomeCurrentNotification object:nil];
    }
    return self;
}
- (void)stop
{
    [self.lowMotor stop];
    [self.highMotor stop];
}
- (void)releaseController
{
    [self stop];
    self.lowMotor = nil;
    self.highMotor = nil;
    self.controller = nil;
    self.deviceId = 0;
    self.output = 0;
    self.retryAfter = 0;
}
- (void)background:(NSNotification*)note { [self stop]; }
- (void)disconnected:(NSNotification*)note
{
    dispatch_async(dispatch_get_main_queue(), ^{
        if (note.object == self.controller) [self releaseController];
    });
}
- (void)currentChanged:(NSNotification*)note
{
    dispatch_async(dispatch_get_main_queue(), ^{
        if (self.controller && note.object != self.controller) [self releaseController];
    });
}
- (GCController*)selectController:(NSString*)product
{
    NSArray<GCController*>* controllers = GCController.controllers;
    if (controllers.count == 1) return controllers.firstObject;
    // Unity device IDs are NOT GameController player indices. For multiple
    // pads use Apple's last-active controller only when its product matches,
    // or a unique product match. Never vibrate all connected controllers.
    GCController* current = GCController.current;
    if (current && [current.vendorName caseInsensitiveCompare:product] == NSOrderedSame) return current;
    GCController* match = nil;
    for (GCController* controller in controllers) {
        if ([controller.vendorName caseInsensitiveCompare:product] != NSOrderedSame) continue;
        if (match) return nil;
        match = controller;
    }
    return match;
}
- (int)setDevice:(int)deviceId low:(float)low high:(float)high
{
    if (low == 0 && high == 0) {
        if (deviceId == self.deviceId) [self stop];
        return 1;
    }
    if (UIApplication.sharedApplication.applicationState != UIApplicationStateActive) return 0;
    NSString* product = self.products[@(deviceId)];
    if (!product) return 0;
    if (self.deviceId != deviceId || !self.controller) {
        [self releaseController];
        self.controller = [self selectController:product];
        self.deviceId = deviceId;
        GCDeviceHaptics* haptics = self.controller.haptics;
        NSSet<GCHapticsLocality>* localities = haptics.supportedLocalities;
        if ([localities containsObject:GCHapticsLocalityLeftHandle] &&
            [localities containsObject:GCHapticsLocalityRightHandle]) {
            self.lowMotor = MakeMotor(haptics, GCHapticsLocalityLeftHandle);
            self.highMotor = MakeMotor(haptics, GCHapticsLocalityRightHandle);
            if (self.lowMotor && self.highMotor) self.output = 2;
        }
        if (!self.output && [localities containsObject:GCHapticsLocalityDefault]) {
            self.lowMotor = MakeMotor(haptics, GCHapticsLocalityDefault);
            self.highMotor = nil;
            if (self.lowMotor) self.output = 1;
        }
    }
    if (!self.output) return 0;
    if (self.retryAfter > NSDate.timeIntervalSinceReferenceDate) return -1;
    BOOL ok = [self.lowMotor setSpeed:self.output == 2 ? low : fmaxf(low, high)];
    if (self.output == 2) ok = [self.highMotor setSpeed:high] && ok;
    if (!ok) {
        [self stop];
        self.lowMotor.player = nil;
        self.highMotor.player = nil;
        self.lowMotor.engineStarted = NO;
        self.highMotor.engineStarted = NO;
        self.retryAfter = NSDate.timeIntervalSinceReferenceDate + 1;
        return -1;
    }
    return self.output;
}
@end

static NativeRumble* Rumble()
{
    static NativeRumble* instance;
    if (!instance) instance = [NativeRumble new];
    return instance;
}
static void OnMain(dispatch_block_t block)
{
    if (NSThread.isMainThread) block();
    else dispatch_async(dispatch_get_main_queue(), block);
}
extern "C" void NativeHaptics_Register(int deviceId, const char* product)
{
    NSString* name = product ? [NSString stringWithUTF8String:product] : @"";
    OnMain(^{ Rumble().products[@(deviceId)] = name ?: @""; });
}
extern "C" int NativeHaptics_Set(int deviceId, float low, float high)
{
    low = std::isfinite(low) ? fminf(1, fmaxf(0, low)) : 0;
    high = std::isfinite(high) ? fminf(1, fmaxf(0, high)) : 0;
    if (NSThread.isMainThread) return [Rumble() setDevice:deviceId low:low high:high];
    OnMain(^{ [Rumble() setDevice:deviceId low:low high:high]; });
    return 1;
}
extern "C" void NativeHaptics_Remove(int deviceId)
{
    OnMain(^{
        NativeRumble* rumble = Rumble();
        if (rumble.deviceId == deviceId) [rumble releaseController];
        [rumble.products removeObjectForKey:@(deviceId)];
    });
}
extern "C" void NativeHaptics_Stop() { OnMain(^{ [Rumble() stop]; }); }
