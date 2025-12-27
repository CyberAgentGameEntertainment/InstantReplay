use csbindgen::Builder;

fn main() {
    common_builder()
        .input_extern_file("src/lib.rs")
        .input_extern_file("src/api/audio.rs")
        .input_extern_file("src/api/mux.rs")
        .input_extern_file("src/api/video.rs")
        .input_extern_file("src/api/runtime.rs")
        .input_extern_file("src/api/encoding_system.rs")
        .input_extern_file("src/api/graphics.rs")
        .input_extern_file("src/types.rs")
        .input_extern_file("src/buffer.rs")
        .input_extern_file("src/ffi.rs")
        .generate_csharp_file("../../../../Packages/jp.co.cyberagent.instant-replay/UniEnc/Runtime/Generated/NativeMethods.g.cs")
        .unwrap();
}

fn common_builder() -> Builder {
    Builder::default()
        .csharp_dll_name("libunienc_c")
        .csharp_dll_name_if("(UNITY_IOS || UNITY_WEBGL) && !UNITY_EDITOR", "__Internal")
        .csharp_namespace("UniEnc.Native")
        .csharp_use_nint_types(true)
        .csharp_use_function_pointer(false)
}
