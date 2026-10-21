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

public sealed class ListModsUseCaseTests
{
    // TEST-01: Trim the search term, map the status, and preserve page metadata and card fields.
    /// <summary>Verifies the complete successful ADM-M1 mapping and repository delegation.</summary>
    [Fact]
    public async Task ExecuteAsync_WithFilters_ShouldReturnMappedPage()
    {
        var createdAt = new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc);
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
        var useCase = new ListModsUseCase(new ListModsRequestDtoValidator(), repository.Object);

        var result = await useCase.ExecuteAsync(
            new ListModsRequestDto { Q = "  Linh  ", Status = "deactivated", Page = 2, PageSize = 10 },
            CancellationToken.None);

        result.TotalItems.Should().Be(11);
        result.Items.Should().ContainSingle().Which.Status.Should().Be("deactivated");
        result.Items[0].CreatedAt.Should().Be(createdAt);
    }

    // TEST-02: Treat blank search and missing status as absent filters.
    /// <summary>Verifies optional ADM-M1 query parameters are normalized to null.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutFilters_ShouldPassNullFilters()
    {
        var repository = new Mock<IAdministrationModRepository>();
        repository.Setup(item => item.GetModsAsync(null, null, 1, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Paged<AdminUserSnapshot> { Page = 1, PageSize = 20 });
        var useCase = new ListModsUseCase(new ListModsRequestDtoValidator(), repository.Object);

        await useCase.ExecuteAsync(new ListModsRequestDto { Q = "   " }, CancellationToken.None);

        repository.VerifyAll();
    }

    // TEST-03: Validate invalid paging before invoking persistence.
    /// <summary>Verifies validation remains the first operation in ADM-M1.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidPaging_ShouldStopBeforeRepository()
    {
        var repository = new Mock<IAdministrationModRepository>();
        var useCase = new ListModsUseCase(new ListModsRequestDtoValidator(), repository.Object);

        var act = () => useCase.ExecuteAsync(
            new ListModsRequestDto { Page = 0, PageSize = 101 },
            CancellationToken.None);

        await act.Should().ThrowAsync<FluentValidation.ValidationException>();
        repository.VerifyNoOtherCalls();
    }
}
