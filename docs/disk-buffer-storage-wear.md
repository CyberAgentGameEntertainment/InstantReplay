# Storage Writes of the Disk Buffer on Mobile Devices

This document records a measurement of the amount of data that the disk buffer of `RealtimeInstantReplaySession` writes to storage on Android and iOS devices, and an assessment of its effect on the lifespan of flash storage. It describes the measurement method, the results, the estimate of storage wear, and the conclusion. The design of the disk buffer itself is described in [disk-buffered-recording.md](disk-buffered-recording.md).

## Background

The disk buffer writes every encoded frame to storage for as long as recording continues. Flash storage tolerates a limited number of program and erase cycles, so continuous writing shortens its lifespan. The design document estimates the write rate from the configured bitrate, but the amount that reaches the storage device also depends on the actual output of the encoder, on the coalescing performed by the operating system, and on the overhead of the file system. The investigation measured these on devices.

The relevant flush policy is `DiskBufferSyncMode.OperatingSystem`, the default. Under this policy each record is handed to the operating system without a device flush, and a device flush is performed only when a segment is closed and when the manifest or codec configuration is written.

## Measurement Conditions

### Devices

| Platform | Device | OS | Storage capacity | File system of the buffer |
|---|---|---|---|---|
| Android | Galaxy S22 (SCG13) | Android 16, `user` build | 256 GB (223 GiB data partition, 16 % used) | f2fs |
| iOS | iPhone 12 | iOS 26.7 | 64 GB | APFS |

### Recording settings

Both devices ran the sample application of the suite with the `long-disk` preset and `DiskBufferSyncMode.OperatingSystem`. The values below were read from the `manifest.json` of each session directory.

| Setting | Android | iOS |
|---|---|---|
| Video | 590×1280, 30 fps, target 2,265,600 bps | 590×1280, 30 fps, target 2,265,600 bps |
| Audio | 48 kHz stereo, target 128,000 bps | 24 kHz stereo, target 128,000 bps |
| `MaxDiskUsageBytes` | 512 MiB | 512 MiB |
| Session directory | `/storage/emulated/0/Android/data/<package>/cache/InstantReplay/DiskBuffer` | `Library/Caches/InstantReplay/DiskBuffer` in the application container |

The combined target bitrate corresponds to approximately 292 KiB/s.

### Procedure

On each device, the application was kept in the foreground while recording for 600 seconds. A baseline was then measured with the application in the background, where the Unity player pauses and recording stops. The baseline lasted 300 seconds on both devices. The devices were connected to the host over USB throughout.

The first 600-second measurement did not reach `MaxDiskUsageBytes`, so the eviction of old segments was not exercised. Eviction deletes whole files and is not expected to add writes.

## Measurement Method

### Android

The device was a `user` build, so the f2fs statistics under `/sys/fs/f2fs`, `/proc/uid_io/stats`, `/sys/block/*/stat` and the health descriptor of the UFS device were not readable from the shell. The following counters were readable and were sampled from the host every 10 seconds:

- `/proc/<pid>/io` of the application process, read through `run-as` because the application is debuggable. `wchar` is the number of bytes passed to write system calls. `write_bytes` is the number of bytes the process caused to be written back from the page cache to the block layer. `cancelled_write_bytes` is the number of bytes that were dirtied and then discarded by truncation or deletion before write-back.
- Field 10 (sectors written) of `/proc/diskstats` for `sda40`, the userdata partition, and for `dm-58`, the encrypted device mounted at `/data`. These cover the whole device and include the writes of other processes.
- The size of the session directory reported by `du`.

### iOS

`xctrace record` was run with the `Activity Monitor` template and `--all-processes`. The following tables were exported with `xctrace export`:

- `activity-monitor-process-live`, column `disk-bytes-written`: the cumulative number of bytes written to the storage device on behalf of each process, sampled every second.
- `sysmon-system`, columns `disk-bytes-written` and `disk-write-ops`: the cumulative number of bytes and of write operations issued to the storage device by the whole system.

The file list of the application container was obtained with `xcrun devicectl device info files`, and diagnostic reports were obtained from the `systemCrashLogs` domain of `devicectl`.

## Results

### Android

| Counter | Recording | Baseline |
|---|---|---|
| `wchar` of the application | 445 KiB/s | 0 |
| `write_bytes` of the application | 418 KiB/s | 0 |
| `cancelled_write_bytes` of the application | 0 | 0 |
| Growth of the session directory | 415 KiB/s | 0 |
| `sda40`, whole device | 578 KiB/s | 220 KiB/s |
| `dm-58`, whole device | 590 KiB/s | 229 KiB/s |

- In each 10-second interval, `write_bytes` of the application stayed between 388 KiB/s and 438 KiB/s. No bursts were observed.
- `write_bytes` matched the growth of the session directory. All data written by the buffer reached the block layer, and none was cancelled before write-back.
- The increase of the whole-device writes during recording (578 − 220 = 358 KiB/s) did not exceed `write_bytes` of the application. The writes of other processes varied by more than the overhead of the file system, so the overhead could not be quantified, but it was not large enough to be distinguished from that variation.
- The written data corresponded to approximately 3.4 Mbps, which is about 1.4 times the combined target bitrate.

### iOS

| Counter | Recording | Baseline |
|---|---|---|
| `disk-bytes-written` of the application | 126 KiB/s | 0 |
| `disk-bytes-written` of `kernel` | 50 KiB/s | 41 KiB/s |
| `disk-bytes-written` of `launchd` | 34 KiB/s | 39 KiB/s |
| System, bytes | 230 KiB/s | 97 KiB/s |
| System, write operations | 9.2 per second | 6.6 per second |

