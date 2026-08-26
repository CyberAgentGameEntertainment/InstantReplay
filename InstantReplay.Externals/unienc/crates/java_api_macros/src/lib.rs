//! `java_api!` — declares the Java API surface a crate calls through JNI, and generates typed
//! wrappers for it.
//!
//! Every declaration is checked, at compile time, against a subset of the Android SDK's
//! `api-versions.xml` (see the `android_api_metadata` tool). The macro fails the build when
//!
//! * the class, method or field does not exist on the platform at all,
//! * the member was introduced after `min_api` but the declaration does not carry `#[api(N)]`,
//! * the `#[api(N)]` annotation disagrees with the API level the member was introduced in, or
//! * the member has been removed from the platform.
//!
//! A member annotated with `#[api(N)]` gets an extra `ApiLevel<N>` parameter, so it cannot be
//! called without first proving at run time that the device is on API level `N` or later.
//!
//! ```ignore
//! java_api! {
//!     metadata = "java-api/android-api-versions.txt";
//!     min_api = 26;
//!     runtime = crate::java_api;
//!
//!     class MediaFormat = "android/media/MediaFormat" {
//!         fn contains_key(key) = "containsKey(Ljava/lang/String;)Z";
//!         #[api(29)]
//!         fn get_keys() = "getKeys()Ljava/util/Set;";
//!     }
//! }
//! ```

mod descriptor;
mod metadata;

use std::path::PathBuf;

use descriptor::JType;
use metadata::Metadata;
use proc_macro2::{Span, TokenStream};
use quote::{format_ident, quote};
use syn::parse::{Parse, ParseStream};
use syn::punctuated::Punctuated;
use syn::{Attribute, Ident, LitInt, LitStr, Path, Token, braced, parenthesized};

#[proc_macro]
pub fn java_api(input: proc_macro::TokenStream) -> proc_macro::TokenStream {
    let declaration = syn::parse_macro_input!(input as JavaApi);
    expand(declaration).into()
}

// ---------------------------------------------------------------------------------------------
// Parsing
// ---------------------------------------------------------------------------------------------

struct JavaApi {
    metadata: LitStr,
    min_api: u32,
    runtime: Path,
    classes: Vec<ClassDecl>,
}

struct ClassDecl {
    attrs: Vec<Attribute>,
    name: Ident,
    jni_name: LitStr,
    members: Vec<MemberDecl>,
}

struct MemberDecl {
    attrs: Vec<Attribute>,
    api: Option<(u32, Span)>,
    may_throw: bool,
    is_static: bool,
    kind: MemberKind,
    name: Ident,
    params: Vec<Ident>,
    signature: LitStr,
}

#[derive(PartialEq, Eq, Clone, Copy)]
enum MemberKind {
    Method,
    Field,
}

impl Parse for JavaApi {
    fn parse(input: ParseStream) -> syn::Result<Self> {
        let mut metadata = None;
        let mut min_api = None;
        let mut runtime = None;

        while input.peek(Ident) && input.peek2(Token![=]) {
            let key: Ident = input.parse()?;
            input.parse::<Token![=]>()?;
            match key.to_string().as_str() {
                "metadata" => metadata = Some(input.parse::<LitStr>()?),
                "min_api" => min_api = Some(input.parse::<LitInt>()?.base10_parse::<u32>()?),
                "runtime" => runtime = Some(input.parse::<Path>()?),
                other => {
                    return Err(syn::Error::new(
                        key.span(),
                        format!(
                            "unknown setting `{other}`; expected `metadata`, `min_api` or `runtime`"
                        ),
                    ));
                }
            }
            input.parse::<Token![;]>()?;
        }

        let metadata = metadata
            .ok_or_else(|| input.error("missing `metadata = \"<path to api-versions.txt>\";`"))?;
        let min_api = min_api.ok_or_else(|| input.error("missing `min_api = <level>;`"))?;
        let runtime =
            runtime.ok_or_else(|| input.error("missing `runtime = <path to support module>;`"))?;

        let mut classes = Vec::new();
        while !input.is_empty() {
            classes.push(input.parse()?);
        }

        Ok(Self {
            metadata,
            min_api,
            runtime,
            classes,
        })
    }
}

impl Parse for ClassDecl {
    fn parse(input: ParseStream) -> syn::Result<Self> {
        let attrs = input.call(Attribute::parse_outer)?;
        let keyword: Ident = input.parse()?;
        if keyword != "class" {
            return Err(syn::Error::new(keyword.span(), "expected `class`"));
        }
        let name: Ident = input.parse()?;
        input.parse::<Token![=]>()?;
        let jni_name: LitStr = input.parse()?;

        let body;
        braced!(body in input);
        let mut members = Vec::new();
        while !body.is_empty() {
            members.push(body.parse()?);
        }

        Ok(Self {
            attrs,
            name,
            jni_name,
            members,
        })
    }
}

