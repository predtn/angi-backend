using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Exercises Vietnamese restaurant contact number ranges, presentation, and input boundaries.</summary>
public sealed class VietnamesePhoneNumberRulesTests
{
    // TEST-01: Accept every supported mobile prefix in domestic and international notation.
    /// <summary>Verifies the mobile prefix table independently of formatting and subscriber digits.</summary>
    [Theory]
    [InlineData("32")]
    [InlineData("33")]
    [InlineData("34")]
    [InlineData("35")]
    [InlineData("36")]
    [InlineData("37")]
    [InlineData("38")]
    [InlineData("39")]
    [InlineData("51")]
    [InlineData("52")]
    [InlineData("55")]
    [InlineData("56")]
    [InlineData("58")]
    [InlineData("59")]
    [InlineData("70")]
    [InlineData("76")]
    [InlineData("77")]
    [InlineData("78")]
    [InlineData("79")]
    [InlineData("81")]
    [InlineData("82")]
    [InlineData("83")]
    [InlineData("84")]
    [InlineData("85")]
    [InlineData("86")]
    [InlineData("87")]
    [InlineData("88")]
    [InlineData("89")]
    [InlineData("90")]
    [InlineData("91")]
    [InlineData("92")]
    [InlineData("93")]
    [InlineData("94")]
    [InlineData("95")]
    [InlineData("96")]
    [InlineData("97")]
    [InlineData("98")]
    [InlineData("99")]
    public void IsValid_WithMobilePrefix_ShouldAcceptBothNotations(string prefix)
    {
        foreach (var subscriber in new[] { "0000000", "1234567", "9999999" })
        {
            VietnamesePhoneNumberRules.IsValid($"0{prefix}{subscriber}").Should().BeTrue();
            VietnamesePhoneNumberRules.IsValid($"+84{prefix}{subscriber}").Should().BeTrue();
        }
    }

    // TEST-02: Accept all geographic area codes, including the shorter Hanoi and Ho Chi Minh City codes.
    /// <summary>Verifies provincial landlines have seven subscriber digits and major-city landlines have eight.</summary>
    [Theory]
    [InlineData("203")]
    [InlineData("204")]
    [InlineData("205")]
    [InlineData("206")]
    [InlineData("207")]
    [InlineData("208")]
    [InlineData("209")]
    [InlineData("210")]
    [InlineData("211")]
    [InlineData("212")]
    [InlineData("213")]
    [InlineData("214")]
    [InlineData("215")]
    [InlineData("216")]
    [InlineData("218")]
    [InlineData("219")]
    [InlineData("220")]
    [InlineData("221")]
    [InlineData("222")]
    [InlineData("225")]
    [InlineData("226")]
    [InlineData("227")]
    [InlineData("228")]
    [InlineData("229")]
    [InlineData("232")]
    [InlineData("233")]
    [InlineData("234")]
    [InlineData("235")]
    [InlineData("236")]
    [InlineData("237")]
    [InlineData("238")]
    [InlineData("239")]
    [InlineData("24")]
    [InlineData("251")]
    [InlineData("252")]
    [InlineData("254")]
    [InlineData("255")]
    [InlineData("256")]
    [InlineData("257")]
    [InlineData("258")]
    [InlineData("259")]
    [InlineData("260")]
    [InlineData("261")]
    [InlineData("262")]
    [InlineData("263")]
    [InlineData("269")]
    [InlineData("270")]
    [InlineData("271")]
    [InlineData("272")]
    [InlineData("273")]
    [InlineData("274")]
    [InlineData("275")]
    [InlineData("276")]
    [InlineData("277")]
    [InlineData("28")]
    [InlineData("290")]
    [InlineData("291")]
    [InlineData("292")]
    [InlineData("293")]
    [InlineData("294")]
    [InlineData("296")]
    [InlineData("297")]
    [InlineData("299")]
    public void IsValid_WithGeographicAreaCode_ShouldAcceptBothNotations(string areaCode)
    {
        var subscriber = areaCode.Length == 2 ? "38123456" : "3812345";

        VietnamesePhoneNumberRules.IsValid($"0{areaCode}{subscriber}").Should().BeTrue();
        VietnamesePhoneNumberRules.IsValid($"+84{areaCode}{subscriber}").Should().BeTrue();
    }

