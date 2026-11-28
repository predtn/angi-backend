namespace ANGI.Application.Common.Models.Agent;

/// <summary>One dish the picker may choose: the backend's id with the facts a reader needs to judge it (the restaurant name tells the cuisine).</summary>
public sealed record AgentPickCandidate(int Id, string Name, string Restaurant, decimal Price);

/// <summary>What the user wants beyond the filters, the dishes that passed them, and how many picks are wanted at most.</summary>
public sealed record AgentDishPickRequest(string Wish, IReadOnlyList<AgentPickCandidate> Candidates, int MaxPicks);

/// <summary>
/// The picks in order of fit. Best are dishes that clearly match the wish, Good ones fit less closely; either may be empty when nothing fits.
/// Ids are as the model wrote them: the caller checks them against the list.
/// </summary>
public sealed record AgentDishPick(int[] Best, int[] Good, int? Tokens = null);
