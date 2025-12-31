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
            const config: VideoEncoderConfig = {
                codec: "avc1.640028",
                width: options.width,
                height: options.height,
                bitrate: options.bitrate,
                framerate: options.framerate,
                avc: {
                    format: "annexb",
                }
            };

            if (!await VideoEncoder.isConfigSupported(config)) {
                throw new Error("The specified video encoder configuration is not supported.");
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
                ]
            };
            const frame = new VideoFrame(data, init);
            encoder.encode(frame, {
                keyFrame: options.isKey,
            })
            frame.close();
        },
        callOutputCallback: (chunk, onOutput, ptr, len, ctx) => {
            makeDynCall(onOutput, 'viidBi', ptr, len, chunk.timestamp / 1000.0 / 1000.0, chunk.type === "key", ctx);
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
            };

            if (!await AudioEncoder.isConfigSupported(config)) {
                throw new Error("The specified video encoder configuration is not supported.");
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

