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
        // STEP-01: Reset the test database.
        await _factory.ResetDatabaseAsync();
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Define email as "register-verify@integration.test".
        const string email = "register-verify@integration.test";

        // STEP-04: Send POST /api/v1/auth/register with a RegisterAccountRequestDto body and keep registerResponse.
        using var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterAccountRequestDto
        {
            Email = email,
            Password = Password,
            DisplayName = "Integration Traveler",
            Role = "TRAVELER"
        });

        // STEP-05: Obtain registered through SuccessDataAsync; assert registerResponse.StatusCode is HttpStatusCode.Created and registered.Email is email.
        registerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var registered = await SuccessDataAsync<RegisterAccountResponseDto>(registerResponse);
        registered.Email.Should().Be(email);
        // STEP-06: Assert in the database user.Status is UserStatus.PendingVerification and context.UserTokens.CountAsync(filtered) is 1.
        await _factory.ExecuteDbAsync(async context =>
        {
            var user = await context.Users.SingleAsync(value => value.Email == email);
            user.Status.Should().Be(UserStatus.PendingVerification);
            (await context.UserTokens.CountAsync(value => value.UserId == user.Id)).Should().Be(1);
        });

        // STEP-07: Send POST /api/v1/auth/verify-email with a VerifyEmailRequestDto body and keep verifyResponse.
        using var verifyResponse = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequestDto
        {
            Token = _factory.Email.VerificationToken(email)
        });

        // STEP-08: Obtain verified through SuccessDataAsync; assert verifyResponse.StatusCode is HttpStatusCode.OK and verified.Status is "active".
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var verified = await SuccessDataAsync<VerifyEmailResponseDto>(verifyResponse);
        verified.Status.Should().Be("active");
        // STEP-09: Assert in the database user.Status is UserStatus.Active, user.EmailVerifiedAt is not null and context.UserTokens.SingleAsync().UsedAt is not null.
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
        // STEP-01: Reset the test database.
        await _factory.ResetDatabaseAsync();
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Obtain auth through RegisterVerifyAndLoginAsync.
        var auth = await RegisterVerifyAndLoginAsync(client, "session-flow@integration.test", "TRAVELER");

        // STEP-04: Send POST /api/v1/auth/refresh with a RefreshTokenRequestDto body and keep refreshResponse.
        using var refreshResponse = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshTokenRequestDto
        {
            RefreshToken = auth.RefreshToken
        });

        // STEP-05: Obtain refreshed through SuccessDataAsync; assert refreshResponse.StatusCode is HttpStatusCode.OK and refreshed.RefreshToken is not auth.RefreshToken.
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshed = await SuccessDataAsync<AuthResultDto>(refreshResponse);
        refreshed.RefreshToken.Should().NotBe(auth.RefreshToken);
        // STEP-06: Attach the bearer token to the HTTP client.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);

        // STEP-07: Send POST /api/v1/auth/logout with a LogoutRequestDto body and keep logoutResponse.
        using var logoutResponse = await client.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequestDto
        {
            RefreshToken = refreshed.RefreshToken
        });

        // STEP-08: Assert logoutResponse.StatusCode is HttpStatusCode.OK.
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        // STEP-09: Assert in the database sessions has 2 item(s) and sessions only holds items matching the condition.
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
        // STEP-01: Reset the test database.
        await _factory.ResetDatabaseAsync();
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Define email as "google-user@integration.test" and add a test value to _factory.Google.
        const string email = "google-user@integration.test";
        _factory.Google.Add("trusted-google-token", new GoogleIdentity("google-subject-1", email, "Google User"));

        // STEP-04: Send POST /api/v1/auth/google with a GoogleLoginRequestDto body and keep response.
        using var response = await client.PostAsJsonAsync("/api/v1/auth/google", new GoogleLoginRequestDto
        {
            IdToken = "trusted-google-token",
            Role = "TRAVELER"
        });

        // STEP-05: Obtain result through SuccessDataAsync; assert response.StatusCode is HttpStatusCode.OK, result.IsNewUser is true and result.AccessToken has a value.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await SuccessDataAsync<GoogleLoginResponseDto>(response);
        result.IsNewUser.Should().BeTrue();
        result.AccessToken.Should().NotBeNullOrWhiteSpace();
        // STEP-06: Assert in the database user.Status is UserStatus.Active, context.UserExternalLogins.CountAsync(filtered) is 1 and 1 more.
        await _factory.ExecuteDbAsync(async context =>
        {
            var user = await context.Users.SingleAsync(value => value.Email == email);
            user.Status.Should().Be(UserStatus.Active);
            (await context.UserExternalLogins.CountAsync(value => value.UserId == user.Id)).Should().Be(1);
            (await context.UserSessions.CountAsync(value => value.UserId == user.Id)).Should().Be(1);
        });
    }

    // TEST-04: Someone registers a victim's email first; the victim's Google sign-in must lock that password out.
    /// <summary>Covers the AUTH-05 rule for linking Google to a password account whose email was never verified.</summary>
    [PostgresEndToEndFact]
    public async Task GoogleLogin_OnUnverifiedPasswordAccount_ShouldRemoveThatPassword()
    {
        // STEP-01: Reset the test database.
        await _factory.ResetDatabaseAsync();
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Define email as "pre-registered@integration.test".
        const string email = "pre-registered@integration.test";
        // STEP-04: Send POST /api/v1/auth/register with a RegisterAccountRequestDto body and keep register.
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterAccountRequestDto
        {
            Email = email,
            Password = Password,
            DisplayName = "Squatter",
            Role = "TRAVELER"
        });
        // STEP-05: Ensure register succeeded and add a test value to _factory.Google.
        register.EnsureSuccessStatusCode();
        _factory.Google.Add("victim-google-token", new GoogleIdentity("google-subject-victim", email, "Real Owner"));

        // STEP-06: Send POST /api/v1/auth/google with a GoogleLoginRequestDto body and keep google.
        using var google = await client.PostAsJsonAsync("/api/v1/auth/google", new GoogleLoginRequestDto
        {
            IdToken = "victim-google-token"
        });
        // STEP-07: Send POST /api/v1/auth/login with a LoginRequestDto body and keep squatterLogin.
        using var squatterLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequestDto
        {
            Email = email,
            Password = Password
        });

        // STEP-08: Assert google.StatusCode is HttpStatusCode.OK, SuccessDataAsync succeeds, SuccessDataAsync().IsNewUser is false and 2 more.
        google.StatusCode.Should().Be(HttpStatusCode.OK, await google.Content.ReadAsStringAsync());
        (await SuccessDataAsync<GoogleLoginResponseDto>(google)).IsNewUser.Should().BeFalse();
        squatterLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await squatterLogin.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be("INVALID_CREDENTIALS");
        // STEP-09: Assert in the database user.PasswordHash is null, user.Status is UserStatus.Active and user.EmailVerifiedAt is not null.
        await _factory.ExecuteDbAsync(async context =>
        {
            var user = await context.Users.SingleAsync(value => value.Email == email);
            user.PasswordHash.Should().BeNull();
            user.Status.Should().Be(UserStatus.Active);
            user.EmailVerifiedAt.Should().NotBeNull();
        });
    }

    // TEST-05: Request and consume a reset token through HTTP, changing the password and revoking every session.
    /// <summary>Covers ANGI-66 and ANGI-70 through the complete reset-password persistence workflow.</summary>
    [PostgresEndToEndFact]
    public async Task ForgotAndResetPassword_ShouldConsumeTokenChangePasswordAndRevokeSessions()
    {
        // STEP-01: Reset the test database.
        await _factory.ResetDatabaseAsync();
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Define email as a test value.
        const string email = "password-flow@integration.test";
        // STEP-04: Assert RegisterVerifyAndLoginAsync confirms TRAVELER.
        _ = await RegisterVerifyAndLoginAsync(client, email, "TRAVELER");

        // STEP-05: Send POST /api/v1/auth/forgot-password with a ForgotPasswordRequestDto body and keep forgotResponse.
        using var forgotResponse = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequestDto
        {
            Email = email
        });
        // STEP-06: Assert forgotResponse.StatusCode is HttpStatusCode.OK.
        forgotResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // STEP-07: Send POST /api/v1/auth/reset-password with a ResetPasswordRequestDto body and keep resetResponse.
        using var resetResponse = await client.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequestDto
        {
            Token = _factory.Email.ResetToken(email),
            NewPassword = "ChangedPassword456"
        });

        // STEP-08: Assert resetResponse.StatusCode is HttpStatusCode.OK.
        resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        // STEP-09: Assert in the database context.UserTokens.SingleAsync(filtered).UsedAt is not null and UserSessions.SingleAsync(filtered).RevokedAt is not null.
        await _factory.ExecuteDbAsync(async context =>
        {
            var user = await context.Users.SingleAsync(value => value.Email == email);
            (await context.UserTokens.SingleAsync(value => value.Purpose == UserTokenPurpose.PasswordReset))
                .UsedAt.Should().NotBeNull();
            (await context.UserSessions.SingleAsync(value => value.UserId == user.Id))
                .RevokedAt.Should().NotBeNull();
        });

        // STEP-10: Send POST /api/v1/auth/login with a LoginRequestDto body and keep loginResponse.
        using var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequestDto
        {
            Email = email,
            Password = "ChangedPassword456"
        });
        // STEP-11: Assert loginResponse.StatusCode is HttpStatusCode.OK.
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // TEST-06: Surface a real database uniqueness conflict through the canonical middleware response.
    /// <summary>Covers ANGI-23 by proving application exceptions cross the hosted HTTP pipeline unchanged.</summary>
    [PostgresEndToEndFact]
    public async Task RegisterDuplicateEmail_ShouldReturnConflictEnvelopeWithoutSecondUser()
    {
        // STEP-01: Reset the test database.
        await _factory.ResetDatabaseAsync();
        // STEP-02: Create the HTTP client.
        using var client = _factory.CreateClient();
        // STEP-03: Define email as "duplicate@integration.test" and create request as RegisterAccountRequestDto.
        const string email = "duplicate@integration.test";
        var request = new RegisterAccountRequestDto
        {
            Email = email,
            Password = Password,
            DisplayName = "Duplicate User",
            Role = "TRAVELER"
        };
        // STEP-04: Send POST /api/v1/auth/register with request and keep first.
        using var first = await client.PostAsJsonAsync("/api/v1/auth/register", request);

        // STEP-05: Send POST /api/v1/auth/register with request and keep second.
        using var second = await client.PostAsJsonAsync("/api/v1/auth/register", request);

        // STEP-06: Read envelope from the response; assert first.StatusCode is HttpStatusCode.Created, second.StatusCode is HttpStatusCode.Conflict and 2 more.
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var envelope = await second.Content.ReadFromJsonAsync<ApiResponse<object>>();
        envelope.Should().NotBeNull();
        envelope!.ErrorCode.Should().Be("EMAIL_ALREADY_EXISTS");
        // STEP-07: Assert in the database context.Users.CountAsync(filtered) is 1.
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
