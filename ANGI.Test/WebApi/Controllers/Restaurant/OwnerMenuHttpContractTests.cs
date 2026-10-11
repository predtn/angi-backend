using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.UseCases.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.WebApi.Controllers.Restaurant;
using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace ANGI.Test.WebApi.Controllers.Restaurant;

public sealed class OwnerMenuHttpContractTests : IClassFixture<OwnerMenuApiFactory>
{
    private readonly OwnerMenuApiFactory _factory;

    public OwnerMenuHttpContractTests(OwnerMenuApiFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<HttpMethod, string, string?> OwnerEndpoints => new()
    {
        { HttpMethod.Get, "/api/v1/owner/restaurant/menu", null },
        { HttpMethod.Put, "/api/v1/owner/restaurant/menu/dishes/1/availability", "{}" },
        { HttpMethod.Post, "/api/v1/owner/restaurant/menu-submissions", null },
        { HttpMethod.Get, "/api/v1/owner/restaurant/menu-submissions", null },
        { HttpMethod.Get, "/api/v1/owner/restaurant/menu-submissions/1", null },
        { HttpMethod.Post, "/api/v1/owner/restaurant/menu-submissions/1/items", "{}" },
        { HttpMethod.Patch, "/api/v1/owner/restaurant/menu-submissions/1/items/1", "{}" },
        { HttpMethod.Delete, "/api/v1/owner/restaurant/menu-submissions/1/items/1", null },
        { HttpMethod.Post, "/api/v1/owner/restaurant/menu-submissions/1/cancel", null }
    };

    // TEST-01: Return the canonical 401 envelope for all nine endpoints without a token.
    [Theory]
    [MemberData(nameof(OwnerEndpoints))]
    public async Task Endpoint_WithoutToken_ShouldReturnStandardUnauthorizedEnvelope(
        HttpMethod method,
        string path,
        string? body)
    {
        using var request = Request(method, path, body);

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            await response.Content.ReadAsStringAsync());
        await AssertErrorEnvelope(response, "UNAUTHORIZED");
    }

    // TEST-02: Return the canonical 403 envelope for all nine endpoints with the wrong role.
    [Theory]
    [MemberData(nameof(OwnerEndpoints))]
    public async Task Endpoint_WithWrongRole_ShouldReturnStandardForbiddenEnvelope(
        HttpMethod method,
        string path,
        string? body)
    {
        using var request = Request(method, path, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("USER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            await response.Content.ReadAsStringAsync());
        await AssertErrorEnvelope(response, "FORBIDDEN");
    }

    // TEST-03: Return VALIDATION_FAILED when a body endpoint receives no body.
    [Theory]
    [InlineData("/api/v1/owner/restaurant/menu/dishes/1/availability", "PUT")]
    [InlineData("/api/v1/owner/restaurant/menu-submissions/1/items", "POST")]
    [InlineData("/api/v1/owner/restaurant/menu-submissions/1/items/1", "PATCH")]
    public async Task BodyEndpoint_WithMissingBody_ShouldReturnValidationEnvelope(string path, string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("RESTAURANT_OWNER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            await response.Content.ReadAsStringAsync());
        await AssertErrorEnvelope(response, "VALIDATION_FAILED");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("message").GetString()
            .Should().Be("Dữ liệu không hợp lệ.");
        document.RootElement.GetProperty("errors").GetProperty("body")[0].GetString()
            .Should().Be("Nội dung JSON là bắt buộc và phải có Content-Type application/json.");
    }

    // TEST-04: Return VALIDATION_FAILED when a body endpoint receives malformed JSON.
    [Theory]
    [InlineData("/api/v1/owner/restaurant/menu/dishes/1/availability", "PUT")]
    [InlineData("/api/v1/owner/restaurant/menu-submissions/1/items", "POST")]
    [InlineData("/api/v1/owner/restaurant/menu-submissions/1/items/1", "PATCH")]
    public async Task BodyEndpoint_WithMalformedJson_ShouldReturnValidationEnvelope(string path, string method)
    {
        using var request = Request(new HttpMethod(method), path, "{");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("RESTAURANT_OWNER"));

        using var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            await response.Content.ReadAsStringAsync());
        await AssertErrorEnvelope(response, "VALIDATION_FAILED");
    }

    // TEST-05: Resolve controller dependencies, use cases, validators, and repository from DI.
    [Fact]
    public void Services_ShouldResolveEveryControllerDependencyUseCaseValidatorAndRepository()
    {
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider;
        var controllerDependencies = typeof(OwnerMenuController).GetConstructors().Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType);

        controllerDependencies.Select(type => provider.GetService(type)).Should().NotContainNulls();
        provider.GetRequiredService<IValidator<AddMenuSubmissionItemRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<UpdateMenuSubmissionItemRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IValidator<ListMenuSubmissionsRequestDto>>().Should().NotBeNull();
        provider.GetRequiredService<IRestaurantMenuRepository>().Should().NotBeNull();
    }

