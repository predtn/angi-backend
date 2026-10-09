namespace ANGI.Application.Common.Exceptions
{
    /// <summary>
    /// Represents a database unique-constraint violation without leaking a persistence provider
    /// into the Application layer.
    /// </summary>
    public sealed class UniqueConstraintViolationException : Exception
    {
        public UniqueConstraintViolationException(string? constraintName, Exception innerException)
            : base("A unique database constraint was violated.", innerException)
        {
            ConstraintName = constraintName;
        }

        public string? ConstraintName { get; }
    }
}
