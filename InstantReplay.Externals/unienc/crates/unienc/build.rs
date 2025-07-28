use csbindgen::Builder;

fn main() {
    common_builder()
        .input_extern_file("src/lib.rs")
        .input_extern_file("src/audio.rs")
        .input_extern_file("src/mux.rs")
        .input_extern_file("src/video.rs")
        .input_extern_file("src/public_types.rs")
        .generate_csharp_file("../../../../Packages/jp.co.cyberagent.instant-replay/UniEnc/Runtime/Generated/NativeMethods.g.cs")
        .unwrap();
}

fn common_builder() -> Builder {
    Builder::default()
        .csharp_dll_name("libunienc")
        .csharp_dll_name_if("UNITY_IOS && !UNITY_EDITOR", "__Internal")
        .csharp_namespace("UniEnc")
        .csharp_use_nint_types(true)
        .csharp_use_function_pointer(false)
}
