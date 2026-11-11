namespace ANGI.Application.Common.Exceptions
{
    public class ServiceUnavailableException : AppException
    {
        public ServiceUnavailableException(string errorCode, string message) : base(errorCode, message) { }
    }
}
