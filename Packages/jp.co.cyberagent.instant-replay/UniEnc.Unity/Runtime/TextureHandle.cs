using System.Runtime.InteropServices;
using UnityEngine;

namespace UniEnc.Unity
{
    /// <summary>
    ///     Opaque handle representing a deferred reference to a <see cref="Texture" />.
    ///     The actual native texture pointer is resolved at graphics-event time,
    ///     ensuring the pointer is always fresh and the texture is still alive.
    /// </summary>
    public readonly struct TextureHandle
    {
        internal readonly nuint value;

        private TextureHandle(nuint value)
        {
            this.value = value;
        }

        /// <summary>
        ///     Creates a <see cref="TextureHandle" /> that weakly references the given texture.
        ///     The handle must eventually be consumed by the graphics-event pipeline or
        ///     freed explicitly via <see cref="Free" />.
        /// </summary>
        public static TextureHandle Alloc(Texture texture)
        {
            var handle = GCHandle.Alloc(texture, GCHandleType.Weak);
            return new TextureHandle((nuint)(nint)GCHandle.ToIntPtr(handle));
        }

        /// <summary>
        ///     Releases the underlying GCHandle without resolving the texture.
        ///     Call this when the handle will not be consumed by a graphics event (e.g., on error).
        /// </summary>
        public void Free()
        {
            if (value == 0) return;
            var handle = GCHandle.FromIntPtr((nint)value);
            handle.Free();
        }
    }
}
