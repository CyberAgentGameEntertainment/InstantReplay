//! Declarations of every Android Java API member this crate calls through JNI.
//!
//! Each declaration is verified at compile time against the platform metadata vendored in
//! `java-api/android-api-versions.txt`; see [`crate::java_api`] for the rationale and for how the
//! `#[api(N)]` annotations are enforced.
//!
//! Adding a call to a new member means adding it here. Adding a call to a member of a class that is
//! not listed yet additionally requires adding the class to `java-api/classes.txt` and running
//! `cargo run -p android_api_metadata -- --classes crates/unienc_android_mc/java-api/classes.txt
//! --out crates/unienc_android_mc/java-api/android-api-versions.txt`.

use java_api_macros::java_api;

java_api! {
    metadata = "java-api/android-api-versions.txt";
    // Keep in sync with `crate::java_api::MIN_API_LEVEL`.
    min_api = 26;
    runtime = crate::java_api;

    class MediaCodec = "android/media/MediaCodec" {
        static fn create_encoder_by_type(mime_type) =
            "createEncoderByType(Ljava/lang/String;)Landroid/media/MediaCodec;";
        fn configure(format, surface, crypto, flags) =
            "configure(Landroid/media/MediaFormat;Landroid/view/Surface;Landroid/media/MediaCrypto;I)V";
        fn start() = "start()V";
        fn stop() = "stop()V";
        fn release() = "release()V";
        fn dequeue_input_buffer(timeout_us) = "dequeueInputBuffer(J)I";
        fn get_input_buffer(index) = "getInputBuffer(I)Ljava/nio/ByteBuffer;";
        fn get_input_image(index) = "getInputImage(I)Landroid/media/Image;";
        fn queue_input_buffer(index, offset, size, presentation_time_us, flags) =
            "queueInputBuffer(IIIJI)V";
        fn dequeue_output_buffer(buffer_info, timeout_us) =
            "dequeueOutputBuffer(Landroid/media/MediaCodec$BufferInfo;J)I";
        fn get_output_buffer(index) = "getOutputBuffer(I)Ljava/nio/ByteBuffer;";
        fn release_output_buffer(index, render) = "releaseOutputBuffer(IZ)V";
        fn get_output_format() = "getOutputFormat()Landroid/media/MediaFormat;";
        fn create_input_surface() = "createInputSurface()Landroid/view/Surface;";
        fn signal_end_of_input_stream() = "signalEndOfInputStream()V";
        fn get_codec_info() = "getCodecInfo()Landroid/media/MediaCodecInfo;";
        fn get_metrics() = "getMetrics()Landroid/os/PersistableBundle;";
    }

    class BufferInfo = "android/media/MediaCodec$BufferInfo" {
        fn new() = "<init>()V";
        field offset = "offset:I";
        field size = "size:I";
        field flags = "flags:I";
        field presentation_time_us = "presentationTimeUs:J";
    }

    class MediaCodecInfo = "android/media/MediaCodecInfo" {
        fn get_name() = "getName()Ljava/lang/String;";
        #[api(29)]
        fn get_canonical_name() = "getCanonicalName()Ljava/lang/String;";
        #[api(29)]
        fn is_hardware_accelerated() = "isHardwareAccelerated()Z";
    }

    class MediaFormat = "android/media/MediaFormat" {
        fn new() = "<init>()V";
        static fn create_video_format(mime_type, width, height) =
            "createVideoFormat(Ljava/lang/String;II)Landroid/media/MediaFormat;";
        static fn create_audio_format(mime_type, sample_rate, channel_count) =
            "createAudioFormat(Ljava/lang/String;II)Landroid/media/MediaFormat;";
        fn set_integer(key, value) = "setInteger(Ljava/lang/String;I)V";
        fn set_long(key, value) = "setLong(Ljava/lang/String;J)V";
        fn set_float(key, value) = "setFloat(Ljava/lang/String;F)V";
        fn set_string(key, value) = "setString(Ljava/lang/String;Ljava/lang/String;)V";
        fn set_byte_buffer(key, value) = "setByteBuffer(Ljava/lang/String;Ljava/nio/ByteBuffer;)V";
        fn contains_key(key) = "containsKey(Ljava/lang/String;)Z";

        // The getters throw when the key is absent or holds a value of another type. On API levels
        // below 29 that is the only way to find out, so the exception must not be logged.
        #[may_throw]
        fn get_integer(key) = "getInteger(Ljava/lang/String;)I";
        #[may_throw]
        fn get_long(key) = "getLong(Ljava/lang/String;)J";
        #[may_throw]
        fn get_float(key) = "getFloat(Ljava/lang/String;)F";
        #[may_throw]
        fn get_string(key) = "getString(Ljava/lang/String;)Ljava/lang/String;";
        #[may_throw]
        fn get_byte_buffer(key) = "getByteBuffer(Ljava/lang/String;)Ljava/nio/ByteBuffer;";

        #[api(29)]
        fn get_keys() = "getKeys()Ljava/util/Set;";
        #[api(29)]
        fn get_value_type_for_key(key) = "getValueTypeForKey(Ljava/lang/String;)I";
    }

    class MediaMuxer = "android/media/MediaMuxer" {
        fn new(path, format) = "<init>(Ljava/lang/String;I)V";
        fn add_track(format) = "addTrack(Landroid/media/MediaFormat;)I";
        fn start() = "start()V";
        fn stop() = "stop()V";
        fn release() = "release()V";
        fn write_sample_data(track_index, byte_buffer, buffer_info) =
            "writeSampleData(ILjava/nio/ByteBuffer;Landroid/media/MediaCodec$BufferInfo;)V";
    }

    class Image = "android/media/Image" {
        fn get_width() = "getWidth()I";
        fn get_height() = "getHeight()I";
        fn get_planes() = "getPlanes()[Landroid/media/Image$Plane;";
        fn set_timestamp(timestamp_ns) = "setTimestamp(J)V";
        #[api(28)]
        fn get_hardware_buffer() = "getHardwareBuffer()Landroid/hardware/HardwareBuffer;";
        fn close() = "close()V";
    }

    class ImagePlane = "android/media/Image$Plane" {
        fn get_buffer() = "getBuffer()Ljava/nio/ByteBuffer;";
        fn get_pixel_stride() = "getPixelStride()I";
        fn get_row_stride() = "getRowStride()I";
    }

    class ImageWriter = "android/media/ImageWriter" {
        #[api(29)]
        static fn new_instance(surface, max_images, format) =
            "newInstance(Landroid/view/Surface;II)Landroid/media/ImageWriter;";
        fn dequeue_input_image() = "dequeueInputImage()Landroid/media/Image;";
        fn queue_input_image(image) = "queueInputImage(Landroid/media/Image;)V";
        fn close() = "close()V";
    }

    class ImageWriterBuilder = "android/media/ImageWriter$Builder" {
        #[api(33)]
        fn new(surface) = "<init>(Landroid/view/Surface;)V";
        #[api(33)]
        fn set_max_images(max_images) = "setMaxImages(I)Landroid/media/ImageWriter$Builder;";
        #[api(33)]
        fn set_width_and_height(width, height) =
            "setWidthAndHeight(II)Landroid/media/ImageWriter$Builder;";
        #[api(33)]
        fn set_hardware_buffer_format(format) =
            "setHardwareBufferFormat(I)Landroid/media/ImageWriter$Builder;";
        #[api(33)]
        fn set_usage(usage) = "setUsage(J)Landroid/media/ImageWriter$Builder;";
        #[api(33)]
        fn build() = "build()Landroid/media/ImageWriter;";
    }

    class HardwareBuffer = "android/hardware/HardwareBuffer" {
        fn close() = "close()V";
    }

    class BuildVersion = "android/os/Build$VERSION" {
        static field sdk_int = "SDK_INT:I";
    }

    class PersistableBundle = "android/os/PersistableBundle" {
        fn key_set() = "keySet()Ljava/util/Set;";
        fn get(key) = "get(Ljava/lang/String;)Ljava/lang/Object;";
    }

    class ByteBuffer = "java/nio/ByteBuffer" {
        static fn wrap(array) = "wrap([B)Ljava/nio/ByteBuffer;";
        fn put(array) = "put([B)Ljava/nio/ByteBuffer;";
        fn get(array, offset, length) = "get([BII)Ljava/nio/ByteBuffer;";
        fn limit() = "limit()I";
        fn position() = "position()I";
        fn set_position(index) = "position(I)Ljava/nio/Buffer;";
    }

    class JavaSet = "java/util/Set" {
        fn iterator() = "iterator()Ljava/util/Iterator;";
    }

    class JavaIterator = "java/util/Iterator" {
        fn has_next() = "hasNext()Z";
        fn next() = "next()Ljava/lang/Object;";
    }

    class JavaObject = "java/lang/Object" {
        fn to_string() = "toString()Ljava/lang/String;";
    }
}