- In each 60-second interval, the writes of the application stayed between 122 KiB/s and 133 KiB/s.
- The segment files were approximately 760 KB each, which is consistent with the measured rate.
- The increase of the system-wide writes during recording (230 − 97 = 133 KiB/s) exceeded the writes of the application by approximately 7 KiB/s, or about 5 %. The writes attributed to `kernel` increased by a similar amount, which is consistent with file-system metadata and write-back caused by the buffer.
- The written data corresponded to approximately 1.0 Mbps, which is about 0.43 times the combined target bitrate.

### Coalescing of small writes

The buffer writes one record per encoded video frame or audio packet. This amounts to approximately 77 records per second on Android and 53 records per second on iOS, with average sizes of approximately 5.5 KB and 2.4 KB respectively. On iOS, recording increased the number of write operations issued to the storage device by only 2.6 per second, with an average size of approximately 51 KiB. The page cache coalesced the records before they reached the device, and the flush policy did not force the records to be written individually. On Android, the process issued approximately 320 write system calls per second, but this figure includes writes other than those of the buffer and was not analyzed further.

### Diagnostic reports on iOS

iOS generates a `diskwrites_resource` diagnostic report when a process writes more than a fixed amount within a period. The reports of system processes on the device stated the limit as follows:

```
Action taken:     none
Writes limit:     1073.74 MB
Limit duration:   86400s
```

At 126 KiB/s, the application reaches this limit after approximately 2.3 hours of recording within 24 hours. No such report for the application was present on the device at the time of the measurement. The action taken is `none`, so the process is not terminated, but the report is delivered through MetricKit as `MXDiskWriteExceptionDiagnostic` and, for distributed applications, appears in the Disk Writes report of the Xcode Organizer. Android has no equivalent mechanism.

## Estimate of Storage Wear

Neither device exposes the number of program and erase cycles consumed or its rated endurance. The following assumptions were therefore made to estimate an order of magnitude:

- The NAND flash tolerates 1,000 program and erase cycles.
- The write amplification inside the storage device is 3.
- The amount of data the host can write over the lifespan of the device is the capacity multiplied by 1,000 and divided by 3: approximately 85 TB for 256 GB and approximately 21 TB for 64 GB.

The write rates used are 418 KiB/s (approximately 1.5 GB per hour) for Android and 133 KiB/s, the system-wide increase, (approximately 0.49 GB per hour) for iOS.

| Usage | Android, 256 GB | iOS, 64 GB |
|---|---|---|
| Continuous recording for 24 hours a day | 13.5 TB per year, 16 % of the lifespan per year | 4.3 TB per year, 20 % of the lifespan per year |
| Recording for 8 hours a day | 4.5 TB per year, 5 % per year | 1.4 TB per year, 7 % per year |
| Recording for 2 hours a day | 1.1 TB per year, 1.3 % per year | 0.36 TB per year, 1.7 % per year |

These figures scale linearly with the actual bitrate and inversely with the storage capacity. For comparison, recording 4K video at 30 fps with the camera of a smartphone, at approximately 50 Mbps, writes approximately 22 GB per hour.

## Conclusion

- With `DiskBufferSyncMode.OperatingSystem`, the data reaching the storage device was approximately equal to the encoded data written by the buffer. The overhead of the file system was within the measurement variation on Android and approximately 5 % on iOS. The default flush policy works as intended.
- The amount written is determined by the actual output bitrate of the encoder, which differed from the target in opposite directions on the two platforms: about 1.4 times the target on Android and about 0.43 times on iOS.
- For development and quality-assurance builds, the effect on storage lifespan is small. Continuous recording for 24 hours a day over several years, particularly on devices with small storage, is the only usage in which the wear becomes significant.
- On iOS, recording for more than approximately 2.3 hours a day causes a `diskwrites_resource` diagnostic report. This is not harmful in development builds, but it should be taken into account before the disk buffer is enabled in distributed applications.

## Other Findings

- **Android output bitrate.** The Android encoder produced about 1.4 times the target bitrate, consistent with the observation recorded in [encoder-power-consumption.md](encoder-power-consumption.md). Because the amount written to storage is proportional to the output bitrate, correcting the bitrate control would reduce the writes of the disk buffer accordingly.
- **Location of the buffer on Android.** On the measured device, `Application.temporaryCachePath` resolved to the application-specific directory on external storage (`/storage/emulated/0/Android/data/<package>/cache`), not to the internal cache directory. This directory requires no permission and resides on the same f2fs partition as internal storage, so the result of this measurement is not affected. The statement in [disk-buffered-recording.md](disk-buffered-recording.md) that writing to external storage was not chosen should be read with this in mind.
- **Abandoned session directories.** A session directory left by an earlier abnormal termination remained in the buffer directory on the Android device. It does not affect wear, but it occupies storage until it is recovered or deleted.

## Notes on Measurement Tools

- **Readable counters on Android `user` builds.** `/proc/diskstats` was readable from the shell, but `/sys/block/*/stat`, `/sys/fs/f2fs`, and `/proc/uid_io/stats` were not. `dumpsys storaged` was available as an alternative source of per-UID statistics. `/proc/<pid>/io` of a debuggable application is readable through `run-as`.
- **Device availability in Instruments.** As also noted in [encoder-power-consumption.md](encoder-power-consumption.md), `xctrace` reported the device as offline ("Timed out waiting for device to boot") after an earlier recording. Running `xcrun devicectl device info details` immediately before recording made the device available again.
- **Exported tables of `xctrace`.** The XML produced by `xctrace export` refers to repeated values by `ref` attributes that point to elements with matching `id` attributes. These references must be resolved when the tables are parsed.
- **Foreground state on iOS.** Recording stops while the application is in the background. `xcrun devicectl device process launch` brings an application that is already running to the foreground and was used to resume recording and to switch to the baseline condition.
