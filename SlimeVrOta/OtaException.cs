namespace SlimeVrOta
{
    public class OtaException : Exception
    {
        public OtaException() { }

        public OtaException(string? message)
            : base(message) { }

        public OtaException(string? message, Exception? innerException)
            : base(message, innerException) { }
    }
}
