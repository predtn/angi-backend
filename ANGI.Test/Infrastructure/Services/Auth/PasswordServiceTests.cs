using ANGI.Infrastructure.Services.Auth;
using FluentAssertions;

namespace ANGI.Test.Infrastructure.Services.Auth;

public sealed class PasswordServiceTests
{
    // TEST-01: Hash a password with a non-deterministic BCrypt salt and never return the plaintext.
    /// <summary>Verifies password hashing uses randomized BCrypt output.</summary>
    [Fact]
    public void Hash_WithSamePassword_ShouldCreateDistinctBcryptHashes()
    {
        var service = new PasswordService();

        var first = service.Hash("MatKhau123");
        var second = service.Hash("MatKhau123");

        first.Should().NotBe("MatKhau123").And.NotBe(second);
        first.Should().StartWith("$2");
    }

    // TEST-02: Verify only the password that produced the stored BCrypt hash.
    /// <summary>Verifies matching and non-matching plaintext passwords against a BCrypt hash.</summary>
    [Fact]
    public void Verify_WithMatchingAndDifferentPasswords_ShouldReturnExpectedResults()
    {
        var service = new PasswordService();
        var hash = service.Hash("MatKhau123");

        service.Verify("MatKhau123", hash).Should().BeTrue();
        service.Verify("SaiMatKhau123", hash).Should().BeFalse();
    }
}
