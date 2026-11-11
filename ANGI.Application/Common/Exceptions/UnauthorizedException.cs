namespace ANGI.Application.Common.Exceptions
{
    public class UnauthorizedException : AppException
    {
        public UnauthorizedException(string errorCode, string message) : base(errorCode, message) { }
    }
}
