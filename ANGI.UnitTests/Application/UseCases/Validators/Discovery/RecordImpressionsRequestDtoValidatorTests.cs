using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Validators.Discovery;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Discovery;

/// <summary>Verifies RecordImpressionsRequestDtoValidator requires a request id and 1 to 200 positive dish ids.</summary>
public sealed class RecordImpressionsRequestDtoValidatorTests
{
    private readonly RecordImpressionsRequestDtoValidator _validator = new();

    // TEST-01: Accept 1 and 200 dish ids.
    /// <summary>Ensures both ends of the allowed dish count are accepted.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public void Validate_WithDocumentedCount_ShouldPass(int count)
    {
        var request = new RecordImpressionsRequestDto
        {
            RequestId = Guid.NewGuid(),
            DishIds = Enumerable.Range(1, count).ToList()
        };

        _validator.Validate(request).IsValid.Should().BeTrue();
    }

    // TEST-02: Reject a missing or empty request id, and missing, empty, too many or non-positive dish ids.
    /// <summary>Ensures each invalid impression body is reported once on the right field.</summary>
    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Validate_WithInvalidValue_ShouldFailOnce(RecordImpressionsRequestDto request, string field)
    {
        _validator.Validate(request).Errors.Should().ContainSingle(error => error.PropertyName == field);
    }

    /// <summary>Supplies each invalid impression body with the field expected to report it.</summary>
    public static TheoryData<RecordImpressionsRequestDto, string> InvalidRequests() => new()
    {
        { new RecordImpressionsRequestDto { DishIds = [1] }, "RequestId" },
        { new RecordImpressionsRequestDto { RequestId = Guid.Empty, DishIds = [1] }, "RequestId" },
        { new RecordImpressionsRequestDto { RequestId = Guid.NewGuid() }, "DishIds" },
        { new RecordImpressionsRequestDto { RequestId = Guid.NewGuid(), DishIds = [] }, "DishIds" },
        { new RecordImpressionsRequestDto { RequestId = Guid.NewGuid(), DishIds = Enumerable.Range(1, 201).ToList() }, "DishIds" },
        { new RecordImpressionsRequestDto { RequestId = Guid.NewGuid(), DishIds = [0] }, "DishIds[0]" }
    };
}
