# Encoder Settings and Power Consumption on Mobile Devices

This document records an investigation into whether H.264 encoder settings, other than resolution and frame rate, can reduce the power consumption and heat generation of InstantReplay recording on mobile devices. It describes the settings that were evaluated, the measurement method, the results, and the conclusion. The settings were not adopted; the document is kept as a reference for future work on the same problem.

## Background

Recording with InstantReplay has been reported to raise device temperature and to make thermal throttling more likely, particularly on mobile devices. Reducing the amount of data processed, by lowering the resolution or the frame rate, is the primary countermeasure. The investigation examined whether the encoder could additionally trade compression efficiency for lower resource consumption.

Hardware encoders consume power mainly in proportion to the pixel rate (width × height × frame rate). Settings that change the coding tools in use, such as the H.264 profile, can affect the work done per pixel, but their effect is expected to be smaller than that of the pixel rate.

## Evaluated Settings

### H.264 Constrained Baseline profile

Without an explicit profile, both VideoToolbox and the Android MediaCodec encoder used in the test (`c2.qti.avc.encoder`) produced High profile streams. The Constrained Baseline profile disables B-frames and CABAC entropy coding, both of which add computation, at the cost of compression efficiency.

- VideoToolbox: `kVTCompressionPropertyKey_ProfileLevel` was set to `H264_ConstrainedBaseline_AutoLevel`, falling back to `kVTProfileLevel_H264_Baseline_AutoLevel`. The constant `kVTProfileLevel_H264_ConstrainedBaseline_AutoLevel` is declared only on iOS 15 and macOS 12 or later, so its literal value was used; an unsupported value is rejected with `kVTPropertyNotSupportedErr` (-12900).
- MediaCodec: `KEY_PROFILE` was set to `AVCProfileConstrainedBaseline` (0x10000), falling back to `AVCProfileBaseline` (1), only when the codec advertises the profile in `MediaCodecInfo.CodecCapabilities.profileLevels`. `KEY_LEVEL` must be set together with `KEY_PROFILE`; it was computed from the resolution, frame rate and bitrate using the limits in Table A-1 of the H.264 specification (MaxMBPS, MaxFS and the Baseline MaxBR). If `configure` failed with the profile, the encoder was configured again without it.

Both changes were confirmed to take effect: the output streams changed from High@3.1 to Constrained Baseline@3.1 on macOS and on the Android device.

### `kVTCompressionPropertyKey_ExpectedFrameRate` (Apple platforms)

The VideoToolbox documentation states that, when `kVTCompressionPropertyKey_RealTime` is true, the expected frame rate is used to optimize energy usage. The configured frame rate was passed as this property.

### Settings not evaluated

- `kVTCompressionPropertyKey_MaximizePowerEfficiency` (iOS 12 and later) is documented as a hint for background or non-real-time encoding. Because InstantReplay encodes in real time with `kVTCompressionPropertyKey_RealTime` enabled, the property is unlikely to have an effect and was not measured.
- Lowering the bitrate reduces storage and memory traffic but does not substantially reduce the work of a hardware encoder, which processes every pixel regardless of the bitrate.

## Measurement Method

### Workload

A dedicated test harness encoded a synthetic video for a fixed wall-clock duration, pushing frames at the configured frame rate as a recording session does. The frames consisted of scrolling color bars with low-amplitude noise, so that every frame differed from the previous one. Only the video encoder ran; audio encoding and muxing were excluded so that the video encoder accounted for as much of the measured cost as possible. Encoded output was received and discarded.

The harness supplies frames as BGRA buffers in CPU memory, whereas the Unity package normally supplies GPU textures. On Android, the BGRA-to-YUV conversion of the CPU path took approximately 29 ms per 1280×720 frame. The measured power therefore includes a CPU load that the production path does not have, and the share of the encoder in the total is smaller than it would be in an application.

Each condition was selected at run time through environment variables in the harness build, so that the same binary was used for all conditions. The conditions were run in an interleaved order (for example A B B A) with cooling periods in between, to cancel linear drift in battery voltage and device temperature.

### Devices

| Platform | Device | OS | Encoder |
|---|---|---|---|
| Android | Galaxy S22 (SCG13, Snapdragon 8 Gen 1) | Android 16 (API 36) | `c2.qti.avc.encoder` |
| iOS | iPhone 12 | iOS 26.7 | VideoToolbox |

The screen was kept on at a fixed brightness throughout, and the devices were disconnected from USB power during measurement.

### Android

A shell script on the device ran the whole sequence unattended, so that the USB cable could be disconnected:

- Battery counters (`batt.current_ua`, `batt.voltage_uv`, `batt.charge_uah`) were recorded with the Perfetto `android.power` data source at 1-second intervals. Power was computed as the mean of current × voltage, excluding the first 60 seconds of each run.
- Temperatures were sampled from `dumpsys thermalservice` every 30 seconds.
- An idle period with the screen on, but without encoding, was measured as a baseline.

### iOS

