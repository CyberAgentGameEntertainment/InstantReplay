using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace InstantReplay.UniversalRP
{
    internal class InstantReplayFrameRenderPass : ScriptableRenderPass
    {
        public static event Action<Camera, IFrameProvider.Frame> OnFrameProvided;

        private static readonly FieldInfo WrappedCommandBufferField =
            typeof(BaseCommandBuffer).GetField("m_WrappedCommandBuffer",
                BindingFlags.NonPublic | BindingFlags.Instance);

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            using var builder =
                renderGraph.AddUnsafePass("InstantReplay frame render pass", out PassData passData);
            var resources = frameData.Get<UniversalResourceData>();
            var camera = frameData.Get<UniversalCameraData>();

            var source = resources.activeColorTexture;
            passData.Camera = camera.camera;
            passData.Source = source;
            builder.AllowPassCulling(false);
            builder.UseTexture(source);
            builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
            {
                var commandBuffer = WrappedCommandBufferField.GetValue(context.cmd) as CommandBuffer;
                OnFrameProvided?.Invoke(data.Camera,
                    new IFrameProvider.Frame(data.Source, Time.unscaledTimeAsDouble,
                        SystemInfo.graphicsUVStartsAtTop, commandBuffer));
            });
        }

        private class PassData
        {
            public Camera Camera;
            public TextureHandle Source;
        }
    }
}