impl Parse for MemberDecl {
    fn parse(input: ParseStream) -> syn::Result<Self> {
        let all_attrs = input.call(Attribute::parse_outer)?;
        let mut attrs = Vec::new();
        let mut api = None;
        let mut may_throw = false;
        for attr in all_attrs {
            if attr.path().is_ident("api") {
                let level: LitInt = attr.parse_args()?;
                api = Some((level.base10_parse::<u32>()?, attr.span()));
            } else if attr.path().is_ident("may_throw") {
                attr.meta.require_path_only()?;
                may_throw = true;
            } else {
                attrs.push(attr);
            }
        }

        let is_static = input.parse::<Option<Token![static]>>()?.is_some();

        let kind = if input.peek(Token![fn]) {
            input.parse::<Token![fn]>()?;
            MemberKind::Method
        } else {
            let keyword: Ident = input.parse()?;
            if keyword != "field" {
                return Err(syn::Error::new(keyword.span(), "expected `fn` or `field`"));
            }
            MemberKind::Field
        };

        let name: Ident = input.parse()?;

        let params = if kind == MemberKind::Method {
            let list;
            parenthesized!(list in input);
            Punctuated::<Ident, Token![,]>::parse_terminated(&list)?
                .into_iter()
                .collect()
        } else {
            Vec::new()
        };

        input.parse::<Token![=]>()?;
        let signature: LitStr = input.parse()?;
        input.parse::<Token![;]>()?;

        Ok(Self {
            attrs,
            api,
            may_throw,
            is_static,
            kind,
            name,
            params,
            signature,
        })
    }
}

use syn::spanned::Spanned as _;

// ---------------------------------------------------------------------------------------------
// Expansion
// ---------------------------------------------------------------------------------------------

fn expand(declaration: JavaApi) -> TokenStream {
    let manifest_dir = match std::env::var("CARGO_MANIFEST_DIR") {
        Ok(dir) => PathBuf::from(dir),
        Err(_) => {
            return error(
                declaration.metadata.span(),
                "CARGO_MANIFEST_DIR is not set; `java_api!` must be expanded by cargo",
            );
        }
    };
    let metadata_path = manifest_dir.join(declaration.metadata.value());
    let metadata = match Metadata::load(&metadata_path) {
        Ok(metadata) => metadata,
        Err(message) => {
            return error(
                declaration.metadata.span(),
                format!(
                    "could not read the Android API metadata: {message}\n\
                     regenerate it with `cargo run -p android_api_metadata -- --classes <classes.txt> --out <out.txt>`"
                ),
            );
        }
    };

    let mut output = TokenStream::new();
    for class in &declaration.classes {
        output.extend(expand_class(&declaration, &metadata, class));
    }
    output
}

fn expand_class(declaration: &JavaApi, metadata: &Metadata, class: &ClassDecl) -> TokenStream {
    let jni_name = class.jni_name.value();
    let name = &class.name;
    let attrs = &class.attrs;

    if !metadata.classes.contains_key(&jni_name) {
        return error(
            class.jni_name.span(),
            format!(
                "class `{jni_name}` is not present in the vendored Android API metadata \
                 (platform {}).\nIf the class exists on the platform, add it to java-api/classes.txt \
                 and regenerate the metadata.",
                metadata.platform
            ),
        );
    }
    let missing = metadata.missing_supertypes(&jni_name);
    if !missing.is_empty() {
        return error(
            class.jni_name.span(),
            format!(
                "the supertypes of `{jni_name}` are missing from the vendored Android API metadata: {}.\n\
                 Regenerate it with `cargo run -p android_api_metadata`.",
                missing.join(", ")
            ),
        );
    }

    let class_since = metadata.classes[&jni_name].since;
    let doc = format!(
        "Wrappers for `{}`.\n\nIntroduced in API level {class_since}.",
        jni_name.replace('/', ".")
    );

    let members: TokenStream = class
        .members
        .iter()
        .map(|member| expand_member(declaration, metadata, class, &jni_name, member))
        .collect();

    quote! {
        #(#attrs)*
        #[doc = #doc]
        #[allow(dead_code, non_camel_case_types)]
        pub struct #name;

        #[allow(dead_code)]
        impl #name {
            /// JNI name of the class, as accepted by `JNIEnv::find_class`.
            pub const CLASS: &'static str = #jni_name;

            #members
        }
    }
}

