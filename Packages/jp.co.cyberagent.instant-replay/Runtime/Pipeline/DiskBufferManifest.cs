// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using UniEnc;
using UnityEngine;

namespace InstantReplay
{
    /// <summary>
    ///     Manifest data stored alongside disk buffer files for crash recovery.
    /// </summary>
    [Serializable]
    internal class DiskBufferManifest
    {
        [SerializeField] private int version;
        [SerializeField] private VideoOptionsData videoOptions;
        [SerializeField] private AudioOptionsData audioOptions;

        public int Version
        {
            get => version;
            set => version = value;
        }

        public VideoOptionsData VideoOptions
        {
            get => videoOptions;
            set => videoOptions = value;
        }

        public AudioOptionsData AudioOptions
        {
            get => audioOptions;
            set => audioOptions = value;
        }

        [Serializable]
        internal class VideoOptionsData
        {
            [SerializeField] private uint width;
            [SerializeField] private uint height;
            [SerializeField] private uint fpsHint;
            [SerializeField] private uint bitrate;

            public uint Width { get => width; set => width = value; }
            public uint Height { get => height; set => height = value; }
            public uint FpsHint { get => fpsHint; set => fpsHint = value; }
            public uint Bitrate { get => bitrate; set => bitrate = value; }

            public VideoEncoderOptions ToEncoderOptions()
            {
                return new VideoEncoderOptions
                {
                    Width = width,
                    Height = height,
                    FpsHint = fpsHint,
                    Bitrate = bitrate
                };
            }
        }

        [Serializable]
        internal class AudioOptionsData
        {
            [SerializeField] private uint sampleRate;
            [SerializeField] private uint channels;
            [SerializeField] private uint bitrate;

            public uint SampleRate { get => sampleRate; set => sampleRate = value; }
            public uint Channels { get => channels; set => channels = value; }
            public uint Bitrate { get => bitrate; set => bitrate = value; }

            public AudioEncoderOptions ToEncoderOptions()
            {
                return new AudioEncoderOptions
                {
                    SampleRate = sampleRate,
                    Channels = channels,
                    Bitrate = bitrate
                };
            }
        }

        public static string ToJson(DiskBufferManifest manifest)
        {
            return JsonUtility.ToJson(manifest, true);
        }

        public static DiskBufferManifest FromJson(string json)
        {
            return JsonUtility.FromJson<DiskBufferManifest>(json);
        }
    }
}
