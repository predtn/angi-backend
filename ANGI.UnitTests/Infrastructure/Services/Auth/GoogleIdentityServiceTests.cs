using ANGI.Application.Common.Exceptions;
using ANGI.Infrastructure.Services.Auth;
using FluentAssertions;
using Google.Apis.Auth;
using Microsoft.Extensions.Options;

namespace ANGI.UnitTests.Infrastructure.Services.Auth
{
    public sealed class GoogleIdentityServiceTests
    {
        // TEST-01: Return trusted identity claims after Google validates the configured audience.
        /// <summary>Verifies the adapter forwards the client id and maps only verified identity claims.</summary>
        [Fact]
        public async Task VerifyIdTokenAsync_WithValidPayload_ShouldReturnIdentity()
        {
            GoogleJsonWebSignature.ValidationSettings? capturedSettings = null;
            var service = CreateService((_, settings) =>
            {
                capturedSettings = settings;
                return Task.FromResult(new GoogleJsonWebSignature.Payload
                {
                    Subject = "google-subject",
                    Email = "User@Gmail.com",
                    EmailVerified = true,
                    Name = "Nguyen An"
                });
            });

            var result = await service.VerifyIdTokenAsync("signed-token", CancellationToken.None);

            result.ProviderUserId.Should().Be("google-subject");
            result.Email.Should().Be("User@Gmail.com");
            result.DisplayName.Should().Be("Nguyen An");
            capturedSettings!.Audience.Should().Equal("google-client-id");
        }

        // TEST-02: Hide Google validation details behind the documented GOOGLE_TOKEN_INVALID error.
        /// <summary>Verifies invalid signatures, issuers, audiences, and expiry share the public AUTH-05 error.</summary>
        [Fact]
        public async Task VerifyIdTokenAsync_WhenGoogleRejectsToken_ShouldThrowDocumentedError()
        {
            var service = CreateService((_, _) =>
                Task.FromException<GoogleJsonWebSignature.Payload>(new InvalidJwtException("invalid")));

            var act = () => service.VerifyIdTokenAsync("bad-token", CancellationToken.None);

            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("GOOGLE_TOKEN_INVALID");
        }

        // TEST-03: Reject trusted payloads that do not contain a verified email and stable subject.
        /// <summary>Verifies required identity claims cannot be bypassed by a cryptographically valid token.</summary>
        [Theory]
        [InlineData(null, "user@gmail.com", true)]
        [InlineData("", "user@gmail.com", true)]
        [InlineData("subject", null, true)]
        [InlineData("subject", "", true)]
        [InlineData("subject", "user@gmail.com", false)]
        public async Task VerifyIdTokenAsync_WithIncompleteIdentity_ShouldThrowDocumentedError(
            string? subject,
            string? email,
            bool emailVerified)
        {
            var service = CreateService((_, _) => Task.FromResult(new GoogleJsonWebSignature.Payload
            {
                Subject = subject,
                Email = email,
                EmailVerified = emailVerified
            }));

            var act = () => service.VerifyIdTokenAsync("signed-token", CancellationToken.None);

            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("GOOGLE_TOKEN_INVALID");
        }

        // TEST-04: Fail safely when the server has no Google OAuth client id configured.
        /// <summary>Verifies missing configuration does not invoke Google or accept an unrestricted audience.</summary>
        [Fact]
        public async Task VerifyIdTokenAsync_WithoutClientId_ShouldThrowWithoutValidation()
        {
            var invoked = false;
            var service = CreateService((_, _) =>
            {
                invoked = true;
                return Task.FromResult(new GoogleJsonWebSignature.Payload());
            }, clientId: string.Empty);

            var act = () => service.VerifyIdTokenAsync("signed-token", CancellationToken.None);

            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("GOOGLE_TOKEN_INVALID");
            invoked.Should().BeFalse();
        }

        // TEST-05: Propagate caller cancellation instead of translating it into an authentication error.
        /// <summary>Verifies request cancellation interrupts the non-cancelable Google library boundary.</summary>
        [Fact]
        public async Task VerifyIdTokenAsync_WhenCallerCancels_ShouldThrowOperationCanceled()
        {
            var pending = new TaskCompletionSource<GoogleJsonWebSignature.Payload>();
            var service = CreateService((_, _) => pending.Task);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var act = () => service.VerifyIdTokenAsync("signed-token", cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        /// <summary>Creates the infrastructure adapter with deterministic Google validation behavior.</summary>
        private static GoogleIdentityService CreateService(
            Func<string, GoogleJsonWebSignature.ValidationSettings,
                Task<GoogleJsonWebSignature.Payload>> validator,
            string clientId = "google-client-id")
        {
            return new GoogleIdentityService(
                Options.Create(new GoogleSettings { ClientId = clientId }),
                validator);
        }
    }
}