fn expand_member(
    declaration: &JavaApi,
    metadata: &Metadata,
    class: &ClassDecl,
    jni_name: &str,
    member: &MemberDecl,
) -> TokenStream {
    let signature = member.signature.value();
    let span = member.signature.span();

    // Parse the declaration into the Java member name, the JNI descriptor and Rust-side types.
    // The declaration reads `name(arguments)return`, matching the keys used by `api-versions.xml`;
    // the descriptor JNI expects is only the `(arguments)return` part.
    let (java_name, descriptor, param_types, return_type) = match member.kind {
        MemberKind::Method => {
            let (java_name, args, ret) = match descriptor::split_method(&signature) {
                Ok(parts) => parts,
                Err(message) => return error(span, message),
            };
            let param_types = match descriptor::parse_types(args) {
                Ok(types) => types,
                Err(message) => return error(span, message),
            };
            let return_type = match descriptor::parse_one_type(ret) {
                Ok(kind) => kind,
                Err(message) => return error(span, message),
            };
            (
                java_name.to_owned(),
                format!("({args}){ret}"),
                param_types,
                return_type,
            )
        }
        MemberKind::Field => {
            let (java_name, type_descriptor) = match descriptor::split_field(&signature) {
                Ok(parts) => parts,
                Err(message) => return error(span, message),
            };
            let ty = match descriptor::parse_one_type(type_descriptor) {
                Ok(kind) => kind,
                Err(message) => return error(span, message),
            };
            (
                java_name.to_owned(),
                type_descriptor.to_owned(),
                Vec::new(),
                ty,
            )
        }
    };

    if member.kind == MemberKind::Method && member.params.len() != param_types.len() {
        return error(
            member.name.span(),
            format!(
                "`{}` declares {} parameter name(s) but the descriptor has {}",
                member.name,
                member.params.len(),
                param_types.len()
            ),
        );
    }

    // Look the member up in the platform metadata.
    let lookup_key = match member.kind {
        MemberKind::Method => signature.clone(),
        MemberKind::Field => java_name.clone(),
    };
    let found = match metadata.find(jni_name, &lookup_key, member.kind == MemberKind::Method) {
        Some(found) => found,
        None => {
            let what = if member.kind == MemberKind::Method {
                "method"
            } else {
                "field"
            };
            return error(
                span,
                format!(
                    "{what} `{lookup_key}` does not exist on `{}` (or any of its supertypes) \
                     on platform {}",
                    jni_name.replace('/', "."),
                    metadata.platform
                ),
            );
        }
    };

    if let Some(removed) = found.removed {
        return error(
            span,
            format!(
                "`{}.{lookup_key}` was removed from the platform in API level {removed}",
                jni_name.replace('/', ".")
            ),
        );
    }

    // Reconcile the introduction level with `min_api` and the `#[api(N)]` annotation.
    let min_api = declaration.min_api;
    let since = found.since;
    match (member.api, since > min_api) {
        (None, true) => {
            return error(
                span,
                format!(
                    "`{}.{lookup_key}` was introduced in API level {since}, above the minimum \
                     supported level {min_api}.\nAnnotate the declaration with `#[api({since})]` \
                     and guard every call site with `ApiLevel::<{since}>::check()`.",
                    jni_name.replace('/', ".")
                ),
            );
        }
        (Some((_, api_span)), false) => {
            return error(
                api_span,
                format!(
                    "`{}.{lookup_key}` has been available since API level {since}, which is at or \
                     below the minimum supported level {min_api}; remove `#[api(..)]`.",
                    jni_name.replace('/', ".")
                ),
            );
        }
        (Some((declared, api_span)), true) if declared != since => {
            return error(
                api_span,
                format!(
                    "`{}.{lookup_key}` was introduced in API level {since}, not {declared}",
                    jni_name.replace('/', ".")
                ),
            );
        }
        _ => {}
    }

    generate(
        declaration,
        class,
        member,
        &java_name,
        &descriptor,
        &param_types,
        return_type,
        since,
    )
}

