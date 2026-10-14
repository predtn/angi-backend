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
    private static async Task<OwnerRestaurantDto> RegisterRestaurantAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/owner/restaurant", new RegisterRestaurantRequestDto
        {
            Name = name,
            Description = "Restaurant created by the end-to-end integration suite.",
            Phone = "0900000000",
            Email = "restaurant@integration.test",
            AddressLine = "1 Integration Street",
            District = "District 1",
            ProvinceName = "Ho Chi Minh City",
            Latitude = 10.775m,
            Longitude = 106.700m,
            PriceLevel = 2
        });
        response.EnsureSuccessStatusCode();
        return await SuccessDataAsync<OwnerRestaurantDto>(response);
    }

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
