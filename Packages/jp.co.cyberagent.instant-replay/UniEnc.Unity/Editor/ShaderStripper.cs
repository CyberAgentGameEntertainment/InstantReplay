#if EXCLUDE_INSTANTREPLAY

using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

namespace UniEnc.Unity.Editor
{
    internal class ShaderStripper : IPreprocessShaders
    {
        private const string StripShaderName = "Hidden/InstantReplay/Rechannel";
        
        public int callbackOrder => 0;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (shader.name == StripShaderName)
            {
                data.Clear();
            }
        }
    }
}

#endif
