using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Administration.GetSystemAuditLogs;
using ANGI.Application.UseCases.Validators.Administration;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.GetSystemAuditLogs;

/// <summary>Verifies ADM-A1 (F-64) audit log listing, pagination and validation.</summary>
public sealed class GetSystemAuditLogsUseCaseTests
{
    private readonly Mock<IAdministrationAuditLogRepository> _repository = new();
    private readonly GetSystemAuditLogsUseCase _useCase;

    public GetSystemAuditLogsUseCaseTests()
    {
        _useCase = new GetSystemAuditLogsUseCase(new GetSystemAuditLogsRequestDtoValidator(), _repository.Object);
    }

    /// <summary>Returns the repository page when filters and paging are valid.</summary>
    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsPagedAuditLogs()
    {
        var request = new GetSystemAuditLogsRequestDto
        {
            Action = AuditActions.UserSuspended,
            ActorRole = "MOD",
            From = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc),
            Page = 2,
            PageSize = 20
        };
        var expected = new Paged<AuditLogDto>
        {
            Items = [new AuditLogDto { Id = 9921, Action = AuditActions.UserSuspended }],
            Page = 2,
            PageSize = 20,
            TotalItems = 21,
            TotalPages = 2
        };
        _repository.Setup(r => r.GetAuditLogsAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await _useCase.ExecuteAsync(request, CancellationToken.None);

        result.Should().BeSameAs(expected);
        _repository.Verify(r => r.GetAuditLogsAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Throws a validation error (400) and skips the query when from is after to.</summary>
    [Fact]
    public async Task ExecuteAsync_FromAfterTo_ThrowsValidationException()
    {
        var request = new GetSystemAuditLogsRequestDto
        {
            From = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var act = () => _useCase.ExecuteAsync(request, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().Contain(e => e.PropertyName == nameof(GetSystemAuditLogsRequestDto.From));
        _repository.Verify(r => r.GetAuditLogsAsync(It.IsAny<GetSystemAuditLogsRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Rejects out-of-range paging and unknown enum filters.</summary>
    [Theory]
    [InlineData(0, 20, null, null)]
    [InlineData(1, 0, null, null)]
    [InlineData(1, 101, null, null)]
    [InlineData(1, 20, "NOT_AN_ACTION", null)]
    [InlineData(1, 20, null, "NOT_A_TYPE")]
    public async Task ExecuteAsync_InvalidParameters_ThrowsValidationException(int page, int pageSize, string? action, string? entityType)
    {
        var request = new GetSystemAuditLogsRequestDto { Page = page, PageSize = pageSize, Action = action, EntityType = entityType };

        var act = () => _useCase.ExecuteAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }
}
