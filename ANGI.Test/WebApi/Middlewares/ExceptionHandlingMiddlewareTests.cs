using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.WebApi.Middlewares;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace ANGI.Test.WebApi.Middlewares
{
    public sealed class ExceptionHandlingMiddlewareTests
    {
        // TEST-01: Return the suspension deadline as structured error data for ACCOUNT_SUSPENDED.
        [Fact]
        public async Task InvokeAsync_WithAccountSuspendedException_ShouldReturnSuspendedUntil()
        {
            var suspendedUntil = new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);
            var middleware = new ExceptionHandlingMiddleware(new Mock<ILogger<ExceptionHandlingMiddleware>>().Object);
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(
                context,
                _ => throw new AccountSuspendedException(suspendedUntil));

            context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            context.Response.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(context.Response.Body);
            var root = document.RootElement;
            root.GetProperty("success").GetBoolean().Should().BeFalse();
            root.GetProperty("errorCode").GetString().Should().Be("ACCOUNT_SUSPENDED");
            root.GetProperty("data").GetProperty("suspendedUntil").GetDateTime().Should().Be(suspendedUntil);
        }
    }
}
