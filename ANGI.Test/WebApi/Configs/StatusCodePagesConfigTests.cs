using System.Text.Json;
using ANGI.WebApi.Configs;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace ANGI.Test.WebApi.Configs
{
    public sealed class StatusCodePagesConfigTests
    {
        // TEST-01: Wrap an empty 404 from routing as ROUTE_NOT_FOUND.
        [Fact]
        public async Task WriteErrorAsync_With404_ShouldWriteRouteNotFound()
        {
            var context = CreateContext(StatusCodes.Status404NotFound);

            await StatusCodePagesConfig.WriteErrorAsync(context);

            var root = await ReadBodyAsync(context);
            root.GetProperty("success").GetBoolean().Should().BeFalse();
            root.GetProperty("errorCode").GetString().Should().Be("ROUTE_NOT_FOUND");
            root.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        }

        // TEST-02: Wrap an empty 405 from routing as METHOD_NOT_ALLOWED.
        [Fact]
        public async Task WriteErrorAsync_With405_ShouldWriteMethodNotAllowed()
        {
            var context = CreateContext(StatusCodes.Status405MethodNotAllowed);

            await StatusCodePagesConfig.WriteErrorAsync(context);

            var root = await ReadBodyAsync(context);
            root.GetProperty("errorCode").GetString().Should().Be("METHOD_NOT_ALLOWED");
        }

        // TEST-03: Leave other statuses untouched.
        [Fact]
        public async Task WriteErrorAsync_WithOtherStatus_ShouldWriteNothing()
        {
            var context = CreateContext(StatusCodes.Status400BadRequest);

            await StatusCodePagesConfig.WriteErrorAsync(context);

            context.Response.Body.Length.Should().Be(0);
        }

        private static DefaultHttpContext CreateContext(int statusCode)
        {
            var context = new DefaultHttpContext();
            context.Response.StatusCode = statusCode;
            context.Response.Body = new MemoryStream();
            return context;
        }

        private static async Task<JsonElement> ReadBodyAsync(HttpContext context)
        {
            context.Response.Body.Position = 0;
            using var document = await JsonDocument.ParseAsync(context.Response.Body);
            return document.RootElement.Clone();
        }
    }
}
