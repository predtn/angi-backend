using ANGI.Infrastructure.Services.Auth;
using FluentAssertions;

namespace ANGI.UnitTests.Infrastructure.Services.Auth;

/// <summary>Verifies PasswordService hashes with salted BCrypt and verifies only the matching password.</summary>
public sealed class PasswordServiceTests
{
    // TEST-01: Hash a password with a non-deterministic BCrypt salt and never return the plaintext.
    /// <summary>Verifies password hashing uses randomized BCrypt output.</summary>
    [Fact]
    public void Hash_WithSamePassword_ShouldCreateDistinctBcryptHashes()
    {
        // STEP-01: Create the PasswordService under test as service.
        var service = new PasswordService();

        // STEP-02: Run service.Hash and keep first.
        var first = service.Hash("MatKhau123");
        // STEP-03: Run service.Hash and keep second.
        var second = service.Hash("MatKhau123");

        // STEP-04: Assert first is not "MatKhau123" and first starts with "$2".
        first.Should().NotBe("MatKhau123").And.NotBe(second);
        first.Should().StartWith("$2");
    }

    // TEST-02: Verify only the password that produced the stored BCrypt hash.
    /// <summary>Verifies matching and non-matching plaintext passwords against a BCrypt hash.</summary>
    [Fact]
    public void Verify_WithMatchingAndDifferentPasswords_ShouldReturnExpectedResults()
    {
        // STEP-01: Create the PasswordService under test as service.
        var service = new PasswordService();
        // STEP-02: Run service.Hash and keep hash.
        var hash = service.Hash("MatKhau123");

        // STEP-03: Assert service.Verify() is true and service.Verify() is false.
        service.Verify("MatKhau123", hash).Should().BeTrue();
        service.Verify("SaiMatKhau123", hash).Should().BeFalse();
    }
}
