namespace ANGI.Application.Common.Exceptions
{
    public abstract class AppException : Exception
    {
        public string ErrorCode { get; }

        protected AppException(string errorCode, string message) : base(message) => ErrorCode = errorCode;
    }
}
