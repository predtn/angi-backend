using System.Net;
using System.Text;
using System.Text.Json;
using ANGI.WebApi.Common;
using FluentAssertions;

namespace ANGI.IntegrationTests.WebApi.Controllers
{
    /// <summary>
    /// Runs the real pipeline to check that VALIDATION_FAILED never shows MVC's English messages, whether the request
    /// is rejected by model binding or by a validator after an explicit null.
    /// </summary>
    public sealed class ValidationMessagesHttpTests : IClassFixture<AuthenticationApiFactory>
    {
        private readonly AuthenticationApiFactory _factory;

        public ValidationMessagesHttpTests(AuthenticationApiFactory factory)
        {
            _factory = factory;
        }

        // TEST-01: A literal null or empty body on an action without [BindRequired] is reported once, in Vietnamese.
        /// <summary>Verifies AUTH-04 (inferred [FromBody]) uses the same body message as the null-body filter.</summary>
        [Theory]
        [InlineData("null")]
        [InlineData("")]
        public async Task Login_WithNullOrEmptyBody_ShouldReportBodyInVietnamese(string body)
        {
            var errors = await PostAndReadErrors("/api/v1/auth/login", body);

            errors.Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                ["body"] = [ModelBindingErrors.MissingBodyMessage]
            });
        }

        // TEST-02: A body that is not JSON at all is reported on the body, in Vietnamese.
        /// <summary>Verifies a JSON syntax error at the root does not leak the serializer's English message.</summary>
        [Fact]
        public async Task Login_WithNonJsonBody_ShouldReportMalformedBody()
        {
            var errors = await PostAndReadErrors("/api/v1/auth/login", "not json");

            errors.Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                ["body"] = [ModelBindingErrors.MalformedBodyMessage]
            });
        }

        // TEST-05: Truncated JSON is reported where the reader stopped, still only with the Vietnamese messages.
        /// <summary>Verifies no serializer message leaks whichever key the error lands on.</summary>
        [Fact]
        public async Task Login_WithTruncatedJson_ShouldUseOnlyVietnameseMessages()
        {
            var errors = await PostAndReadErrors("/api/v1/auth/login", "{\"email\":");

            errors.Values.SelectMany(messages => messages).Should().OnlyContain(message =>
                message == ModelBindingErrors.MalformedBodyMessage ||
                message == ModelBindingErrors.InvalidValueMessage);
        }

        // TEST-03: A value of the wrong JSON type is reported on its field, in Vietnamese.
        /// <summary>Verifies a type error keeps the API field name.</summary>
        [Fact]
        public async Task Login_WithWrongValueType_ShouldReportTheField()
        {
            var errors = await PostAndReadErrors("/api/v1/auth/login", "{\"email\":5,\"password\":\"Password1\"}");

            errors.Should().ContainKey("email")
                .WhoseValue.Should().Equal(ModelBindingErrors.InvalidValueMessage);
        }

        // TEST-04: An explicit null field reaches the validator: one Vietnamese message per field.
        /// <summary>Verifies implicit [Required] no longer answers before FluentValidation.</summary>
        [Fact]
        public async Task Login_WithNullFields_ShouldUseValidatorMessages()
        {
            var errors = await PostAndReadErrors("/api/v1/auth/login", "{\"email\":null,\"password\":null}");

            errors.Should().BeEquivalentTo(new Dictionary<string, string[]>
            {
                ["email"] = ["Email là bắt buộc."],
                ["password"] = ["Mật khẩu là bắt buộc."]
            });
        }

        private async Task<Dictionary<string, string[]>> PostAndReadErrors(string path, string body)
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _factory.CreateClient().PostAsync(path, content);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("errorCode").GetString().Should().Be("VALIDATION_FAILED");
            return document.RootElement.GetProperty("errors").EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.EnumerateArray().Select(item => item.GetString()!).ToArray());
        }
    }
}
