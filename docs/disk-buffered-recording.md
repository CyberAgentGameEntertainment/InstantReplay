# Disk-Buffered Recording and Crash Recovery

This document describes the design of the opt-in disk buffer for `RealtimeInstantReplaySession`
(issue #39).

## Motivation

`RealtimeInstantReplaySession` keeps encoded frames in a bounded in-memory ring buffer
(`BoundedEncodedFrameBuffer`, 20 MiB by default). Two limitations follow from this:

1. The buffer competes with the application for memory, which constrains the recordable duration on
   memory-limited devices.
2. The buffer is lost when the process terminates abnormally, so the footage leading up to a crash
   cannot be examined.

The disk buffer addresses both by writing encoded frames to persistent storage as they are produced,
and by allowing a later process to reconstruct an MP4 file from the frames a crashed process left
behind.

The trade-off is an increase in storage traffic, which shortens the lifespan of flash memory.
Because the feature is intended primarily for development and quality-assurance builds, it is
disabled by default and must be enabled explicitly.

## Scope

The feature is implemented entirely in the C# layer. No change to the native library (UniEnc) is
required, because the payload the C# layer receives from the encoder is already a self-contained
byte sequence:

- `UniEnc.EncodedFrame` holds an opaque payload, a timestamp, and a sample kind.
- The payload is a `bincode` serialization of the platform's encoded-data type, produced by the
  encoder and consumed by the muxer (`unienc_muxer_push_video` and `unienc_muxer_push_audio` decode
  it again).
- The serialized form contains no pointers or process-local handles. On Apple platforms, for
  example, `CMSampleBuffer` is converted to a plain structure holding H.264 parameter sets, timing
  information, and sample bytes.

A payload written by one process can therefore be pushed into a muxer created by another process,
provided both processes run the same native library on the same platform. This premise is verified
end to end by the automated checks described under **Verification**.

## 1. On-Disk Format

### Directory layout

```
<root>/
  <session-id>/
    manifest.json
    metadata.irb
    seg-00000000.irb
    seg-00000001.irb
    ...
```

`<root>` defaults to `Application.temporaryCachePath/InstantReplay/DiskBuffer`. `<session-id>` is
`yyyyMMdd_HHmmssfff` followed by a process-wide counter, which keeps the identifier unique across
sessions created within the same millisecond.

### Record structure

Segment files and the metadata file share one record format. Records are appended without padding:

| Offset | Size | Field           | Description                                                  |
| -----: | ---: | --------------- | ------------------------------------------------------------ |
|      0 |    4 | `payloadLength` | Payload length in bytes, little-endian                        |
|      4 |    1 | `track`         | `0` video, `1` audio                                          |
|      5 |    1 | `kind`          | `UniencSampleKind`: `0` interpolated, `1` key, `2` metadata   |
|      6 |    2 | `reserved`      | Written as zero                                               |
|      8 |    8 | `timestamp`     | Presentation timestamp in seconds, IEEE 754 double            |
|     16 |    4 | `crc32`         | CRC-32 (IEEE 802.3 polynomial) over the payload               |
|     20 |  var | `payload`       | The `EncodedFrame` payload verbatim                           |

Each file begins with a 16-byte header holding the magic value `IRSG`, the format version, and the
segment index (`0xFFFFFFFF` for the metadata file).

Integers are read and written with explicit shifts rather than `BinaryPrimitives`, and the code
contains no conditional compilation, so the same source compiles under every API compatibility
level the package supports.

### Segment rotation

A segment is closed and a new one opened when the record about to be written is a video key frame
**and** either the segment has covered `SegmentDuration` seconds or it has reached
`MaxSegmentBytes`.

Requiring a key frame makes each segment independently decodable from its first video record, so
discarding an older segment never leaves a partial group of pictures at the head of the buffer. The
size condition is a safety valve. With the one-second IDR interval the encoders currently produce,
the overshoot beyond the target is bounded by approximately one second.

### Codec configuration

Records whose kind is `UniencSampleKind.Metadata` carry codec configuration: H.264 parameter sets,
the AAC `AudioSpecificConfig`, or the platform equivalent. They are emitted once at the start of the
stream and are required in order to mux any later frame.

They are therefore written to `metadata.irb`, which is never evicted, rather than into a segment.
Keeping them only in memory would make recovery impossible on every platform that emits them, which
is the defect this design exists to avoid: `unienc_android_mc`, `unienc_windows_mf`, and
`unienc_ffmpeg` all produce metadata records, while `unienc_apple_vt` and `unienc_webcodecs` do not,
because on Apple platforms the parameter sets travel inside every sample. The reader treats a
missing or empty metadata file as normal rather than as an error, so both kinds of platform recover
correctly.

Only distinct payloads are stored. A platform that reissued the same configuration repeatedly would
otherwise erode the space reserved for segments. The metadata file is additionally capped at 1 MiB.

Repeating the configuration at the head of every segment was considered and rejected: the encoder
does not reissue it, so it would have to be cached and rewritten, which gains nothing over a single
unevictable file.

### Session manifest

`manifest.json` is written once, before any frame is accepted, and is flushed to the storage device
immediately. It records what a later process needs in order to build a compatible muxer:

```json
{
  "formatVersion": 2,
  "startedAtUtc": "2026-08-19T05:00:00.0000000Z",
  "platform": "Android",
  "unityVersion": "2022.3.0f1",
  "applicationVersion": "1.0.0",
  "videoWidth": 1280,
  "videoHeight": 720,
  "videoFpsHint": 30,
  "videoBitrate": 2500000,
  "audioSampleRate": 44100,
  "audioChannels": 2,
  "audioBitrate": 128000
}
```

The encoder options are recorded because `EncodingSystem` requires them and creating the muxer
requires an `EncodingSystem`. Creating one is inexpensive: the native constructor only stores the
options, and the platform encoders are instantiated lazily by `CreateVideoEncoder` and
`CreateAudioEncoder`, which the recovery path never calls.

The document is a flat object of strings and numbers, serialized and parsed by hand rather than with
`UnityEngine.JsonUtility`. This keeps the whole storage layer free of any dependency on UnityEngine,
which is what allows it to be tested outside the Unity Editor. A nested document is rejected rather
than partially accepted.

## 2. The Size Limit Is a Hard Bound

`MaxDiskUsageBytes` bounds the total size of the session directory — the manifest, the metadata
file, and every segment — and it is a bound rather than a target.

The guarantee is upheld by reserving space before writing rather than trimming afterwards. Before
each record is appended, and before each new segment file is created, the writer computes the size
the operation would produce and deletes the oldest closed segments until the result fits. When the
result still does not fit, the record is dropped and the write does not happen. The directory
therefore never exceeds the limit at any instant, not even transiently between a write and a
subsequent eviction.

Rotation is ordered so that the buffer cannot deadlock. The open segment is closed first, which
makes it evictable, and only then is space reserved for the new segment. Without this ordering, a
single open segment that had grown to fill the budget could never be reclaimed and every subsequent
record would be dropped forever.

Two consequences follow and are documented on the option itself:

- Setting `MaxDiskUsageBytes` close to `MaxSegmentBytes` degrades the recording rather than the
  retention, because the writer prefers dropping records to exceeding the bound. `Validate` rejects
  a limit below 4 MiB and a limit smaller than `MaxSegmentBytes`.
- The metadata file and the manifest are counted against the limit and are never evicted, so they
  reduce the space available to segments. Together they occupy a few kilobytes.

An alternative was considered in which the limit applied only to segment files. It was rejected
because it would make the number the user sets differ from the amount of storage the feature
actually consumes, which is the property the user is trying to control.

## 3. Durability

### Flush policy

Two failure modes are distinguished and treated differently:

- **Process termination** — a native fault, an out-of-memory kill, or an abort. Data that has
  reached the operating system through a write survives this, because the kernel owns the page cache
  and flushes it independently of the process. Only data still held in the user-space `FileStream`
  buffer is lost.
- **Power loss or a kernel panic** — data survives only if it has been flushed to the storage device.

Recovering the footage that precedes a crash targets the first mode. Defending against it costs no
additional device writes, because it requires only a write system call. Defending against the second
requires a device flush, which multiplies the erase cycles the storage device performs.

The default policy, `DiskBufferSyncMode.OperatingSystem`, is therefore:

- After every batch drained from the write queue, the open segment is flushed to the operating
  system and no further.
- When a segment is closed, it is flushed to the storage device. This bounds the exposure to power
  loss to roughly one segment while keeping device flushes to approximately one every five seconds,
  rather than the thirty to forty per second a per-record flush would cause.
- The manifest and every codec configuration record are flushed to the storage device immediately.
  They are written a handful of times per session and the buffer is worthless without them.

`DiskBufferSyncMode.EveryRecord` flushes every record to the device. It is documented as intended
for diagnosing storage-layer problems rather than for routine use, because of its effect on flash
wear.

### Recovering a torn file

A process killed mid-write leaves a truncated final record. Because records are appended and never
rewritten in place, a truncation can only occur at the end of a file. The reader detects it without
a separate journal:

1. Read the file header. If the magic value or the format version does not match, discard the file.
2. Read a 20-byte record header. If fewer than 20 bytes remain, stop.
3. If `payloadLength` is negative, exceeds the implementation limit, or exceeds the bytes remaining
   in the file, stop. The same applies to an out-of-range track or kind, or a non-finite timestamp.
4. Accept the record and continue.

Scanning reads headers only and seeks over payloads, so it does not have to read the whole buffer.
The checksum is verified when a payload is read for muxing; a record whose checksum does not match
truncates the stream there, because a decoder cannot proceed past a corrupt sample. Scanning stops
at the first record that cannot be complete, rather than skipping it and continuing, so a stream
with a hole in the middle is never produced.

## 4. Recovery API

```csharp
public sealed class DiskEncodedFrameBufferRecovery
{
    public static bool TryGetRecoverable(string storagePath, out DiskEncodedFrameBufferRecovery recovery);
    public static IReadOnlyList<DiskEncodedFrameBufferRecovery> FindRecoverable(string rootDirectory = null);

    public string StoragePath { get; }
    public DateTime StartedAtUtc { get; }
    public string Platform { get; }
    public string ApplicationVersion { get; }
    public bool IsCompatible { get; }
    public long SizeBytes { get; }

    public ValueTask<string> ExportAsync(double? durationSeconds = null, string outputPath = null);
    public void Delete();
}
```

A session that is disposed normally removes its own directory, so every directory that remains
denotes an abnormal termination. `FindRecoverable` enumerates them; several may be present when the
application has crashed more than once, and the caller decides which to export and which to delete.

Recovery never deletes anything implicitly. `Delete` must be called explicitly, so that a failed
export can be retried and the raw directory can still be retrieved from the device. An earlier
design in which `Dispose` deleted the directory was rejected: a session left behind by a crash is
the only copy of the footage that preceded it, and destroying it as a side effect of a failed export
defeats the purpose of the feature.

`IsCompatible` reports whether the format version and the platform match the running build. These
conditions are necessary but not sufficient. The payload is a `bincode` serialization of a
platform-specific structure belonging to the native library, and that structure may change between
package versions without any signal the C# layer can observe. `ExportAsync` therefore proceeds with
a warning rather than refusing, and a genuine mismatch surfaces as a decode failure from the muxer.
Refusing whenever the version differed was rejected because it would make recovery useless after any
package update, including updates that do not touch the serialization.

`ExportAsync` selects frames and muxes them through `EncodedFrameMuxer`, the same helper
`RealtimeInstantReplaySession` uses. Both therefore follow the completion protocol the muxer
requires, in which `FinishVideoAsync` and `FinishAudioAsync` are called even after a push has
failed, because that is where the muxer reports the underlying error.

## 5. Opt-In API

```csharp
public struct RealtimeEncodingOptions
{
    // ... existing members ...
    public DiskBufferOptions? DiskBuffer { get; set; }
}

public struct DiskBufferOptions
{
    public string Directory { get; set; }
    public long MaxDiskUsageBytes { get; set; }
    public double SegmentDuration { get; set; }
    public long MaxSegmentBytes { get; set; }
    public long MaxPendingWriteBytes { get; set; }
    public bool RetainOnDispose { get; set; }
    public DiskBufferSyncMode SyncMode { get; set; }

    public static ref readonly DiskBufferOptions Default { get; }
}
```

`DiskBuffer` is null by default, so existing behaviour is unchanged. When it is set,
`MaxMemoryUsageBytesForCompressedFrames` is not used.

| Member                 | Default                                                    |
| ---------------------- | ---------------------------------------------------------- |
| `Directory`            | `Application.temporaryCachePath/InstantReplay/DiskBuffer`   |
| `MaxDiskUsageBytes`    | 256 MiB                                                    |
| `SegmentDuration`      | 5 seconds                                                  |
| `MaxSegmentBytes`      | 8 MiB                                                      |
| `MaxPendingWriteBytes` | 4 MiB                                                      |
| `RetainOnDispose`      | `false`                                                    |
| `SyncMode`             | `DiskBufferSyncMode.OperatingSystem`                       |

`Application.temporaryCachePath` is chosen because it is writable on every supported platform, is
excluded from backup on iOS, and is not visible to the user, so a leftover buffer does not appear as
clutter in the user's file browser.

## 6. Relationship to the In-Memory Buffer

When `DiskBuffer` is set, the disk buffer replaces the in-memory buffer rather than augmenting it.
Encoded payloads are held in memory only while they sit in the write queue.

Both implementations satisfy `IEncodedFrameBuffer`, so the pipeline construction in
`RealtimeInstantReplaySession` differs only in which implementation it instantiates.

Frame selection — locating the key frame nearest to `latest - duration`, aligning the audio start,
rebasing timestamps to zero, and prepending the codec configuration — is identical for both and is
implemented once in `EncodedFrameSelector`. The disk implementation reads its records back from the
files rather than keeping a parallel in-memory index, so the live export path and the crash-recovery
path run the same reader and the same selection, and every export exercises the recovery code.

## 7. I/O Threading

Encoder output is delivered on a background thread by `VideoEncoderInput` and `AudioEncoderInput`.
Writing synchronously on that thread would couple the encoder drain loop to storage latency, so
writes are handed to a queue drained by one dedicated thread.

The queue is bounded by the total payload size of its entries, capped at `MaxPendingWriteBytes`.
When the bound is reached the incoming frame is dropped and a warning is logged once, rather than
blocking the encoder. This follows the behaviour `DroppingChannelInput` already applies to raw
frames. A drop leaves a gap in the stream, and playback may show artefacts until the next key frame;
the alternative, blocking the encoder, would stall capture and drop frames further upstream anyway.

Frames are queued as `EncodedFrame` values, which already own pooled arrays, and the writer combines
the record header and the payload in a scratch buffer that is grown once and reused. No allocation
occurs per frame on either the producer or the writer side.

Shutdown order, on both `Dispose` and export:

1. Stop accepting new frames.
2. Complete the queue and join the writer thread, so every accepted frame reaches a file.
3. Flush the open segment to the storage device and close it.
4. For export, read the files back and select frames.
5. On `Dispose`, delete the session directory unless `RetainOnDispose` is set. After a successful
   export the directory is deleted, because the exported file supersedes it.

## 8. Verification

The storage layer has no dependency on UnityEngine, so it is compiled directly into
`InstantReplay.Externals/src/InstantReplay.DiskBuffer.Tests` and exercised without the Unity Editor:

```bash
cd InstantReplay.Externals/src/InstantReplay.DiskBuffer.Tests
dotnet run
```

The project links the storage-layer sources rather than copying them, so a UnityEngine reference
added to one of those files breaks the build, which is the intended guard.

The checks cover the record round trip, recovery from a file truncated in the middle of a payload
and from one truncated in the middle of a record header, detection of a corrupt payload by checksum,
the hard bound on disk usage under sustained eviction, survival of the codec configuration across
eviction of every segment, deduplication of repeated configuration, the key-frame alignment of
segment boundaries, the manifest round trip, and frame selection.

The final check is an end-to-end run: it drives the real platform encoder, persists every encoded
frame through the disk buffer, reads the buffer back from the files and the manifest alone, and
muxes the result. It is skipped when the native library for the running platform is absent. On macOS
it produces a valid MP4 that `ffprobe` reports as H.264 with the expected frame count alongside an
AAC track, and that `ffmpeg` decodes without error. This is the check that validates the premise of
the feature.

## 9. Risks and Open Questions

**Storage-device wear.** The default flush policy avoids device-level flushes except at segment
boundaries, but the data itself is still written. At the default bitrate of 2.5 Mbps the buffer
writes roughly 320 KiB per second, or about 1.1 GiB per hour. Continuous use over the lifetime of a
product is not advisable, which is why the feature is disabled by default and documented as intended
for development and quality-assurance builds.

**Platform differences.** `Application.temporaryCachePath` resolves to the application's cache
directory on every supported platform, so no permission is required and no additional Android
manifest entry is needed. The operating system may reclaim the cache directory when storage runs
low, so a session directory can disappear between the crash and the recovery attempt;
`FindRecoverable` simply will not list it. Writing to external storage on Android was not chosen,
because it would require a runtime permission and would make the data visible to the user. WebGL is
not supported, because it has neither a persistent filesystem by default nor threads.

**Free-space exhaustion.** `MaxDiskUsageBytes` bounds the buffer, but the device may run out of space
for other reasons. A write failure is reported and the frame is dropped; the session continues
recording so that the application is not disrupted, and sessions already written remain recoverable.

**Relationship to `UnboundedRecordingSession`.** That session writes an MP4 continuously through the
muxer, so it does not benefit from a replay buffer and is not covered here. It is not crash-resilient
either: an MP4 whose `moov` box was never written is not playable. Making it crash-resilient requires
either fragmented MP4 output or a repair pass, and is out of scope.

**Relationship to the legacy mode.** `InstantReplaySession` writes JPEG frames and PCM audio to disk
and transcodes them on export. Its intermediate files survive a crash, but it has no recovery entry
point, its intermediate representation is far larger than an encoded stream, and its export latency
is high. The disk buffer supersedes that approach for the realtime pipeline; the legacy mode is left
unchanged.

**Payload compatibility across package versions.** Discussed in section 4. There is no mechanism by
which the C# layer can detect that the native serialization has changed. If this becomes a practical
problem, a version constant exported from the native library through the FFI and recorded in the
manifest would resolve it, at the cost of a native change.