fn generate(
    declaration: &JavaApi,
    class: &ClassDecl,
    member: &MemberDecl,
    java_name: &str,
    descriptor: &str,
    param_types: &[JType],
    return_type: JType,
    since: u32,
) -> TokenStream {
    let runtime = &declaration.runtime;
    let check = if member.may_throw {
        quote!(#runtime::checked_quiet)
    } else {
        quote!(#runtime::checked)
    };
    let class_name = &class.jni_name;
    let attrs = &member.attrs;
    let is_constructor = java_name == "<init>";

    // The `ApiLevel<N>` witness parameter, present only for members above `min_api`.
    let api_param = member.api.map(|(level, _)| {
        let level = LitInt::new(&level.to_string(), Span::call_site());
        quote!(_api: #runtime::ApiLevel<#level>,)
    });

    let doc = {
        let java = class.jni_name.value().replace('/', ".");
        let availability = if member.api.is_some() {
            format!(
                "\n\nIntroduced in API level {since}; requires an [`ApiLevel<{since}>`] witness."
            )
        } else {
            format!("\n\nAvailable since API level {since}.")
        };
        format!("`{java}.{}`{availability}", member.signature.value())
    };

    let receiver =
        (!member.is_static && !is_constructor).then(|| quote!(this: &::jni::objects::JObject<'_>,));

    let param_names: Vec<Ident> = if member.kind == MemberKind::Method {
        member.params.clone()
    } else {
        vec![format_ident!("value")]
    };
    let params: Vec<TokenStream> = param_names
        .iter()
        .zip(param_types)
        .map(|(name, kind)| {
            let ty = kind.param_type();
            quote!(#name: #ty)
        })
        .collect();
    let args: Vec<TokenStream> = param_names
        .iter()
        .zip(param_types)
        .map(|(name, kind)| kind.to_jvalue(name))
        .collect();

    let name = &member.name;
    let return_ty = return_type.return_type();

    match member.kind {
        MemberKind::Method if is_constructor => {
            quote! {
                #(#attrs)*
                #[doc = #doc]
                #[inline]
                pub fn #name<'local>(
                    env: &mut ::jni::JNIEnv<'local>,
                    #api_param
                    #(#params),*
                ) -> ::jni::errors::Result<::jni::objects::JObject<'local>> {
                    let __result = env.new_object(#class_name, #descriptor, &[#(#args),*]);
                    #check(env, __result)
                }
            }
        }
        MemberKind::Method => {
            let call = if member.is_static {
                quote!(env.call_static_method(#class_name, #java_name, #descriptor, &[#(#args),*]))
            } else {
                quote!(env.call_method(this, #java_name, #descriptor, &[#(#args),*]))
            };
            let extract = return_type.from_jvalue(quote!(__value));
            quote! {
                #(#attrs)*
                #[doc = #doc]
                #[inline]
                pub fn #name<'local>(
                    env: &mut ::jni::JNIEnv<'local>,
                    #api_param
                    #receiver
                    #(#params),*
                ) -> ::jni::errors::Result<#return_ty> {
                    let __result = #call;
                    let __value = #check(env, __result)?;
                    #extract
                }
            }
        }
        MemberKind::Field => {
            let setter = format_ident!("set_{}", name);
            let extract = return_type.from_jvalue(quote!(__value));
            let get = if member.is_static {
                quote!(env.get_static_field(#class_name, #java_name, #descriptor))
            } else {
                quote!(env.get_field(this, #java_name, #descriptor))
            };
            let set = if member.is_static {
                // Static field writes are not generated: nothing in this crate needs them, and the
                // JNI helper for them differs enough to not be worth generating unused.
                None
            } else {
                let value_arg = &param_names[0];
                let value_ty = return_type.param_type();
                let value_jvalue = return_type.to_jvalue(value_arg);
                Some(quote! {
                    #[doc = #doc]
                    #[inline]
                    pub fn #setter<'local>(
                        env: &mut ::jni::JNIEnv<'local>,
                        #api_param
                        this: &::jni::objects::JObject<'_>,
                        #value_arg: #value_ty,
                    ) -> ::jni::errors::Result<()> {
                        let __result = env.set_field(this, #java_name, #descriptor, #value_jvalue);
                        #check(env, __result)
                    }
                })
            };
            let receiver = (!member.is_static).then(|| quote!(this: &::jni::objects::JObject<'_>,));
            quote! {
                #(#attrs)*
                #[doc = #doc]
                #[inline]
                pub fn #name<'local>(
                    env: &mut ::jni::JNIEnv<'local>,
                    #api_param
                    #receiver
                ) -> ::jni::errors::Result<#return_ty> {
                    let __result = #get;
                    let __value = #check(env, __result)?;
                    #extract
                }

                #set
            }
        }
    }
}

fn error(span: Span, message: impl std::fmt::Display) -> TokenStream {
    syn::Error::new(span, message).to_compile_error()
}

#[cfg(test)]
mod tests {
    use super::*;

    const METADATA: &str = "\
C android/media/MediaFormat 16
X java/lang/Object
M <init>()V 16
M containsKey(Ljava/lang/String;)Z 16
M createVideoFormat(Ljava/lang/String;II)Landroid/media/MediaFormat; 16
M getKeys()Ljava/util/Set; 29
F flags 16
C java/lang/Object 1
";

    /// Expands one class declaration and returns the generated tokens as text.
    fn expand_one(source: &str) -> String {
        let declaration: JavaApi = syn::parse_str(&format!(
            "metadata = \"unused\"; min_api = 26; runtime = crate::rt; {source}"
        ))
        .expect("the declaration should parse");
        let metadata = Metadata::parse(METADATA, "test").expect("the metadata should parse");
        let class = &declaration.classes[0];
        let jni_name = class.jni_name.value();
        class
            .members
            .iter()
            .map(|member| {
                expand_member(&declaration, &metadata, class, &jni_name, member).to_string()
            })
            .collect::<Vec<_>>()
            .join("\n")
    }

    /// The JNI descriptor is only the `(arguments)return` part; passing the declaration verbatim
    /// would hand `call_method` a signature with the method name still attached.
    #[test]
    fn emits_the_jni_descriptor_without_the_member_name() {
        let generated = expand_one(
            r#"class MediaFormat = "android/media/MediaFormat" {
                fn new() = "<init>()V";
                static fn create_video_format(mime, width, height) =
                    "createVideoFormat(Ljava/lang/String;II)Landroid/media/MediaFormat;";
                fn contains_key(key) = "containsKey(Ljava/lang/String;)Z";
                #[api(29)]
                fn get_keys() = "getKeys()Ljava/util/Set;";
                field flags = "flags:I";
            }"#,
        );

        assert!(
            generated.contains(r#""containsKey" , "(Ljava/lang/String;)Z""#),
            "{generated}"
        );
        assert!(
            generated.contains(
                r#""createVideoFormat" , "(Ljava/lang/String;II)Landroid/media/MediaFormat;""#
            ),
            "{generated}"
        );
        assert!(
            generated.contains(r#""getKeys" , "()Ljava/util/Set;""#),
            "{generated}"
        );
        assert!(
            generated.contains(r#"new_object ("android/media/MediaFormat" , "()V""#),
            "{generated}"
        );
        assert!(generated.contains(r#""flags" , "I""#), "{generated}");
        // The declaration string itself must never reach a JNI call.
        assert!(
            !generated.contains(r#""getKeys()Ljava/util/Set;""#),
            "{generated}"
        );
        assert!(!generated.contains(r#""<init>()V" ,"#), "{generated}");
        assert!(!generated.contains(r#""flags:I""#), "{generated}");
    }

    #[test]
    fn requires_an_api_witness_above_min_api() {
        let generated = expand_one(
            r#"class MediaFormat = "android/media/MediaFormat" {
                fn contains_key(key) = "containsKey(Ljava/lang/String;)Z";
                #[api(29)]
                fn get_keys() = "getKeys()Ljava/util/Set;";
            }"#,
        );
        assert!(
            generated.contains("_api : crate :: rt :: ApiLevel < 29 >"),
            "{generated}"
        );
        // The member available at `min_api` takes no witness.
        assert_eq!(
            generated.matches("_api : crate :: rt :: ApiLevel").count(),
            1,
            "{generated}"
        );
    }

    #[test]
    fn rejects_an_unguarded_call_to_a_newer_member() {
        let generated = expand_one(
            r#"class MediaFormat = "android/media/MediaFormat" {
                fn get_keys() = "getKeys()Ljava/util/Set;";
            }"#,
        );
        assert!(generated.contains("compile_error"), "{generated}");
        assert!(
            generated.contains("introduced in API level 29"),
            "{generated}"
        );
    }

    #[test]
    fn rejects_a_member_that_does_not_exist() {
        let generated = expand_one(
            r#"class MediaFormat = "android/media/MediaFormat" {
                fn no_such_method() = "noSuchMethod()V";
            }"#,
        );
        assert!(generated.contains("compile_error"), "{generated}");
        assert!(generated.contains("does not exist"), "{generated}");
    }

    #[test]
    fn rejects_a_redundant_api_annotation() {
        let generated = expand_one(
            r#"class MediaFormat = "android/media/MediaFormat" {
                #[api(29)]
                fn contains_key(key) = "containsKey(Ljava/lang/String;)Z";
            }"#,
        );
        assert!(generated.contains("compile_error"), "{generated}");
        assert!(generated.contains("remove `#[api(..)]`"), "{generated}");
    }
}
