using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ANGI.Application.DTOs.Auth;
using ANGI.Application.DTOs.Media;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ANGI.IntegrationTests.EndToEnd;

/// <summary>Exercises owner media, restaurant, verification, and menu workflows through HTTP and PostgreSQL.</summary>
public sealed class OwnerRestaurantEndToEndTests : IClassFixture<PostgresApiFactory>
{
    private const string Password = "Password123";
    private readonly PostgresApiFactory _factory;

    /// <summary>Initializes the suite with its shared real-host PostgreSQL factory.</summary>
    public OwnerRestaurantEndToEndTests(PostgresApiFactory factory)
    {
        _factory = factory;
    }

    // TEST-01: Upload multipart media through HTTP and persist its provider metadata for the authenticated owner.
    /// <summary>Covers ANGI-17 across model binding, the real media use case, repository, and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task UploadMedia_ShouldReturnCreatedAndPersistMetadata()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await RegisterOwnerAndLoginAsync(client, "media-owner@integration.test");
        Authorize(client, auth.AccessToken);

        using var response = await UploadAsync(client, "avatar", "avatar.png", "image/png", [1, 2, 3, 4]);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var media = await SuccessDataAsync<MediaFileDto>(response);
        await _factory.ExecuteDbAsync(async context =>
        {
            var persisted = await context.MediaFiles.SingleAsync(value => value.Id == media.Id);
            persisted.UploaderId.Should().Be(auth.User.Id);
            persisted.MimeType.Should().Be("image/png");
            persisted.StorageKey.Should().StartWith("image/upload/avatar/");
        });
    }

    // TEST-02: Register, read, and patch an owner restaurant through HTTP and verify the stored profile.
    /// <summary>Covers ANGI-39 and ANGI-42 with real authorization, use cases, repositories, and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task RegisterGetAndUpdateRestaurant_ShouldPersistCompleteProfileLifecycle()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await RegisterOwnerAndLoginAsync(client, "profile-owner@integration.test");
        Authorize(client, auth.AccessToken);

        var restaurant = await RegisterRestaurantAsync(client, "Initial Integration Restaurant");
        using var getResponse = await client.GetAsync("/api/v1/owner/restaurant");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SuccessDataAsync<OwnerRestaurantDto>(getResponse)).Id.Should().Be(restaurant.Id);

        using var patchResponse = await client.PatchAsJsonAsync("/api/v1/owner/restaurant", new
        {
            Name = "Updated Integration Restaurant",
            Description = "Updated through the full HTTP and PostgreSQL integration pipeline."
        });

        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await SuccessDataAsync<OwnerRestaurantDto>(patchResponse);
        updated.Name.Should().Be("Updated Integration Restaurant");
        await _factory.ExecuteDbAsync(async context =>
        {
            var persisted = await context.Restaurants.SingleAsync(value => value.Id == restaurant.Id);
            persisted.Name.Should().Be("Updated Integration Restaurant");
            persisted.Description.Should().Contain("full HTTP");
            (await context.AuditLogs.CountAsync(value => value.EntityId == restaurant.Id)).Should().BeGreaterThan(0);
        });
    }

    // TEST-03: Upload a private document and submit restaurant verification through HTTP with persisted documents.
    /// <summary>Covers ANGI-75 from multipart upload through verification aggregate persistence.</summary>
    [PostgresEndToEndFact]
    public async Task SubmitVerification_ShouldPersistPendingVerificationAndOwnedDocument()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await RegisterOwnerAndLoginAsync(client, "verification-owner@integration.test");
        Authorize(client, auth.AccessToken);
        var restaurant = await RegisterRestaurantAsync(client, "Verification Integration Restaurant");
        using var uploadResponse = await UploadAsync(
            client,
            "verification_doc",
            "license.pdf",
            "application/pdf",
            "%PDF integration"u8.ToArray());
        var media = await SuccessDataAsync<MediaFileDto>(uploadResponse);

        using var response = await client.PostAsJsonAsync("/api/v1/owner/restaurant/verifications", new SubmitVerificationRequestDto
        {
            LegalName = "Integration Restaurant Company",
            BusinessLicenseNo = "LICENSE-001",
            TaxCode = "TAX-001",
            Documents =
            [
                new VerificationDocumentRequestDto
                {
                    DocType = "business_license",
                    MediaId = media.Id
                }
            ]
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var verification = await SuccessDataAsync<VerificationDto>(response);
        verification.Status.Should().Be("pending");
        await _factory.ExecuteDbAsync(async context =>
        {
            var persistedRestaurant = await context.Restaurants.SingleAsync(value => value.Id == restaurant.Id);
            persistedRestaurant.VerificationStatus.Should().Be(RestaurantVerificationStatus.Pending);
            var persisted = await context.RestaurantVerifications
                .Include(value => value.Documents)
                .SingleAsync(value => value.Id == verification.Id);
            persisted.Status.Should().Be(VerificationStatus.Pending);
            persisted.Documents.Should().ContainSingle(value => value.MediaId == media.Id);
        });
    }

    // TEST-04: Manage a draft menu through HTTP and submit the persisted changes for approval.
    /// <summary>Covers ANGI-53 and ANGI-76 across all principal menu mutation layers and final database state.</summary>
    [PostgresEndToEndFact]
    public async Task ManageAndSubmitMenu_ShouldPersistDraftItemsAndPendingTransition()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await RegisterOwnerAndLoginAsync(client, "menu-owner@integration.test");
        Authorize(client, auth.AccessToken);
        var restaurant = await RegisterRestaurantAsync(client, "Menu Integration Restaurant");
        var dishId = await _factory.ExecuteDbAsync(async context =>
        {
            var persistedRestaurant = await context.Restaurants.SingleAsync(value => value.Id == restaurant.Id);
            persistedRestaurant.VerificationStatus = RestaurantVerificationStatus.Verified;
            var dish = new Dish
            {
                RestaurantId = restaurant.Id,
                Name = "Original Dish",
                Description = "Original dish description long enough for menu integration testing.",
                DishForm = DishForm.Soup,
                Price = 50000,
                SortOrder = 1
            };
            context.Dishes.Add(dish);
            await context.SaveChangesAsync();
            return dish.Id;
        });

        using var availabilityResponse = await client.PutAsJsonAsync(
            $"/api/v1/owner/restaurant/menu/dishes/{dishId}/availability",
            new UpdateDishAvailabilityRequestDto { IsAvailable = false });
        availabilityResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var createResponse = await client.PostAsync("/api/v1/owner/restaurant/menu-submissions", null);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var submission = await SuccessDataAsync<MenuSubmissionWithItemsDto>(createResponse);
        var copiedItem = submission.Items.Should().ContainSingle(value => value.SourceDishId == dishId).Subject;

        using var addResponse = await client.PostAsJsonAsync(
            $"/api/v1/owner/restaurant/menu-submissions/{submission.Id}/items",
            new AddMenuSubmissionItemRequestDto
            {
                Name = "Temporary Dish",
                Description = "Temporary dish description long enough to pass complete validation.",
                DishForm = "dry",
                Price = 35000,
                TagIds = []
            });
        addResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var addedItem = await SuccessDataAsync<MenuSubmissionItemDto>(addResponse);

        using var updateResponse = await client.PatchAsJsonAsync(
            $"/api/v1/owner/restaurant/menu-submissions/{submission.Id}/items/{copiedItem.Id}",
            new
            {
                Name = "Updated Original Dish",
                Price = 65000
            });
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var deleteResponse = await client.DeleteAsync(
            $"/api/v1/owner/restaurant/menu-submissions/{submission.Id}/items/{addedItem.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var listResponse = await client.GetAsync("/api/v1/owner/restaurant/menu-submissions?page=1&pageSize=20");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var detailResponse = await client.GetAsync($"/api/v1/owner/restaurant/menu-submissions/{submission.Id}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var submitResponse = await client.PostAsJsonAsync(
            $"/api/v1/owner/restaurant/menu-submissions/{submission.Id}/submit",
            new SubmitMenuSubmissionRequestDto { OwnerNote = "Ready for integration approval" });

        submitResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var submitted = await SuccessDataAsync<MenuSubmissionDto>(submitResponse);
        submitted.Status.Should().Be("pending");
        await _factory.ExecuteDbAsync(async context =>
        {
            (await context.Dishes.SingleAsync(value => value.Id == dishId)).IsAvailable.Should().BeFalse();
            var persisted = await context.MenuSubmissions
                .Include(value => value.Items)
                .SingleAsync(value => value.Id == submission.Id);
            persisted.Status.Should().Be(MenuSubmissionStatus.Pending);
            persisted.OwnerNote.Should().Be("Ready for integration approval");
            persisted.Items.Should().ContainSingle();
            persisted.Items.Single().Name.Should().Be("Updated Original Dish");
            persisted.Items.Single().Price.Should().Be(65000);
        });
    }

    // TEST-05: Reject invalid registration phones with one Vietnamese field error and no persisted restaurant aggregate.
    /// <summary>Exercises real validation, HTTP error mapping, and PostgreSQL state for rejected OWN-01 contacts.</summary>
    [PostgresEndToEndFact]
    public async Task RegisterRestaurant_WithInvalidPhones_ShouldReturnValidationWithoutCreatingRows()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await RegisterOwnerAndLoginAsync(client, "invalid-phone-owner@integration.test");
        Authorize(client, auth.AccessToken);

        foreach (var phone in InvalidPhones())
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/owner/restaurant", RegistrationRequest("Rejected Phone Restaurant", phone!));

            await AssertPhoneValidationAsync(response, phone, isRegistration: true);
        }

        // Omitting the property must behave like the empty DTO default.
        using var omittedResponse = await client.PostAsJsonAsync("/api/v1/owner/restaurant", new
        {
            Name = "Missing Phone Restaurant", AddressLine = "1 Integration Street",
            District = "District 1", ProvinceName = "Ho Chi Minh City", Latitude = 10m, Longitude = 106m
        });
        await AssertPhoneValidationAsync(omittedResponse, "", isRegistration: true);

        await _factory.ExecuteDbAsync(async context =>
        {
            (await context.Restaurants.CountAsync()).Should().Be(0);
            (await context.RestaurantImages.CountAsync()).Should().Be(0);
            (await context.RestaurantBusinessHours.CountAsync()).Should().Be(0);
        });

        // An earlier invalid attempt must not consume the owner's right to register.
        var restaurant = await RegisterRestaurantAsync(client, "Recovered Phone Restaurant");
        restaurant.Phone.Should().Be("0900000000");
    }

    // TEST-06: Register valid domestic and international mobile and landline contacts and preserve their readable formatting.
    /// <summary>Verifies successful OWN-01 responses, GET round trips, and persisted phone values with real use cases.</summary>
    [PostgresEndToEndFact]
    public async Task RegisterRestaurant_WithValidPhones_ShouldPersistAndReturnFormattedNumbers()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        string[] phones =
        [
            "0901234567", " 0901234567 ", "0236 3812 345", "024 3812 3456",
            "028 3812 3456", "+84 90 123 4567", "+84 236 3812 345", "090.123.4567",
            "090-123-4567", "0901234567".PadRight(20)
        ];
        for (var index = 0; index < phones.Length; index++)
        {
            client.DefaultRequestHeaders.Authorization = null;
            var auth = await RegisterOwnerAndLoginAsync(client, $"valid-phone-{index}@integration.test");
            Authorize(client, auth.AccessToken);
            var restaurant = await RegisterRestaurantAsync(client, $"Valid Phone Restaurant {index}", phones[index]);

            restaurant.Phone.Should().Be(phones[index].Trim());
            restaurant.VerificationStatus.Should().Be("unverified");
            restaurant.OperatingStatus.Should().Be("open");
            restaurant.ModerationStatus.Should().Be("visible");
            using var get = await client.GetAsync("/api/v1/owner/restaurant");
            (await SuccessDataAsync<OwnerRestaurantDto>(get)).Phone.Should().Be(restaurant.Phone);
            await _factory.ExecuteDbAsync(async context =>
            {
                var stored = await context.Restaurants.SingleAsync(value => value.Id == restaurant.Id);
                stored.OwnerId.Should().Be(auth.User.Id);
                stored.Phone.Should().Be(restaurant.Phone);
            });
        }
    }

    // TEST-07: Reject invalid PATCH phones without changing other supplied fields, the stored contact, or audit history.
    /// <summary>Proves the registration fix cannot be bypassed by a later partial update.</summary>
    [PostgresEndToEndFact]
    public async Task UpdateRestaurant_WithInvalidPhones_ShouldLeaveProfileAndAuditUnchanged()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await RegisterOwnerAndLoginAsync(client, "patch-phone-owner@integration.test");
        Authorize(client, auth.AccessToken);
        var restaurant = await RegisterRestaurantAsync(client, "Unchanged Phone Restaurant");
        var auditCount = await _factory.ExecuteDbAsync(context => context.AuditLogs.CountAsync());
        var updatedAt = await _factory.ExecuteDbAsync(context => context.Restaurants
            .Where(value => value.Id == restaurant.Id).Select(value => value.UpdatedAt).SingleAsync());

        foreach (var phone in InvalidPhones())
        {
            using var response = await client.PatchAsJsonAsync("/api/v1/owner/restaurant", new
            {
                Phone = phone, Name = "Must Not Be Saved"
            });
            await AssertPhoneValidationAsync(response, phone, isRegistration: false);
        }

        await _factory.ExecuteDbAsync(async context =>
        {
            var stored = await context.Restaurants.SingleAsync(value => value.Id == restaurant.Id);
            stored.Phone.Should().Be(restaurant.Phone);
            stored.Name.Should().Be(restaurant.Name);
            stored.UpdatedAt.Should().Be(updatedAt);
            (await context.AuditLogs.CountAsync()).Should().Be(auditCount);
        });

        // Omitting phone still permits a legitimate partial update.
        using var omitted = await client.PatchAsJsonAsync("/api/v1/owner/restaurant", new { Description = "Phone omitted" });
        omitted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SuccessDataAsync<OwnerRestaurantDto>(omitted)).Phone.Should().Be(restaurant.Phone);

        using var valid = await client.PatchAsJsonAsync("/api/v1/owner/restaurant", new { Phone = "+84 236 3812 345" });
        valid.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SuccessDataAsync<OwnerRestaurantDto>(valid)).Phone.Should().Be("+84 236 3812 345");
        await _factory.ExecuteDbAsync(async context =>
        {
            (await context.Restaurants.SingleAsync(value => value.Id == restaurant.Id))
                .Phone.Should().Be("+84 236 3812 345");
            (await context.AuditLogs.CountAsync()).Should().Be(auditCount + 2);
        });
    }

    // TEST-08: Concurrent valid registrations for one owner create exactly one restaurant.
    /// <summary>Verifies phone validation preserves transactional owner uniqueness and the documented conflict response.</summary>
    [PostgresEndToEndFact]
    public async Task RegisterRestaurant_WithConcurrentValidPhones_ShouldCreateOnlyOneRestaurant()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await RegisterOwnerAndLoginAsync(client, "concurrent-phone-owner@integration.test");
        Authorize(client, auth.AccessToken);

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/v1/owner/restaurant", RegistrationRequest("First Contact", "0901234567")),
            client.PostAsJsonAsync("/api/v1/owner/restaurant", RegistrationRequest("Second Contact", "0236 3812 345")));
        try
        {
            responses.Select(response => response.StatusCode)
                .Should().BeEquivalentTo(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
            var conflict = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
            var error = await conflict.Content.ReadFromJsonAsync<ApiResponse<object>>();
            error!.Success.Should().BeFalse();
            error.ErrorCode.Should().Be("OWNER_ALREADY_HAS_RESTAURANT");
            await _factory.ExecuteDbAsync(async context =>
                (await context.Restaurants.CountAsync(value => value.OwnerId == auth.User.Id)).Should().Be(1));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    // TEST-09: Reject a JSON null business-hour element with an indexed validation error and no restaurant writes.
    /// <summary>Guards the registration aggregate mapper from malformed nested requests.</summary>
    [PostgresEndToEndFact]
    public async Task RegisterRestaurant_WithNullBusinessHour_ShouldRejectBeforePersistence()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await RegisterOwnerAndLoginAsync(client, "null-hours-owner@integration.test");
        Authorize(client, auth.AccessToken);
        var request = RegistrationRequest("Null Hours Restaurant", "0901234567");
        request.BusinessHours = [null!];

        using var response = await client.PostAsJsonAsync("/api/v1/owner/restaurant", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        envelope!.Success.Should().BeFalse();
        envelope.ErrorCode.Should().Be("VALIDATION_FAILED");
        envelope.Errors.Should().BeEquivalentTo(new Dictionary<string, string[]>
        {
            ["businessHours[0]"] = ["Khung giờ mở cửa không hợp lệ."]
        });
        await _factory.ExecuteDbAsync(async context => (await context.Restaurants.CountAsync()).Should().Be(0));
    }

    // TEST-10: Reject owned PDFs as cover or gallery images, then register successfully with an owned image and opening hours.
    /// <summary>Verifies image-only validation, rollback, and complete aggregate persistence through real HTTP and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task RegisterRestaurant_WithOwnedMedia_ShouldRequireImagesAndPersistValidAggregate()
    {
        await _factory.ResetDatabaseAsync();
        using var client = _factory.CreateClient();
        var auth = await RegisterOwnerAndLoginAsync(client, "image-contact-owner@integration.test");
        Authorize(client, auth.AccessToken);
        using var pdfUpload = await UploadAsync(client, "verification_doc", "license.pdf", "application/pdf", "%PDF test"u8.ToArray());
        var document = await SuccessDataAsync<MediaFileDto>(pdfUpload);

        foreach (var isCover in new[] { true, false })
        {
            var invalid = RegistrationRequest("Document Image Restaurant", "0901234567");
            invalid.CoverMediaId = isCover ? document.Id : null;
            invalid.ImageMediaIds = isCover ? null : [document.Id];
            using var rejected = await client.PostAsJsonAsync("/api/v1/owner/restaurant", invalid);

            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var error = await rejected.Content.ReadFromJsonAsync<ApiResponse<object>>();
            error!.Success.Should().BeFalse();
            error.ErrorCode.Should().Be("VALIDATION_FAILED");
            error.Errors.Should().ContainSingle().Which.Key.Should().Be(isCover ? "coverMediaId" : "imageMediaIds");
        }

        await _factory.ExecuteDbAsync(async context => (await context.Restaurants.CountAsync()).Should().Be(0));
        using var imageUpload = await UploadAsync(client, "restaurant", "restaurant.png", "image/png", [1, 2, 3, 4]);
        var image = await SuccessDataAsync<MediaFileDto>(imageUpload);
        var request = RegistrationRequest("Complete Image Restaurant", "0236 3812 345");
        request.CoverMediaId = image.Id;
        request.ImageMediaIds = [image.Id];
        request.BusinessHours = [new BusinessHourDto { DayOfWeek = 6, OpenTime = "22:00", CloseTime = "02:00" }];

        using var response = await client.PostAsJsonAsync("/api/v1/owner/restaurant", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var restaurant = await SuccessDataAsync<OwnerRestaurantDto>(response);
        restaurant.CoverUrl.Should().Be(image.Url);
        restaurant.Images.Should().Equal(image.Url);
        restaurant.BusinessHours.Should().BeEquivalentTo(request.BusinessHours);
        await _factory.ExecuteDbAsync(async context =>
        {
            var stored = await context.Restaurants.Include(value => value.Images).Include(value => value.BusinessHours)
                .AsSplitQuery().SingleAsync(value => value.Id == restaurant.Id);
            stored.Phone.Should().Be(request.Phone);
            stored.CoverMediaId.Should().Be(image.Id);
            stored.Images.Should().ContainSingle().Which.MediaId.Should().Be(image.Id);
            stored.BusinessHours.Should().ContainSingle().Which.OpenTime.Should().Be(new TimeOnly(22, 0));
        });
    }

    /// <summary>Provides invalid phone regions shared by registration and update HTTP regression scenarios.</summary>
    private static string?[] InvalidPhones() =>
    [
        null, "", "   ", "1234567890", "0000000000", "0123456789", "0301234567",
        "0541234567", "0711234567", "02023812345", "090123456", "09012345678",
        "0236381234", "023638123456", "+840901234567", "+6590123456",
        "090123456a", "０９０１２３４５６７", "090\t1234567", "0901234567\n",
        "090\u200b1234567", "090..1234567", "0901234567".PadRight(21)
    ];

    /// <summary>Asserts the exact Vietnamese validation envelope and a single camelCase phone field error.</summary>
    private static async Task AssertPhoneValidationAsync(HttpResponseMessage response, string? phone, bool isRegistration)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeFalse();
        envelope.ErrorCode.Should().Be("VALIDATION_FAILED");
        envelope.Data.Should().BeNull();
        var expected = string.IsNullOrWhiteSpace(phone)
            ? isRegistration ? "Số điện thoại là bắt buộc." : "Số điện thoại không được để trống."
            : phone.Length > 20 ? "Số điện thoại không được vượt quá 20 ký tự."
            : "Số điện thoại phải là số di động hoặc số cố định Việt Nam hợp lệ.";
        envelope.Errors.Should().BeEquivalentTo(new Dictionary<string, string[]> { ["phone"] = [expected] });
    }

    /// <summary>Registers, verifies, and logs in a restaurant owner through public HTTP endpoints.</summary>
    private async Task<AuthResultDto> RegisterOwnerAndLoginAsync(HttpClient client, string email)
    {
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterAccountRequestDto
        {
            Email = email,
            Password = Password,
            DisplayName = "Integration Owner",
            Role = "RESTAURANT_OWNER"
        });
        register.EnsureSuccessStatusCode();
        using var verify = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequestDto
        {
            Token = _factory.Email.VerificationToken(email)
        });
        verify.EnsureSuccessStatusCode();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequestDto
        {
            Email = email,
            Password = Password
        });
        login.EnsureSuccessStatusCode();
        return await SuccessDataAsync<AuthResultDto>(login);
    }

    /// <summary>Registers a valid restaurant through the authenticated owner endpoint.</summary>
    private static async Task<OwnerRestaurantDto> RegisterRestaurantAsync(
        HttpClient client, string name, string phone = "0900000000")
    {
        using var response = await client.PostAsJsonAsync("/api/v1/owner/restaurant", RegistrationRequest(name, phone));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await SuccessDataAsync<OwnerRestaurantDto>(response);
    }

    /// <summary>Creates a valid registration request while allowing each test to choose the contact input.</summary>
    private static RegisterRestaurantRequestDto RegistrationRequest(string name, string phone) => new()
        {
            Name = name,
            Description = "Restaurant created by the end-to-end integration suite.",
            Phone = phone,
            Email = "restaurant@integration.test",
            AddressLine = "1 Integration Street",
            District = "District 1",
            ProvinceName = "Ho Chi Minh City",
            Latitude = 10.775m,
            Longitude = 106.700m,
            PriceLevel = 2
        };

    /// <summary>Uploads one multipart file through MEDIA-01 and returns the live HTTP response.</summary>
    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        string purpose,
        string fileName,
        string mimeType,
        byte[] bytes)
    {
        var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        multipart.Add(file, "File", fileName);
        multipart.Add(new StringContent(purpose), "Purpose");
        return client.PostAsync("/api/v1/media", multipart);
    }

    /// <summary>Adds the issued bearer token to subsequent owner requests.</summary>
    private static void Authorize(HttpClient client, string accessToken)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    /// <summary>Reads a successful API envelope and returns its required data payload.</summary>
    private static async Task<T> SuccessDataAsync<T>(HttpResponseMessage response)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }
}
