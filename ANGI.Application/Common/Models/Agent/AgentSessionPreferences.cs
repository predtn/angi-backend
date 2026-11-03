namespace ANGI.Application.Common.Models.Agent;

/// <summary>Backend-owned profile snapshot and request vector, independent of long-term feedback.</summary>
public sealed record AgentSessionPreferences
{
    public int OntologyVersion { get; init; } = 1;
    public double[]? BaselineVector { get; init; }
    public double[]? EffectiveVector { get; init; }
    public DateTime? ProfileUpdatedAt { get; init; }
    public DateTime? ProfileReadAt { get; init; }
    public bool Initialized { get; init; }
}

/// <summary>A user-specified target; an omitted dimension inherits its profile value.</summary>
public sealed record AgentTasteTarget(string Dimension, double Target);

/// <summary>Fixed ontology and deterministic application of validated taste targets.</summary>
public static class AgentTasteOntology
{
    public static readonly string[] Dimensions = ["sweet", "sour", "salty", "bitter", "umami", "spicy",
        "cooling", "astringent", "fatty", "fresh", "rich", "aromatic"];
    public static readonly double[] Scale = [0, .25, .5, .75, 1];

    public static bool ValidVector(double[]? vector) => vector is null || vector.Length == 12 && vector.All(double.IsFinite);

    public static AgentSessionPreferences Apply(AgentSessionPreferences preferences, AgentTasteTarget[] targets)
    {
        var vector = preferences.BaselineVector?.ToArray() ?? new double[12];
        foreach (var target in targets) vector[Array.IndexOf(Dimensions, target.Dimension)] = target.Target;
        return preferences with { EffectiveVector = vector.Any(x => x != 0) ? vector : null };
    }
}
