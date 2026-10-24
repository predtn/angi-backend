namespace ANGI.Application.Common.Models.Discovery;

/// <summary>
/// A DISH-01 list ranked once by reco and paged by cursor. <see cref="Key"/> is reco's request_id, or a backend id when
/// reco returned none (Guest, fallback).
/// </summary>
public sealed record RankedDishList(
    Guid Key,
    int? UserId,
    Guid? RequestId,
    IReadOnlyList<int> DishIds,
    long? ScopeId = null);
