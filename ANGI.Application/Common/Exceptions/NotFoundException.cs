namespace ANGI.Application.Common.Exceptions
{
    public class NotFoundException : AppException
    {
        public NotFoundException(string errorCode, string message) : base(errorCode, message) { }
    }
}
