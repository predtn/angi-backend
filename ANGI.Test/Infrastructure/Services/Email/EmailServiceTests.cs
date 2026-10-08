using System.Net;
using System.Text;
using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Infrastructure.Services.Email;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ANGI.Test.Infrastructure.Services.Email
{
    public sealed class EmailServiceTests
    {
        // TEST-01: Send the verification email to Brevo with the API key, sender, recipient and frontend link.
        [Fact]
        public async Task SendEmailVerificationAsync_WithValidInput_ShouldPostBrevoEmailWithVerifyLink()
        {
            HttpRequestMessage? capturedRequest = null;
            string? capturedBody = null;
            var handler = new StubHttpMessageHandler(request =>
            {
                capturedRequest = request;
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{\"messageId\":\"<id@smtp-relay.brevo.com>\"}", Encoding.UTF8, "application/json")
                };
            });
            var service = CreateService(handler);

            await service.SendEmailVerificationAsync("an.nguyen@gmail.com", "An Nguyễn", "abc+/=", TimeSpan.FromHours(24), CancellationToken.None);

            capturedRequest.Should().NotBeNull();
            capturedRequest!.Method.Should().Be(HttpMethod.Post);
            capturedRequest.RequestUri.Should().Be(new Uri("https://brevo.angi.test/v3/smtp/email"));
            capturedRequest.Headers.GetValues("api-key").Should().ContainSingle("test-brevo-key");

            using var json = JsonDocument.Parse(capturedBody!);
            var root = json.RootElement;
            root.GetProperty("sender").GetProperty("email").GetString().Should().Be("no-reply@angi.test");
            root.GetProperty("sender").GetProperty("name").GetString().Should().Be("ANGI");
            root.GetProperty("to")[0].GetProperty("email").GetString().Should().Be("an.nguyen@gmail.com");
            root.GetProperty("to")[0].GetProperty("name").GetString().Should().Be("An Nguyễn");
            root.GetProperty("subject").GetString().Should().Be("Xác minh email tài khoản ANGI");

            const string link = "https://app.angi.test/verify-email?token=abc%2B%2F%3D";
            root.GetProperty("htmlContent").GetString().Should().Contain(link).And.Contain("24 giờ");
            root.GetProperty("textContent").GetString().Should().Contain(link).And.Contain("24 giờ");
        }

        // TEST-02: Send the password-reset email with the reset-password link and its validity.
        [Fact]
        public async Task SendPasswordResetAsync_WithValidInput_ShouldPostResetLink()
        {
            string? capturedBody = null;
            var handler = new StubHttpMessageHandler(request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.Created);
            });
            var service = CreateService(handler, frontendBaseUrl: "https://app.angi.test/");

            await service.SendPasswordResetAsync("an.nguyen@gmail.com", "An", "reset-token", TimeSpan.FromMinutes(30), CancellationToken.None);

            using var json = JsonDocument.Parse(capturedBody!);
            var root = json.RootElement;
            root.GetProperty("subject").GetString().Should().Be("Đặt lại mật khẩu ANGI");
            root.GetProperty("textContent").GetString().Should()
                .Contain("https://app.angi.test/reset-password?token=reset-token")
                .And.Contain("30 phút");
        }

        // TEST-03: HTML-encode the display name in the HTML part so it cannot inject markup.
        [Fact]
        public async Task SendEmailVerificationAsync_WithMarkupInDisplayName_ShouldEncodeItInHtml()
        {
            string? capturedBody = null;
            var handler = new StubHttpMessageHandler(request =>
            {
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.Created);
            });
            var service = CreateService(handler);

            await service.SendEmailVerificationAsync("an@gmail.com", "<a href=\"x\">An</a>", "token", TimeSpan.FromHours(24), CancellationToken.None);

            using var json = JsonDocument.Parse(capturedBody!);
            var html = json.RootElement.GetProperty("htmlContent").GetString();
            html.Should().Contain("&lt;a href=&quot;x&quot;&gt;An&lt;/a&gt;");
            html.Should().NotContain("<a href=\"x\">");
        }

        // TEST-04: Return SERVICE_UNAVAILABLE when Brevo rejects the email.
        [Theory]
        [InlineData(HttpStatusCode.BadRequest)]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.InternalServerError)]
        public async Task SendEmailVerificationAsync_WhenBrevoReturnsError_ShouldThrowServiceUnavailable(HttpStatusCode statusCode)
        {
            var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{\"code\":\"unauthorized\",\"message\":\"Key not found\"}", Encoding.UTF8, "application/json")
            });
            var service = CreateService(handler);

            var act = () => service.SendEmailVerificationAsync("an@gmail.com", "An", "token", TimeSpan.FromHours(24), CancellationToken.None);

            var exception = await act.Should().ThrowAsync<ServiceUnavailableException>();
            exception.Which.ErrorCode.Should().Be("SERVICE_UNAVAILABLE");
        }

        // TEST-05: Return SERVICE_UNAVAILABLE when Brevo cannot be reached or does not answer in time.
        [Fact]
        public async Task SendPasswordResetAsync_WhenBrevoIsUnreachableOrTimesOut_ShouldThrowServiceUnavailable()
        {
            var unreachable = CreateService(new StubHttpMessageHandler(_ => throw new HttpRequestException("Unavailable")));
            var timedOut = CreateService(new StubHttpMessageHandler(_ => throw new TaskCanceledException("Timeout")));

            var actUnreachable = () => unreachable.SendPasswordResetAsync("an@gmail.com", "An", "token", TimeSpan.FromMinutes(30), CancellationToken.None);
            var actTimedOut = () => timedOut.SendPasswordResetAsync("an@gmail.com", "An", "token", TimeSpan.FromMinutes(30), CancellationToken.None);

            (await actUnreachable.Should().ThrowAsync<ServiceUnavailableException>()).Which.ErrorCode.Should().Be("SERVICE_UNAVAILABLE");
            (await actTimedOut.Should().ThrowAsync<ServiceUnavailableException>()).Which.ErrorCode.Should().Be("SERVICE_UNAVAILABLE");
        }

        // TEST-06: Let the caller's cancellation pass through instead of reporting the service as unavailable.
        [Fact]
        public async Task SendEmailVerificationAsync_WhenCallerCancels_ShouldThrowOperationCanceled()
        {
            using var cts = new CancellationTokenSource();
            var handler = new StubHttpMessageHandler(_ =>
            {
                cts.Cancel();
                throw new TaskCanceledException("Canceled");
            });
            var service = CreateService(handler);

            var act = () => service.SendEmailVerificationAsync("an@gmail.com", "An", "token", TimeSpan.FromHours(24), cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        // TEST-07: Return SERVICE_UNAVAILABLE without calling Brevo when a required setting is missing.
        [Theory]
        [InlineData("", "no-reply@angi.test", "https://app.angi.test")]
        [InlineData("test-brevo-key", "", "https://app.angi.test")]
        [InlineData("test-brevo-key", "no-reply@angi.test", "")]
        [InlineData("test-brevo-key", "no-reply@angi.test", "not-a-url")]
        [InlineData("test-brevo-key", "no-reply@angi.test", "ftp://app.angi.test")]
        public async Task SendEmailVerificationAsync_WhenNotConfigured_ShouldThrowServiceUnavailableWithoutCallingBrevo(
            string apiKey,
            string senderEmail,
            string frontendBaseUrl)
        {
            var called = false;
            var handler = new StubHttpMessageHandler(_ =>
            {
                called = true;
                return new HttpResponseMessage(HttpStatusCode.Created);
            });
            var service = CreateService(handler, apiKey, senderEmail, frontendBaseUrl);

            var act = () => service.SendEmailVerificationAsync("an@gmail.com", "An", "token", TimeSpan.FromHours(24), CancellationToken.None);

            (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which.ErrorCode.Should().Be("SERVICE_UNAVAILABLE");
            called.Should().BeFalse();
        }

        // TEST-08: Reject a missing recipient, display name or token and a non-positive validity.
        [Theory]
        [InlineData("", "An", "token", 60)]
        [InlineData("an@gmail.com", " ", "token", 60)]
        [InlineData("an@gmail.com", "An", "", 60)]
        [InlineData("an@gmail.com", "An", "token", 0)]
        public async Task SendEmailVerificationAsync_WithInvalidArguments_ShouldThrowArgumentException(
            string toEmail,
            string displayName,
            string token,
            int validForMinutes)
        {
            var service = CreateService(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)));

            var act = () => service.SendEmailVerificationAsync(toEmail, displayName, token, TimeSpan.FromMinutes(validForMinutes), CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>();
        }

        // TEST-09: Register IEmailService with the Brevo base address (trailing slash added) and timeout.
        [Fact]
        public void AddEmailInfrastructure_ShouldRegisterTypedClientWithBrevoSettings()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Brevo:BaseUrl"] = "https://brevo.angi.test/v3",
                    ["Brevo:TimeoutMilliseconds"] = "5000",
                    ["Frontend:BaseUrl"] = "https://app.angi.test"
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEmailInfrastructure(configuration);
            using var provider = services.BuildServiceProvider();

            var emailService = provider.GetRequiredService<IEmailService>();
            var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IEmailService));

            emailService.Should().BeOfType<EmailService>();
            client.BaseAddress.Should().Be(new Uri("https://brevo.angi.test/v3/"));
            client.Timeout.Should().Be(TimeSpan.FromMilliseconds(5000));
            provider.GetRequiredService<IOptions<FrontendSettings>>().Value.BaseUrl.Should().Be("https://app.angi.test");
        }

        private static EmailService CreateService(
            HttpMessageHandler handler,
            string apiKey = "test-brevo-key",
            string senderEmail = "no-reply@angi.test",
            string frontendBaseUrl = "https://app.angi.test")
        {
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://brevo.angi.test/v3/")
            };
            return new EmailService(
                client,
                Options.Create(new BrevoSettings
                {
                    BaseUrl = "https://brevo.angi.test/v3/",
                    ApiKey = apiKey,
                    SenderEmail = senderEmail,
                    SenderName = "ANGI",
                    TimeoutMilliseconds = 10000
                }),
                Options.Create(new FrontendSettings { BaseUrl = frontendBaseUrl }),
                NullLogger<EmailService>.Instance);
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;

            public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
            {
                _send = send;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(_send(request));
            }
        }
    }
}
