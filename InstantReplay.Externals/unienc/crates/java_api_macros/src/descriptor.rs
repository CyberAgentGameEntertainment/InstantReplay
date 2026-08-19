//! Minimal JVM type-descriptor parser.

use proc_macro2::TokenStream;
use quote::quote;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum JType {
    Void,
    Bool,
    Byte,
    Char,
    Short,
    Int,
    Long,
    Float,
    Double,
    /// `Ljava/lang/String;`, mapped to `JString` so that call sites do not have to convert.
    String,
    /// Any other reference type, including arrays.
    Object,
}

impl JType {
    /// Type of a function parameter carrying this value.
    pub fn param_type(self) -> TokenStream {
        match self {
            JType::Void => quote!(()),
            JType::Bool => quote!(bool),
            JType::Byte => quote!(::jni::sys::jbyte),
            JType::Char => quote!(::jni::sys::jchar),
            JType::Short => quote!(::jni::sys::jshort),
            JType::Int => quote!(::jni::sys::jint),
            JType::Long => quote!(::jni::sys::jlong),
            JType::Float => quote!(::jni::sys::jfloat),
            JType::Double => quote!(::jni::sys::jdouble),
            // Reference parameters take `&JObject`; `&JString`, `&JByteArray` and `&JByteBuffer`
            // coerce to it, so no call site needs an explicit conversion.
            JType::String | JType::Object => quote!(&::jni::objects::JObject<'_>),
        }
    }

    /// Type of the value returned by a wrapper, in the `'local` frame of the `JNIEnv`.
    pub fn return_type(self) -> TokenStream {
        match self {
            JType::Void => quote!(()),
            JType::String => quote!(::jni::objects::JString<'local>),
            JType::Object => quote!(::jni::objects::JObject<'local>),
            other => other.param_type(),
        }
    }

    /// Expression wrapping `#name` into a `JValue`.
    pub fn to_jvalue(self, name: &proc_macro2::Ident) -> TokenStream {
        match self {
            JType::Void => quote!(compile_error!("void is not a valid parameter type")),
            JType::Bool => quote!(::jni::objects::JValue::Bool(#name as ::jni::sys::jboolean)),
            JType::Byte => quote!(::jni::objects::JValue::Byte(#name)),
            JType::Char => quote!(::jni::objects::JValue::Char(#name)),
            JType::Short => quote!(::jni::objects::JValue::Short(#name)),
            JType::Int => quote!(::jni::objects::JValue::Int(#name)),
            JType::Long => quote!(::jni::objects::JValue::Long(#name)),
            JType::Float => quote!(::jni::objects::JValue::Float(#name)),
            JType::Double => quote!(::jni::objects::JValue::Double(#name)),
            JType::String | JType::Object => quote!(::jni::objects::JValue::Object(#name)),
        }
    }

    /// Expression extracting this type out of `#value`, a `JValueOwned`.
    pub fn from_jvalue(self, value: TokenStream) -> TokenStream {
        match self {
            JType::Void => quote!(#value.v()),
            JType::Bool => quote!(#value.z()),
            JType::Byte => quote!(#value.b()),
            JType::Char => quote!(#value.c()),
            JType::Short => quote!(#value.s()),
            JType::Int => quote!(#value.i()),
            JType::Long => quote!(#value.j()),
            JType::Float => quote!(#value.f()),
            JType::Double => quote!(#value.d()),
            JType::String => quote!(#value.l().map(::jni::objects::JString::from)),
            JType::Object => quote!(#value.l()),
        }
    }
}

/// Splits `name(arguments)return` into its three parts.
pub fn split_method(signature: &str) -> Result<(&str, &str, &str), String> {
    let open = signature
        .find('(')
        .ok_or_else(|| format!("`{signature}` is not a method signature (no `(`)"))?;
    let close = signature
        .find(')')
        .ok_or_else(|| format!("`{signature}` is not a method signature (no `)`)"))?;
    if close < open {
        return Err(format!("`{signature}` has `)` before `(`"));
    }
    let name = &signature[..open];
    if name.is_empty() {
        return Err(format!("`{signature}` has an empty method name"));
    }
    Ok((name, &signature[open + 1..close], &signature[close + 1..]))
}

/// Splits `name:descriptor` into its two parts.
pub fn split_field(signature: &str) -> Result<(&str, &str), String> {
    let (name, descriptor) = signature
        .split_once(':')
        .ok_or_else(|| format!("`{signature}` is not a field signature (expected `name:type`)"))?;
    if name.is_empty() || descriptor.is_empty() {
        return Err(format!("`{signature}` is not a field signature"));
    }
    Ok((name, descriptor))
}

/// Parses a sequence of type descriptors, as found between the parentheses of a method descriptor.
pub fn parse_types(descriptors: &str) -> Result<Vec<JType>, String> {
    let mut types = Vec::new();
    let mut rest = descriptors;
    while !rest.is_empty() {
        let (parsed, remainder) = parse_type(rest)?;
        types.push(parsed);
        rest = remainder;
    }
    Ok(types)
}

/// Parses exactly one type descriptor, which must span the whole input.
pub fn parse_one_type(descriptor: &str) -> Result<JType, String> {
    let (parsed, rest) = parse_type(descriptor)?;
    if !rest.is_empty() {
        return Err(format!("trailing `{rest}` after type `{descriptor}`"));
    }
    Ok(parsed)
}

fn parse_type(descriptor: &str) -> Result<(JType, &str), String> {
    let mut chars = descriptor.char_indices();
    let (_, first) = chars
        .next()
        .ok_or_else(|| "empty type descriptor".to_owned())?;
    let simple = |t: JType| Ok((t, &descriptor[1..]));
    match first {
        'V' => simple(JType::Void),
        'Z' => simple(JType::Bool),
        'B' => simple(JType::Byte),
        'C' => simple(JType::Char),
        'S' => simple(JType::Short),
        'I' => simple(JType::Int),
        'J' => simple(JType::Long),
        'F' => simple(JType::Float),
        'D' => simple(JType::Double),
        '[' => {
            // Arrays are handled as plain objects; only the element type has to be consumed.
            let (_, rest) = parse_type(&descriptor[1..])?;
            Ok((JType::Object, rest))
        }
        'L' => {
            let end = descriptor
                .find(';')
                .ok_or_else(|| format!("unterminated class type in `{descriptor}`"))?;
            let class = &descriptor[1..end];
            let kind = if class == "java/lang/String" {
                JType::String
            } else {
                JType::Object
            };
            Ok((kind, &descriptor[end + 1..]))
        }
        other => Err(format!(
            "unknown type descriptor `{other}` in `{descriptor}`"
        )),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn splits_method_signatures() {
        assert_eq!(
            split_method("createVideoFormat(Ljava/lang/String;II)Landroid/media/MediaFormat;")
                .unwrap(),
            (
                "createVideoFormat",
                "Ljava/lang/String;II",
                "Landroid/media/MediaFormat;"
            )
        );
        assert_eq!(split_method("start()V").unwrap(), ("start", "", "V"));
        assert!(split_method("start").is_err());
    }

    #[test]
    fn splits_field_signatures() {
        assert_eq!(
            split_field("presentationTimeUs:J").unwrap(),
            ("presentationTimeUs", "J")
        );
        assert!(split_field("SDK_INT").is_err());
    }

    #[test]
    fn parses_argument_lists() {
        assert_eq!(parse_types("").unwrap(), vec![]);
        assert_eq!(
            parse_types("IIIJI").unwrap(),
            vec![JType::Int, JType::Int, JType::Int, JType::Long, JType::Int]
        );
        assert_eq!(
            parse_types("Ljava/lang/String;[BZ").unwrap(),
            vec![JType::String, JType::Object, JType::Bool]
        );
    }

    #[test]
    fn maps_strings_and_arrays_to_reference_types() {
        assert_eq!(parse_one_type("Ljava/lang/String;").unwrap(), JType::String);
        assert_eq!(
            parse_one_type("[Landroid/media/Image$Plane;").unwrap(),
            JType::Object
        );
        assert_eq!(parse_one_type("V").unwrap(), JType::Void);
        assert!(parse_one_type("IJ").is_err());
        assert!(parse_one_type("Ljava/lang/String").is_err());
    }
}
