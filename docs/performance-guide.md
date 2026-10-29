# Performance Guide

This document is intended for developers who use Instant Replay in projects with strict performance requirements. It describes the memory and the CPU / GPU work used by `RealtimeInstantReplaySession`, and how each setting affects them. Measurements on physical devices are given in the second half.

The measured values are examples for specific devices, scenes and settings, and are not guaranteed for other environments. Verify the impact on your own project on physical devices, using the methods described in this document. The descriptions apply to the versions after Instant Replay v1.7.0, which include the changes described in [Changes after v1.7.0](#changes-after-v170). Unless otherwise noted, the measured values were obtained with v1.7.0.

A Japanese version of this document is available in [performance-guide.ja.md](performance-guide.ja.md).

## Overview of the Processing

During recording, the following steps are performed on every recorded frame.

1. Frame capture: the default `ScreenshotFrameProvider` copies the whole screen into a screen-sized RenderTexture with `ScreenCapture.CaptureScreenshotIntoRenderTexture()` at the end of the frame.
2. Input to the encoder: the captured texture is scaled down to the encoding size (`VideoOptions.Width` × `VideoOptions.Height`) and passed to the encoder. There are two paths.
   - Blit path: the native plugin draws directly into the input surface of the encoder, using the GPU on the render thread. It is used on iOS / macOS (Metal) and on Android (Vulkan, API 29 or later).
   - Readback path: the texture scaled down on the Unity side is read back into CPU memory with `AsyncGPUReadback` and passed to the encoder. It is used on platforms that do not support the blit path (OpenGL ES on Android, Windows, Linux, Web and others), and when `ForceReadback` is enabled.
3. Encoding: the encoder of the platform (VideoToolbox, MediaCodec, Media Foundation and others) encodes the video into H.264 and the audio into AAC. The audio samples are obtained with `OnAudioFilterRead`.
4. Buffering: the encoded data is copied to the C# side and held in a ring buffer in memory. When the disk buffer is enabled, it is written to files instead.

Calling `StopAndExportAsync()` multiplexes (muxes) the data in the buffer into an MP4 file. The data is not encoded again.

## Memory

### Breakdown

| Item | Approximate size | Condition | Shown in Memory Profiler as |
| --- | --- | --- | --- |
| RenderTexture for capture | Screen width × height × 4 bytes | When `ScreenshotFrameProvider` is used | RenderTexture `InstantReplay ScreenshotFrameProvider` |
| RenderTexture for scaling | Encoding width × height × 4 bytes | Readback path | RenderTexture `InstantReplay Preprocessing` |
| Frames before encoding (CPU) | Encoding width × height × 4 bytes × number of frames | Readback path | Native heap of UniEnc (described below) |
| Input surfaces of the encoder | Encoding width × height × 4 bytes × a few | Blit path | Not shown |
| Internal memory of the encoder | Unknown | Always | Not shown |
| Buffer of encoded data | Up to `MaxMemoryUsageBytesForCompressedFrames` | Memory buffer | `System.Byte[]` of 64 KiB in the managed heap |
| Write queue of the disk buffer | Up to `DiskBufferOptions.MaxPendingWriteBytes` | Disk buffer | `System.Byte[]` in the managed heap |
| Native heap of UniEnc | Allocated in units of 32 MiB | Always | `UniEnc` / `mimalloc` |

Each item is described below.

#### RenderTexture for capture

`ScreenshotFrameProvider` allocates one ARGB32 RenderTexture of the screen size. Its size is proportional to the screen size rather than to the encoding size, so it is larger on devices with a higher resolution. For example, it is approximately 9.8 MB for a 1080 × 2340 screen. This texture is not allocated when a custom `IFrameProvider` is used.

#### Frames before encoding

On the readback path, a buffer of the encoding size × 4 bytes is allocated for each frame as the destination of `AsyncGPUReadback`. The number of buffers that exist at the same time can be limited with `MaxNumberOfRawFrameBuffers`. With the default value (null), no limit is applied, and the actual number is determined by how many frames are read back before the encoder catches up. Frames that exceed the limit are dropped, so a value that is too small causes frame drops.

On the blit path, the GPU draws directly into the input surfaces of the encoder (`CVPixelBuffer` backed by IOSurface on iOS, `AHardwareBuffer` on Android), so no buffer on the CPU side is used. Each input surface is the encoding size × 4 bytes; two to four are allocated on iOS and up to three on Android. They are memory managed by the OS and are outside the memory management of Unity.

#### Buffer of encoded data

With the memory buffer, each encoded frame is copied into chunks of 64 KiB on the managed heap. The video and audio streams take their chunks from one pool, and the total size of the chunks does not exceed `MaxMemoryUsageBytesForCompressedFrames`. A chunk is allocated when it is first needed and is reused after all frames in it have been discarded, so the managed heap grows while the buffer fills and stops growing when the buffer reaches the limit. Each chunk is smaller than the threshold of the large object heap. When the buffer is full, the oldest frames are discarded in timestamp order across the video and audio streams. The chunks are released when the session is disposed.

In the measurements on Android, the managed heap usage (GC Used Memory) with a full buffer increased by approximately 23 MB with the default of 20 MiB, and by approximately 70 MB with 60 MiB. These values include a few megabytes of allocations other than the buffer.

In v1.7.0, each encoded frame was stored in a `byte[]` rented from `ArrayPool<byte>.Shared`, which returns arrays whose sizes are rounded up to a power of two. The managed heap usage therefore increased by 35 to 50 MB with 20 MiB, and by approximately 103 MB with 60 MiB. In addition, after an export, the buffer could remain on the managed heap even after the session was disposed.

Once the managed heap has grown, it rarely shrinks, so GC Reserved Memory remains increased after recording ends.

With the disk buffer, encoded frames are written to files through a write queue and are not held in memory. The size of the queue is limited by `DiskBufferOptions.MaxPendingWriteBytes` (4 MiB by default).

#### Native heap of UniEnc

Memory allocated inside the native library (unienc) is obtained through the memory manager of Unity, and is shown in Memory Profiler as the object `mimalloc` in the area `UniEnc`. Because this area is allocated in units of 32 MiB, the value in Memory Profiler is larger than the memory actually in use and grows in steps. In the measurements, snapshots taken during recording showed approximately 68 MB on both iOS and Android. In addition, a large allocation, such as the frame buffers of the readback path, sometimes triggered a further increase of approximately 64 MiB. The allocated area is not released after the session is disposed.

The value in Memory Profiler is the amount of reserved address space and does not match the physical memory usage. Check the effect on physical memory with the values reported by the OS (`phys_footprint` on iOS, PSS on Android).

#### Memory not shown in Memory Profiler

The input surfaces of the encoder, the internal buffers of the hardware encoder, and objects on the Java heap on Android are outside the memory management of Unity. In Memory Profiler, they are included in Untracked or are not shown at all. Use the tools of the platform to examine them.

- iOS: the Memory Report of Xcode, and the VM Tracker instrument of Instruments (IOSurface regions)
- Android: Graphics and Private Other in `adb shell dumpsys meminfo <package name>`

### Examining with Memory Profiler

1. Start recording and wait until the buffer is full. With the memory buffer, this can be determined by the used size obtained from `GetBufferStatistics()` approaching the limit.
2. Take a snapshot with Memory Profiler (`com.unity.memoryprofiler`).
3. Examine the following items.
   - Unity Objects: the RenderTextures `InstantReplay ScreenshotFrameProvider` and `InstantReplay Preprocessing`
   - The `UniEnc` area under Native in All Of Memory
   - Managed Objects: the total size of `System.Byte[]`. The chunks of encoded data are arrays of 65,536 bytes referenced through `InstantReplay.EncodedFrameTrackStorage` and `InstantReplay.EncodedFrameChunkPool`.
4. Perform the same steps without recording, and compare the two snapshots.

## CPU / GPU Work

### Work on Unity threads

| Thread | Work | Shown in the Profiler as |
| --- | --- | --- |
| Main Thread | Screen capture | `Awaitable.OnEndOfFrame()` |
| Main Thread | Continuations of the asynchronous pipeline | `UnitySynchronizationContext.ExecuteTasks()` |
| Render Thread | Scaling and color conversion on the blit path | `UniEnc.Unity.GraphicsEventIssuer`, `unienc_android_mc::vulkan::preprocess::blit` (Android), `unienc_apple_vt::metal::custom_blit` (iOS / macOS) |
| Render Thread | Readback on the readback path | `Gfx.RequestAsyncReadbackData`, `Gfx.UpdateAsyncReadbackData` |
| Audio Mixer Thread | Acquisition of audio samples | `UnityAudioSampleProviderReceiver.OnAudioFilterRead()` |

### Work outside Unity threads

The input to the encoder, the reception of encoded data and its copy to the C# side are performed on the worker threads of the native library and on the threads of the encoder of the platform. These threads are either not shown in the Unity Profiler or are shown as unnamed threads. Use the tools of the platform (Time Profiler of Xcode Instruments, the CPU Profiler of Android Studio, or Perfetto) to examine their CPU usage. On Android, the threads of the native library appear with names of the form `Thread-<number>`.

This work runs in parallel with frame rendering and is not added directly to the frame time. However, it increases the CPU load and the power consumption of the whole device, and can affect the frame time through clock reductions caused by heat. See [encoder-power-consumption.md](encoder-power-consumption.md) for power consumption and heat.

### Temporary load

The following work occurs at specific points rather than on every frame.

- Session creation: the constructor of `RealtimeInstantReplaySession` initializes the encoders. In the measurements, it took 30 to 110 ms on iOS and 55 to 220 ms on Android, the longest being with the disk buffer on both platforms.
- Export: `StopAndExportAsync()` multiplexes the video and audio on the thread pool. In the measurements on Android with the memory buffer, the longest frame during export was 33 to 50 ms. In v1.7.0, the multiplexing and the first initialization of the local time zone, which is used for the default name of the output file, ran on the main thread, and one frame of 95 to 135 ms occurred. The time zone is now initialized on the thread pool when the session is created. On Web, which has no threads, these steps still run on the main thread.

## Effect of Settings on Memory and Frame Time

### Type of buffer (memory or disk)

The memory buffer holds the encoded data in the managed heap. The duration that can be held is determined by `MaxMemoryUsageBytesForCompressedFrames` and the bitrate, and the managed heap usage increases by up to about that size.

The disk buffer writes the encoded data to files, so the managed heap hardly increases. In the measurements with v1.7.0, the increase in physical memory with a full buffer was approximately 40 % (iOS) and 60 % (Android) of that with the memory buffer. The difference is smaller in later versions, because the memory buffer no longer rounds up the size of each frame. On the other hand, the continuous writes to storage increased the CPU usage on Android and shorten the lifespan of flash memory. The disk buffer is intended primarily for development and quality-assurance builds. See [disk-buffered-recording.md](disk-buffered-recording.md) for details.

### Screen size and encoding size

The screen size affects the size of the RenderTexture for capture and the GPU load of capturing and scaling. The encoding size affects the input surfaces of the encoder, the frame buffers of the readback path, and the load of the encoder. On the readback path, the capacity of the frame buffers is proportional to the encoding size, so the encoding size has a particularly large effect. In the measurements, changing the encoding size from 1280 × 720 to 1920 × 1080 on the readback path on Android raised the increase in physical memory from approximately 130 MB to approximately 210 MB.

### Frame rate

The frame rate (`FixedFrameRate` and `VideoOptions.FpsHint`) increases the CPU and GPU load in proportion to the number of frames processed. In the measurements, changing from 30 fps to 60 fps multiplied the increase in CPU usage of the whole process by approximately 1.9 on iOS and 1.5 on Android. The duration that a buffer of a given size can hold is determined by the bitrate, so raising the frame rate does not shorten it.

### Readback path

Even on platforms where the blit path is available, enabling `ForceReadback` selects the readback path. Because the readback path requires a readback into CPU memory and frame buffers before encoding, it uses more physical memory and more CPU than the blit path. In the measurements, the increase in physical memory was larger by approximately 20 MB on iOS and 60 MB on Android, and the increase in CPU usage of the process was approximately twice as large on iOS. The increase in GPU time was larger with the readback path on iOS and with the blit path on Android.

The number of frames waiting for the encoder is limited by `VideoInputQueueSize` (5 by default), but frames being read back are not included in it. Setting `MaxNumberOfRawFrameBuffers` is recommended when the readback path is used, to prevent the frame buffers from growing when the encoder falls behind. In the measurements, a limit of 3 hardly changed the increase in physical memory, so regard this setting as a means of preventing growth rather than of reducing memory usage.

### Bitrate

The bitrate hardly affects the amount of work of the encoder, but it determines the duration that a buffer of a given size can hold. The duration is approximately the buffer size × 8 ÷ (video bitrate + audio bitrate) seconds. For example, it is approximately 64 seconds with the default 20 MiB, 2.5 Mbps and 128 kbps. When the required duration is fixed, lowering the bitrate allows a smaller buffer and reduces memory usage.

However, the encoder does not follow the specified bitrate on some Android devices. On the device used for the measurements, the actual bitrate at 1280 × 720 was approximately 4.4 Mbps whether 1 Mbps or 2.5 Mbps was specified, and 20 MiB held approximately 38 seconds. Check the duration actually held on the target devices with `GetBufferStatistics()`.

### Audio

Disabling audio recording (using `NullAudioSampleProvider`) removes the audio encoding. In the measurements, the increase in CPU usage of the process decreased by approximately 40 % on iOS and 30 % on Android.

## Recommendations

- Determine the size of the memory buffer from the bitrate and the required duration, and allow for the managed heap usage to increase by about that size.
- Set `MaxNumberOfRawFrameBuffers` when the readback path is used.
- Use the smallest frame rate and encoding size that meet the purpose. The frame rate in particular directly affects the CPU and GPU load.
- Create sessions at points where frame drops are acceptable, such as during loading or on a result screen. Exporting no longer stalls the main thread for a long time, but it still adds work to the frames during the export.
- On Android, check the bitrate and the duration held on the target devices.
- Check the effect on physical memory with the values reported by the OS, not only with Memory Profiler.

## Measurement Results

The values in this section were measured with v1.7.0. The values affected by later changes are described in [Changes after v1.7.0](#changes-after-v170).

### Conditions

| Item | iOS | Android |
| --- | --- | --- |
| Device | iPhone 12 | Galaxy S22 (SCG13, Snapdragon 8 Gen 1) |
| OS | iOS 26.7 | Android 16 (API 36) |
| Graphics API | Metal | Vulkan |
| Screen size | 1170 × 2532 | 1080 × 2340 |

- Unity 6000.3.26f1, the Universal Render Pipeline, IL2CPP and release builds were used.
- A scene rendering 400 rotating cubes was run with a target of 60 fps.
- For each condition, the application was launched anew, the state without recording was measured for 20 seconds, a session was created, and the state during recording was measured for 20 seconds. The values in the tables are increases from the state without recording.
- The default settings are 1280 × 720, 30 fps, 2.5 Mbps video, 128 kbps audio and a 20 MiB memory buffer. The "Condition" column shows the changes from the default settings.
- The screen was in portrait orientation, so black bars were added to the left and right when it was scaled to the landscape encoding size. The video therefore contained less information than a video using the whole frame.
- Frame times were obtained with `FrameTimingManager`. The CPU usage of the process is the sum of the CPU time of all threads divided by the elapsed time; 1000 ms/s corresponds to the full time of one core.
- Physical memory is `phys_footprint` on iOS and the total PSS on Android. "Start" is the value 10 seconds after recording started, and "Full" is the value after the buffer reached its limit.

### iOS (iPhone 12)

| Condition | Main Thread (ms) | GPU (ms) | Process CPU (ms/s) | Physical memory, start (MB) | Physical memory, full (MB) | GC Used Memory, full (MB) | Duration held (s) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Default | +0.19 | +0.76 | +70 | +28 | +66 | +44 | 66 |
| 640 × 360 | +0.37 | +0.69 | +80 | +19 | +59 | +43 | 67 |
| 1920 × 1080 | +0.30 | +0.82 | +75 | +40 | +76 | +41 | 66 |
| 60 fps | +0.52 | +0.80 | +132 | +29 | +72 | +49 | 65 |
| 1920 × 1080, 60 fps | +0.63 | +0.85 | +139 | +41 | +83 | +47 | 65 |
| 1 Mbps | +0.26 | +0.76 | +73 | +28 | +79 | +48 | 162 |
| 5 Mbps | +0.19 | +0.75 | +57 | +33 | +62 | +40 | 33 |
| 10 Mbps | +0.28 | +0.76 | +78 | +37 | +69 | +42 | 16 |
| Readback | +0.22 | +1.18 | +157 | +48 | +86 | +44 | 65 |
| Readback, 1920 × 1080 | -0.27 | +1.40 | +155 | +86 | +123 | +43 | 65 |
| Disk buffer | +0.06 | +0.81 | +70 | +26 | +27 | +2 | 75 |
| No audio | +0.24 | +0.77 | +41 | +27 | +64 | +43 | 66 |

Without recording, the main thread took approximately 3.1 ms, the GPU approximately 2.0 ms, and the process CPU usage was approximately 490 ms/s.

### Android (Galaxy S22)

| Condition | Main Thread (ms) | GPU (ms) | Process CPU (ms/s) | Physical memory, start (MB) | Physical memory, full (MB) | GC Used Memory, full (MB) | Duration held (s) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Default | +0.42 | +2.61 | +465 | +45 | +73 | +37 | 38 |
| 640 × 360 | +0.5 | +2.85 | +478 | +40 | +76 | +42 | 63 |
| 1920 × 1080 | +0.62 | +2.67 | +506 | +46 | +82 | +37 | 63 |
| 60 fps | +0.75 | +4.53 | +680 | +43 | +84 | +39 | 38 |
| 1920 × 1080, 60 fps | +0.68 | +4.42 | +686 | +43 | +97 | +51 | 63 |
| 1 Mbps | +0.54 | +2.64 | +484 | +43 | +71 | +37 | 38 |
| 5 Mbps | +0.59 | +2.61 | +488 | +46 | +76 | +37 | 32 |
| 10 Mbps | +0.52 | +2.65 | +490 | +52 | +77 | +38 | 16 |
| 60 MiB buffer | +0.47 | +2.47 | +476 | +47 | +150 | +103 | 117 |
| Readback | -0.42 | +0.40 | +570 | +97 | +132 | +33 | 38 |
| Readback, `MaxNumberOfRawFrameBuffers` = 3 | -0.39 | +0.40 | +566 | +93 | +130 | +34 | 38 |
| Readback, 1920 × 1080 | -1.43 | +0.66 | +704 | +170 | +208 | +37 | 63 |
| Disk buffer | +0.59 | +2.59 | +551 | +41 | +46 | +2 | 76 |
| No audio | +0.27 | +2.53 | +335 | +45 | +80 | +35 | 39 |

Without recording, the main thread took approximately 6.5 ms, the GPU approximately 2.7 ms, and the process CPU usage was approximately 1010 ms/s. The main thread value for 640 × 360 is the difference of the medians, because the mean was affected by outliers.

### Per-frame time measured with the Profiler

The state of rendering at 60 fps while recording with the default settings (30 fps) was measured with the Unity Profiler in development builds, and the mean time (self time) per rendered frame was obtained for the main markers. Because half of the rendered frames are recorded, the time per recorded frame is approximately twice these values. The readback path on iOS was not measured.

| Thread | Marker | iOS, blit path (ms) | Android, blit path (ms) | Android, readback path (ms) |
| --- | --- | --- | --- | --- |
| Main Thread | `Awaitable.OnEndOfFrame()` | 0.06 | 0.19 | 0.25 |
| Main Thread | `UnitySynchronizationContext.ExecuteTasks()` | 0.03 | - | 0.11 |
| Render Thread | Total under `unienc_apple_vt::metal::custom_blit` | 0.17 | - | - |
| Render Thread | Total under `unienc_android_mc::vulkan::preprocess::blit` | - | 0.73 | - |
| Render Thread | `UniEnc.Unity.GraphicsEventIssuer` | 0.01 | 0.16 | - |
| Render Thread | `Gfx.RequestAsyncReadbackData` | - | - | 0.46 |
| Render Thread | `Gfx.UpdateAsyncReadbackData` | - | - | 1.53 |
| Audio Mixer Thread | `UnityAudioSampleProviderReceiver.OnAudioFilterRead()` | 0.06 | 0.28 | 0.28 |

### Observations

- On iOS, recording increased both the main thread time and the GPU time by less than 1 ms. On Android, the blit path increased the GPU time by approximately 2.6 ms (approximately 4.5 ms at 60 fps).
- The increase in CPU usage of the process was larger on Android than on iOS, and most of it occurred outside Unity threads (on the worker threads of the native library and the threads of MediaCodec).
- The increase in physical memory at the start is due to the RenderTexture for capture, the encoders and the initialization of the native library; the growth of the managed heap is added as the buffer fills.
- With the disk buffer, the increase in physical memory with a full buffer was 25 to 40 MB smaller than with the memory buffer.
- On iOS, the duration held changed according to the specified bitrate, whereas on Android it did not change between 1 Mbps and 2.5 Mbps (the default). On Android, the duration held changed with the encoding size because the bitrate actually produced by the encoder changed.

### Changes after v1.7.0

The following changes were made after v1.7.0. Their effect was measured on the Galaxy S22 under the same conditions as above, with the default settings and with a 60 MiB buffer. iOS was not measured again.

- The buffer of encoded data stores the frames in chunks of 64 KiB within `MaxMemoryUsageBytesForCompressedFrames`, instead of in arrays rounded up to a power of two.
- The memory held by a session is released when the session is disposed, also after an export.
- Exporting no longer runs the multiplexing or the first initialization of the local time zone on the main thread.
- On Android, the CPU usage of the threads of the native library is lower. The waits for MediaCodec are no longer handed to a thread pool one by one, the Vulkan objects created for each input surface of the encoder are reused, and the lookups of Java methods are cached.

| Item | Condition | v1.7.0 | After the changes |
| --- | --- | --- | --- |
| GC Used Memory, full (MB) | Default | +38 to +39 | +23 to +24 |
| GC Used Memory, full (MB) | 60 MiB buffer | +108 | +70 to +71 |
| Physical memory, full (MB) | Default | +66 to +71 | +47 to +60 |
| Physical memory, full (MB) | 60 MiB buffer | +151 to +153 | +117 to +120 |
| GC Used Memory after the session is disposed (MB) | Default | 38 to 39 | 2.5 to 2.6 |
| Longest frame during export (ms) | Default | 100 to 117 | 33 to 50 |

The GC Used Memory without recording was approximately 2.1 MB. The CPU usage was measured with simpleperf on the same application, replacing only the native library. The CPU time of the threads of the native library during recording decreased from approximately 195 ms/s to approximately 120 ms/s. The CPU usage of the whole process varied between runs by more than this difference, and decreased by approximately 50 ms/s in a separate measurement.
