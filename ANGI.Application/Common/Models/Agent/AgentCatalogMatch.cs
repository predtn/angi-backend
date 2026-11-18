namespace ANGI.Application.Common.Models.Agent;

/// <summary>Public catalog identity; availability is separate from existence.</summary>
public sealed record AgentCatalogMatch(long Id, string Name, long RestaurantId, string RestaurantName,
    string? Province, string Address, bool Available);

/// <summary>The words of one public, available dish that a near-name search reads: its name, description and tag names.</summary>
public sealed record AgentDishText(string Name, string? Description, decimal Price, long RestaurantId, string[] Tags);

/// <summary>A dish name that shares words with a name the catalog does not hold; Score only orders the list, the model decides what means the same.</summary>
public sealed record AgentNearDish(string Name, string? Description, decimal Price, int Restaurants, int Score);

/// <summary>A name lookup cannot authorize a roadmap write or silently choose a near match.</summary>
public sealed record AgentCatalogInput(string Kind, string Name, string? Province = null, long? SelectedId = null);
