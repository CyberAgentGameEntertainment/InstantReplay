declare var Module: {
    _malloc: ((size: number) => number) | undefined;
    _free: ((ptr: number) => void) | undefined;
    asm: { malloc: (size: number) => number, free: (ptr: number) => void } | undefined
    HEAPU8: Uint8Array;
    HEAPU32: Uint32Array;
};

declare var UTF8ToString: (ptr: number) => string;

declare var getWasmTableEntry: any;

declare var lengthBytesUTF8: (str: string) => number;
declare var stringToUTF8: (str: string, outPtr: number, maxBytesToWrite: number) => void;

type EncoderSlot<Encoder> = {
    encoder: Encoder | null;
    next: EncoderSlot<Encoder> | null;
    index: number;
}

type EncoderGeneral = {
    flush: () => Promise<void>;
    close: () => void;
}

type EncoderImpl<Encoder, EncoderOptions, FrameOptions> = {
    _encoders: EncoderSlot<Encoder>[],
    _encoderEmptyRoot: EncoderSlot<Encoder> | null,
    new: (options: EncoderOptions, onOutput: number, onOutputCtx: any, onComplete: number, onCompleteCtx: any) => void;
    free: (index: number) => void;
    push: (encoderIndex: number, array: Uint8Array<ArrayBuffer>, options: FrameOptions) => void;
    flush: (index: number, onComplete: number, onCompleteCtx: number) => void;
}

type EncoderHandler<Encoder, EncoderOptions, FrameOptions, Chunk extends EncodedChunk> = {
    createEncoder: (options: EncoderOptions, onChunk: (chunk: Chunk) => void) => Promise<Encoder>;
    encodeFrame: (encoder: Encoder, data: Uint8Array<ArrayBuffer>, options: FrameOptions) => void;
    callOutputCallback: (chunk: Chunk, onOutput: number, ptr: number, len: number, ctx: any) => void;
}

type EncodedChunk = {
    readonly byteLength: number;
    readonly duration: number | null;
    readonly timestamp: number;
    copyTo(destination: AllowSharedBufferSource): void;
}

console.log('Initializing unienc_webcodecs module');

function makeDynCall(callback: number, name: string, ...args: any) {
    if (typeof getWasmTableEntry !== "undefined") getWasmTableEntry(callback)(...args); else if (typeof Module[`dynCall_${name}`] !== "undefined") Module[`dynCall_${name}`](callback, ...args); else throw "Could not make dynCall because neither getWasmTableEntry nor Module.dynCall_* is available";
}

function createEncoderImpl<Encoder extends EncoderGeneral, EncoderOptions, FrameOptions, Chunk extends EncodedChunk>(handler: EncoderHandler<Encoder, EncoderOptions, FrameOptions, Chunk>): EncoderImpl<Encoder, EncoderOptions, FrameOptions> {
    return {
        _encoders: [],
        _encoderEmptyRoot: null,
        new: async function (options, onOutput, onOutputCtx, onComplete, onCompleteCtx) {
            // as EncoderImpl<Encoder, EncoderOptions, FrameOptions>;
            const encoder = await handler.createEncoder(options, (chunk) => {
                const buf = (Module._malloc || Module.asm.malloc)(chunk.byteLength);
                try {
                    chunk.copyTo(Module.HEAPU8.subarray(buf, buf + chunk.byteLength));
                    handler.callOutputCallback(chunk, onOutput, buf, chunk.byteLength, onOutputCtx);
                } catch (e) {
                    (Module._free || Module.asm.free)(buf);
                    throw e;
                }
                (Module._free || Module.asm.free)(buf);

            });

            let index;
            if (!this._encoderEmptyRoot) {
                const entry = {encoder: encoder, next: null, index: this._encoders.length};
                this._encoders.push(entry);
                index = entry.index;
            } else {
                const entry = this._encoderEmptyRoot;
                this._encoderEmptyRoot = this._encoderEmptyRoot.next;
                entry.encoder = encoder;
                entry.next = null;
                index = entry.index;
            }
            makeDynCall(onComplete, "vii", index, onCompleteCtx);
        },
        flush: async function (index: number) {
            const entry = this._encoders[index];
            await entry.encoder?.flush();
        },
        free: function (index) {
            const entry = this._encoders[index];
            entry.encoder?.close();
            entry.encoder = null;
            entry.next = this._encoderEmptyRoot;
            this._encoderEmptyRoot = entry;
        },
        push: function (encoderIndex, array, options) {
            const encoder = this._encoders[encoderIndex].encoder;
            if (!encoder) return;
            handler.encodeFrame(encoder, array, options);
        },

    }
}

