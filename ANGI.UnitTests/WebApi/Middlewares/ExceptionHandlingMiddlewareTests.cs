using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.WebApi.Middlewares;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace ANGI.UnitTests.WebApi.Middlewares
{
    public sealed class ExceptionHandlingMiddlewareTests
    {
        // TEST-01: Return the suspension deadline as structured error data for ACCOUNT_SUSPENDED.
        /// <summary>Verifies the account suspension response retains its deadline.</summary>
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

        // TEST-02: Return the existing menu submission id for SUBMISSION_ALREADY_OPEN.
        /// <summary>Verifies the menu workflow conflict exposes the draft the client should reopen.</summary>
        [Fact]
        public async Task InvokeAsync_WithSubmissionAlreadyOpenException_ShouldReturnSubmissionId()
        {
            var middleware = new ExceptionHandlingMiddleware(new Mock<ILogger<ExceptionHandlingMiddleware>>().Object);
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(
                context,
                _ => throw new SubmissionAlreadyOpenException(14));

            context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
            context.Response.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(context.Response.Body);
            var root = document.RootElement;
            root.GetProperty("errorCode").GetString().Should().Be("SUBMISSION_ALREADY_OPEN");
            root.GetProperty("data").GetProperty("id").GetInt64().Should().Be(14);
        }

        // TEST-03: Return a generic INTERNAL_ERROR without exposing exception details.
        /// <summary>Verifies unexpected failures produce a safe HTTP 500 response.</summary>
        [Fact]
        public async Task InvokeAsync_WithUnexpectedException_ShouldReturnSafeInternalError()
        {
            var middleware = new ExceptionHandlingMiddleware(new Mock<ILogger<ExceptionHandlingMiddleware>>().Object);
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(
                context,
                _ => throw new InvalidOperationException("database password must not be exposed"));

            context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
            context.Response.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(context.Response.Body);
            var root = document.RootElement;
            root.GetProperty("success").GetBoolean().Should().BeFalse();
            root.GetProperty("errorCode").GetString().Should().Be("INTERNAL_ERROR");
            root.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
            root.ToString().Should().NotContain("database password");
        }

        // TEST-04: Key nested validation errors by the camelCase path of the JSON body.
        /// <summary>Verifies every segment of a nested FluentValidation property path is camel-cased.</summary>
        [Fact]
        public async Task InvokeAsync_WithNestedValidationFailure_ShouldUseCamelCasePath()
        {
            var middleware = new ExceptionHandlingMiddleware(new Mock<ILogger<ExceptionHandlingMiddleware>>().Object);
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(
                context,
                _ => throw new FluentValidation.ValidationException(
                [
                    new FluentValidation.Results.ValidationFailure("Answers[0].ScaleValue", "Mức độ phải nằm trong khoảng từ 0 đến 1."),
                    new FluentValidation.Results.ValidationFailure("Answers", "Khảo sát phải có từ 1 đến 50 câu trả lời.")
                ]));

            context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            context.Response.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(context.Response.Body);
            var errors = document.RootElement.GetProperty("errors");
            errors.EnumerateObject().Select(property => property.Name)
                .Should().BeEquivalentTo("answers[0].scaleValue", "answers");
        }
    }
}
