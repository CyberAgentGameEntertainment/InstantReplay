// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using android.graphics;
using android.media;
using java.nio;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace InstantReplay
{
    [BurstCompile]
    internal class AndroidMediaCodecTranscoder : ITranscoder
    {
        private const int MaxConcurrentPreparedFrames = 4;

        private readonly int _audioChannels;
        private readonly MediaCodec _audioCodec;
        private readonly int _audioSampleRate;

        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<bool> _completionSource = new();
        private readonly ChannelWriter<ValueTask<Frame>> _framesChannelWriter;
        private readonly MediaMuxer _muxer;
        private readonly AudioPcmEncoding _pcmEncoding;
        private readonly MediaCodec _videoCodec;
        private readonly Task<(Yuv420Layout layout, int initialBufferId)> _yuvLayoutTask;
        private int _audioTrackIndex;
        private int _audioWroteSamples;
        private int _videoTrackIndex;

        public AndroidMediaCodecTranscoder(int outputWidth, int outputHeight, int channels, int sampleRate,
            string outputFilename)
        {
            using var scope = JniScope.Create();

            const string videoMime = "video/avc";
            const string audioMime = "audio/mp4a-latm";

            MediaCodecInfo[] availableCodecs = null;

            // video codec must support YUV420Flexible. (assumes NV12)
            using var videoCodecInfo = FindCodec(videoMime,
                MediaCodecInfo.CodecCapabilities.get_COLOR_FormatYUV420Flexible(), ref availableCodecs);
            using var audioCodecInfo = FindCodec(audioMime, null, ref availableCodecs);

            var videoCodec = _videoCodec = MediaCodec.createByCodecName(videoCodecInfo!.getName())!;

            // NOTE: Encoders on some devices don't support non-16-aligned size.
            // Green padding may appear on the right and bottom of the frame.
            var extendedWidth = (outputWidth + 15) / 16 * 16;
            var extendedHeight = (outputHeight + 15) / 16 * 16;

            using var videoFormat = MediaFormat.createVideoFormat(videoMime, extendedWidth, extendedHeight)!;
            videoFormat.setInteger(MediaFormat.get_KEY_COLOR_FORMAT(),
                MediaCodecInfo.CodecCapabilities.get_COLOR_FormatYUV420Flexible());
            videoFormat.setInteger(MediaFormat.get_KEY_BIT_RATE(),
                outputWidth * outputHeight * 4); // TODO: make configurable
            videoFormat.setInteger(MediaFormat.get_KEY_FRAME_RATE(), 30);
            videoFormat.setInteger(MediaFormat.get_KEY_I_FRAME_INTERVAL(), 1);
            videoFormat.setInteger(MediaFormat.get_KEY_WIDTH(), extendedWidth);
            videoFormat.setInteger(MediaFormat.get_KEY_HEIGHT(), extendedHeight);

            videoCodec.configure(videoFormat, null!, null!, MediaCodec.get_CONFIGURE_FLAG_ENCODE());
            videoCodec.start();

            // we need to know actual device-specific YUV420 plane layout
            _yuvLayoutTask = DetermineYuvLayoutAsync().Inner.AsTask();

            _audioSampleRate = sampleRate;
            _audioChannels = channels;

            var audioCodec = _audioCodec = MediaCodec.createByCodecName(audioCodecInfo!.getName())!;

            using var audioFormat = MediaFormat.createAudioFormat(audioMime, sampleRate, channels)!;
            audioFormat.setInteger(MediaFormat.get_KEY_BIT_RATE(), 384 * 1024); // TODO: make configurable
            audioFormat.setInteger(MediaFormat.get_KEY_AAC_PROFILE(),
                MediaCodecInfo.CodecProfileLevel.get_AACObjectLC());

            audioCodec.configure(audioFormat, null!, null!, MediaCodec.get_CONFIGURE_FLAG_ENCODE());
            audioCodec.start();

            using var audioInputFormat = audioCodec.getInputFormat()!;
            var pcmEncodingNum = audioInputFormat.getInteger(MediaFormat.get_KEY_PCM_ENCODING(),
                AudioFormat.get_ENCODING_PCM_16BIT());

            AudioPcmEncoding pcmEncoding;
            if (pcmEncodingNum == AudioFormat.get_ENCODING_PCM_8BIT())
                pcmEncoding = AudioPcmEncoding.Int8;
            else if (pcmEncodingNum == AudioFormat.get_ENCODING_PCM_16BIT())
                pcmEncoding = AudioPcmEncoding.Int16;
            else if (pcmEncodingNum == AudioFormat.get_ENCODING_PCM_24BIT_PACKED())
                pcmEncoding = AudioPcmEncoding.Int24Packed;
            else if (pcmEncodingNum == AudioFormat.get_ENCODING_PCM_32BIT())
                pcmEncoding = AudioPcmEncoding.Int32;
            else if (pcmEncodingNum == AudioFormat.get_ENCODING_PCM_FLOAT())
                pcmEncoding = AudioPcmEncoding.Float32;
            else
                throw new NotSupportedException(
                    $"Unsupported PCM format for encoder input (KEY_PCM_ENCODING): {pcmEncodingNum}");

            _pcmEncoding = pcmEncoding;

            _muxer = MediaMuxer.New(outputFilename, MediaMuxer.OutputFormat.get_MUXER_OUTPUT_MPEG_4());

            /* Pipeline Overview
             *
             * - When PushFrameAsync() is called:
             *   - LoadFrameAsync() is initiated but not awaited and the task is enqueued to the channel.
             *   - Backpressure is applied by the bounded channel.
             *   - LoadFrameAsync() processes the frame on a background thread and enqueued tasks are run concurrently.
             * - When LoadFrameAsync() completes:
             *   - The result is dequeued by EncodeAsync() loop enqueued to the MediaCodec input buffer.
             * - When the frame is encoded:
             *   - The output frame is dequeued by MuxAsync() loop and written to the MediaMuxer.
             *   - MediaMuxer directly writes to the file.
             *
             */

            var channel = Channel.CreateBounded<ValueTask<Frame>>(new BoundedChannelOptions(MaxConcurrentPreparedFrames)
            {
                AllowSynchronousContinuations = true,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

            _framesChannelWriter = channel.Writer;

            Task.Run(async () =>
            {
                try
                {
                    await EncodeAsync(channel.Reader, _cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            });

            Task.Run(async () =>
            {
                try
                {
                    await MuxAsync(_cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            try
            {
                await _completionSource.Task;
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
        }

        public async ValueTask PushFrameAsync(string path, double timestamp, CancellationToken ct = default)
        {
            ct = CancellationTokenSource.CreateLinkedTokenSource(ct, _cancellation.Token).Token;

            await _framesChannelWriter.WaitToWriteAsync(ct);
            await _framesChannelWriter.WriteAsync(LoadFrameAsync(path, timestamp), ct);
        }

        public ValueTask PushAudioSamplesAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            return PushAudioSamplesAsyncCore(buffer, ct).Inner;
        }

        public async ValueTask CompleteAsync()
        {
            _framesChannelWriter.Complete();
            await _completionSource.Task;
        }

        private void ConvertAudioSamples(ReadOnlyMemory<byte> buffer, out ArraySegment<sbyte> segment,
            out bool needReturn)
        {
            // input is signed 16-bit PCM

            var bufferShort = MemoryMarshal.Cast<byte, short>(buffer.Span); // NOTE: is this alignment safe?
            switch (_pcmEncoding)
            {
                case AudioPcmEncoding.Int8:
                {
                    var array = ArrayPool<sbyte>.Shared.Rent(bufferShort.Length);
                    segment = new ArraySegment<sbyte>(array, 0, bufferShort.Length);
                    needReturn = true;
                    for (var i = 0; i < bufferShort.Length; i++)
                        array[i] = unchecked((sbyte)(byte)((ushort)(bufferShort[i] - short.MinValue) /
                            (double)ushort.MaxValue * byte.MaxValue));

                    break;
                }
                case AudioPcmEncoding.Int16:
                {
                    // no conversion needed
                    if (!MemoryMarshal.TryGetArray(buffer, out var byteSegment))
                        throw new NotSupportedException();

                    segment = new ArraySegment<sbyte>((sbyte[])(object)byteSegment.Array!, byteSegment.Offset,
                        byteSegment.Count);
                    needReturn = false;

                    break;
                }
                case AudioPcmEncoding.Int24Packed:
                {
                    var array = ArrayPool<sbyte>.Shared.Rent(bufferShort.Length * 3);
                    segment = new ArraySegment<sbyte>(array, 0, bufferShort.Length * 3);
                    needReturn = true;
                    for (var i = 0; i < bufferShort.Length; i++)
                    {
                        var destIndex = i * 3;
                        var scaled = unchecked((int)(bufferShort[i] / -(double)short.MinValue * 8388608.0));
                        if (BitConverter.IsLittleEndian)
                        {
                            array[destIndex] = (sbyte)(scaled & 0xFF);
                            array[destIndex + 1] = (sbyte)((scaled >> 8) & 0xFF);
                            array[destIndex + 2] = (sbyte)((scaled >> 16) & 0xFF);
                        }
                        else
                        {
                            array[destIndex] = (sbyte)((scaled >> 16) & 0xFF);
                            array[destIndex + 1] = (sbyte)((scaled >> 8) & 0xFF);
                            array[destIndex + 2] = (sbyte)(scaled & 0xFF);
                        }
                    }

                    break;
                }
                case AudioPcmEncoding.Int32:
                {
                    var array = ArrayPool<sbyte>.Shared.Rent(bufferShort.Length * sizeof(int));
                    var arrayView = MemoryMarshal.Cast<sbyte, int>(array.AsSpan());
                    segment = new ArraySegment<sbyte>(array, 0, bufferShort.Length * sizeof(int));
                    needReturn = true;
                    for (var i = 0; i < bufferShort.Length; i++)
                        arrayView[i] =
                            unchecked((int)(bufferShort[i] / -(double)short.MinValue * -(double)int.MinValue));

                    break;
                }
                case AudioPcmEncoding.Float32:
                {
                    var array = ArrayPool<sbyte>.Shared.Rent(bufferShort.Length * sizeof(float));
                    var arrayView = MemoryMarshal.Cast<sbyte, float>(array.AsSpan());
                    segment = new ArraySegment<sbyte>(array, 0, bufferShort.Length * sizeof(float));
                    needReturn = true;
                    for (var i = 0; i < bufferShort.Length; i++)
                        arrayView[i] = (float)(bufferShort[i] / -(double)short.MinValue);

                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        ///     Determines the actual YUV420 layout of the device.
        /// </summary>
        /// <returns>YUV420 layout and id of the first input buffer we need to use</returns>
        private async JniTask<(Yuv420Layout layout, int initialBufferId)> DetermineYuvLayoutAsync()
        {
            while (true)
            {
                lock (_videoCodec)
                {
                    var inputBufferId = _videoCodec.dequeueInputBuffer(0);
                    if (inputBufferId >= 0)
                    {
                        using var inputImage = _videoCodec.getInputImage(inputBufferId);
                        var layout = new Yuv420Layout(inputImage);
                        return (layout, inputBufferId);
                    }
                }

                await Task.Yield();
            }
        }

        private async JniTask PushAudioSamplesAsyncCore(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ConvertAudioSamples(buffer, out var segment, out var needReturn);
            try
            {
                while (segment.Count > 0)
                {
                    AndroidJNI.PushLocalFrame(16);
                    try
                    {
                        lock (_audioCodec)
                        {
                            var inputBufferId = _audioCodec.dequeueInputBuffer(0);
                            if (inputBufferId >= 0)
                            {
                                using var inputBuffer = _audioCodec.getInputBuffer(inputBufferId)!;

                                inputBuffer.clear();
                                var length = Mathf.Min(segment.Count, inputBuffer.limit());
                                inputBuffer.putWithoutReadback(segment.Array!, segment.Offset, length);
                                _audioCodec.queueInputBuffer(inputBufferId, 0, length,
                                    (long)Math.Round(_audioWroteSamples * 1000000.0 / _audioSampleRate),
                                    0);

                                segment = segment[length..];
                                _audioWroteSamples += length / _audioChannels / sizeof(short);

                                continue;
                            }
                        }
                    }
                    finally
                    {
                        AndroidJNI.PopLocalFrame((nint)0);
                    }

                    await Task.Delay(1, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                if (needReturn)
                    ArrayPool<sbyte>.Shared.Return(segment.Array);
            }
        }

        /// <summary>
        ///     Find a codec that supports the specified MIME type.
        ///     It prefers hardware accelerated codecs and the specified color format.
        /// </summary>
        /// <param name="mime"></param>
        /// <param name="preferredColorFormat"></param>
        /// <param name="availableCodecs"></param>
        /// <returns></returns>
        private static MediaCodecInfo FindCodec(string mime, int? preferredColorFormat,
            ref MediaCodecInfo[] availableCodecs)
        {
            availableCodecs ??= MediaCodecList.New(MediaCodecList.get_REGULAR_CODECS()).getCodecInfos()!;
            MediaCodecInfo matchedCodecInfo = null;
            foreach (var codecInfo in availableCodecs)
            {
                if (!codecInfo!.isEncoder()) continue;
                foreach (var type in codecInfo.getSupportedTypes()!)
                    if (type!.Equals(mime, StringComparison.OrdinalIgnoreCase))
                        goto TYPE_MATCHED;

                continue;

                TYPE_MATCHED:

                if (preferredColorFormat is { } preferredColorFormatValue)
                {
                    var caps = codecInfo.getCapabilitiesForType(mime);
                    foreach (var colorFormat in caps!.get_colorFormats()!)
                        if (colorFormat == preferredColorFormatValue)
                            goto FORMAT_MATCHED;

                    continue;

                    FORMAT_MATCHED: ;
                }

                // Use the first hardware accelerated codec if available.
                if (codecInfo.isHardwareAccelerated())
                {
                    matchedCodecInfo = codecInfo;
                    break;
                }

                matchedCodecInfo ??= codecInfo;
            }

            return matchedCodecInfo;
        }

        [BurstCompile]
        private static unsafe void Argb32ToYuv420Burst([NoAlias] byte* argb, nint argbLength, [NoAlias] byte* yuv420,
            int width, in Yuv420Layout layout)
        {
            Argb32ToYuv420Core(argb, argbLength, yuv420, width, layout);
        }

        private static unsafe void Argb32ToYuv420Core([NoAlias] byte* argb, nint argbLength, [NoAlias] byte* yuv420,
            int width, in Yuv420Layout layout)
        {
            if ((argbLength & 0b11) != 0) throw new InvalidOperationException();
            var argbPixels = (uint*)argb;
            var argbPixelLength = argbLength >> 2;

            var planeY = yuv420 + layout._y._offset;
            var planeU = yuv420 + layout._u._offset;
            var planeV = yuv420 + layout._v._offset;

            for (var i = 0; i < argbPixelLength; i++)
            {
                var pX = i % width;
                var pY = i / width;

                var argbPixel = argbPixels[i];
                var r = (byte)((argbPixel >> 16) & 0xFF);
                var g = (byte)((argbPixel >> 8) & 0xFF);
                var b = (byte)((argbPixel >> 0) & 0xFF);

                var y = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                var u = (byte)(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                var v = (byte)(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);

                planeY[pY * layout._y._rowStride + pX * layout._y._pixelStride] = y;
                if ((pX & 1) == 0 && (pY & 1) == 0)
                {
                    planeU[(pY >> 1) * layout._u._rowStride + (pX >> 1) * layout._u._pixelStride] = u;
                    planeV[(pY >> 1) * layout._v._rowStride + (pX >> 1) * layout._v._pixelStride] = v;
                }
            }
        }

        private async ValueTask<Frame> LoadFrameAsync(string path, double timestamp)
        {
            var (layout, _) = await _yuvLayoutTask;

            return await Task.Run(() =>
            {
                using var scope = JniScope.Create();

                int bitmapLength;
                int width;
                NativeArray<sbyte> argb;
                using (var bitmap = BitmapFactory.decodeFile(path)!)
                {
                    bitmapLength = bitmap.getByteCount();
                    width = bitmap.getWidth();

                    // to reduce memory allocation and copy, we use direct ByteBuffer backed by NativeArray
                    argb = new NativeArray<sbyte>(bitmapLength, Allocator.Persistent);
                    try
                    {
                        var tempByteBufferPtr = AndroidJNI.NewDirectByteBuffer(argb);
                        using var tempByteBuffer =
                            ByteBuffer.UnsafeFromRawObjectAndDeleteLocalRef(tempByteBufferPtr);

                        bitmap.copyPixelsToBuffer(tempByteBuffer);
                    }
                    catch
                    {
                        argb.Dispose();
                        throw;
                    }
                }

                using var _ = argb;

                var yuv420Array = ArrayPool<sbyte>.Shared.Rent((int)layout._length);
                Array.Clear(yuv420Array, 0, yuv420Array.Length);
                try
                {
                    unsafe
                    {
                        fixed (sbyte* yuv420Ptr = yuv420Array)
                        {
                            Argb32ToYuv420Burst((byte*)argb.GetUnsafePtr(), bitmapLength,
                                (byte*)yuv420Ptr, width, layout);
                        }
                    }

                    return new Frame(yuv420Array, 0, (int)layout._length, timestamp);
                }
                catch
                {
                    ArrayPool<sbyte>.Shared.Return(yuv420Array);
                    throw;
                }
            });
        }

        private async JniTask EncodeAsync(ChannelReader<ValueTask<Frame>> frames, CancellationToken ct)
        {
            double lastTimeStamp = 0;

            // wait for layout
            var (_, initialBufferId) = await _yuvLayoutTask;

            var isInitial = true;
            await foreach (var frameTask in frames.ReadAllAsync(ct))
            {
                using var frame = await frameTask;

                while (true)
                {
                    AndroidJNI.PushLocalFrame(16);
                    try
                    {
                        lock (_videoCodec)
                        {
                            var inputBufferId = isInitial ? initialBufferId : _videoCodec.dequeueInputBuffer(0);
                            isInitial = false;
                            if (inputBufferId >= 0)
                            {
                                using var inputBuffer = _videoCodec.getInputBuffer(inputBufferId)!;
                                inputBuffer.clear();
                                inputBuffer.putWithoutReadback(frame.ImageArray, frame.ImageOffset,
                                    Mathf.Min(frame.ImageLength, inputBuffer.limit()));
                                _videoCodec.queueInputBuffer(inputBufferId, 0, frame.ImageLength,
                                    (long)Math.Round(frame.Timestamp * 1000000),
                                    0);
                                lastTimeStamp = frame.Timestamp;
                                break;
                            }
                        }
                    }
                    finally
                    {
                        AndroidJNI.PopLocalFrame((nint)0);
                    }

                    await Task.Delay(1, ct).ConfigureAwait(false);
                }
            }

            // complete
            while (true)
            {
                lock (_videoCodec)
                {
                    var inputBufferId = _videoCodec.dequeueInputBuffer(0);
                    if (inputBufferId >= 0)
                    {
                        _videoCodec.queueInputBuffer(inputBufferId, 0, 0, (long)Math.Round(lastTimeStamp * 1000000),
                            MediaCodec.get_BUFFER_FLAG_END_OF_STREAM());
                        break;
                    }
                }

                await Task.Delay(1, ct).ConfigureAwait(false);
            }

            while (true)
            {
                lock (_audioCodec)
                {
                    var inputBufferId = _audioCodec.dequeueInputBuffer(0);
                    if (inputBufferId >= 0)
                    {
                        _audioCodec.queueInputBuffer(inputBufferId, 0, 0,
                            (long)Math.Round(_audioWroteSamples * 1000000.0 / _audioSampleRate),
                            MediaCodec.get_BUFFER_FLAG_END_OF_STREAM());
                        break;
                    }
                }

                await Task.Delay(1, ct).ConfigureAwait(false);
            }
        }

        private async JniTask MuxAsync(CancellationToken ct)
        {
            try
            {
                using var bufferInfo = MediaCodec.BufferInfo.New();
                var isVideoStart = false;
                var isAudioStart = false;
                var isVideoEnd = false;
                var isAudioEnd = false;
                while (!isVideoEnd || !isAudioEnd)
                {
                    if (!isVideoEnd && (!isVideoStart || isAudioStart))
                        lock (_videoCodec)
                        {
                            var outputBufferId = _videoCodec.dequeueOutputBuffer(bufferInfo, 0);

                            if (outputBufferId == MediaCodec.get_INFO_OUTPUT_FORMAT_CHANGED())
                            {
                                _videoTrackIndex = _muxer.addTrack(_videoCodec.getOutputFormat());
                                if (!isVideoStart)
                                {
                                    isVideoStart = true;
                                    if (isAudioStart)
                                        _muxer.start();
                                }
                            }
                            else if (outputBufferId >= 0)
                            {
                                using var outputBuffer = _videoCodec.getOutputBuffer(outputBufferId);

                                if (outputBuffer != null)
                                {
                                    isVideoEnd =
                                        (bufferInfo.get_flags() & MediaCodec.get_BUFFER_FLAG_END_OF_STREAM()) !=
                                        0;

                                    if (bufferInfo.get_size() > 0)
                                        _muxer.writeSampleData(_videoTrackIndex, outputBuffer, bufferInfo);

                                    _videoCodec.releaseOutputBuffer(outputBufferId, false);
                                }
                            }
                        }

                    if (!isAudioEnd && (!isAudioStart || isVideoStart))
                        lock (_audioCodec)
                        {
                            var outputBufferId = _audioCodec.dequeueOutputBuffer(bufferInfo, 0);

                            if (outputBufferId == MediaCodec.get_INFO_OUTPUT_FORMAT_CHANGED())
                            {
                                _audioTrackIndex = _muxer.addTrack(_audioCodec.getOutputFormat());
                                if (!isAudioStart)
                                {
                                    isAudioStart = true;
                                    if (isVideoStart)
                                        _muxer.start();
                                }
                            }
                            else if (outputBufferId >= 0)
                            {
                                if (isVideoStart)
                                {
                                    using var outputBuffer = _audioCodec.getOutputBuffer(outputBufferId);

                                    if (outputBuffer != null)
                                    {
                                        isAudioEnd =
                                            (bufferInfo.get_flags() & MediaCodec.get_BUFFER_FLAG_END_OF_STREAM()) !=
                                            0;

                                        if (bufferInfo.get_size() > 0)
                                            _muxer.writeSampleData(_audioTrackIndex, outputBuffer, bufferInfo);
                                    }

                                    _audioCodec.releaseOutputBuffer(outputBufferId, false);
                                }
                            }
                        }

                    await Task.Delay(1, ct).ConfigureAwait(false);
                }

                lock (_videoCodec)
                {
                    _videoCodec.stop();
                    _videoCodec.release();
                    _videoCodec.Dispose();
                    _audioCodec.stop();
                    _audioCodec.release();
                    _audioCodec.Dispose();
                    _muxer.stop();
                    _muxer.release();
                    _muxer.Dispose();
                }

                _completionSource.SetResult(false);
            }
            catch (Exception ex)
            {
                _completionSource.SetException(ex);
            }
        }

        private readonly struct Yuv420Layout
        {
            public readonly PlaneInfo _y;
            public readonly PlaneInfo _u;
            public readonly PlaneInfo _v;
            public readonly nint _length;

            public Yuv420Layout(Image image)
            {
                var planes = image.getPlanes();
                if (planes.Length != 3)
                    throw new NotSupportedException($"Unsupported YUV layout (plane count: {planes.Length}).");

                _y = new PlaneInfo(planes[0]);
                _u = new PlaneInfo(planes[2]); // it seems Y-V-U but correct
                _v = new PlaneInfo(planes[1]);

                // NOTE: is it safe to assume that all planes are contiguous?
                var baseAddress = (nint)Math.Min(_y._offset, Math.Min(_u._offset, _v._offset));

                _y._offset -= baseAddress;
                _u._offset -= baseAddress;
                _v._offset -= baseAddress;

                _length = (nint)Math.Max(_y._offset + _y._size, Math.Max(_u._offset + _u._size, _v._offset + _v._size));
            }

            public override string ToString()
            {
                return $"{{ Length: {_length}, Y: {_y}, U: {_u}, V: {_v} }}";
            }
        }

        private struct PlaneInfo
        {
            public readonly int _pixelStride;
            public readonly int _rowStride;
            public nint _offset;
            public readonly nint _size;

            public PlaneInfo(Image.Plane plane)
            {
                using var buffer = plane.getBuffer();

                unsafe
                {
                    _offset = (nint)AndroidJNI.GetDirectBufferAddress(buffer!.GetRawObject());
                }

                if (_offset == 0) throw new NotSupportedException("Unsupported YUV layout.");

                _offset += buffer.position();
                _size = buffer.remaining();
                _rowStride = plane.getRowStride();
                _pixelStride = plane.getPixelStride();
            }

            public override string ToString()
            {
                return
                    $"{{ Start: {_offset}, Size: {_size}, RowStride: {_rowStride}, PixelStride: {_pixelStride} }}";
            }
        }

        private enum AudioPcmEncoding
        {
            Int8,
            Int16,
            Int24Packed,
            Int32,
            Float32
        }

        private readonly struct Frame : IDisposable
        {
            public readonly sbyte[] ImageArray;
            public readonly int ImageOffset;
            public readonly int ImageLength;
            public readonly double Timestamp;

            public Frame(sbyte[] imageArray, int imageOffset, int imageLength, double timestamp)
            {
                ImageArray = imageArray;
                ImageOffset = imageOffset;
                ImageLength = imageLength;
                Timestamp = timestamp;
            }

            public void Dispose()
            {
                ArrayPool<sbyte>.Shared.Return(ImageArray);
            }
        }
    }
}
