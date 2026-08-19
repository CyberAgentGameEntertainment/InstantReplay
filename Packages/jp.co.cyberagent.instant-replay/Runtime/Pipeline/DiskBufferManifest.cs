// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UniEnc;

namespace InstantReplay
{
    /// <summary>
    ///     Contents of the manifest written alongside the buffer files. It records everything a later process needs in
    ///     order to build a muxer that accepts the payloads persisted next to it.
    /// </summary>
    /// <remarks>
    ///     The manifest is serialized by hand rather than with UnityEngine.JsonUtility, so that the storage layer can be
    ///     exercised outside the Unity Editor. The document is a flat object of strings and numbers.
    /// </remarks>
    internal sealed class DiskBufferManifest
    {
        public int FormatVersion { get; set; }
        public string StartedAtUtc { get; set; }
        public string Platform { get; set; }
        public string UnityVersion { get; set; }
        public string ApplicationVersion { get; set; }

        public uint VideoWidth { get; set; }
        public uint VideoHeight { get; set; }
        public uint VideoFpsHint { get; set; }
        public uint VideoBitrate { get; set; }

        public uint AudioSampleRate { get; set; }
        public uint AudioChannels { get; set; }
        public uint AudioBitrate { get; set; }

        public static DiskBufferManifest Create(in VideoEncoderOptions video, in AudioEncoderOptions audio,
            string platform, string unityVersion, string applicationVersion)
        {
            return new DiskBufferManifest
            {
                FormatVersion = DiskBufferFormat.FormatVersion,
                StartedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Platform = platform ?? string.Empty,
                UnityVersion = unityVersion ?? string.Empty,
                ApplicationVersion = applicationVersion ?? string.Empty,
                VideoWidth = video.Width,
                VideoHeight = video.Height,
                VideoFpsHint = video.FpsHint,
                VideoBitrate = video.Bitrate,
                AudioSampleRate = audio.SampleRate,
                AudioChannels = audio.Channels,
                AudioBitrate = audio.Bitrate
            };
        }

        public VideoEncoderOptions ToVideoOptions()
        {
            return new VideoEncoderOptions
            {
                Width = VideoWidth,
                Height = VideoHeight,
                FpsHint = VideoFpsHint,
                Bitrate = VideoBitrate
            };
        }

        public AudioEncoderOptions ToAudioOptions()
        {
            return new AudioEncoderOptions
            {
                SampleRate = AudioSampleRate,
                Channels = AudioChannels,
                Bitrate = AudioBitrate
            };
        }

        public DateTime GetStartedAtUtc()
        {
            // The round-trip format carries the offset itself, so RoundtripKind must not be combined with
            // AdjustToUniversal; doing so throws rather than parsing.
            return DateTime.TryParse(StartedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out var value)
                ? value.ToUniversalTime()
                : default;
        }

        /// <summary>
        ///     Whether this manifest can be read back by the running build. Matching the format version and the platform is
        ///     necessary but not sufficient: the payloads are serialized by the native library, whose schema may change
        ///     between package versions without any signal observable here. A mismatch of that kind surfaces as a decode
        ///     failure from the muxer, which is why the build identifiers are recorded.
        /// </summary>
        public bool IsCompatibleWith(string currentPlatform)
        {
            return FormatVersion == DiskBufferFormat.FormatVersion &&
                   string.Equals(Platform, currentPlatform, StringComparison.Ordinal);
        }

        public string ToJson()
        {
            var builder = new StringBuilder(512);
            builder.Append("{\n");
            AppendNumber(builder, "formatVersion", FormatVersion.ToString(CultureInfo.InvariantCulture), true);
            AppendString(builder, "startedAtUtc", StartedAtUtc, true);
            AppendString(builder, "platform", Platform, true);
            AppendString(builder, "unityVersion", UnityVersion, true);
            AppendString(builder, "applicationVersion", ApplicationVersion, true);
            AppendNumber(builder, "videoWidth", VideoWidth.ToString(CultureInfo.InvariantCulture), true);
            AppendNumber(builder, "videoHeight", VideoHeight.ToString(CultureInfo.InvariantCulture), true);
            AppendNumber(builder, "videoFpsHint", VideoFpsHint.ToString(CultureInfo.InvariantCulture), true);
            AppendNumber(builder, "videoBitrate", VideoBitrate.ToString(CultureInfo.InvariantCulture), true);
            AppendNumber(builder, "audioSampleRate", AudioSampleRate.ToString(CultureInfo.InvariantCulture), true);
            AppendNumber(builder, "audioChannels", AudioChannels.ToString(CultureInfo.InvariantCulture), true);
            AppendNumber(builder, "audioBitrate", AudioBitrate.ToString(CultureInfo.InvariantCulture), false);
            builder.Append("}\n");
            return builder.ToString();
        }

