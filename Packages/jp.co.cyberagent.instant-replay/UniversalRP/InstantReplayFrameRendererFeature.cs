using UnityEngine.Rendering.Universal;

namespace InstantReplay.UniversalRP
{
    public class InstantReplayFrameRendererFeature : ScriptableRendererFeature
    {
        private InstantReplayFrameRenderPass _renderPass;

        public override void Create()
        {
            _renderPass = new InstantReplayFrameRenderPass
            {
                renderPassEvent = RenderPassEvent.AfterRendering
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(_renderPass);
        }
    }
}
