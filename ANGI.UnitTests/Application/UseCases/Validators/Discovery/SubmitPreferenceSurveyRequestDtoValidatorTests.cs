using System.Reflection;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Validators.Discovery;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Discovery;

/// <summary>Verifies SubmitPreferenceSurveyRequestDtoValidator enforces the answer count, option codes and scale range.</summary>
public sealed class SubmitPreferenceSurveyRequestDtoValidatorTests
{
    private readonly SubmitPreferenceSurveyRequestDtoValidator _validator = new();

    /// <summary>Creates a survey request holding the given answers.</summary>
    private static SubmitPreferenceSurveyRequestDto WithAnswers(params SurveyAnswerDto[] answers) =>
        new() { Answers = [.. answers] };

    // TEST-01: Accept answers with and without a scale value, including both bounds of 0..1.
    /// <summary>Ensures valid answers, with or without a scale, are accepted at both scale bounds.</summary>
    [Fact]
    public void Validate_WithDocumentedAnswers_ShouldPass()
    {
        var request = WithAnswers(
            new SurveyAnswerDto { OptionCode = "broth_rich", ScaleValue = 0.8m },
            new SurveyAnswerDto { OptionCode = "spicy_hot" },
            new SurveyAnswerDto { OptionCode = "sweet_low", ScaleValue = 0m },
            new SurveyAnswerDto { OptionCode = "sour_high", ScaleValue = 1m });

        _validator.Validate(request).IsValid.Should().BeTrue();
    }

    // TEST-02: Accept exactly 50 answers.
    /// <summary>Ensures the maximum answer count is accepted.</summary>
    [Fact]
    public void Validate_WithFiftyAnswers_ShouldPass()
    {
        var answers = Enumerable.Range(1, 50).Select(i => new SurveyAnswerDto { OptionCode = $"option_{i}" }).ToArray();

        _validator.Validate(WithAnswers(answers)).IsValid.Should().BeTrue();
    }

    // TEST-03: Reject an empty list and more than 50 answers (PREF-02 takes 1..50; skipping is PREF-03).
    /// <summary>Ensures empty and oversized surveys are rejected; skipping uses its own endpoint.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Validate_WithAnswerCountOutsideRange_ShouldFailOnAnswers(int count)
    {
        var answers = Enumerable.Range(1, count).Select(i => new SurveyAnswerDto { OptionCode = $"option_{i}" }).ToArray();

        var result = _validator.Validate(WithAnswers(answers));

        result.Errors.Should().ContainSingle(e => e.PropertyName == "Answers");
    }

    // TEST-04: Reject a missing option code.
    /// <summary>Ensures every answer names an option.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithBlankOptionCode_ShouldFail(string optionCode)
    {
        var result = _validator.Validate(WithAnswers(new SurveyAnswerDto { OptionCode = optionCode }));

        result.Errors.Should().ContainSingle(e => e.PropertyName == "Answers[0].OptionCode");
    }

    // TEST-05: Reject a scale value outside 0..1.
    /// <summary>Ensures scale values stay within 0..1.</summary>
    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Validate_WithScaleValueOutsideRange_ShouldFail(double scaleValue)
    {
        var request = WithAnswers(new SurveyAnswerDto { OptionCode = "broth_rich", ScaleValue = (decimal)scaleValue });

        var result = _validator.Validate(request);

        result.Errors.Should().ContainSingle(e => e.PropertyName == "Answers[0].ScaleValue");
    }

    // TEST-06: Reject a null item instead of failing later in the use case.
    /// <summary>Ensures a null answer is caught here instead of crashing the use case.</summary>
    [Fact]
    public void Validate_WithNullAnswer_ShouldFail()
    {
        var request = new SubmitPreferenceSurveyRequestDto { Answers = [null!] };

        _validator.Validate(request).IsValid.Should().BeFalse();
    }

    // TEST-07: Report a null answers array once, with the validator's Vietnamese message.
    /// <summary>Ensures a null answer list produces one clear message.</summary>
    [Fact]
    public void Validate_WithNullAnswers_ShouldFailOnceOnAnswers()
    {
        var result = _validator.Validate(new SubmitPreferenceSurveyRequestDto { Answers = null });

        result.Errors.Should().ContainSingle(e => e.PropertyName == "Answers")
            .Which.ErrorMessage.Should().Be("Câu trả lời khảo sát là bắt buộc.");
    }

    // TEST-08: Report a null option code with the validator's message.
    /// <summary>Ensures a null option code is reported with the validator's own message.</summary>
    [Fact]
    public void Validate_WithNullOptionCode_ShouldFailOnOptionCode()
    {
        var result = _validator.Validate(WithAnswers(new SurveyAnswerDto { OptionCode = null }));

        result.Errors.Should().ContainSingle(e => e.PropertyName == "Answers[0].OptionCode");
    }

    // TEST-09: Keep both fields nullable. MVC adds an implicit [Required] with an English message to non-nullable
    // reference properties and rejects an explicit null before the validator runs.
    /// <summary>Guards against MVC's implicit English required message replacing the validator's message.</summary>
    [Theory]
    [InlineData(typeof(SubmitPreferenceSurveyRequestDto), nameof(SubmitPreferenceSurveyRequestDto.Answers))]
    [InlineData(typeof(SurveyAnswerDto), nameof(SurveyAnswerDto.OptionCode))]
    public void RequestFields_ShouldBeNullableSoTheValidatorReportsNull(Type dtoType, string propertyName)
    {
        var property = dtoType.GetProperty(propertyName)!;

        new NullabilityInfoContext().Create(property).WriteState.Should().Be(NullabilityState.Nullable);
    }
}
