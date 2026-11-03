using ANGI.Application.Common.Models.Agent;

namespace ANGI.Application.Services.Agent;

/// <summary>
/// Attaches the model's wording about dishes and days to the final preview. It runs after every backend repair, so text about a
/// dish that was swapped out never reaches the user; anything unverifiable is dropped and the card simply shows less.
/// </summary>
public static class AgentProposalNotes
{
    private const int MaxNoteLength = 200;
    private const int MaxSummaryLength = 320;

    /// <summary>Keeps earlier notes for what did not change and adds this turn's checked notes.</summary>
    public static AgentSnapshot Attach(AgentSnapshot snapshot, AgentContext context)
    {
        var dishNotes = (context.Source?.DishNotes ?? []).ToDictionary(note => note.DishId);
        var written = context.PreviewOutput?.DishNotes ?? [];
        var toolResults = string.Join(' ', context.Exchanges.SelectMany(exchange => exchange.Results).Select(result => result.GetRawText()));
        foreach (var note in written)
        {
            if (snapshot.Items.FirstOrDefault(item => item.DishId == note.DishId) is not { } item) continue;
            var trusted = string.Join(' ', item.DishName, item.RestaurantName, snapshot.State.Province, toolResults);
            string? Checked(string? text) => AgentGroundedReply.IsGroundedNote(text, trusted, MaxNoteLength) ? text!.Trim() : null;
            if (Checked(note.Intro) is var intro && Checked(note.Reason) is var reason && (intro is not null || reason is not null))
                dishNotes[note.DishId] = new(note.DishId, intro, reason);
        }
        var present = snapshot.Items.Select(item => item.DishId).ToHashSet();
        return snapshot with
        {
            DishNotes = [.. dishNotes.Values.Where(note => present.Contains(note.DishId)).OrderBy(note => note.DishId)],
            DaySummaries = [.. snapshot.Items.Select(item => item.DayNumber).Distinct().Order().SelectMany(day => DaySummary(day, snapshot, context))]
        };
    }

    /// <summary>A day's new summary, or its earlier one if the day still holds the same dishes; an old summary must not describe dishes that left.</summary>
    private static IEnumerable<AgentDaySummary> DaySummary(short day, AgentSnapshot snapshot, AgentContext context)
    {
        var items = snapshot.Items.Where(item => item.DayNumber == day).ToArray();
        var trusted = string.Join(' ', items.SelectMany(item => new[] { item.DishName, item.RestaurantName }).Append(snapshot.State.Province).Append(snapshot.State.Title));
        var written = (context.PreviewOutput?.DaySummaries ?? []).FirstOrDefault(summary => summary.DayNumber == day);
        if (written is not null && AgentGroundedReply.IsGroundedNote(written.Summary, trusted, MaxSummaryLength))
        {
            yield return new(day, written.Summary.Trim());
            yield break;
        }
        var before = context.Source?.Items.Where(item => item.DayNumber == day).Select(item => item.DishId).ToHashSet();
        if (before is not null && before.SetEquals(items.Select(item => item.DishId)) &&
            context.Source?.DaySummaries?.FirstOrDefault(summary => summary.DayNumber == day) is { } earlier)
            yield return earlier;
    }
}
