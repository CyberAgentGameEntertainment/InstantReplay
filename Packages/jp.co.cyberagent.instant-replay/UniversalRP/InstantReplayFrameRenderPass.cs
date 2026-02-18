using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if INSTANTREPLAY_URP_17_0_OR_NEWER
using System.Reflection;
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace InstantReplay.UniversalRP
{
    internal class InstantReplayFrameRenderPass : ScriptableRenderPass
    {
        public static event Action<Camera, IFrameProvider.Frame> OnFrameProvided;

#if INSTANTREPLAY_URP_17_0_OR_NEWER
        private static readonly FieldInfo WrappedCommandBufferField =
            typeof(BaseCommandBuffer).GetField("m_WrappedCommandBuffer",
                BindingFlags.NonPublic | BindingFlags.Instance);

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            using var builder =
                renderGraph.AddUnsafePass("InstantReplay frame render pass", out PassData passData);
            var resources = frameData.Get<UniversalResourceData>();
            var camera = frameData.Get<UniversalCameraData>();

            var source = resources.cameraColor;
            passData.CameraData = camera;
            passData.Source = source;
            builder.AllowPassCulling(false);
            builder.UseTexture(source);
            builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
            {
                var commandBuffer = WrappedCommandBufferField.GetValue(context.cmd) as CommandBuffer;
                var flipped = data.CameraData.IsHandleYFlipped(data.Source);
                OnFrameProvided?.Invoke(data.CameraData.camera,
                    new IFrameProvider.Frame(data.Source, Time.unscaledTimeAsDouble,
                        SystemInfo.graphicsUVStartsAtTop ^ flipped, commandBuffer));
            });
        }

        private class PassData
        {
            public TextureHandle Source;
            public UniversalCameraData CameraData;
        }
#endif

#if INSTANTREPLAY_URP_17_0_OR_NEWER
        [Obsolete]
#endif
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var commandBuffer = CommandBufferPool.Get();
            var target = renderingData.cameraData.renderer.cameraColorTargetHandle;
            var flipped = renderingData.cameraData.IsHandleYFlipped(target);
            OnFrameProvided?.Invoke(renderingData.cameraData.camera,
                new IFrameProvider.Frame(target, Time.unscaledTimeAsDouble,
                    SystemInfo.graphicsUVStartsAtTop ^ flipped, commandBuffer));
            context.ExecuteCommandBuffer(commandBuffer);
        }
    }
}