        public static bool TryParse(string json, out DiskBufferManifest manifest)
        {
            manifest = null;
            if (!TryParseFlatObject(json, out var values)) return false;

            var parsed = new DiskBufferManifest
            {
                StartedAtUtc = GetString(values, "startedAtUtc"),
                Platform = GetString(values, "platform"),
                UnityVersion = GetString(values, "unityVersion"),
                ApplicationVersion = GetString(values, "applicationVersion")
            };

            if (!TryGetInt(values, "formatVersion", out var formatVersion)) return false;
            parsed.FormatVersion = formatVersion;

            if (!TryGetUInt(values, "videoWidth", out var videoWidth)) return false;
            if (!TryGetUInt(values, "videoHeight", out var videoHeight)) return false;
            if (!TryGetUInt(values, "videoFpsHint", out var videoFpsHint)) return false;
            if (!TryGetUInt(values, "videoBitrate", out var videoBitrate)) return false;
            if (!TryGetUInt(values, "audioSampleRate", out var audioSampleRate)) return false;
            if (!TryGetUInt(values, "audioChannels", out var audioChannels)) return false;
            if (!TryGetUInt(values, "audioBitrate", out var audioBitrate)) return false;

            parsed.VideoWidth = videoWidth;
            parsed.VideoHeight = videoHeight;
            parsed.VideoFpsHint = videoFpsHint;
            parsed.VideoBitrate = videoBitrate;
            parsed.AudioSampleRate = audioSampleRate;
            parsed.AudioChannels = audioChannels;
            parsed.AudioBitrate = audioBitrate;

            manifest = parsed;
            return true;
        }

        /// <summary>
        ///     Writes the manifest and flushes it to the storage device. The buffer cannot be recovered without it, so it is
        ///     never left in the operating system's cache. Returns the number of bytes written.
        /// </summary>
        public long Write(string path)
        {
            var bytes = new UTF8Encoding(false).GetBytes(ToJson());

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            return bytes.Length;
        }

        public static bool TryRead(string path, out DiskBufferManifest manifest, Action<Exception> onError = null)
        {
            manifest = null;

            try
            {
                if (!File.Exists(path)) return false;
                return TryParse(File.ReadAllText(path, new UTF8Encoding(false)), out manifest);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
                return false;
            }
        }

        private static string GetString(IReadOnlyDictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out var value) ? value : string.Empty;
        }

        private static bool TryGetInt(IReadOnlyDictionary<string, string> values, string key, out int result)
        {
            result = 0;
            return values.TryGetValue(key, out var value) &&
                   int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryGetUInt(IReadOnlyDictionary<string, string> values, string key, out uint result)
        {
            result = 0;
            return values.TryGetValue(key, out var value) &&
                   uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static void AppendString(StringBuilder builder, string key, string value, bool comma)
        {
            builder.Append("  \"").Append(key).Append("\": \"").Append(Escape(value ?? string.Empty)).Append('"');
            builder.Append(comma ? ",\n" : "\n");
        }

        private static void AppendNumber(StringBuilder builder, string key, string value, bool comma)
        {
            builder.Append("  \"").Append(key).Append("\": ").Append(value);
            builder.Append(comma ? ",\n" : "\n");
        }

        private static string Escape(string value)
        {
            var builder = new StringBuilder(value.Length + 8);
            foreach (var c in value)
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(c);
                        break;
                }

            return builder.ToString();
        }

        /// <summary>
        ///     Parses a flat JSON object of string and number values into key-value pairs. Nested objects and arrays are
        ///     not supported, and their presence makes the parse fail rather than produce a partial result.
        /// </summary>
        private static bool TryParseFlatObject(string json, out Dictionary<string, string> values)
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(json)) return false;

            var i = 0;
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '{') return false;
            i++;

            while (true)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length) return false;

                if (json[i] == '}') return true;

                if (json[i] == ',')
                {
                    i++;
                    continue;
                }

                if (json[i] != '"') return false;
                if (!TryReadString(json, ref i, out var key)) return false;

                SkipWhitespace(json, ref i);
                if (i >= json.Length || json[i] != ':') return false;
                i++;
                SkipWhitespace(json, ref i);
                if (i >= json.Length) return false;

                string value;
                if (json[i] == '"')
                {
                    if (!TryReadString(json, ref i, out value)) return false;
                }
                else
                {
                    var start = i;
                    while (i < json.Length && json[i] != ',' && json[i] != '}' && !char.IsWhiteSpace(json[i])) i++;
                    if (i == start) return false;
                    value = json.Substring(start, i - start);
                    if (value.IndexOf('{') >= 0 || value.IndexOf('[') >= 0) return false;
                }

                values[key] = value;
            }
        }

        private static void SkipWhitespace(string json, ref int i)
        {
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        }

        private static bool TryReadString(string json, ref int i, out string result)
        {
            result = null;
            if (i >= json.Length || json[i] != '"') return false;
            i++;

            var builder = new StringBuilder();
            while (i < json.Length)
            {
                var c = json[i++];

                if (c == '"')
                {
                    result = builder.ToString();
                    return true;
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (i >= json.Length) return false;
                var escape = json[i++];
                switch (escape)
                {
                    case '"':
                        builder.Append('"');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case '/':
                        builder.Append('/');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (i + 4 > json.Length) return false;
                        if (!ushort.TryParse(json.Substring(i, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out var code)) return false;
                        builder.Append((char)code);
                        i += 4;
                        break;
                    default:
                        return false;
                }
            }

            return false;
        }
    }
}
