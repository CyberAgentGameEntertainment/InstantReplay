using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

namespace UniEnc.Unity.Editor
{
    public class ShaderStripper : IPreprocessShaders
    {
        private const string StripShaderName = "Hidden/InstantReplay/Rechannel";
        
        public int callbackOrder { get; }

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            if (shader.name == StripShaderName)
            {
                data.Clear();
            }
        }
    }
}
