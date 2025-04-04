// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

import AVFoundation
import Foundation
import VideoToolbox

struct TranscoderError: LocalizedError {
  var errorDescription: String?
  init(_ errorDescription: String) {
    self.errorDescription = errorDescription
  }
}

public enum PullState: Int32 {
  case pulled = 0
  case continues = 1
  case completed = -1
}

public struct PullSampleBufferCallback {
  private let pullSampleBuffer:
    @convention(c) (
      UnsafeRawPointer /* context */, UnsafeMutablePointer<UnsafePointer<CMSampleBuffer>?>
    ) -> Int32
  private let context: UnsafeRawPointer

  init(
    pullSampleBuffer: @convention(c) (
      UnsafeRawPointer /* context */, UnsafeMutablePointer<UnsafePointer<CMSampleBuffer>?>
    ) -> Int32,
    context: UnsafeRawPointer
  ) {
    self.pullSampleBuffer = pullSampleBuffer
    self.context = context
  }

  func pull(result: UnsafeMutablePointer<UnsafePointer<CMSampleBuffer>?>) -> PullState? {
    return PullState(rawValue: self.pullSampleBuffer(self.context, result))
  }
}

class Transcoder {
  private let session: VTCompressionSession
  private let sink: VideoSink
  private let width: Int32
  private let height: Int32
  private let sampleRate: Int
  private let channels: Int32
  private var wroteSamples: Int = 0
  private let audioFormatDesc: CMFormatDescription

  init(
    width: Int32,
    height: Int32,
    sampleRate: Int,
    channels: Int32,
    destination: String,
    pullVideoSampleBuffer: PullSampleBufferCallback,
    pullAudioSampleBuffer: PullSampleBufferCallback
  ) throws {
    let sourceImageBufferAttributes = [
      kCVPixelBufferWidthKey: width,
      kCVPixelBufferHeightKey: height,
    ]
    self.width = width
    self.height = height

    var compressionSessionOut: VTCompressionSession?
    var err = VTCompressionSessionCreate(
      allocator: kCFAllocatorDefault,
      width: width,
      height: height,
      codecType: kCMVideoCodecType_H264,
      encoderSpecification: nil,
      imageBufferAttributes: sourceImageBufferAttributes as CFDictionary,
      compressedDataAllocator: nil,
      outputCallback: nil,
      refcon: nil,
      compressionSessionOut: &compressionSessionOut)
    guard err == noErr, let session = compressionSessionOut else {
      throw TranscoderError("VTCompressionSessionCreate failed: \(err)")
    }

    self.session = session

    err = VTSessionSetProperty(
      session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanFalse)
    if noErr != err {
      print("Warning: VTSessionSetProperty(kVTCompressionPropertyKey_RealTime) failed (\(err))")
    }

    err = VTSessionSetProperty(
      session, key: kVTCompressionPropertyKey_AllowTemporalCompression, value: kCFBooleanTrue)
    if noErr != err {
      print(
        "Warning: VTSessionSetProperty(kVTCompressionPropertyKey_AllowTemporalCompression) failed (\(err))"
      )
    }

    err = VTSessionSetProperty(
      session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanTrue)
    if noErr != err {
      print(
        "Warning: VTSessionSetProperty(kVTCompressionPropertyKey_AllowFrameReordering) failed (\(err))"
      )
    }

    if #available(macOS 10.14, *) {
      err = VTSessionSetProperty(
        session, key: kVTCompressionPropertyKey_MaximizePowerEfficiency, value: kCFBooleanTrue)
      if noErr != err {
        print(
          "Warning: VTSessionSetProperty(kVTCompressionPropertyKey_MaximizePowerEfficiency) failed (\(err))"
        )
      }
    }

    self.sink = try VideoSink(
      filePath: destination, fileType: AVFileType.mp4, codec: kCMVideoCodecType_H264,
      width: Int(width) as Int, height: Int(height) as Int, sampleRate: Float64(sampleRate),
      channels: UInt32(channels), pullVideoSampleBuffer: pullVideoSampleBuffer,
      pullAudioSampleBuffer: pullAudioSampleBuffer)

    self.channels = channels
    self.sampleRate = sampleRate

