using ANGI.Application.Common.Exceptions;
using ANGI.Infrastructure.Services.Auth;
using FluentAssertions;
using Google.Apis.Auth;
using Microsoft.Extensions.Options;

namespace ANGI.UnitTests.Infrastructure.Services.Auth
{
    /// <summary>Verifies GoogleIdentityService trusts only validated Google tokens with a verified email.</summary>
    public sealed class GoogleIdentityServiceTests
    {
        // TEST-01: Return trusted identity claims after Google validates the configured audience.
        /// <summary>Verifies the adapter forwards the client id and maps only verified identity claims.</summary>
        [Fact]
        public async Task VerifyIdTokenAsync_WithValidPayload_ShouldReturnIdentity()
        {
            // STEP-01: Declare capturedSettings to capture a call argument.
            GoogleJsonWebSignature.ValidationSettings? capturedSettings = null;
            // STEP-02: Build service under test through CreateService.
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

            // STEP-03: Run service.VerifyIdTokenAsync and keep result.
            var result = await service.VerifyIdTokenAsync("signed-token", CancellationToken.None);

            // STEP-04: Assert result.ProviderUserId is "google-subject", result.Email is "User@Gmail.com", result.DisplayName is "Nguyen An" and 1 more.
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
            // STEP-01: Build service under test through CreateService.
            var service = CreateService((_, _) =>
                Task.FromException<GoogleJsonWebSignature.Payload>(new InvalidJwtException("invalid")));

            // STEP-02: Wrap service.VerifyIdTokenAsync in act so its outcome can be asserted.
            var act = () => service.VerifyIdTokenAsync("bad-token", CancellationToken.None);

            // STEP-03: Assert act throws UnauthorizedException and exception.ErrorCode is "GOOGLE_TOKEN_INVALID".
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
            // STEP-01: Build service under test through CreateService.
            var service = CreateService((_, _) => Task.FromResult(new GoogleJsonWebSignature.Payload
            {
                Subject = subject,
                Email = email,
                EmailVerified = emailVerified
            }));

            // STEP-02: Wrap service.VerifyIdTokenAsync in act so its outcome can be asserted.
            var act = () => service.VerifyIdTokenAsync("signed-token", CancellationToken.None);

            // STEP-03: Assert act throws UnauthorizedException and exception.ErrorCode is "GOOGLE_TOKEN_INVALID".
            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("GOOGLE_TOKEN_INVALID");
        }

        // TEST-04: Fail safely when the server has no Google OAuth client id configured.
        /// <summary>Verifies missing configuration does not invoke Google or accept an unrestricted audience.</summary>
        [Fact]
        public async Task VerifyIdTokenAsync_WithoutClientId_ShouldThrowWithoutValidation()
        {
            // STEP-01: Define invoked as false.
            var invoked = false;
            // STEP-02: Build service under test through CreateService.
            var service = CreateService((_, _) =>
            {
                invoked = true;
                return Task.FromResult(new GoogleJsonWebSignature.Payload());
            }, clientId: string.Empty);

            // STEP-03: Wrap service.VerifyIdTokenAsync in act so its outcome can be asserted.
            var act = () => service.VerifyIdTokenAsync("signed-token", CancellationToken.None);

            // STEP-04: Assert act throws UnauthorizedException, exception.ErrorCode is "GOOGLE_TOKEN_INVALID" and invoked is false.
            var exception = await act.Should().ThrowAsync<UnauthorizedException>();
            exception.Which.ErrorCode.Should().Be("GOOGLE_TOKEN_INVALID");
            invoked.Should().BeFalse();
        }

        // TEST-05: Propagate caller cancellation instead of translating it into an authentication error.
        /// <summary>Verifies request cancellation interrupts the non-cancelable Google library boundary.</summary>
        [Fact]
        public async Task VerifyIdTokenAsync_WhenCallerCancels_ShouldThrowOperationCanceled()
        {
            // STEP-01: Create pending as TaskCompletionSource<GoogleJsonWebSignature.Payload>.
            var pending = new TaskCompletionSource<GoogleJsonWebSignature.Payload>();
            // STEP-02: Build service under test through CreateService.
            var service = CreateService((_, _) => pending.Task);
            // STEP-03: Create cancellation as CancellationTokenSource and cancel cancellation.
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            // STEP-04: Wrap service.VerifyIdTokenAsync in act so its outcome can be asserted.
            var act = () => service.VerifyIdTokenAsync("signed-token", cancellation.Token);

            // STEP-05: Assert act throws OperationCanceledException.
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
