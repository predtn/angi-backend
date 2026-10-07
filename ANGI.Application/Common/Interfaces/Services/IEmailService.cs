using System.Threading.Tasks;

namespace ANGI.Application.Common.Interfaces.Services;

public interface IEmailService
{
    Task<bool> SendVerificationEmailAsync(string toEmail, string toName, string verificationLink);
    Task<bool> SendPasswordResetEmailAsync(string toEmail, string toName, string resetLink);
}
