using System.Runtime.CompilerServices;

// Tests need the internal EncodedFrame factory without widening UniEnc's public API.
[assembly: InternalsVisibleTo("InstantReplay.Editor.Tests")]