Instruments was used over a Wi-Fi connection, because the battery drain reported by Power Profiler is zero while the device is powered over USB. `xctrace record` launched the harness application with the `Power Profiler` and `Thermal State` instruments. The `SystemPowerLevel` table reports the battery drain in percent per hour with a resolution of 0.1 %/hr; it was converted to milliwatts using the nominal battery energy of the iPhone 12 (approximately 10.8 Wh), so that 1 %/hr corresponds to approximately 108 mW. The first 60 seconds of each run were excluded.

The per-process power impact values reported by Power Profiler cover CPU, GPU and display only. They do not include the hardware encoder and were not used.

## Results

### Android (1280×720, 30 fps, 2.5 Mbps, 10 minutes per run)

| Condition | Mean power | Individual runs | Increase over idle |
|---|---|---|---|
| Idle (screen on) | 754 mW | 754 | — |
| A: default profile (High) | 1212 mW | 1204, 1220 | 458 mW |
| B: Constrained Baseline | 1175 mW | 1182, 1168 | 421 mW |

Constrained Baseline reduced the power consumption by approximately 37 mW, which is about 3 % of the total and about 8 % of the increase over idle. The difference exceeds the spread between repeated runs of the same condition (approximately 15 mW). The AP temperature rose by approximately 2 °C during each run, with no difference between the conditions.

### iOS (1280×720, 30 fps, 2.5 Mbps, 10 minutes per run)

| Condition | Mean power | Individual runs |
|---|---|---|
| A: neither setting | 647 mW | 647, 646 |
| B: Constrained Baseline and ExpectedFrameRate | 647 mW | 645, 649 |
| C: ExpectedFrameRate only | 641 mW | 640, 642 |

No difference was observed. The largest difference, approximately 6 mW, is comparable to the spread between repeated runs (approximately 4 mW). For reference, the same harness running at 1 fps consumed 583 mW, so encoding at 1280×720 and 30 fps added only about 64 mW in total, which leaves little room for a measurable difference.

### iOS (1920×1080, 60 fps, 8 Mbps, 5 minutes per run)

| Condition | Mean power | Individual runs |
|---|---|---|
| A: neither setting | 1024 mW | 1027, 1020 |
| B: Constrained Baseline and ExpectedFrameRate | 1011 mW | 1015, 1008 |

At the higher load, the settings reduced the power consumption by approximately 13 mW, or about 1.3 % of the total. The spread between repeated runs was approximately 7 mW. The thermal state remained nominal in all iOS runs.

Changing from 1280×720 at 30 fps to 1920×1080 at 60 fps increased the power consumption by approximately 370 mW on the iPhone 12, which is more than ten times the effect of the encoder settings.

## Conclusion

- The Constrained Baseline profile and `kVTCompressionPropertyKey_ExpectedFrameRate` reduced power consumption by between 0 % and 3 % of the total in the measured configurations. No configuration consumed more power with the settings applied.
- The reduction is small compared with the effect of the pixel rate, and Constrained Baseline also lowers compression efficiency. The settings were therefore not adopted.
- Reducing the resolution and the frame rate remains the effective countermeasure against heat generation during recording.

## Other Findings

The following observations were made during the investigation and are not specific to the evaluated settings.

- **Android output bitrate.** With a target of 2.5 Mbps, the Android encoder produced approximately 4.0 Mbps in all runs, measured from the size of the serialized encoded data. The bitrate control of the MediaCodec backend may not be working as intended and should be examined separately.
- **Xcode 27 and CoreMedia.** When the iOS test binary was built with the Xcode 27 SDK, it linked against `@rpath/libswiftCoreMedia.dylib` instead of `CoreMedia.framework` and failed to launch on the device. The CoreMedia text-based stub in this SDK contains only the arm64e slice, so the linker resolves the symbols from the Swift toolchain stub. Whether the product build is affected has not been verified.

## Notes on Measurement Tools

- **Power Profiler template.** With Xcode 27 and iOS 26.7, recording with the `Power Profiler` template causes `xctrace` to crash (exit status 133, `EXC_BREAKPOINT`) in `NetworkConnectionStatsModeling` while the recording is being saved. Specifying the instruments individually (`--instrument 'Power Profiler' --instrument 'Thermal State'`) avoids the crash.
- **Device availability in Instruments.** `xctrace` intermittently reported the device as offline ("Timed out waiting for device to boot"). Running `xcrun devicectl device info details` immediately before recording made the device available again.
- **Launch arguments.** `xctrace record --launch` did not pass the arguments of a Rust test binary, so a test marked `#[ignore]` did not run. Environment variables passed with `--env` did take effect.
- **Battery current unit on Galaxy devices.** The device reported `batt.current_ua` in milliamperes rather than microamperes. The values were consistent with the decrease of `batt.charge_uah`.
- **Lock screen timeout on Android.** While the lock screen is shown, a short timeout applies regardless of `screen_off_timeout`, and the device suspends shortly after the screen turns off. Unattended measurements must start with the device unlocked, and should record `mWakefulness` from `dumpsys power` to detect runs in which the screen turned off.
- **Toolbox `grep` on Android.** The `grep` on the device does not interpret `\|` as alternation in basic regular expressions; `grep -E` must be used.
