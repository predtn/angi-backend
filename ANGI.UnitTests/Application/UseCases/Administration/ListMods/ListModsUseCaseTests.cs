using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.UseCases.Administration.ListMods;
using ANGI.Application.UseCases.Validators.Administration;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.ListMods;

/// <summary>Verifies ListModsUseCase normalizes filters, validates paging and maps MOD cards with page metadata.</summary>
public sealed class ListModsUseCaseTests
{
    // TEST-01: Trim the search term, map the status, and preserve page metadata and card fields.
    /// <summary>Verifies the complete successful ADM-M1 mapping and repository delegation.</summary>
    [Fact]
    public async Task ExecuteAsync_WithFilters_ShouldReturnMappedPage()
    {
        // STEP-01: Create createdAt as DateTime.
        var createdAt = new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc);
        // STEP-02: Mock IAdministrationModRepository; stub repository.GetModsAsync.
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.GetModsAsync("Linh", UserStatus.Deactivated, 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Paged<AdminUserSnapshot>
            {
                Items = [new(12, "linh@angi.vn", "Linh", null, "MOD", UserStatus.Deactivated, null, null, createdAt)],
                Page = 2,
                PageSize = 10,
                TotalItems = 11,
                TotalPages = 2
            });
        // STEP-03: Create the ListModsUseCase under test as useCase.
        var useCase = new ListModsUseCase(new ListModsRequestDtoValidator(), repository.Object);

        // STEP-04: Run useCase.ExecuteAsync with ListModsRequestDto { Q = " Linh ", Status = "deactivated", Page = 2, ... } and keep result.
        var result = await useCase.ExecuteAsync(
            new ListModsRequestDto { Q = "  Linh  ", Status = "deactivated", Page = 2, PageSize = 10 },
            CancellationToken.None);

        // STEP-05: Assert result.TotalItems is 11, result.Items has exactly one matching item, result.Items.Status is "deactivated" and 1 more.
        result.TotalItems.Should().Be(11);
        result.Items.Should().ContainSingle().Which.Status.Should().Be("deactivated");
        result.Items[0].CreatedAt.Should().Be(createdAt);
    }

    // TEST-02: Treat blank search and missing status as absent filters.
    /// <summary>Verifies optional ADM-M1 query parameters are normalized to null.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutFilters_ShouldPassNullFilters()
    {
        // STEP-01: Mock IAdministrationModRepository; stub repository.GetModsAsync.
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.GetModsAsync(null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Paged<AdminUserSnapshot> { Page = 1, PageSize = 20 });
        // STEP-02: Create the ListModsUseCase under test as useCase.
        var useCase = new ListModsUseCase(new ListModsRequestDtoValidator(), repository.Object);

        // STEP-03: Run useCase.ExecuteAsync with ListModsRequestDto { Q = " " }.
        await useCase.ExecuteAsync(new ListModsRequestDto { Q = "   " }, CancellationToken.None);

        // STEP-04: Verify every setup on repository ran.
        repository.VerifyAll();
    }

    // TEST-03: Validate invalid paging before invoking persistence.
    /// <summary>Verifies validation remains the first operation in ADM-M1.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidPaging_ShouldStopBeforeRepository()
    {
        // STEP-01: Mock IAdministrationModRepository.
        var repository = new Mock<IAdministrationModRepository>();
        // STEP-02: Create the ListModsUseCase under test as useCase.
        var useCase = new ListModsUseCase(new ListModsRequestDtoValidator(), repository.Object);

        // STEP-03: Wrap useCase.ExecuteAsync with ListModsRequestDto { Page = 0, PageSize = 101 } in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            new ListModsRequestDto { Page = 0, PageSize = 101 },
            CancellationToken.None);

        // STEP-04: Assert act throws FluentValidation.ValidationException.
        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
        // STEP-05: Verify repository receives no other calls.
        repository.VerifyNoOtherCalls();
    }
}
