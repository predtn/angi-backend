using ANGI.Application.DTOs.Notification;
using ANGI.Application.UseCases.Validators.Notification;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Notification;

public sealed class GetNotificationsRequestDtoValidatorTests
{
    private readonly GetNotificationsRequestDtoValidator _validator = new();

    // TEST-01: Verify Validate when default request; should be valid.
    /// <summary>Verify Validate when default request; should be valid.</summary>
    [Fact]
    public void Validate_WhenDefaultRequest_ShouldBeValid()
    {
        var request = new GetNotificationsRequestDto();
        var result = _validator.Validate(request);
        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Verify Validate when all valid properties provided; should be valid.
    /// <summary>Verify Validate when all valid properties provided; should be valid.</summary>
    [Fact]
    public void Validate_WhenAllValidPropertiesProvided_ShouldBeValid()
    {
        var request = new GetNotificationsRequestDto
        {
            Page = 2,
            PageSize = 50,
            IsRead = true,
            Type = "BLOG_COMMENTED"
        };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeTrue();
    }

    // TEST-03: Verify Validate when page less than1; should fail.
    /// <summary>Verify Validate when page less than1; should fail.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenPageLessThan1_ShouldFail(int page)
    {
        var request = new GetNotificationsRequestDto { Page = page };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GetNotificationsRequestDto.Page));
    }

    // TEST-04: Verify Validate when page size out of range; should fail.
    /// <summary>Verify Validate when page size out of range; should fail.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public void Validate_WhenPageSizeOutOfRange_ShouldFail(int pageSize)
    {
        var request = new GetNotificationsRequestDto { PageSize = pageSize };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GetNotificationsRequestDto.PageSize));
    }

    // TEST-05: Verify Validate when type exceeds40 characters; should fail.
    /// <summary>Verify Validate when type exceeds40 characters; should fail.</summary>
    [Fact]
    public void Validate_WhenTypeExceeds40Characters_ShouldFail()
    {
        var request = new GetNotificationsRequestDto
        {
            Type = new string('A', 41)
        };
        var result = _validator.Validate(request);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(GetNotificationsRequestDto.Type));
    }
}
