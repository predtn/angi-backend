using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.UseCases.Administration.GetSystemAuditLogDetail;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.GetSystemAuditLogDetail;

/// <summary>Verifies ADM-A2 (F-64) audit log detail retrieval.</summary>
public sealed class GetSystemAuditLogDetailUseCaseTests
{
    private readonly Mock<IAdministrationAuditLogRepository> _repository = new();

    /// <summary>Maps jsonb old/new values to JSON objects.</summary>
    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsDetailWithParsedValues()
    {
        _repository.Setup(r => r.GetAuditLogByIdAsync(9921, It.IsAny<CancellationToken>())).ReturnsAsync(new AuditLogDetailRecord
        {
            Id = 9921,
            Action = AuditActions.UserSuspended,
            EntityType = AuditEntityTypes.User,
            EntityId = 12,
            OldValues = "{\"status\":\"active\"}",
            NewValues = "{\"status\":\"suspended\",\"reason\":\"Spam\"}",
            IpAddress = "113.161.4.20",
            UserAgent = "Mozilla/5.0"
        });
        var useCase = new GetSystemAuditLogDetailUseCase(_repository.Object);

        var result = await useCase.ExecuteAsync(9921, CancellationToken.None);

        result.Id.Should().Be(9921);
        result.OldValues!.Value.GetProperty("status").GetString().Should().Be("active");
        result.NewValues!.Value.GetProperty("reason").GetString().Should().Be("Spam");
        result.IpAddress.Should().Be("113.161.4.20");
    }

    /// <summary>Keeps old/new values null when the columns are null.</summary>
    [Fact]
    public async Task ExecuteAsync_NullValues_ReturnsNullObjects()
    {
        _repository.Setup(r => r.GetAuditLogByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditLogDetailRecord { Id = 1, Action = AuditActions.UserLogin });
        var useCase = new GetSystemAuditLogDetailUseCase(_repository.Object);

        var result = await useCase.ExecuteAsync(1, CancellationToken.None);

        result.OldValues.Should().BeNull();
        result.NewValues.Should().BeNull();
    }

    /// <summary>Throws 404 AUDIT_LOG_NOT_FOUND when the record does not exist.</summary>
    [Fact]
    public async Task ExecuteAsync_NotFound_ThrowsNotFoundException()
    {
        _repository.Setup(r => r.GetAuditLogByIdAsync(404, It.IsAny<CancellationToken>())).ReturnsAsync((AuditLogDetailRecord?)null);
        var useCase = new GetSystemAuditLogDetailUseCase(_repository.Object);

        var act = () => useCase.ExecuteAsync(404, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("AUDIT_LOG_NOT_FOUND");
    }

    /// <summary>Treats a non-positive id as not found without querying.</summary>
    [Fact]
    public async Task ExecuteAsync_NonPositiveId_ThrowsNotFoundWithoutQuery()
    {
        var useCase = new GetSystemAuditLogDetailUseCase(_repository.Object);

        var act = () => useCase.ExecuteAsync(0, CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        _repository.Verify(r => r.GetAuditLogByIdAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
