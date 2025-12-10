#import "UnityAppController.h"
#include "Unity/IUnityGraphics.h"

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API unienc_UnityPluginLoad(IUnityInterfaces* unityInterfaces);
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API unienc_UnityPluginUnload();

@interface MyAppController : UnityAppController
{
}
- (void)shouldAttachRenderDelegate;
@end
@implementation MyAppController
- (void)shouldAttachRenderDelegate
{
    UnityRegisterRenderingPluginV5(&unienc_UnityPluginLoad, &unienc_UnityPluginUnload);
}

@end
IMPL_APP_CONTROLLER_SUBCLASS(MyAppController);
