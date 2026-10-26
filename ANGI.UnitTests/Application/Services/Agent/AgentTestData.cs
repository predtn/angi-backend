using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Provides deterministic business fixtures shared by mirrored Agent service tests.</summary>
internal static class AgentTestData
{
    /// <summary>Returns a complete budgeted trip with no inferred dietary guarantees.</summary>
    public static AgentState State() => new() { Province = "Da Nang", NumDays = 2, BudgetAmount = 500000 };
    /// <summary>Returns a stable day-one lunch proposal item.</summary>
    public static AgentItem Item(int id = 1, string key = "a", short day = 1, string meal = "lunch") => new(key, null, day, meal, 0, id);
    /// <summary>Returns an owned in-memory context whose state is independent from transport version.</summary>
    public static AgentContext Context(AgentSnapshot? source = null) => new()
    {
        ConversationId = 7, UserId = 3, State = State(), Source = source,
        Request = new SendAgentMessageRequestDto { ClientMessageId = 1, ExpectedConversationVersion = 1, Content = "Plan lunch" }
    };
    /// <summary>Returns a public, verified and available dish with backend-owned price and coordinates.</summary>
    public static Dish Dish(int id = 1) => new()
    {
        Id = id, Name = $"Dish {id}", Price = 50000, IsAvailable = true, Status = DishStatus.Active,
        Restaurant = new Domain.Entities.Restaurant { Name = "Real restaurant", ProvinceName = "Da Nang", Latitude = 16.05m,
            Longitude = 108.2m, VerificationStatus = RestaurantVerificationStatus.Verified }
    };
    /// <summary>Creates provenance that can only authorize dishes inside its supplied scope.</summary>
    public static AgentCandidateGrant Grant(int[]? ids = null, short? day = null, string? meal = null) =>
        new(day, meal, ids ?? [1, 2, 3], Guid.NewGuid(), State());
    /// <summary>Creates a test snapshot with trusted grants and a complete trip state.</summary>
    public static AgentSnapshot Snapshot(params AgentItem[] items) => new(State(), items, [Grant()]);
    /// <summary>Creates a complete structured answer without implying a roadmap write.</summary>
    public static AgentOutput Answer(string reply = "Please clarify") => new() { Action = "answer", Reply = reply };
    /// <summary>Configures public catalog and exact route metrics while keeping provider calls observable.</summary>
    public static (AgentProposalValidator Validator, Mock<IDishRepository> Dishes, Mock<ITagRepository> Tags, Mock<IRouteService> Routes) Validator(params Dish[] catalog)
    {
        var dishes = new Mock<IDishRepository>();
        dishes.Setup(x => x.GetCardDishesAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) => catalog.Where(x => ids.Contains(x.Id)).ToArray());
        var tags = new Mock<ITagRepository>();
        tags.Setup(x => x.GetTagsAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Tag>());
        var routes = new Mock<IRouteService>();
        routes.Setup(x => x.CalculateRouteAsync(It.IsAny<RouteCoordinate?>(), It.IsAny<IReadOnlyList<RouteCoordinate>>(), It.IsAny<RouteProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RouteCoordinate? origin, IReadOnlyList<RouteCoordinate> destinations, RouteProfile _, CancellationToken _) =>
                new RouteCalculationResult(Enumerable.Range(0, Math.Max(0, destinations.Count + (origin is null ? 0 : 1) - 1)).Select(_ => new RouteMetric(100, 20)).ToArray(), false));
        return (new(dishes.Object, tags.Object, new(routes.Object)), dishes, tags, routes);
    }
}
