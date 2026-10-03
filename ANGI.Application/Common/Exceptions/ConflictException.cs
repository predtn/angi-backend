namespace ANGI.Application.Common.Exceptions
{
    public class ConflictException : AppException
    {
        public ConflictException(string errorCode, string message) : base(errorCode, message) { }
    }
}
