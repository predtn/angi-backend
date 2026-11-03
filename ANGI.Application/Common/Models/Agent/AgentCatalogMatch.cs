namespace ANGI.Application.Common.Models.Agent;

/// <summary>Public catalog identity; availability is separate from existence.</summary>
public sealed record AgentCatalogMatch(long Id, string Name, long RestaurantId, string RestaurantName,
    string? Province, string Address, bool Available);

/// <summary>A name lookup cannot authorize a roadmap write or silently choose a near match.</summary>
public sealed record AgentCatalogInput(string Kind, string Name, string? Province = null, long? SelectedId = null);
