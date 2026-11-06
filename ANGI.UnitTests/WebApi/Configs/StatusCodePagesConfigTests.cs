using System.Text.Json;
using ANGI.WebApi.Configs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace ANGI.UnitTests.WebApi.Configs
{
    /// <summary>Verifies StatusCodePagesConfig wraps empty routing 404 and 405 responses in the API error envelope.</summary>
    public sealed class StatusCodePagesConfigTests
    {
        // TEST-01: Wrap an empty 404 from routing as ROUTE_NOT_FOUND.
        /// <summary>Ensures an unknown route returns the documented ROUTE_NOT_FOUND envelope.</summary>
        [Fact]
        public async Task WriteErrorAsync_With404_ShouldWriteRouteNotFound()
        {
            // STEP-01: Create context through CreateContext and call StatusCodePagesConfig.WriteErrorAsync.
            var context = CreateContext(StatusCodes.Status404NotFound);

            await StatusCodePagesConfig.WriteErrorAsync(context);

            // STEP-02: Read root through ReadBodyAsync; assert root.success is false, root.errorCode is "ROUTE_NOT_FOUND" and root.data.ValueKind is JsonValueKind.Null.
            var root = await ReadBodyAsync(context);
            root.GetProperty("success").GetBoolean().Should().BeFalse();
            root.GetProperty("errorCode").GetString().Should().Be("ROUTE_NOT_FOUND");
            root.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        }

        // TEST-02: Wrap an empty 405 from routing as METHOD_NOT_ALLOWED.
        /// <summary>Ensures a wrong HTTP method returns the documented METHOD_NOT_ALLOWED envelope.</summary>
        [Fact]
        public async Task WriteErrorAsync_With405_ShouldWriteMethodNotAllowed()
        {
            // STEP-01: Create context through CreateContext and call StatusCodePagesConfig.WriteErrorAsync.
            var context = CreateContext(StatusCodes.Status405MethodNotAllowed);

            await StatusCodePagesConfig.WriteErrorAsync(context);

            // STEP-02: Read root through ReadBodyAsync; assert root.errorCode is "METHOD_NOT_ALLOWED".
            var root = await ReadBodyAsync(context);
            root.GetProperty("errorCode").GetString().Should().Be("METHOD_NOT_ALLOWED");
        }

        // TEST-03: Leave other statuses untouched.
        /// <summary>Ensures responses with other statuses are not rewritten.</summary>
        [Fact]
        public async Task WriteErrorAsync_WithOtherStatus_ShouldWriteNothing()
        {
            // STEP-01: Create context through CreateContext and call StatusCodePagesConfig.WriteErrorAsync.
            var context = CreateContext(StatusCodes.Status400BadRequest);

            await StatusCodePagesConfig.WriteErrorAsync(context);

            // STEP-02: Assert context.Response.Body.Length is 0.
            context.Response.Body.Length.Should().Be(0);
        }

        /// <summary>Creates an HTTP context with the given status and a readable response body.</summary>
        private static DefaultHttpContext CreateContext(int statusCode)
        {
            var context = new DefaultHttpContext();
            context.Response.StatusCode = statusCode;
            context.Response.Body = new MemoryStream();
            return context;
        }

        /// <summary>Reads the response body written so far as JSON.</summary>
        private static async Task<JsonElement> ReadBodyAsync(HttpContext context)
        {
            context.Response.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(context.Response.Body);
            return document.RootElement.Clone();
        }
    }
}
