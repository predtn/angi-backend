using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;
using System.Text.Json;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Verifies UpdateRestaurantRequestDtoValidator applies PATCH null semantics and the restaurant field limits.</summary>
public sealed class UpdateRestaurantRequestDtoValidatorTests
{
    private readonly UpdateRestaurantRequestDtoValidator _validator = new();

    // TEST-01: Accept explicit null for fields documented as nullable.
    /// <summary>Verifies PATCH can clear nullable profile fields.</summary>
    [Fact]
    public async Task ValidateAsync_WithNullableFieldsCleared_ShouldBeValid()
    {
        // STEP-01: Create request as UpdateRestaurantRequestDto.
        var request = new UpdateRestaurantRequestDto
        {
            Description = null,
            Email = null,
            Website = null,
            Ward = null,
            PriceLevel = null,
            CoverMediaId = null
        };

        // STEP-02: Validate request and keep result.
        var result = await _validator.ValidateAsync(request);

        // STEP-03: Assert result.IsValid is true.
        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject null for a supplied coordinate and more than ten gallery images.
    /// <summary>Verifies numeric nullability and gallery size from the OWN-03 contract.</summary>
    [Fact]
    public async Task ValidateAsync_WithInvalidSuppliedFields_ShouldReturnFieldErrors()
    {
        // STEP-01: Create request as UpdateRestaurantRequestDto.
        var request = new UpdateRestaurantRequestDto
        {
            Latitude = null,
            ImageMediaIds = Enumerable.Range(1, 11).Select(value => (long)value).ToArray()
        };

        // STEP-02: Validate request and keep result.
        var result = await _validator.ValidateAsync(request);

        // STEP-03: Assert result.Errors.Select(filtered) contains the expected items.
        result.Errors.Select(error => error.PropertyName)
            .Should().Contain(["Latitude", "ImageMediaIds"]);
    }

    // TEST-03: Distinguish an omitted PATCH field from an explicit null JSON value.
    /// <summary>Verifies the presence flags used to implement nullable partial updates.</summary>
    [Fact]
    public void Deserialize_WithExplicitNull_ShouldMarkOnlyThatFieldAsSpecified()
    {
        // STEP-01: Read request from the response; assert request is not null, request.DescriptionSpecified is true and request.CoverMediaIdSpecified is false.
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
        // STEP-01: Validate UpdateRestaurantRequestDto { Name = name } and keep result.
        var result = await _validator.ValidateAsync(new UpdateRestaurantRequestDto { Name = name });

        // STEP-02: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-05: Accept the documented maximum restaurant name length.
    /// <summary>Verifies the documented maximum name length.</summary>
    [Fact]
    public async Task ValidateAsync_WithTwoHundredCharacterName_ShouldBeValid()
    {
        // STEP-01: Validate UpdateRestaurantRequestDto { Name = a new string } and keep result.
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { Name = new string('a', 200) });

        // STEP-02: Assert result.IsValid is true.
        result.IsValid.Should().BeTrue();
    }

    // TEST-06: Enforce the description length boundary.
    /// <summary>Verifies 2000 characters are accepted and 2001 are rejected.</summary>
    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    public async Task ValidateAsync_WithDescriptionLength_ShouldMatchContract(int length, bool expectedValid)
    {
        // STEP-01: Validate UpdateRestaurantRequestDto { Description = a new string } and keep result.
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { Description = new string('a', length) });

        // STEP-02: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-07: Enforce supplied phone nullability, Vietnamese formats, and length boundaries.
    /// <summary>Verifies invalid contact numbers cannot bypass registration rules through a partial update.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("12345678901234567890", false)]
    [InlineData("123456789012345678901", false)]
    [InlineData("1234567890", false)]
    [InlineData("0123456789", false)]
    [InlineData("090123456", false)]
    [InlineData("09012345678", false)]
    [InlineData("0901234567\n", false)]
    [InlineData("+840901234567", false)]
    [InlineData("+6590123456", false)]
    [InlineData("0901234567", true)]
    [InlineData("0236 3812 345", true)]
    [InlineData("+84 90 123 4567", true)]
    public async Task ValidateAsync_WithPhoneBoundary_ShouldMatchContract(string? phone, bool expectedValid)
    {
        // STEP-01: Validate UpdateRestaurantRequestDto { Phone = phone } and keep result.
        var result = await _validator.ValidateAsync(new UpdateRestaurantRequestDto { Phone = phone });

        // STEP-02: Assert result.IsValid is expectedValid, result.Errors has exactly one matching item and result.Errors.PropertyName is "Phone".
        result.IsValid.Should().Be(expectedValid);
        if (!expectedValid)
        {
            result.Errors.Should().ContainSingle().Which.PropertyName.Should().Be("Phone");
        }
    }

    // TEST-08: Validate email syntax, including normalization-safe surrounding whitespace.
    /// <summary>Verifies representative valid and invalid email values.</summary>
    [Theory]
    [InlineData("owner@example.com", true)]
    [InlineData(" OWNER@EXAMPLE.COM ", true)]
    [InlineData("owner.example.com", false)]
    [InlineData("@example.com", false)]
    [InlineData("owner@", false)]
    public async Task ValidateAsync_WithEmailFormat_ShouldMatchContract(string email, bool expectedValid)
    {
        // STEP-01: Validate UpdateRestaurantRequestDto { Email = email } and keep result.
        var result = await _validator.ValidateAsync(new UpdateRestaurantRequestDto { Email = email });

        // STEP-02: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-09: Allow only absolute HTTP and HTTPS website URLs.
    /// <summary>Verifies supported schemes and rejects other or relative URLs.</summary>
    [Theory]
    [InlineData("http://example.com", true)]
    [InlineData("https://example.com", true)]
    [InlineData(" https://example.com ", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com", false)]
    public async Task ValidateAsync_WithWebsiteScheme_ShouldMatchContract(string website, bool expectedValid)
    {
        // STEP-01: Validate UpdateRestaurantRequestDto { Website = website } and keep result.
        var result = await _validator.ValidateAsync(new UpdateRestaurantRequestDto { Website = website });

        // STEP-02: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-10: Enforce address component length boundaries.
    /// <summary>Verifies address line, ward, and province maximum lengths.</summary>
    [Theory]
    [InlineData(255, 100, 100, true)]
    [InlineData(256, 100, 100, false)]
    [InlineData(255, 101, 100, false)]
    [InlineData(255, 100, 101, false)]
    public async Task ValidateAsync_WithAddressLengths_ShouldMatchContract(
        int addressLength,
        int wardLength,
        int provinceLength,
        bool expectedValid)
    {
        // STEP-01: Create request as UpdateRestaurantRequestDto.
        var request = new UpdateRestaurantRequestDto
        {
            AddressLine = new string('a', addressLength),
            Ward = new string('w', wardLength),
            ProvinceName = new string('p', provinceLength)
        };

        // STEP-02: Validate request and keep result.
        var result = await _validator.ValidateAsync(request);

        // STEP-03: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-11: Enforce latitude boundaries.
    /// <summary>Verifies inclusive latitude bounds and values immediately outside them.</summary>
    [Theory]
    [InlineData(-90, true)]
    [InlineData(90, true)]
    [InlineData(-90.000001, false)]
    [InlineData(90.000001, false)]
    public async Task ValidateAsync_WithLatitudeBoundary_ShouldMatchContract(double latitude, bool expectedValid)
    {
        // STEP-01: Validate UpdateRestaurantRequestDto { Latitude = latitude } and keep result.
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { Latitude = (decimal)latitude });

        // STEP-02: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-12: Enforce longitude boundaries.
    /// <summary>Verifies inclusive longitude bounds and values immediately outside them.</summary>
    [Theory]
    [InlineData(-180, true)]
    [InlineData(180, true)]
    [InlineData(-180.000001, false)]
    [InlineData(180.000001, false)]
    public async Task ValidateAsync_WithLongitudeBoundary_ShouldMatchContract(double longitude, bool expectedValid)
    {
        // STEP-01: Validate UpdateRestaurantRequestDto { Longitude = longitude } and keep result.
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { Longitude = (decimal)longitude });

        // STEP-02: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-13: Enforce nullable price-level boundaries.
    /// <summary>Verifies null, inclusive bounds, and invalid adjacent values.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData((short)1, true)]
    [InlineData((short)4, true)]
    [InlineData((short)0, false)]
    [InlineData((short)5, false)]
    public async Task ValidateAsync_WithPriceLevelBoundary_ShouldMatchContract(short? priceLevel, bool expectedValid)
    {
        // STEP-01: Validate UpdateRestaurantRequestDto { PriceLevel = priceLevel } and keep result.
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { PriceLevel = priceLevel });

        // STEP-02: Assert result.IsValid is expectedValid.
        result.IsValid.Should().Be(expectedValid);
    }

    // TEST-14: Reject duplicate gallery identifiers.
    /// <summary>Verifies gallery collection and identity constraints.</summary>
    [Fact]
    public async Task ValidateAsync_WithDuplicateGalleryIds_ShouldBeInvalid()
    {
        // STEP-01: Validate UpdateRestaurantRequestDto { ImageMediaIds = the expected items } and keep result.
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { ImageMediaIds = [1, 1] });

        // STEP-02: Assert result.IsValid is false and result.Errors contains a matching item.
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(UpdateRestaurantRequestDto.ImageMediaIds));
    }

    // TEST-15: Reject an explicitly null gallery while allowing an omitted gallery.
    /// <summary>Verifies an explicit null gallery differs from an omitted gallery.</summary>
    [Fact]
    public async Task ValidateAsync_WithExplicitNullGallery_ShouldBeInvalid()
    {
        // STEP-01: Validate UpdateRestaurantRequestDto { ImageMediaIds = null } and keep result.
        var result = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { ImageMediaIds = null });

        // STEP-02: Assert result.IsValid is false and result.Errors contains a matching item.
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(UpdateRestaurantRequestDto.ImageMediaIds));
    }

    // TEST-16: Reject non-positive media identifiers in the cover and gallery fields.
    /// <summary>Verifies database identity values must be positive for both image fields.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ValidateAsync_WithNonPositiveMediaIds_ShouldBeInvalid(long mediaId)
    {
        // STEP-01: Validate UpdateRestaurantRequestDto { CoverMediaId = mediaId } and keep coverResult.
        var coverResult = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { CoverMediaId = mediaId });
        // STEP-02: Validate UpdateRestaurantRequestDto { ImageMediaIds = the expected items } and keep galleryResult.
        var galleryResult = await _validator.ValidateAsync(
            new UpdateRestaurantRequestDto { ImageMediaIds = [mediaId] });

        // STEP-03: Assert coverResult.IsValid is false and galleryResult.IsValid is false.
        coverResult.IsValid.Should().BeFalse();
        galleryResult.IsValid.Should().BeFalse();
    }
}
