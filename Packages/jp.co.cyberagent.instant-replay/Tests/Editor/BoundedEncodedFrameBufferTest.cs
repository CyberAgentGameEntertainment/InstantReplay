// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

using System;
using System.Linq;
using NUnit.Framework;
using UniEnc;

namespace InstantReplay.Editor.Tests
{
    public sealed class BoundedEncodedFrameBufferTest
    {
        // Frame diagram legend:
        //   [K10 @1] means keyframe with Data[0] == 10 and Timestamp == 1.
        //   K = keyframe, I = interpolated frame, M = metadata frame.
        //   Output timestamps are normalized after GetFramesForDuration returns.

        [Test]
        public void GetFramesForDuration_WithNullDuration_ReturnsFramesFromFirstKeyframe()
        {
            using var buffer = new BoundedEncodedFrameBuffer(1024);

            // Null duration should keep every frame from the first keyframe onward.

            // Frames:
            //   | Track | Input     | Note           | Output    |
            //   |-------|-----------|----------------|-----------|
            //   | Video | [I10 @0]  | before key     |           |
            //   | Video | [K11 @1]  | selected start | [K11 @0]  |
            //   | Video | [I12 @2]  |                | [I12 @1]  |
            Assert.That(buffer.TryAddVideoFrame(Frame(0, UniencSampleKind.Interpolated, 10, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(1, UniencSampleKind.Key, 11, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(2, UniencSampleKind.Interpolated, 12, 4)), Is.True);

            buffer.GetFramesForDuration(null, out var videoFramesMemory, out var audioFramesMemory);
            var videoFrames = videoFramesMemory.ToArray();
            var audioFrames = audioFramesMemory.ToArray();

            try
            {
                Assert.That(videoFrames.Select(frame => frame.Data[0]).ToArray(), Is.EqualTo(new[] { 11, 12 }));
                Assert.That(videoFrames.Select(frame => frame.Timestamp).ToArray(), Is.EqualTo(new[] { 0.0, 1.0 }));
                Assert.That(audioFrames, Is.Empty);
            }
            finally
            {
                foreach (var frame in videoFrames) frame.Dispose();
                foreach (var frame in audioFrames) frame.Dispose();
            }
        }

        [Test]
        public void GetFramesForDuration_WithDuration_ReturnsFramesFromClosestKeyframe()
        {
            using var buffer = new BoundedEncodedFrameBuffer(1024);

            // Duration trimming should snap the start to the closest available keyframe.

            // Frames:
            //   | Track | Input     | Note           | Output    |
            //   |-------|-----------|----------------|-----------|
            //   | Video | [K10 @0]  |                |           |
            //   | Video | [I11 @1]  |                |           |
            //   | Video | [K12 @4]  | selected start | [K12 @0]  |
            //   | Video | [I13 @5]  |                | [I13 @1]  |
            //   | Video | [K14 @8]  |                | [K14 @4]  |
            //   | Video | [I15 @10] | latest         | [I15 @6]  |
            //   Duration: 6s, ideal start: 4s.
            Assert.That(buffer.TryAddVideoFrame(Frame(0, UniencSampleKind.Key, 10, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(1, UniencSampleKind.Interpolated, 11, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(4, UniencSampleKind.Key, 12, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(5, UniencSampleKind.Interpolated, 13, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(8, UniencSampleKind.Key, 14, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(10, UniencSampleKind.Interpolated, 15, 4)), Is.True);

            buffer.GetFramesForDuration(6, out var videoFramesMemory, out var audioFramesMemory);
            var videoFrames = videoFramesMemory.ToArray();
            var audioFrames = audioFramesMemory.ToArray();

            try
            {
                Assert.That(videoFrames.Select(frame => frame.Data[0]).ToArray(),
                    Is.EqualTo(new[] { 12, 13, 14, 15 }));
                Assert.That(videoFrames.Select(frame => frame.Timestamp).ToArray(),
                    Is.EqualTo(new[] { 0.0, 1.0, 4.0, 6.0 }));
                Assert.That(audioFrames, Is.Empty);
            }
            finally
            {
                foreach (var frame in videoFrames) frame.Dispose();
                foreach (var frame in audioFrames) frame.Dispose();
            }
        }

        [Test]
        public void GetFramesForDuration_WithoutKeyframe_ReturnsEmptyFrames()
        {
            using var buffer = new BoundedEncodedFrameBuffer(1024);

            // Encoded video cannot be replayed safely without a keyframe start.

            // Frames:
            //   | Track | Input    | Note        | Output |
            //   |-------|----------|-------------|--------|
            //   | Video | [I10 @0] | no keyframe |        |
            //   | Video | [I11 @1] | no keyframe |        |
            Assert.That(buffer.TryAddVideoFrame(Frame(0, UniencSampleKind.Interpolated, 10, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(1, UniencSampleKind.Interpolated, 11, 4)), Is.True);

            buffer.GetFramesForDuration(null, out var videoFramesMemory, out var audioFramesMemory);
            var videoFrames = videoFramesMemory.ToArray();
            var audioFrames = audioFramesMemory.ToArray();

            Assert.That(videoFrames, Is.Empty);
            Assert.That(audioFrames, Is.Empty);
        }

        [Test]
        public void GetFramesForDuration_AlignsAudioStartToSelectedVideoDuration()
        {
            using var buffer = new BoundedEncodedFrameBuffer(1024);

            // Audio should start near the selected video range and normalize independently.

            // Frames:
            //   | Track | Input     | Note                 | Output    |
            //   |-------|-----------|----------------------|-----------|
            //   | Video | [K10 @10] | selected video start | [K10 @0]  |
            //   | Video | [I11 @14] | latest video         | [I11 @4]  |
            //   | Audio | [I20 @6]  | before audio start   |           |
            //   | Audio | [I21 @8]  | selected audio start | [I21 @0]  |
            //   | Audio | [I22 @10] |                      | [I22 @2]  |
            //   | Audio | [I23 @12] |                      | [I23 @4]  |
            Assert.That(buffer.TryAddVideoFrame(Frame(10, UniencSampleKind.Key, 10, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(14, UniencSampleKind.Interpolated, 11, 4)), Is.True);
            Assert.That(buffer.TryAddAudioFrame(Frame(6, UniencSampleKind.Interpolated, 20, 4)), Is.True);
            Assert.That(buffer.TryAddAudioFrame(Frame(8, UniencSampleKind.Interpolated, 21, 4)), Is.True);
            Assert.That(buffer.TryAddAudioFrame(Frame(10, UniencSampleKind.Interpolated, 22, 4)), Is.True);
            Assert.That(buffer.TryAddAudioFrame(Frame(12, UniencSampleKind.Interpolated, 23, 4)), Is.True);

            buffer.GetFramesForDuration(null, out var videoFramesMemory, out var audioFramesMemory);
            var videoFrames = videoFramesMemory.ToArray();
            var audioFrames = audioFramesMemory.ToArray();

            try
            {
                Assert.That(videoFrames.Select(frame => frame.Data[0]).ToArray(), Is.EqualTo(new[] { 10, 11 }));
                Assert.That(videoFrames.Select(frame => frame.Timestamp).ToArray(), Is.EqualTo(new[] { 0.0, 4.0 }));
                Assert.That(audioFrames.Select(frame => frame.Data[0]).ToArray(), Is.EqualTo(new[] { 21, 22, 23 }));
                Assert.That(audioFrames.Select(frame => frame.Timestamp).ToArray(),
                    Is.EqualTo(new[] { 0.0, 2.0, 4.0 }));
            }
            finally
            {
                foreach (var frame in videoFrames) frame.Dispose();
                foreach (var frame in audioFrames) frame.Dispose();
            }
        }

        [Test]
        public void GetFramesForDuration_PrependsMetadataOnFirstDrainAndClearsItAfterwards()
        {
            using var buffer = new BoundedEncodedFrameBuffer(1024);

            // Codec metadata must prefix the first drain, then be consumed with that drain.

            // Frames:
            //   | Drain  | Track | Input     | Note           | Output    |
            //   |--------|-------|-----------|----------------|-----------|
            //   | First  | Video | [M100 @0] | metadata       | [M100 @0] |
            //   | First  | Video | [K10 @1]  | selected start | [K10 @0]  |
            //   | First  | Audio | [M200 @0] | metadata       | [M200 @0] |
            //   | First  | Audio | [I20 @1]  | selected start | [I20 @0]  |
            //   | Second | Video | [K11 @2]  | metadata spent | [K11 @0]  |
            Assert.That(buffer.TryAddVideoFrame(Frame(0, UniencSampleKind.Metadata, 100, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(1, UniencSampleKind.Key, 10, 4)), Is.True);
            Assert.That(buffer.TryAddAudioFrame(Frame(0, UniencSampleKind.Metadata, 200, 4)), Is.True);
            Assert.That(buffer.TryAddAudioFrame(Frame(1, UniencSampleKind.Interpolated, 20, 4)), Is.True);

            buffer.GetFramesForDuration(null, out var firstVideoFramesMemory, out var firstAudioFramesMemory);
            var firstVideoFrames = firstVideoFramesMemory.ToArray();
            var firstAudioFrames = firstAudioFramesMemory.ToArray();

            try
            {
                Assert.That(firstVideoFrames.Select(frame => frame.Kind).ToArray(),
                    Is.EqualTo(new[] { UniencSampleKind.Metadata, UniencSampleKind.Key }));
                Assert.That(firstVideoFrames.Select(frame => frame.Data[0]).ToArray(),
                    Is.EqualTo(new[] { 100, 10 }));
                Assert.That(firstAudioFrames.Select(frame => frame.Kind).ToArray(),
                    Is.EqualTo(new[] { UniencSampleKind.Metadata, UniencSampleKind.Interpolated }));
                Assert.That(firstAudioFrames.Select(frame => frame.Data[0]).ToArray(),
                    Is.EqualTo(new[] { 200, 20 }));
            }
            finally
            {
                foreach (var frame in firstVideoFrames) frame.Dispose();
                foreach (var frame in firstAudioFrames) frame.Dispose();
            }

            Assert.That(buffer.TryAddVideoFrame(Frame(2, UniencSampleKind.Key, 11, 4)), Is.True);

            buffer.GetFramesForDuration(null, out var videoFramesMemory, out var audioFramesMemory);
            var videoFrames = videoFramesMemory.ToArray();
            var audioFrames = audioFramesMemory.ToArray();

            try
            {
                Assert.That(videoFrames.Select(frame => frame.Kind).ToArray(),
                    Is.EqualTo(new[] { UniencSampleKind.Key }));
                Assert.That(videoFrames.Select(frame => frame.Data[0]).ToArray(), Is.EqualTo(new[] { 11 }));
                Assert.That(audioFrames, Is.Empty);
            }
            finally
            {
                foreach (var frame in videoFrames) frame.Dispose();
                foreach (var frame in audioFrames) frame.Dispose();
            }
        }

        [Test]
        public void TryAddVideoFrame_WhenMemoryLimitExceeded_DropsOldestVideoFrames()
        {
            using var buffer = new BoundedEncodedFrameBuffer(8);

            // A third 4-byte video frame should evict the oldest frame from an 8-byte buffer.

            // Frames:
            //   | Track | Input    | Note    | Output    |
            //   |-------|----------|---------|-----------|
            //   | Video | [K10 @0] | evicted |           |
            //   | Video | [K11 @1] | kept    | [K11 @0]  |
            //   | Video | [K12 @2] | kept    | [K12 @1]  |
            Assert.That(buffer.TryAddVideoFrame(Frame(0, UniencSampleKind.Key, 10, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(1, UniencSampleKind.Key, 11, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(2, UniencSampleKind.Key, 12, 4)), Is.True);

            buffer.GetFramesForDuration(null, out var videoFramesMemory, out var audioFramesMemory);
            var videoFrames = videoFramesMemory.ToArray();
            var audioFrames = audioFramesMemory.ToArray();

            try
            {
                Assert.That(videoFrames.Select(frame => frame.Data[0]).ToArray(), Is.EqualTo(new[] { 11, 12 }));
                Assert.That(audioFrames, Is.Empty);
            }
            finally
            {
                foreach (var frame in videoFrames) frame.Dispose();
                foreach (var frame in audioFrames) frame.Dispose();
            }
        }

        [Test]
        public void TryAddFrames_WhenMemoryLimitExceeded_DropsOldestFrameAcrossAudioAndVideo()
        {
            using var buffer = new BoundedEncodedFrameBuffer(12);

            // Eviction should compare audio and video timestamps instead of preferring one queue.

            // Frames:
            //   | Track | Input     | Note                 | Output    |
            //   |-------|-----------|----------------------|-----------|
            //   | Audio | [I20 @9]  | oldest, evicted      |           |
            //   | Video | [K10 @10] | selected video start | [K10 @0]  |
            //   | Video | [K11 @11] |                      | [K11 @1]  |
            //   | Audio | [I21 @12] | kept                 | [I21 @0]  |
            Assert.That(buffer.TryAddVideoFrame(Frame(10, UniencSampleKind.Key, 10, 4)), Is.True);
            Assert.That(buffer.TryAddAudioFrame(Frame(9, UniencSampleKind.Interpolated, 20, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(11, UniencSampleKind.Key, 11, 4)), Is.True);
            Assert.That(buffer.TryAddAudioFrame(Frame(12, UniencSampleKind.Interpolated, 21, 4)), Is.True);

            buffer.GetFramesForDuration(null, out var videoFramesMemory, out var audioFramesMemory);
            var videoFrames = videoFramesMemory.ToArray();
            var audioFrames = audioFramesMemory.ToArray();

            try
            {
                Assert.That(videoFrames.Select(frame => frame.Data[0]).ToArray(), Is.EqualTo(new[] { 10, 11 }));
                Assert.That(audioFrames.Select(frame => frame.Data[0]).ToArray(), Is.EqualTo(new[] { 21 }));
            }
            finally
            {
                foreach (var frame in videoFrames) frame.Dispose();
                foreach (var frame in audioFrames) frame.Dispose();
            }
        }

        [Test]
        public void GetFramesForDuration_AfterDrainingBuffer_DoesNotKeepCountingReturnedFrames()
        {
            // The 8-byte limit holds exactly two 4-byte frames, so a stale memory count
            // from the first drain would make the second pair look over capacity.
            using var buffer = new BoundedEncodedFrameBuffer(8);

            // Frames:
            //   | Drain  | Track | Input    | Note                | Output    |
            //   |--------|-------|----------|---------------------|-----------|
            //   | First  | Video | [K10 @0] | drained             | [K10 @0]  |
            //   | First  | Video | [K11 @1] | drained             | [K11 @1]  |
            //   | Second | Video | [K12 @2] | should still fit    | [K12 @0]  |
            //   | Second | Video | [K13 @3] | should still fit    | [K13 @1]  |
            Assert.That(buffer.TryAddVideoFrame(Frame(0, UniencSampleKind.Key, 10, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(1, UniencSampleKind.Key, 11, 4)), Is.True);

            // Draining transfers ownership of the returned frames to the caller.
            // Disposing them here mirrors the production contract and leaves the buffer empty.
            buffer.GetFramesForDuration(null, out var drainedVideoFramesMemory, out var drainedAudioFramesMemory);
            foreach (var frame in drainedVideoFramesMemory.ToArray()) frame.Dispose();
            foreach (var frame in drainedAudioFramesMemory.ToArray()) frame.Dispose();

            // These frames should fit because the drained frames must no longer count
            // against the buffer's memory bound.
            Assert.That(buffer.TryAddVideoFrame(Frame(2, UniencSampleKind.Key, 12, 4)), Is.True);
            Assert.That(buffer.TryAddVideoFrame(Frame(3, UniencSampleKind.Key, 13, 4)), Is.True);

            buffer.GetFramesForDuration(null, out var videoFramesMemory, out var audioFramesMemory);
            var videoFrames = videoFramesMemory.ToArray();
            var audioFrames = audioFramesMemory.ToArray();

            try
            {
                Assert.That(videoFrames.Select(frame => frame.Data[0]).ToArray(), Is.EqualTo(new[] { 12, 13 }));
                Assert.That(audioFrames, Is.Empty);
            }
            finally
            {
                foreach (var frame in videoFrames) frame.Dispose();
                foreach (var frame in audioFrames) frame.Dispose();
            }
        }

        [Test]
        public void TryAddFrame_AfterDispose_ReturnsFalse()
        {
            var buffer = new BoundedEncodedFrameBuffer(1024);
            buffer.Dispose();

            // Adding after disposal should reject ownership so the caller can dispose of the frame.

            // Frames:
            //   | Track | Input    | Note             | Output |
            //   |-------|----------|------------------|--------|
            //   | Video | [K10 @0] | rejected by add  |        |
            var frame = Frame(0, UniencSampleKind.Key, 10, 4);

            try
            {
                Assert.That(buffer.TryAddVideoFrame(frame), Is.False);
            }
            finally
            {
                frame.Dispose();
            }
        }

        [Test]
        public void GetFramesForDuration_AfterDispose_ThrowsObjectDisposedException()
        {
            var buffer = new BoundedEncodedFrameBuffer(1024);
            buffer.Dispose();

            // Draining after disposal should fail loudly instead of returning partial state.

            // Frames:
            //   | Track | Input | Note                | Output |
            //   |-------|-------|---------------------|--------|
            //   | Video |       | drain after dispose | throws |
            Assert.Throws<ObjectDisposedException>(() =>
                buffer.GetFramesForDuration(null, out _, out _));
        }

        private static EncodedFrame Frame(double timestamp, UniencSampleKind kind, byte firstByte, int length)
        {
            var data = Enumerable.Repeat(firstByte, length).ToArray();
            return EncodedFrame.CreateWithCopy(data, timestamp, kind);
        }
    }
}
