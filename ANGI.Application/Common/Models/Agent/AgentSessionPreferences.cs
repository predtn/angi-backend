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

/// <summary>A user-specified target; an omitted dimension inherits its profile value. <paramref name="LevelQuote"/> holds the user's own words for the level while the model proposes it, and is never stored.</summary>
public sealed record AgentTasteTarget(string Dimension, double Target, string? LevelQuote = null);

/// <summary>Fixed ontology and deterministic application of validated taste targets.</summary>
public static class AgentTasteOntology
{
    public static readonly string[] Dimensions = ["sweet", "sour", "salty", "bitter", "umami", "spicy",
        "cooling", "astringent", "fatty", "fresh", "rich", "aromatic"];
    public static readonly double[] Scale = [0, .25, .5, .75, 1];

    public static bool ValidVector(double[]? vector) => vector is null || vector.Length == 12 && vector.All(double.IsFinite);

    /// <summary>How much of a taste the user did not mention stays in the ranking vector once they stated another one.</summary>
    public const double UnstatedWeight = 0.5;

    /// <summary>
    /// The ranking vector for the stated targets. A taste the user wants (0.5 and up) never sits below their profile, and the tastes they did not mention
    /// count half, so the stated one leads: "muốn ăn cay" on a profile already at spicy 0.5 with cooling and fresh at 1 changed nothing and planned
    /// steamed fish and soups (conversation 109, 2026-11-27). A taste they do not want (below 0.5) is used as stated.
    /// </summary>
    public static AgentSessionPreferences Apply(AgentSessionPreferences preferences, AgentTasteTarget[] targets)
    {
        var baseline = preferences.BaselineVector ?? new double[12];
        var vector = baseline.ToArray();
        var stated = new HashSet<int>();
        foreach (var target in targets)
        {
            var index = Array.IndexOf(Dimensions, target.Dimension);
            if (index < 0) continue;
            stated.Add(index);
            vector[index] = target.Target >= 0.5 ? Math.Max(target.Target, baseline[index]) : target.Target;
        }
        if (stated.Count > 0)
            for (var index = 0; index < vector.Length; index++)
                if (!stated.Contains(index)) vector[index] *= UnstatedWeight;
        return preferences with { EffectiveVector = vector.Any(x => x != 0) ? vector : null };
    }
}
