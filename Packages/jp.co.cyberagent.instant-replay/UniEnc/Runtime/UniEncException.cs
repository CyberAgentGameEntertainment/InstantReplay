using System;

namespace UniEnc
{
    /// <summary>
    ///     Exception thrown when UniEnc operations fail.
    /// </summary>
    public class UniEncException : Exception
    {
        /// <summary>
        ///     Creates a new UniEncException.
        /// </summary>
        internal UniEncException(UniencErrorKind errorKind, string message)
            : base(FormatMessage(errorKind, message))
        {
            ErrorKind = errorKind;
        }

        /// <summary>
        ///     Creates a new UniEncException with an inner exception.
        /// </summary>
        internal UniEncException(UniencErrorKind errorKind, string message, Exception innerException)
            : base(FormatMessage(errorKind, message), innerException)
        {
            ErrorKind = errorKind;
        }

        /// <summary>
        ///     The kind of error that occurred.
        /// </summary>
        internal UniencErrorKind ErrorKind { get; }

        private static string FormatMessage(UniencErrorKind errorKind, string message)
        {
            return string.IsNullOrEmpty(message)
                ? $"UniEnc error: {errorKind}"
                : $"UniEnc error ({errorKind}): {message}";
        }
    }
}
