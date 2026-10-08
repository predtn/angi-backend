using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace ANGI.Infrastructure.Services.Email
{
    internal sealed record EmailContent(string Subject, string HtmlContent, string TextContent);

    /// <summary>The Vietnamese account emails. The display name is HTML-encoded in the HTML part.</summary>
    internal static class EmailTemplates
    {
        // Encodes only HTML-special characters, so Vietnamese letters stay readable.
        private static readonly HtmlEncoder _htmlEncoder = HtmlEncoder.Create(UnicodeRanges.All);

        public static EmailContent EmailVerification(string displayName, string link, TimeSpan validFor)
        {
            var validity = FormatValidity(validFor);

            return Build(
                subject: "Xác minh email tài khoản ANGI",
                displayName: displayName,
                intro: "Cảm ơn bạn đã đăng ký ANGI. Bấm nút bên dưới để xác minh email và bắt đầu dùng tài khoản.",
                buttonText: "Xác minh email",
                link: link,
                validity: $"Liên kết có hiệu lực trong {validity} và chỉ dùng được một lần.",
                ignore: "Nếu bạn không đăng ký ANGI, hãy bỏ qua email này.");
        }

        public static EmailContent PasswordReset(string displayName, string link, TimeSpan validFor)
        {
            var validity = FormatValidity(validFor);

            return Build(
                subject: "Đặt lại mật khẩu ANGI",
                displayName: displayName,
                intro: "Chúng tôi nhận được yêu cầu đặt lại mật khẩu cho tài khoản ANGI của bạn. Bấm nút bên dưới để đặt mật khẩu mới.",
                buttonText: "Đặt lại mật khẩu",
                link: link,
                validity: $"Liên kết có hiệu lực trong {validity} và chỉ dùng được một lần.",
                ignore: "Nếu bạn không yêu cầu, hãy bỏ qua email này; mật khẩu của bạn không thay đổi.");
        }

        /// <summary>Whole hours read as "24 giờ", anything else as minutes rounded up ("30 phút").</summary>
        internal static string FormatValidity(TimeSpan validFor) =>
            validFor >= TimeSpan.FromHours(1) && validFor.Ticks % TimeSpan.TicksPerHour == 0
                ? $"{(long)validFor.TotalHours} giờ"
                : $"{(long)Math.Ceiling(validFor.TotalMinutes)} phút";

        private static EmailContent Build(
            string subject,
            string displayName,
            string intro,
            string buttonText,
            string link,
            string validity,
            string ignore)
        {
            var name = _htmlEncoder.Encode(displayName);
            var href = _htmlEncoder.Encode(link);

            var html = $$"""
                <!DOCTYPE html>
                <html lang="vi">
                <head><meta charset="utf-8"><title>{{subject}}</title></head>
                <body style="margin:0;padding:24px;background:#f5f5f5;font-family:Arial,Helvetica,sans-serif;color:#222;">
                  <div style="max-width:560px;margin:0 auto;background:#ffffff;border-radius:8px;padding:32px;">
                    <h1 style="margin:0 0 24px;font-size:22px;color:#e4572e;">ANGI</h1>
                    <p style="margin:0 0 16px;font-size:15px;">Chào {{name}},</p>
                    <p style="margin:0 0 24px;font-size:15px;line-height:1.5;">{{intro}}</p>
                    <p style="margin:0 0 24px;">
                      <a href="{{href}}" style="display:inline-block;padding:12px 24px;background:#e4572e;color:#ffffff;text-decoration:none;border-radius:6px;font-weight:bold;">{{buttonText}}</a>
                    </p>
                    <p style="margin:0 0 16px;font-size:14px;line-height:1.5;">{{validity}}</p>
                    <p style="margin:0 0 16px;font-size:13px;line-height:1.5;color:#555;">Nếu nút không hoạt động, hãy mở liên kết này:<br><a href="{{href}}" style="color:#e4572e;word-break:break-all;">{{href}}</a></p>
                    <p style="margin:0;font-size:13px;color:#555;">{{ignore}}</p>
                  </div>
                </body>
                </html>
                """;

            var text = $"""
                Chào {displayName},

                {intro}

                {buttonText}: {link}

                {validity}

                {ignore}

                ANGI
                """;

            return new EmailContent(subject, html, text);
        }
    }
}
