// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

import AVFoundation
import Foundation

public class VideoSink {
  private let assetWriter: AVAssetWriter
  private let videoInput: AVAssetWriterInput
  private let audioInput: AVAssetWriterInput

  public init(
    filePath: String,
    fileType: AVFileType,
    codec: CMVideoCodecType,
    width: Int,
    height: Int,
    sampleRate: Float64,
    channels: UInt32,
    pullVideoSampleBuffer: PullSampleBufferCallback,
    pullAudioSampleBuffer: PullSampleBufferCallback
  ) throws {
    let sinkURL = URL(fileURLWithPath: filePath)

    assetWriter = try AVAssetWriter(outputURL: sinkURL, fileType: fileType)

    let videoFormatDesc = try CMFormatDescription(
      videoCodecType: CMFormatDescription.MediaSubType(rawValue: codec), width: width,
      height: height)

    videoInput = AVAssetWriterInput(
      mediaType: AVMediaType.video, outputSettings: nil, sourceFormatHint: videoFormatDesc)
    assetWriter.add(videoInput)

    audioInput = AVAssetWriterInput(
      mediaType: AVMediaType.audio,
      outputSettings: [
        AVFormatIDKey: kAudioFormatMPEG4AAC,
        AVSampleRateKey: sampleRate,
        AVNumberOfChannelsKey: channels,
        AVEncoderBitRateKey: 128000,
      ])
    assetWriter.add(audioInput)

    guard assetWriter.startWriting() else {
      throw assetWriter.error!
    }

    assetWriter.startSession(atSourceTime: .zero)

    videoInput.requestMediaDataWhenReady(on: DispatchQueue(label: "video input")) { [weak self] in
      guard let self = self else {
        return
      }
      while self.videoInput.isReadyForMoreMediaData {
        var sampleBufferOut: UnsafePointer<CMSampleBuffer>?
        switch pullVideoSampleBuffer.pull(result: &sampleBufferOut) {
        case .pulled:
          if let sampleBuffer = sampleBufferOut {
            let sampleBuffer = Unmanaged<CMSampleBuffer>.fromOpaque(sampleBuffer)
              .takeRetainedValue()
            self.videoInput.append(sampleBuffer)
          }
        case .completed:
          self.videoInput.markAsFinished()
          return
        default:
          return
        }
      }
    }

    audioInput.requestMediaDataWhenReady(on: DispatchQueue(label: "audio input")) { [weak self] in
      guard let self = self else {
        return
      }
      while self.audioInput.isReadyForMoreMediaData {
        var sampleBufferOut: UnsafePointer<CMSampleBuffer>?
        switch pullAudioSampleBuffer.pull(result: &sampleBufferOut) {
        case .pulled:
          if let sampleBuffer = sampleBufferOut {
            let sampleBuffer = Unmanaged<CMSampleBuffer>.fromOpaque(sampleBuffer)
              .takeRetainedValue()
            self.audioInput.append(sampleBuffer)
          }
        case .completed:
          self.audioInput.markAsFinished()
          return
        default:
          return
        }
      }
    }
  }

  public func close() async throws {
    await assetWriter.finishWriting()

    if assetWriter.status == .failed {
      throw assetWriter.error!
    }
  }
}
