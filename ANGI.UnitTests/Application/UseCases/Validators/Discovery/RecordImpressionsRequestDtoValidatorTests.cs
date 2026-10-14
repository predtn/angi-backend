using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Validators.Discovery;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Discovery;

public sealed class RecordImpressionsRequestDtoValidatorTests
{
    private readonly RecordImpressionsRequestDtoValidator _validator = new();

    // TEST-01: Accept 1 and 200 dish ids.
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
    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Validate_WithInvalidValue_ShouldFailOnce(RecordImpressionsRequestDto request, string field)
    {
        _validator.Validate(request).Errors.Should().ContainSingle(error => error.PropertyName == field);
    }

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
