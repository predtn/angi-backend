namespace ANGI.Application.Common.Exceptions
{
    public class ForbiddenException : AppException
    {
        public ForbiddenException(string errorCode, string message) : base(errorCode, message) { }
    }
}
