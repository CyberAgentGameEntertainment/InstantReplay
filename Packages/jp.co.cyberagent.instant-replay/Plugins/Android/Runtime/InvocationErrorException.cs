#nullable enable

using System;

namespace AndroidBindgen
{
    public class InvocationErrorException : Exception
    {
        public InvocationErrorException()
        {
        }

        public InvocationErrorException(string message) : base(message)
        {
        }

        public InvocationErrorException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