window["unienc_webcodecs"] = {
    call: function (closure: () => void, onError: number, onErrorCtx: number) {
        try {
            closure();
        } catch (e) {
            const msg = e.toString();
            const len = lengthBytesUTF8(msg) + 1;
            const msgPtr = (Module._malloc || Module.asm.malloc)(len);
            stringToUTF8(msg, msgPtr, len);
            try {{
                makeDynCall(onError, 'vii', msgPtr, onErrorCtx);
            }} finally {{
                (Module._free || Module.asm.free)(msgPtr);
            }}
        }
    },
    call_async: async function (closure: () => Promise<void>, onComplete: number, onCompleteCtx: number) {
        try {
            await closure();
        } catch (e) {
            const msg = e.toString();
            const len = lengthBytesUTF8(msg) + 1;
            const msgPtr = (Module._malloc || Module.asm.malloc)(len);
            stringToUTF8(msg, msgPtr, len);
            try {{
                makeDynCall(onComplete, 'vii', msgPtr, onCompleteCtx);
            }} finally {{
                (Module._free || Module.asm.free)(msgPtr);
            }}
            return;
        }
        makeDynCall(onComplete, 'vii', 0, onCompleteCtx);
    },
    video: createEncoderImpl<
        VideoEncoder,
        { width: number, height: number, bitrate: number, framerate: number },
        {
            width: number,
            height: number,
            timestamp: number,
            isKey: boolean
        },
        EncodedVideoChunk
    >({
        createEncoder: async (options, onChunk) => {
            // Which H.264 profiles a browser will encode is not fixed: Chrome on
            // Linux, for one, refuses High. Asking for a single profile means
            // failing outright wherever it is missing, so these are tried in
            // descending order of what they buy and the first the browser accepts
            // is used. Baseline is the most widely playable of the three, so the
            // fallback costs compression rather than compatibility.
            //
            // `isConfigSupported` resolves to a support object rather than a
            // boolean, so `supported` has to be read from it; negating the object
            // is always false and lets an unsupported configuration through to
            // `configure`, where the encoder closes itself and the first `encode`
            // fails with an unrelated InvalidStateError.
            const profiles = [
                "avc1.640028", // High, level 4.0
                "avc1.4d0028", // Main, level 4.0
                "avc1.42001f", // Baseline, level 3.1
            ];

            let config: VideoEncoderConfig | null = null;
            for (const codec of profiles) {
                const candidate: VideoEncoderConfig = {
                    codec,
                    width: options.width,
                    height: options.height,
                    bitrate: options.bitrate,
                    framerate: options.framerate,
                    avc: {
                        format: "annexb",
                    }
                };
                // The candidate is kept rather than the normalized config the
                // browser returns, because the muxer depends on the annexb
                // format and a sanitized config need not preserve it.
                if ((await VideoEncoder.isConfigSupported(candidate)).supported) {
                    config = candidate;
                    break;
                }
            }

            if (!config) {
                throw new Error(
                    `No supported H.264 profile for ${options.width}x${options.height}`
                    + ` at ${options.bitrate} bps; tried ${profiles.join(", ")}`);
            }
            const init: VideoEncoderInit = {
                output: (chunk, metadata) => {
                    if (metadata?.decoderConfig) {
                        if (metadata.decoderConfig.description) {
                            const desc = new Uint8Array(metadata.decoderConfig.description as ArrayBuffer);
                        }
                    }
                    onChunk(chunk);
                }, error: (e) => {
                    console.error(e);
                },
            };

            const encoder = new VideoEncoder(init);
            encoder.configure(config);
            return encoder;
        },
        encodeFrame: (encoder, data, options) => {
            const init: VideoFrameBufferInit = {
                timestamp: options.timestamp * 1000 * 1000,
                codedWidth: options.width,
                codedHeight: options.height,
                visibleRect: {x: 0, y: 0, width: options.width, height: options.height},
                displayWidth: options.width,
                displayHeight: options.height,
                format: "BGRA",
                layout: [
                    {
                        offset: 0,
                        stride: options.width * 4  // BGRA = 4 bytes per pixel
                    }
                ],
                // Describe the source buffer as sRGB. WebCodecs has no way to ask the encoder for
                // a particular output color space, so this is the only lever available: the user
                // agent converts to the encoder's YUV space itself and tags the stream from what
                // the source frame declares. Left unset it has to guess, which is what makes the
                // output carry no color information today.
                colorSpace: {
                    primaries: "bt709",
                    transfer: "iec61966-2-1",
                    matrix: "rgb",
                    fullRange: true
                }
            };
            const frame = new VideoFrame(data, init);
            encoder.encode(frame, {
                keyFrame: options.isKey,
            })
            frame.close();
        },
        callOutputCallback: (chunk, onOutput, ptr, len, ctx) => {
            makeDynCall(onOutput, 'viidii', ptr, len, chunk.timestamp / 1000.0 / 1000.0, chunk.type === "key" ? 1 : 0, ctx);
        }
    }),
    audio: createEncoderImpl<
        AudioEncoder,
        { bitrate: number, channels: number, sampleRate: number },
        {
            channels: number,
            sampleRate: number,
            timestamp: number,
        },
        EncodedAudioChunk
    >({
        createEncoder: async (options, onChunk) => {
            const config: AudioEncoderConfig = {
                codec: "mp4a.40.2",
                bitrate: options.bitrate,
                numberOfChannels: options.channels,
                sampleRate: options.sampleRate,
                // The muxer takes ADTS framed AAC, as the other backends
                // produce. Without this the encoder emits raw AAC and the
                // muxer rejects every frame for having no header. This is the
                // audio counterpart of the annexb format asked for above.
                aac: {
                    format: "adts",
                },
            };

            // See the note on the video encoder above.
            const support = await AudioEncoder.isConfigSupported(config);
            if (!support.supported) {
                throw new Error(
                    `The audio encoder configuration is not supported: ${JSON.stringify(config)}`);
            }
            const init: AudioEncoderInit = {
                output: (chunk, _metadata) => {
                    onChunk(chunk);
                }, error: (e) => {
                    console.error(e);
                },
            };

            const encoder = new AudioEncoder(init);
            encoder.configure(config);
            return encoder;
        },
        encodeFrame: (encoder, data, options) => {
            const init: AudioDataInit = {
                data: data,
                format: "s16",
                numberOfChannels: options.channels,
                numberOfFrames: data.length / 2 / options.channels,
                sampleRate: options.sampleRate,
                timestamp: options.timestamp * 1000 * 1000,
            };
            const frame = new AudioData(init);
            encoder.encode(frame)
            frame.close();
        },
        callOutputCallback: (chunk, onOutput, ptr, len, ctx) => {
            makeDynCall(onOutput, 'viidi', ptr, len, chunk.timestamp / 1000.0 / 1000.0, ctx);
        }
    }),
    makeDownload: function (partsPtr: number, numParts: number, mimePtr: number, filenamePtr: number) {
        const jsParts = [];

        const mimeStr = UTF8ToString(mimePtr);
        const filenameStr = UTF8ToString(filenamePtr);

        const partBuf = Module.HEAPU32.subarray(partsPtr >> 2, (partsPtr >> 2) + numParts * 2);
        for (let i = 0; i < numParts; i++) {
            let ptr = partBuf[i * 2];
            let len = partBuf[i * 2 + 1];
            let segment = Module.HEAPU8.subarray(ptr, ptr + len);
            jsParts.push(segment);
        }

        let blob = new Blob(jsParts, {type: mimeStr});
        let url = URL.createObjectURL(blob);

        let a = document.createElement('a');
        a.href = url;
        a.download = filenameStr;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }
};

