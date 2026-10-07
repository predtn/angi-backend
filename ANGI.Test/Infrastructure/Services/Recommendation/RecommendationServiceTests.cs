using System.Net;
using System.Text;
using ANGI.Infrastructure.Services.Recommendation;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ANGI.Test.Infrastructure.Services.Recommendation
{
    public sealed class RecommendationServiceTests
    {
        // TEST-13: Read the survey completion flag and send the configured internal API key.
        [Fact]
        public async Task GetSurveyCompletionAsync_WithSuccessfulResponse_ShouldReturnCompletionStatus()
        {
            HttpRequestMessage? capturedRequest = null;
            var handler = new StubHttpMessageHandler(request =>
            {
                capturedRequest = request;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"completed\":true}", Encoding.UTF8, "application/json")
                };
            });
            var service = CreateService(handler);

            var completed = await service.GetSurveyCompletionAsync(12, CancellationToken.None);

            completed.Should().BeTrue();
            capturedRequest.Should().NotBeNull();
            capturedRequest!.RequestUri.Should().Be(new Uri("https://reco.angi.test/users/12/survey"));
            capturedRequest.Headers.GetValues("X-Api-Key").Should().ContainSingle("test-api-key");
        }

        // TEST-14: Fall back to an unknown survey status when the recommendation service is unavailable.
        [Fact]
        public async Task GetSurveyCompletionAsync_WhenProviderFails_ShouldReturnNull()
        {
            var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("Unavailable"));
            var service = CreateService(handler);

            var completed = await service.GetSurveyCompletionAsync(12, CancellationToken.None);

            completed.Should().BeNull();
        }

        private static RecommendationService CreateService(HttpMessageHandler handler)
        {
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://reco.angi.test/")
            };
            return new RecommendationService(
                client,
                Options.Create(new RecommendationSettings
                {
                    BaseUrl = "https://reco.angi.test/",
                    ApiKey = "test-api-key",
                    TimeoutMilliseconds = 800
                }));
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
