using ANGI.Infrastructure.Services.Email;
using FluentAssertions;

namespace ANGI.UnitTests.Infrastructure.Services.Email
{
    /// <summary>Verifies EmailTemplates formats validity honestly and keeps links and names safe in both email parts.</summary>
    public sealed class EmailTemplatesTests
    {
        // TEST-01: Show whole hours in hours and anything else in minutes, rounded up.
        /// <summary>Ensures validity reads naturally in hours or minutes.</summary>
        [Theory]
        [InlineData(24 * 60, "24 giờ")]
        [InlineData(60, "1 giờ")]
        [InlineData(30, "30 phút")]
        [InlineData(90, "90 phút")]
        public void FormatValidity_ShouldUseHoursOnlyForWholeHours(int minutes, string expected)
        {
            EmailTemplates.FormatValidity(TimeSpan.FromMinutes(minutes)).Should().Be(expected);
        }

        // TEST-02: Round a partial minute up so the email never promises more time than the token has.
        /// <summary>Ensures the email never promises more time than the token actually has.</summary>
        [Fact]
        public void FormatValidity_WithPartialMinute_ShouldRoundUp()
        {
            EmailTemplates.FormatValidity(TimeSpan.FromSeconds(90)).Should().Be("2 phút");
        }

        // TEST-03: Put the link in both parts and the raw display name only in the plain-text part.
        /// <summary>Ensures every email client shows the link and the raw name appears only in plain text.</summary>
        [Fact]
        public void PasswordReset_ShouldContainLinkInHtmlAndText()
        {
            const string link = "https://app.angi.test/reset-password?token=a&b";

            var content = EmailTemplates.PasswordReset("An & Bình", link, TimeSpan.FromMinutes(30));

            content.Subject.Should().Be("Đặt lại mật khẩu ANGI");
            content.HtmlContent.Should().Contain("href=\"https://app.angi.test/reset-password?token=a&amp;b\"");
            content.HtmlContent.Should().Contain("Chào An &amp; Bình,");
            content.TextContent.Should().Contain(link).And.Contain("Chào An & Bình,");
        }
    }
}