    // TEST-03: Accept documented landline spacing and common mobile grouping separators.
    /// <summary>Preserves the API's spaced example and supports readable domestic and +84 numbers.</summary>
    [Theory]
    [InlineData("0236 3812 345")]
    [InlineData("024 3812 3456")]
    [InlineData("028 3812 3456")]
    [InlineData("090 123 4567")]
    [InlineData("090.123.4567")]
    [InlineData("090-123-4567")]
    [InlineData("+84 90 123 4567")]
    [InlineData("+84 236 3812 345")]
    [InlineData(" 0901234567 ")]
    public void IsValid_WithSupportedPresentation_ShouldBeTrue(string phone)
    {
        VietnamesePhoneNumberRules.IsValid(phone).Should().BeTrue();
    }

    // TEST-04: Reject empty values, arbitrary digits, unsupported prefixes, foreign numbers, and malformed presentation.
    /// <summary>Guards against invalid digit counts, Unicode digits, embedded controls, and misplaced separators or plus signs.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1234567890")]
    [InlineData("0000000000")]
    [InlineData("0123456789")]
    [InlineData("01234567890")]
    [InlineData("0301234567")]
    [InlineData("0311234567")]
    [InlineData("0541234567")]
    [InlineData("0571234567")]
    [InlineData("0711234567")]
    [InlineData("0751234567")]
    [InlineData("0801234567")]
    [InlineData("02023812345")]
    [InlineData("02173812345")]
    [InlineData("02233812345")]
    [InlineData("02533812345")]
    [InlineData("02643812345")]
    [InlineData("02783812345")]
    [InlineData("02953812345")]
    [InlineData("02983812345")]
    [InlineData("090123456")]
    [InlineData("09012345678")]
    [InlineData("0236381234")]
    [InlineData("023638123456")]
    [InlineData("+8490123456")]
    [InlineData("+849012345678")]
    [InlineData("+84236381234")]
    [InlineData("+8423638123456")]
    [InlineData("+840901234567")]
    [InlineData("84901234567")]
    [InlineData("0084901234567")]
    [InlineData("+1901234567")]
    [InlineData("+6590123456")]
    [InlineData("090123456a")]
    [InlineData("０９０１２３４５６７")]
    [InlineData("٠٩٠١٢٣٤٥٦٧")]
    [InlineData("090\t1234567")]
    [InlineData("0901234567\n")]
    [InlineData("\r0901234567")]
    [InlineData("090\u00a01234567")]
    [InlineData("090\u200b1234567")]
    [InlineData("090  1234567")]
    [InlineData("090..1234567")]
    [InlineData("090--1234567")]
    [InlineData("0901234567-")]
    [InlineData("0901234567.")]
    [InlineData("(090)1234567")]
    [InlineData("090/1234567")]
    [InlineData("090+1234567")]
    [InlineData("++84901234567")]
    [InlineData("+84+901234567")]
    [InlineData("+8 4 901234567")]
    [InlineData("+8.4.901234567")]
    [InlineData("0 901234567")]
    public void IsValid_WithInvalidNumber_ShouldBeFalse(string? phone)
    {
        VietnamesePhoneNumberRules.IsValid(phone).Should().BeFalse();
    }

    // TEST-05: Apply the API length limit to the original input, including surrounding spaces.
    /// <summary>Checks immediately below, at, and above 20 characters without accepting oversized input.</summary>
    [Theory]
    [InlineData(19, true)]
    [InlineData(20, true)]
    [InlineData(21, false)]
    [InlineData(10000, false)]
    public void IsValid_WithRawLengthBoundary_ShouldMatchLimit(int length, bool expectedValid)
    {
        VietnamesePhoneNumberRules.IsValid("0901234567".PadRight(length)).Should().Be(expectedValid);
    }
}