    // TEST-06: Declare exactly nine documented actions with response schemas.
    [Fact]
    public void Controller_ShouldDeclareExactlyNineDocumentedRoutesAndResponseSchemas()
    {
        var actions = typeof(OwnerMenuController).GetMethods()
            .Where(method => method.DeclaringType == typeof(OwnerMenuController))
            .Where(method => method.GetCustomAttributes(typeof(HttpMethodAttribute), true).Length > 0)
            .ToArray();

        actions.Should().HaveCount(9);
        actions.Should().OnlyContain(method =>
            method.GetCustomAttributes(typeof(ProducesResponseTypeAttribute), true).Length > 0);
    }

    // TEST-07: Publish all nine controller operations through the runtime endpoint data source.
    [Fact]
    public void RuntimeEndpointDataSource_ShouldExposeAllNineOwnerMenuOperations()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?
                .ControllerTypeInfo.AsType() == typeof(OwnerMenuController))
            .ToArray();

        endpoints.Should().HaveCount(9);
        endpoints.Should().OnlyContain(endpoint =>
            endpoint.Metadata.GetMetadata<ProducesResponseTypeAttribute>() != null);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static async Task AssertErrorEnvelope(HttpResponseMessage response, string errorCode)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("errorCode").GetString().Should().Be(errorCode);
        document.RootElement.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private static string Token(string role)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(OwnerMenuApiFactory.Secret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            OwnerMenuApiFactory.Issuer,
            OwnerMenuApiFactory.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "12"), new Claim("role", role)],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    internal sealed class NoOpAccountCheck : IEnsureActiveAccountUseCase
    {
        public Task ExecuteAsync(int userId, CancellationToken ct) => Task.CompletedTask;
    }
}

public sealed class OwnerMenuApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "ANGI.HttpTests";
    public const string Audience = "ANGI.HttpTests.Client";
    public const string Secret = "ANGI-53-http-contract-test-secret-key-123456789";

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var previousIssuer = Environment.GetEnvironmentVariable("Jwt__Issuer");
        var previousAudience = Environment.GetEnvironmentVariable("Jwt__Audience");
        var previousSecret = Environment.GetEnvironmentVariable("Jwt__SecretKey");
        Environment.SetEnvironmentVariable("Jwt__Issuer", Issuer);
        Environment.SetEnvironmentVariable("Jwt__Audience", Audience);
        Environment.SetEnvironmentVariable("Jwt__SecretKey", Secret);
        try
        {
            return base.CreateHost(builder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Jwt__Issuer", previousIssuer);
            Environment.SetEnvironmentVariable("Jwt__Audience", previousAudience);
            Environment.SetEnvironmentVariable("Jwt__SecretKey", previousSecret);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:SecretKey"] = Secret,
                ["ConnectionStrings:Default"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEnsureActiveAccountUseCase>();
            services.AddScoped<IEnsureActiveAccountUseCase, OwnerMenuHttpContractTests.NoOpAccountCheck>();
        });
    }
}
