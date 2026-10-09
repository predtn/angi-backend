using System.Buffers;
using System.Globalization;
using System.Text;
using ANGI.Application.DTOs.Auth;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Auth;

/// <summary>Validates the complete AUTH-01 registration request.</summary>
public sealed class RegisterAccountRequestDtoValidator : AbstractValidator<RegisterAccountRequestDto>
{
    private static readonly IReadOnlySet<string> _selfRegistrationRoles =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "TRAVELER",
            "RESTAURANT_OWNER"
        };

    /// <summary>Creates validation rules matching the AUTH-01 API contract.</summary>
    public RegisterAccountRequestDtoValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email là bắt buộc.")
            .EmailAddress().WithMessage("Email không đúng định dạng.")
            .MaximumLength(255).WithMessage("Email không được vượt quá 255 ký tự.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Mật khẩu là bắt buộc.")
            .MinimumLength(8).WithMessage("Mật khẩu phải có ít nhất 8 ký tự.")
            .MaximumLength(64).WithMessage("Mật khẩu không được vượt quá 64 ký tự.")
            .Must(password => password is not null && password.Any(char.IsLetter))
                .WithMessage("Mật khẩu phải có ít nhất một chữ cái.")
            .Must(password => password is not null && password.Any(char.IsDigit))
                .WithMessage("Mật khẩu phải có ít nhất một chữ số.");

        RuleFor(x => x.DisplayName)
            .NotEmpty().WithMessage("Tên hiển thị là bắt buộc.")
            .Must(name => name is not null && GetUnicodeLength(name.Trim()) >= 2)
                .WithMessage("Tên hiển thị phải có ít nhất 2 ký tự.")
            .Must(name => name is not null && GetUnicodeLength(name.Trim()) <= 100)
                .WithMessage("Tên hiển thị không được vượt quá 100 ký tự.")
            .Must(ContainsOnlySafeUnicode)
                .WithMessage("Tên hiển thị chứa ký tự Unicode không được hỗ trợ.");

        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Vai trò là bắt buộc.")
            .Must(role => _selfRegistrationRoles.Contains(role))
                .WithMessage("Vai trò chỉ có thể là TRAVELER hoặc RESTAURANT_OWNER.");
    }

    /// <summary>
    /// Rejects control, formatting, private-use, unassigned, replacement, and malformed Unicode
    /// while preserving ordinary international names, punctuation, symbols, and emoji.
    /// </summary>
    private static bool ContainsOnlySafeUnicode(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done ||
                rune.Value == Rune.ReplacementChar.Value)
            {
                return false;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or
                UnicodeCategory.Format or
                UnicodeCategory.PrivateUse or
                UnicodeCategory.OtherNotAssigned or
                UnicodeCategory.LineSeparator or
                UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }

    private static int GetUnicodeLength(string value) => value.EnumerateRunes().Count();
}
