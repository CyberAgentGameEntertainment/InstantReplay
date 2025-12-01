
use crate::*;

#[cfg(target_vendor = "apple")]
pub type PlatformEncodingSystem = unienc_apple_vt::VideoToolboxEncodingSystem<
    VideoEncoderOptionsNative,
    AudioEncoderOptionsNative,
    UniencGraphicsEventIssuer,
>;

#[cfg(target_os = "android")]
pub type PlatformEncodingSystem = unienc_android_mc::MediaCodecEncodingSystem<
    VideoEncoderOptionsNative,
    AudioEncoderOptionsNative,
>;

#[cfg(windows)]
pub type PlatformEncodingSystem = unienc_windows_mf::MediaFoundationEncodingSystem<
    VideoEncoderOptionsNative,
    AudioEncoderOptionsNative,
    UniencGraphicsEventIssuer,
>;

#[cfg(all(
    unix,
    not(any(target_vendor = "apple", target_os = "android", windows))
))]
pub type PlatformEncodingSystem =
unienc_ffmpeg::FFmpegEncodingSystem<VideoEncoderOptionsNative, AudioEncoderOptionsNative, UniencGraphicsEventIssuer>;

#[cfg(not(any(target_vendor = "apple", target_os = "android", windows, unix)))]
pub type PlatformEncodingSystem = ();

#[cfg(not(any(target_vendor = "apple", target_os = "android", windows, unix)))]
compile_error!("Unsupported platform");



use unienc_common::EncoderOutput;

type VideoEncoder =
<PlatformEncodingSystem as unienc_common::EncodingSystem>::VideoEncoderType;
pub type VideoEncoderInput = <VideoEncoder as unienc_common::Encoder>::InputType;
pub type VideoEncoderOutput = <VideoEncoder as unienc_common::Encoder>::OutputType;
type AudioEncoder =
<PlatformEncodingSystem as unienc_common::EncodingSystem>::AudioEncoderType;
pub type AudioEncoderInput = <AudioEncoder as unienc_common::Encoder>::InputType;
pub type AudioEncoderOutput = <AudioEncoder as unienc_common::Encoder>::OutputType;
type Muxer = <PlatformEncodingSystem as unienc_common::EncodingSystem>::MuxerType;
pub type VideoMuxerInput = <Muxer as unienc_common::Muxer>::VideoInputType;
pub type AudioMuxerInput = <Muxer as unienc_common::Muxer>::AudioInputType;
pub type MuxerCompletionHandle = <Muxer as unienc_common::Muxer>::CompletionHandleType;

pub type VideoEncodedData = <VideoEncoderOutput as EncoderOutput>::Data;
pub type AudioEncodedData = <AudioEncoderOutput as EncoderOutput>::Data;
pub type BlitSource = <PlatformEncodingSystem as unienc_common::EncodingSystem>::BlitSourceType;