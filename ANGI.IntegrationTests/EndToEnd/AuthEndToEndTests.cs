using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ANGI.Application.Common.Models.Auth;
using ANGI.Application.DTOs.Auth;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Exercises authentication HTTP endpoints through real use cases, repositories, and PostgreSQL.</summary>
public sealed class AuthEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private const string Password = "Password123";
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with its shared real-host PostgreSQL factory.</summary>
    public AuthEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Register through HTTP, persist the pending account and token, then verify it through HTTP.
    /// <summary>Covers ANGI-29 and ANGI-58 across the HTTP pipeline and persisted account state.</summary>
    [PostgresEndToEndFact]
    public async Task RegisterAndVerifyEmail_ShouldPersistAndActivateAccount()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        const string email = "register-verify@integration.test";

        using var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterAccountRequestDto
        {
            Email = email,
            Password = Password,
            DisplayName = "Integration Traveler",
            Role = "TRAVELER"
        });

        registerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var registered = await SuccessDataAsync<RegisterAccountResponseDto>(registerResponse);
        registered.Email.Should().Be(email);
        await _factory.ExecuteDbAsync(async context =>
        {
            var user = await context.Users.SingleAsync(value => value.Email == email);
            user.Status.Should().Be(UserStatus.PendingVerification);
            (await context.UserTokens.CountAsync(value => value.UserId == user.Id)).Should().Be(1);
        });

        using var verifyResponse = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequestDto
        {
            Token = _factory.Email.VerificationToken(email)
        });

        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var verified = await SuccessDataAsync<VerifyEmailResponseDto>(verifyResponse);
        verified.Status.Should().Be("active");
        await _factory.ExecuteDbAsync(async context =>
        {
            var user = await context.Users.SingleAsync(value => value.Email == email);
            user.Status.Should().Be(UserStatus.Active);
            user.EmailVerifiedAt.Should().NotBeNull();
            (await context.UserTokens.SingleAsync()).UsedAt.Should().NotBeNull();
        });
    }

    // TEST-02: Login, rotate the refresh token, and logout through HTTP while persisting both revocations.
    /// <summary>Covers ANGI-33 with real password verification, JWT creation, and PostgreSQL sessions.</summary>
    [PostgresEndToEndFact]
    public async Task LoginRefreshAndLogout_ShouldPersistSessionLifecycle()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await RegisterVerifyAndLoginAsync(client, "session-flow@integration.test", "TRAVELER");

        using var refreshResponse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshTokenRequestDto
        {
            RefreshToken = auth.RefreshToken
        });

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshed = await SuccessDataAsync<AuthResultDto>(refreshResponse);
        refreshed.RefreshToken.Should().NotBe(auth.RefreshToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);

        using var logoutResponse = await client.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequestDto
        {
            RefreshToken = refreshed.RefreshToken
        });

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory.ExecuteDbAsync(async context =>
        {
            var sessions = await context.UserSessions.OrderBy(value => value.Id).ToArrayAsync();
            sessions.Should().HaveCount(2);
            sessions.Should().OnlyContain(value => value.RevokedAt.HasValue);
        });
    }

    // TEST-03: Authenticate a new Google identity through HTTP and persist its account, link, and session.
    /// <summary>Covers ANGI-62 while replacing only Google's external token-verification boundary.</summary>
    [PostgresEndToEndFact]
    public async Task GoogleLogin_ShouldPersistNewLinkedAccountAndSession()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        const string email = "google-user@integration.test";
        _factory.Google.Add("trusted-google-token", new GoogleIdentity("google-subject-1", email, "Google User"));

        using var response = await client.PostAsJsonAsync("/api/v1/auth/google", new GoogleLoginRequestDto
        {
            IdToken = "trusted-google-token",
            Role = "TRAVELER"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await SuccessDataAsync<GoogleLoginResponseDto>(response);
        result.IsNewUser.Should().BeTrue();
        result.AccessToken.Should().NotBeNullOrWhiteSpace();
        await _factory.ExecuteDbAsync(async context =>
        {
            var user = await context.Users.SingleAsync(value => value.Email == email);
            user.Status.Should().Be(UserStatus.Active);
            (await context.UserExternalLogins.CountAsync(value => value.UserId == user.Id)).Should().Be(1);
            (await context.UserSessions.CountAsync(value => value.UserId == user.Id)).Should().Be(1);
        });
    }

    // TEST-04: Request and consume a reset token through HTTP, changing the password and revoking every session.
    /// <summary>Covers ANGI-66 and ANGI-70 through the complete reset-password persistence workflow.</summary>
    [PostgresEndToEndFact]
    public async Task ForgotAndResetPassword_ShouldConsumeTokenChangePasswordAndRevokeSessions()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        const string email = "password-flow@integration.test";
        _ = await RegisterVerifyAndLoginAsync(client, email, "TRAVELER");

        using var forgotResponse = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequestDto
        {
            Email = email
        });
        forgotResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var resetResponse = await client.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequestDto
        {
            Token = _factory.Email.ResetToken(email),
            NewPassword = "ChangedPassword456"
        });

        resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory.ExecuteDbAsync(async context =>
        {
            var user = await context.Users.SingleAsync(value => value.Email == email);
            (await context.UserTokens.SingleAsync(value => value.Purpose == UserTokenPurpose.PasswordReset))
                .UsedAt.Should().NotBeNull();
            (await context.UserSessions.SingleAsync(value => value.UserId == user.Id))
                .RevokedAt.Should().NotBeNull();
        });

        using var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequestDto
        {
            Email = email,
            Password = "ChangedPassword456"
        });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // TEST-05: Surface a real database uniqueness conflict through the canonical middleware response.
    /// <summary>Covers ANGI-23 by proving application exceptions cross the hosted HTTP pipeline unchanged.</summary>
    [PostgresEndToEndFact]
    public async Task RegisterDuplicateEmail_ShouldReturnConflictEnvelopeWithoutSecondUser()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        const string email = "duplicate@integration.test";
        var request = new RegisterAccountRequestDto
        {
            Email = email,
            Password = Password,
            DisplayName = "Duplicate User",
            Role = "TRAVELER"
        };
        using var first = await client.PostAsJsonAsync("/api/v1/auth/register", request);

        using var second = await client.PostAsJsonAsync("/api/v1/auth/register", request);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var envelope = await second.Content.ReadFromJsonAsync<ApiResponse<object>>();
        envelope.Should().NotBeNull();
        envelope!.ErrorCode.Should().Be("EMAIL_ALREADY_EXISTS");
        await _factory.ExecuteDbAsync(async context =>
            (await context.Users.CountAsync(value => value.Email == email)).Should().Be(1));
    }

    /// <summary>Registers, verifies, and logs in one account through public HTTP endpoints.</summary>
    private async Task<AuthResultDto> RegisterVerifyAndLoginAsync(HttpClient client, string email, string role)
    {
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterAccountRequestDto
        {
            Email = email,
            Password = Password,
            DisplayName = "Integration Account",
            Role = role
        });
        register.EnsureSuccessStatusCode();
        using var verify = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequestDto
        {
            Token = _factory.Email.VerificationToken(email)
        });
        verify.EnsureSuccessStatusCode();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequestDto
        {
            Email = email,
            Password = Password
        });
        login.EnsureSuccessStatusCode();
        return await SuccessDataAsync<AuthResultDto>(login);
    }

    /// <summary>Reads a successful API envelope and returns its required data payload.</summary>
    private static async Task<T> SuccessDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }
}
