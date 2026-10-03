namespace ANGI.Application.Common.Exceptions
{
    public class BadRequestException : AppException
    {
        public BadRequestException(string errorCode, string message) : base(errorCode, message) { }
    }
}
