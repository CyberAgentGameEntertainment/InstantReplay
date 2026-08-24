//! Reader for the compact `api-versions.txt` produced by the `android_api_metadata` tool.

use std::collections::HashMap;
use std::path::Path;

#[derive(Debug, Clone, Copy)]
pub struct Member {
    pub since: u32,
    pub removed: Option<u32>,
}

#[derive(Debug, Default)]
pub struct Class {
    pub since: u32,
    pub supers: Vec<String>,
    pub methods: HashMap<String, Member>,
    pub fields: HashMap<String, Member>,
}

#[derive(Debug, Default)]
pub struct Metadata {
    pub platform: String,
    pub classes: HashMap<String, Class>,
}

/// Where a member lookup found the member, and at which API level it became available.
pub struct Found {
    /// API level at which the member can be called through the receiver class. This is the later
    /// of the receiver class' own introduction and the member's own introduction.
    pub since: u32,
    pub removed: Option<u32>,
}

impl Metadata {
    pub fn load(path: &Path) -> Result<Self, String> {
        let text = std::fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
        Self::parse(&text, &path.display().to_string())
    }

    /// Parses the metadata format. `source` only appears in error messages.
    pub fn parse(text: &str, source: &str) -> Result<Self, String> {
        let mut metadata = Metadata::default();
        let mut current: Option<String> = None;

        for (index, line) in text.lines().enumerate() {
            let line = line.trim();
            if line.is_empty() || line.starts_with('#') {
                continue;
            }
            let number = index + 1;
            let mut parts = line.split(' ');
            let kind = parts.next().unwrap_or("");
            let at = |what: &str| format!("{source}:{number}: malformed {what} entry");

            match kind {
                "!platform" => {
                    metadata.platform = parts.next().unwrap_or("").to_owned();
                }
                "C" => {
                    let name = parts.next().ok_or_else(|| at("class"))?.to_owned();
                    let since = parse_level(parts.next()).ok_or_else(|| at("class"))?;
                    metadata.classes.insert(
                        name.clone(),
                        Class {
                            since,
                            ..Default::default()
                        },
                    );
                    current = Some(name);
                }
                "X" => {
                    let name = parts.next().ok_or_else(|| at("supertype"))?.to_owned();
                    let class = current
                        .as_ref()
                        .and_then(|c| metadata.classes.get_mut(c))
                        .ok_or_else(|| at("supertype"))?;
                    class.supers.push(name);
                }
                "M" | "F" => {
                    let name = parts.next().ok_or_else(|| at("member"))?.to_owned();
                    let since = parse_level(parts.next()).ok_or_else(|| at("member"))?;
                    let removed = parts.next().and_then(|s| s.parse().ok());
                    let class = current
                        .as_ref()
                        .and_then(|c| metadata.classes.get_mut(c))
                        .ok_or_else(|| at("member"))?;
                    let member = Member { since, removed };
                    if kind == "M" {
                        class.methods.insert(name, member);
                    } else {
                        class.fields.insert(name, member);
                    }
                }
                _ => return Err(format!("{source}:{number}: unknown entry `{kind}`")),
            }
        }

        Ok(metadata)
    }

    /// Looks a method or field up on `class`, walking supertypes.
    ///
    /// `key` is `name(arguments)return` for methods and the plain name for fields, matching the
    /// keys used by `api-versions.xml`.
    pub fn find(&self, class: &str, key: &str, is_method: bool) -> Option<Found> {
        let receiver = self.classes.get(class)?;

        let mut visited = vec![class.to_owned()];
        let mut queue = vec![class.to_owned()];
        while let Some(name) = queue.pop() {
            let Some(info) = self.classes.get(&name) else {
                continue;
            };
            let table = if is_method {
                &info.methods
            } else {
                &info.fields
            };
            if let Some(member) = table.get(key) {
                return Some(Found {
                    since: member.since.max(receiver.since),
                    removed: member.removed,
                });
            }
            for parent in &info.supers {
                if !visited.contains(parent) {
                    visited.push(parent.clone());
                    queue.push(parent.clone());
                }
            }
        }
        None
    }

    /// True when the class hierarchy of `class` is fully present in the metadata subset.
    ///
    /// A missing supertype would make [`Self::find`] report a false "does not exist", so it has to
    /// be reported as a metadata problem rather than as a declaration problem.
    pub fn missing_supertypes(&self, class: &str) -> Vec<String> {
        let mut missing = Vec::new();
        let mut visited = vec![class.to_owned()];
        let mut queue = vec![class.to_owned()];
        while let Some(name) = queue.pop() {
            let Some(info) = self.classes.get(&name) else {
                missing.push(name);
                continue;
            };
            for parent in &info.supers {
                if !visited.contains(parent) {
                    visited.push(parent.clone());
                    queue.push(parent.clone());
                }
            }
        }
        missing
    }
}

fn parse_level(value: Option<&str>) -> Option<u32> {
    value?.parse().ok()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The metadata subset vendored for `unienc_android_mc`.
    fn vendored() -> Metadata {
        let path = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../unienc_android_mc/java-api/android-api-versions.txt");
        Metadata::load(&path).expect("the vendored metadata should parse")
    }

    #[test]
    fn reports_the_level_a_member_was_introduced_in() {
        let metadata = vendored();
        let format = "android/media/MediaFormat";
        assert_eq!(
            metadata
                .find(format, "containsKey(Ljava/lang/String;)Z", true)
                .unwrap()
                .since,
            16
        );
        // The method whose unguarded use this whole mechanism exists to prevent.
        assert_eq!(
            metadata
                .find(format, "getKeys()Ljava/util/Set;", true)
                .unwrap()
                .since,
            29
        );
        assert!(metadata.find(format, "noSuchMethod()V", true).is_none());
    }

    #[test]
    fn finds_members_declared_on_supertypes() {
        let metadata = vendored();
        // Buffer.position(int), reached through ByteBuffer.
        assert_eq!(
            metadata
                .find("java/nio/ByteBuffer", "position(I)Ljava/nio/Buffer;", true)
                .unwrap()
                .since,
            1
        );
        // AutoCloseable.close(), reached through HardwareBuffer: the interface has been around
        // since API 19, but the class only since 26, so 26 is the level that matters.
        assert_eq!(
            metadata
                .find("android/hardware/HardwareBuffer", "close()V", true)
                .unwrap()
                .since,
            26
        );
    }

    #[test]
    fn reports_fields() {
        let metadata = vendored();
        assert_eq!(
            metadata
                .find(
                    "android/media/MediaCodec$BufferInfo",
                    "presentationTimeUs",
                    false
                )
                .unwrap()
                .since,
            16
        );
        assert_eq!(
            metadata
                .find("android/os/Build$VERSION", "SDK_INT", false)
                .unwrap()
                .since,
            4
        );
    }

    #[test]
    fn the_vendored_subset_is_closed_over_supertypes() {
        let metadata = vendored();
        for class in metadata.classes.keys() {
            assert_eq!(metadata.missing_supertypes(class), Vec::<String>::new());
        }
    }
}
