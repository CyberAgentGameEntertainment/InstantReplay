
use unienc::EncoderOutput;
use crate::runtime::{Runtime, RuntimeSpawner};
use crate::types::{AudioEncoderOptionsNative, VideoEncoderOptionsNative};

pub type PlatformEncodingSystem = unienc::PlatformEncodingSystem<VideoEncoderOptionsNative, AudioEncoderOptionsNative, RuntimeSpawner>;

type VideoEncoder =
<PlatformEncodingSystem as unienc::EncodingSystem>::VideoEncoderType;
pub type VideoEncoderInput = <VideoEncoder as unienc::Encoder>::InputType;
pub type VideoEncoderOutput = <VideoEncoder as unienc::Encoder>::OutputType;
type AudioEncoder =
<PlatformEncodingSystem as unienc::EncodingSystem>::AudioEncoderType;
pub type AudioEncoderInput = <AudioEncoder as unienc::Encoder>::InputType;
pub type AudioEncoderOutput = <AudioEncoder as unienc::Encoder>::OutputType;
type Muxer = <PlatformEncodingSystem as unienc::EncodingSystem>::MuxerType;
pub type VideoMuxerInput = <Muxer as unienc::Muxer>::VideoInputType;
pub type AudioMuxerInput = <Muxer as unienc::Muxer>::AudioInputType;
pub type MuxerCompletionHandle = <Muxer as unienc::Muxer>::CompletionHandleType;

pub type VideoEncodedData = <VideoEncoderOutput as EncoderOutput>::Data;
pub type AudioEncodedData = <AudioEncoderOutput as EncoderOutput>::Data;
pub type BlitSource = <PlatformEncodingSystem as unienc::EncodingSystem>::BlitSourceType;