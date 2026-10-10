using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;
using System.Text.Json;

namespace ANGI.Test.Application.UseCases.Validators.Restaurant;

public sealed class UpdateRestaurantRequestDtoValidatorTests
{
    private readonly UpdateRestaurantRequestDtoValidator _validator = new();

    // TEST-01: Accept explicit null for fields documented as nullable.
    /// <summary>Verifies PATCH can clear nullable profile fields.</summary>
    [Fact]
    public async Task ValidateAsync_WithNullableFieldsCleared_ShouldBeValid()
    {
        var request = new UpdateRestaurantRequestDto
        {
            Description = null,
            Email = null,
            Website = null,
            Ward = null,
            PriceLevel = null,
            CoverMediaId = null
        };

        var result = await _validator.ValidateAsync(request);

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject null for a supplied coordinate and more than ten gallery images.
    /// <summary>Verifies numeric nullability and gallery size from the OWN-03 contract.</summary>
    [Fact]
    public async Task ValidateAsync_WithInvalidSuppliedFields_ShouldReturnFieldErrors()
    {
        var request = new UpdateRestaurantRequestDto
        {
            Latitude = null,
            ImageMediaIds = Enumerable.Range(1, 11).Select(value => (long)value).ToArray()
        };

        var result = await _validator.ValidateAsync(request);

        result.Errors.Select(error => error.PropertyName)
            .Should().Contain(["Latitude", "ImageMediaIds"]);
    }

    // TEST-03: Distinguish an omitted PATCH field from an explicit null JSON value.
    /// <summary>Verifies the presence flags used to implement nullable partial updates.</summary>
    [Fact]
    public void Deserialize_WithExplicitNull_ShouldMarkOnlyThatFieldAsSpecified()
    {
        var request = JsonSerializer.Deserialize<UpdateRestaurantRequestDto>(
            """{"description":null}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        request.Should().NotBeNull();
        request!.DescriptionSpecified.Should().BeTrue();
        request.CoverMediaIdSpecified.Should().BeFalse();
    }

    // TEST-04: Enforce required name nullability and accept documented length boundaries.
    /// <summary>Verifies null, blank, minimum, and maximum name values.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("A", true)]
    public async Task ValidateAsync_WithNameBoundary_ShouldMatchContract(string? name, bool expectedValid)
    {
        var result = await _validator.ValidateAsync(new UpdateRestaurantRequestDto { Name = name });

        result.IsValid.Should().Be(expectedValid);
    }

    /// <summary>Verifies the documented maximum name length.</summary>
    [Fact]
    public async Task ValidateAsync_WithTwoHundredCharacterName_ShouldBeValid()
    {
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { Name = new string('a', 200) });

        result.IsValid.Should().BeTrue();
    }

    // TEST-05: Enforce the description length boundary.
    /// <summary>Verifies 2000 characters are accepted and 2001 are rejected.</summary>
    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public async Task ValidateAsync_WithDescriptionLength_ShouldMatchContract(int length, bool expectedValid)
    {
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { Description = new string('a', length) });

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-06: Enforce required phone nullability and length boundaries.
    /// <summary>Verifies null, blank, maximum, and over-maximum phone values.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("12345678901234567890", true)]
    [InlineData("123456789012345678901", false)]
    public async Task ValidateAsync_WithPhoneBoundary_ShouldMatchContract(string? phone, bool expectedValid)
    {
        var result = await _validator.ValidateAsync(new UpdateRestaurantRequestDto { Phone = phone });

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-07: Validate email syntax, including normalization-safe surrounding whitespace.
    /// <summary>Verifies representative valid and invalid email values.</summary>
    [Theory]
    [InlineData("owner@example.com", true)]
    [InlineData(" OWNER@EXAMPLE.COM ", true)]
    [InlineData("owner.example.com", false)]
    [InlineData("@example.com", false)]
    [InlineData("owner@", false)]
    public async Task ValidateAsync_WithEmailFormat_ShouldMatchContract(string email, bool expectedValid)
    {
        var result = await _validator.ValidateAsync(new UpdateRestaurantRequestDto { Email = email });

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-08: Allow only absolute HTTP and HTTPS website URLs.
    /// <summary>Verifies supported schemes and rejects other or relative URLs.</summary>
    [Theory]
    [InlineData("http://example.com", true)]
    [InlineData("https://example.com", true)]
    [InlineData(" https://example.com ", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com", false)]
    public async Task ValidateAsync_WithWebsiteScheme_ShouldMatchContract(string website, bool expectedValid)
    {
        var result = await _validator.ValidateAsync(new UpdateRestaurantRequestDto { Website = website });

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-09: Enforce address component length boundaries.
    /// <summary>Verifies address line, district, and province maximum lengths.</summary>
    [Theory]
    [InlineData(255, 100, 100, true)]
    [InlineData(256, 100, 100, false)]
    [InlineData(255, 101, 100, false)]
    [InlineData(255, 100, 101, false)]
    public async Task ValidateAsync_WithAddressLengths_ShouldMatchContract(
        int addressLength,
        int districtLength,
        int provinceLength,
        bool expectedValid)
    {
        var request = new UpdateRestaurantRequestDto
        {
            AddressLine = new string('a', addressLength),
            District = new string('d', districtLength),
            ProvinceName = new string('p', provinceLength)
        };

        var result = await _validator.ValidateAsync(request);

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-10: Enforce latitude boundaries.
    /// <summary>Verifies inclusive latitude bounds and values immediately outside them.</summary>
    [Theory]
    [InlineData(-90, true)]
    [InlineData(90, true)]
    [InlineData(-90.000001, false)]
    [InlineData(90.000001, false)]
    public async Task ValidateAsync_WithLatitudeBoundary_ShouldMatchContract(double latitude, bool expectedValid)
    {
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { Latitude = (decimal)latitude });

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-11: Enforce longitude boundaries.
    /// <summary>Verifies inclusive longitude bounds and values immediately outside them.</summary>
    [Theory]
    [InlineData(-180, true)]
    [InlineData(180, true)]
    [InlineData(-180.000001, false)]
    [InlineData(180.000001, false)]
    public async Task ValidateAsync_WithLongitudeBoundary_ShouldMatchContract(double longitude, bool expectedValid)
    {
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { Longitude = (decimal)longitude });

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-12: Enforce nullable price-level boundaries.
    /// <summary>Verifies null, inclusive bounds, and invalid adjacent values.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData((short)1, true)]
    [InlineData((short)4, true)]
    [InlineData((short)0, false)]
    [InlineData((short)5, false)]
    public async Task ValidateAsync_WithPriceLevelBoundary_ShouldMatchContract(short? priceLevel, bool expectedValid)
    {
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { PriceLevel = priceLevel });

        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-13: Reject duplicate, null, zero, and negative gallery identifiers.
    /// <summary>Verifies gallery collection and identity constraints.</summary>
    [Fact]
    public async Task ValidateAsync_WithDuplicateGalleryIds_ShouldBeInvalid()
    {
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { ImageMediaIds = [1, 1] });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(UpdateRestaurantRequestDto.ImageMediaIds));
    }

    /// <summary>Verifies an explicit null gallery differs from an omitted gallery.</summary>
    [Fact]
    public async Task ValidateAsync_WithExplicitNullGallery_ShouldBeInvalid()
    {
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { ImageMediaIds = null });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(UpdateRestaurantRequestDto.ImageMediaIds));
    }

    /// <summary>Verifies database identity values must be positive for both image fields.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ValidateAsync_WithNonPositiveMediaIds_ShouldBeInvalid(long mediaId)
    {
        var coverResult = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { CoverMediaId = mediaId });
        var galleryResult = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { ImageMediaIds = [mediaId] });

        coverResult.IsValid.Should().BeFalse();
        galleryResult.IsValid.Should().BeFalse();
    }
}
