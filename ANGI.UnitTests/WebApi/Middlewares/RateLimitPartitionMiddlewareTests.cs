using System.Text;
using ANGI.WebApi;
using ANGI.WebApi.Configs;
using ANGI.WebApi.Middlewares;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ANGI.UnitTests.WebApi.Middlewares;

/// <summary>Verifies email extraction and body rewinding for authentication rate-limit partitions.</summary>
public sealed class RateLimitPartitionMiddlewareTests
{
    // TEST-01: Verify that the rate-limit middleware normalizes email and rewinds the request body.
    /// <summary>Verifies email partition extraction preserves the request body for model binding.</summary>
    [Fact]
    public async Task RateLimitPartitionMiddleware_ShouldNormalizeEmailAndRewindBody()
    {
        const string requestJson = "{\"email\":\"  User@ANGI.Test  \",\"password\":\"secret\"}";
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/auth/login";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        var middleware = new RateLimitPartitionMiddleware();

        await middleware.InvokeAsync(context, async nextContext =>
        {
            nextContext.Items[RateLimitPartitionMiddleware.EmailItemKey]
                .Should().Be("user@angi.test");

            using var reader = new StreamReader(
                nextContext.Request.Body,
                Encoding.UTF8,
                leaveOpen: true);
            (await reader.ReadToEndAsync()).Should().Be(requestJson);
        });
    }
}
