// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

import AVFoundation
import Foundation

@_cdecl("InstantReplay_CreateSession")
public func InstantReplay_CreateSession(
  width: Int32,
  height: Int32,
  sampleRate: Int,
  channels: Int32,
  destination: UnsafePointer<CChar>,
  pullVideoSampleBuffer: @convention(c) (
    UnsafeRawPointer, UnsafeMutablePointer<UnsafePointer<CMSampleBuffer>?>
  ) -> Int32,
  pullVideoSampleBufferCtx: UnsafeRawPointer,
  pullAudioSampleBuffer: @convention(c) (
    UnsafeRawPointer, UnsafeMutablePointer<UnsafePointer<CMSampleBuffer>?>
  ) -> Int32,
  pullAudioSampleBufferCtx: UnsafeRawPointer
) -> UnsafeRawPointer? {
  do {
    let transcoder = try Transcoder.init(
      width: width,
      height: height,
      sampleRate: sampleRate,
      channels: channels,
      destination: String(cString: destination),
      pullVideoSampleBuffer: PullSampleBufferCallback(
        pullSampleBuffer: pullVideoSampleBuffer, context: pullVideoSampleBufferCtx),
      pullAudioSampleBuffer: PullSampleBufferCallback(
        pullSampleBuffer: pullAudioSampleBuffer, context: pullAudioSampleBufferCtx)
    )
    let unmanaged = Unmanaged<Transcoder>.passRetained(transcoder)
    return UnsafeRawPointer(unmanaged.toOpaque())
  } catch {
    print(error)
    return nil
  }
}

@_cdecl("InstantReplay_EncodeVideoFrame")
public func InstantReplay_EncodeVideoFrame(
  transcoderPtr: UnsafeRawPointer,
  pixelBufferPtr: UnsafeRawPointer,
  timestamp: Double,
  callback: @convention(c) (
    UnsafeRawPointer /* ctx */, UnsafeRawPointer? /* result */, UnsafePointer<CChar>? /* error */
  ) -> Void,
  ctx: UnsafeRawPointer
) {

  let transcoder = Unmanaged<Transcoder>.fromOpaque(transcoderPtr).takeUnretainedValue()
  let pixelBuffer = Unmanaged<CVPixelBuffer>.fromOpaque(pixelBufferPtr).takeRetainedValue()

  transcoder.encodeVideoFrame(
    pixelBuffer: pixelBuffer, timestamp: timestamp, callback: callback, ctx: ctx)
}

@_cdecl("InstantReplay_CompleteVideoFrames")
public func InstantReplay_CompleteVideoFrames(transcoderPtr: UnsafeRawPointer) -> Int32 {
  return Unmanaged<Transcoder>.fromOpaque(transcoderPtr).takeUnretainedValue().completeVideoFrames()
}

@_cdecl("InstantReplay_CreateAudioSampleBuffer")
public func InstantReplay_CreateAudioSampleBuffer(
  transcoderPtr: UnsafeRawPointer,
  audioSamplesPtr: UnsafeMutableRawPointer,
  length: Int
) -> UnsafeRawPointer? {

  let transcoder = Unmanaged<Transcoder>.fromOpaque(transcoderPtr).takeUnretainedValue()
  do {
    let sampleBuffer = try transcoder.createAudioSampleBuffer(ptr: audioSamplesPtr, length: length)
    return UnsafeRawPointer(Unmanaged<CMSampleBuffer>.passRetained(sampleBuffer).toOpaque())
  } catch {
    print(error)
    return nil
  }
}

@_cdecl("InstantReplay_Complete")
public func InstantReplay_Complete(
  transcoderPtr: UnsafeRawPointer,
  onComplete: @convention(c) (UnsafeRawPointer, UnsafePointer<CChar>?) -> Void,
  ctx: UnsafeRawPointer
) -> Int32 {
  return Unmanaged<Transcoder>.fromOpaque(transcoderPtr).takeRetainedValue().complete(
    onComplete: onComplete, ctx: ctx)
}

@_cdecl("InstantReplay_LoadJpeg")
public func InstantReplay_LoadJpeg(transcoderPtr: UnsafeRawPointer, filename: UnsafePointer<CChar>)
  -> UnsafeRawPointer?
{
  let transcoder = Unmanaged<Transcoder>.fromOpaque(transcoderPtr).takeUnretainedValue()
  guard let provider = CGDataProvider(filename: String(cString: filename)) else {
    return nil
  }
  guard
    let image = CGImage(
      jpegDataProviderSource: provider, decode: nil, shouldInterpolate: false,
      intent: CGColorRenderingIntent.defaultIntent)
  else {
    return nil
  }

  do {
    return try UnsafeRawPointer(
      Unmanaged<CVPixelBuffer>.passRetained(transcoder.createPixelBuffer(frame: image)).toOpaque())
  } catch {
    print(error)
    return nil
  }
}