    self.audioFormatDesc = try CMFormatDescription(
      audioStreamBasicDescription: AudioStreamBasicDescription(
        mSampleRate: Float64(sampleRate),
        mFormatID: kAudioFormatLinearPCM,
        mFormatFlags: kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked,
        mBytesPerPacket: UInt32(channels) * 2,
        mFramesPerPacket: 1,
        mBytesPerFrame: UInt32(channels) * 2,
        mChannelsPerFrame: UInt32(channels),
        mBitsPerChannel: 16,
        mReserved: 0))
  }

  func createPixelBuffer(frame: CGImage) throws -> CVPixelBuffer {
    let attributes = [
      kCVPixelBufferCGImageCompatibilityKey: kCFBooleanTrue,
      kCVPixelBufferCGBitmapContextCompatibilityKey: kCFBooleanTrue,
    ]

    var imageBufferOut: CVPixelBuffer?
    var err = CVPixelBufferCreate(
      kCFAllocatorDefault,
      Int(self.width),
      Int(self.height),
      kCVPixelFormatType_32ARGB,
      attributes as CFDictionary,
      &imageBufferOut)
    guard err == noErr, let imageBuffer = imageBufferOut else {
      throw TranscoderError("CVPixelBufferCreate failed: \(err)")
    }

    CVPixelBufferLockBaseAddress(imageBuffer, CVPixelBufferLockFlags(rawValue: 0))
    guard
      let context = CGContext(
        data: CVPixelBufferGetBaseAddress(imageBuffer),
        width: Int(self.width),
        height: Int(self.height),
        bitsPerComponent: 8,
        bytesPerRow: CVPixelBufferGetBytesPerRow(imageBuffer),
        space: CGColorSpace(name: CGColorSpace.sRGB)!,
        bitmapInfo: CGImageAlphaInfo.noneSkipFirst.rawValue)
    else {
      throw TranscoderError("CGContext failed: \(err)")
    }
    context.clear(CGRect(x: 0, y: 0, width: Int(self.width), height: Int(self.height)))
    context.draw(frame, in: CGRect(x: 0, y: 0, width: frame.width, height: frame.height))
    CVPixelBufferUnlockBaseAddress(imageBuffer, CVPixelBufferLockFlags(rawValue: 0))

    return imageBuffer
  }

  func createAudioSampleBuffer(ptr: UnsafeMutableRawPointer, length: Int) throws -> CMSampleBuffer {
    let numSamples = length / 2 / Int(self.channels)
    var sampleTimingInfo = CMSampleTimingInfo(
      duration: CMTime(value: CMTimeValue(numSamples), timescale: CMTimeScale(self.sampleRate)),
      presentationTimeStamp: CMTime(
        value: 0, timescale: CMTimeScale(self.sampleRate)),
      decodeTimeStamp: .invalid)

    self.wroteSamples += numSamples

    var blockBufferOut: CMBlockBuffer?
    var res = CMBlockBufferCreateEmpty(
      allocator: kCFAllocatorDefault, capacity: 0, flags: 0, blockBufferOut: &blockBufferOut)

    guard res == noErr, let blockBuffer = blockBufferOut else {
      throw TranscoderError("CMBlockBufferCreateEmpty failed: \(res)")
    }
    res = CMBlockBufferAppendMemoryBlock(
      blockBuffer, memoryBlock: nil, length: length, blockAllocator: kCFAllocatorDefault,
      customBlockSource: nil, offsetToData: 0, dataLength: length, flags: 0)

    guard res == noErr else {
      throw TranscoderError("CMBlockBufferAppendMemoryBlock failed: \(res)")
    }
    res = CMBlockBufferReplaceDataBytes(
      with: ptr, blockBuffer: blockBuffer, offsetIntoDestination: 0, dataLength: length)

    guard res == noErr else {
      throw TranscoderError("CMBlockBufferReplaceDataBytes failed: \(res)")
    }

    var sampleBufferOut: CMSampleBuffer?
    res = CMSampleBufferCreate(
      allocator: kCFAllocatorDefault,
      dataBuffer: blockBuffer,
      dataReady: true,
      makeDataReadyCallback: nil,
      refcon: nil,
      formatDescription: self.audioFormatDesc,
      sampleCount: numSamples,
      sampleTimingEntryCount: 1,
      sampleTimingArray: &sampleTimingInfo,
      sampleSizeEntryCount: 0,
      sampleSizeArray: nil,
      sampleBufferOut: &sampleBufferOut)

    guard res == noErr, let sampleBuffer = sampleBufferOut else {
      throw TranscoderError("CMSampleBufferCreate failed: \(res)")
    }

    return sampleBuffer

  }

  func encodeVideoFrame(
    pixelBuffer: CVPixelBuffer,
    timestamp: Double,
    callback: @convention(c) (
      UnsafeRawPointer /* ctx */, UnsafeRawPointer? /* result */,
      UnsafePointer<CChar>? /* error */
    ) -> Void,
    ctx: UnsafeRawPointer
  ) {
    let err = VTCompressionSessionEncodeFrame(
      self.session,
      imageBuffer: pixelBuffer,
      presentationTimeStamp: CMTime(seconds: timestamp, preferredTimescale: 720),
      duration: .invalid,
      frameProperties: nil,
      infoFlagsOut: nil
    ) { status, infoFlags, sbuf in
      guard status == noErr, let sbufGuard = sbuf else {
        "VTCompressionSessionEncodeFrame failed: \(status)".utf8CString.withUnsafeBufferPointer {
          ptr in
          callback(ctx, nil, ptr.baseAddress)
        }
        return
      }

      callback(ctx, Unmanaged<CMSampleBuffer>.passRetained(sbufGuard).toOpaque(), nil)
    }

    if err != noErr {
      print("VTCompressionSessionEncodeFrame failed: \(err)")
    }
  }

  func completeVideoFrames() -> Int32 {
    let err = VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
    if err != noErr {
      print("VTCompressionSessionCompleteFrames failed: \(err)")
      return -1
    }

    return 0
  }

  func complete(
    onComplete: @convention(c) (UnsafeRawPointer /* ctx */, UnsafePointer<CChar>? /* err */) ->
      Void, ctx: UnsafeRawPointer
  ) -> Int32 {

    _ = Task.detached {
      do {
        try await self.sink.close()
      } catch {
        "\(error)".utf8CString.withUnsafeBufferPointer { ptr in
          onComplete(ctx, ptr.baseAddress)
        }

        return
      }
      onComplete(ctx, UnsafePointer<CChar>(bitPattern: 0))
    }

    return 0
  }
}
