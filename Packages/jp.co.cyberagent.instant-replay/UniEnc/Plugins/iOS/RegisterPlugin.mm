#import "UnityAppController.h"
#import "PluginBase/LifeCycleListener.h"
#include "Unity/IUnityGraphics.h"

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API unienc_UnityPluginLoad(IUnityInterfaces* unityInterfaces);
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API unienc_UnityPluginUnload();

@interface UniEncLifeCycleListener : NSObject<LifeCycleListener>
@end

@implementation UniEncLifeCycleListener

+ (void)load {
    static UniEncLifeCycleListener* listener = nil;
    listener = [[self alloc] init];
    UnityRegisterLifeCycleListener(listener);
}

- (void)didFinishLaunching:(NSNotification*)notification {
    UnityRegisterRenderingPluginV5(&unienc_UnityPluginLoad,
                                   &unienc_UnityPluginUnload);
}

@end
