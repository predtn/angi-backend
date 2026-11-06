using System.Text.Json;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Infrastructure.Services.Agent;
using ANGI.Infrastructure.Services.Ai;
using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace ANGI.IntegrationTests.Infrastructure.Services.Agent;

/// <summary>Measures how the real model reads spoken Vietnamese, one provider call per sentence, before a backend rule is removed.</summary>
public sealed class OpenAiAgentLlmServiceLiveTests(ITestOutputHelper output)
{
    private const int CaseCount = 178;
    private const int MaxConsecutiveFailures = 3;
    private const string ChangeQuestion = "Mình đổi số ngày: 3 nhé? Bạn nhắn “ok” để đổi hoặc “thôi” để giữ nguyên. Bản lịch hiện tại chưa thay đổi.";
    private static readonly string[] Actions = ["clarify", "answer", "answer:accept", "answer:decline", "answer:defer", "answer:unclear", "create_proposal", "patch_proposal", "request_save", "chat", "food_info", "out_of_scope"];

    // TEST-01: The sentence set is complete and well formed: unique sentences, known stages and outcomes, at least 20 that must be refused and at least 10 that must never count as agreeing. Makes no provider call.
    /// <summary>Catches a broken set before anyone pays to run it.</summary>
    [Fact]
    public void Cases_AreWellFormed()
    {
        // STEP-01: Get cases from Load.
        var cases = Load();

        // STEP-02: Assert cases has CaseCount item(s), cases.Select(filtered) has no duplicates, cases.Count(filtered) is at least 20 and 1 more.
        cases.Should().HaveCount(CaseCount);
        cases.Select(x => x.Id).Should().OnlyHaveUniqueItems();
        cases.Select(x => (x.Stage, x.Message)).Should().OnlyHaveUniqueItems();
        cases.Count(x => x.MustRefuse).Should().BeGreaterThanOrEqualTo(20);
        cases.Count(x => x.MustNotAccept).Should().BeGreaterThanOrEqualTo(10);
        cases.Select(x => x.Stage).Distinct().Should().BeSubsetOf(["none", "budget_question", "plan", "offered", "save_question", "save_date", "state_change"]);
        // STEP-03: Assert cases only holds items matching the condition and cases.Where(filtered) only holds items matching the condition.
        cases.Should().OnlyContain(x => x.Expected.Length > 0 &&
            x.Expected.All(value => Actions.Contains(value) || AgentLlmSchema.Tools.ContainsKey(value.Replace("tool:", ""))));
        cases.Where(x => x.MustRefuse).Should().OnlyContain(x => x.Expected.SequenceEqual(new[] { "out_of_scope" }));
    }

    // TEST-02: The real model refuses every sentence outside ANGI; the other groups are reported, not asserted, because their accuracy is what this run measures.
    /// <summary>Paid: about one provider call per sentence. It stops after repeated provider failures so an outage does not spend the budget on nothing.</summary>
    [LiveAgentEvaluationFact]
    public async Task RunAsync_RealModel_RefusesEverythingOutOfScope()
    {
        // STEP-01: Create results as List<AgentEvaluationResult>, define failures as 0 and 5 more.
        var results = new List<AgentEvaluationResult>();
        var failures = 0;
        foreach (var testCase in Load())
        {
            var step = await Service().RunAsync(Context(testCase), false, CancellationToken.None);
            failures = step?.Failure is null ? 0 : failures + 1;
            results.Add(Result(testCase, step));
            if (failures >= MaxConsecutiveFailures) break;
        }

        var report = Environment.GetEnvironmentVariable("ANGI_TEST_AGENT_REPORT") ?? Path.Combine(Path.GetTempPath(), "angi-agent-evaluation.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        output.WriteLine($"Report: {report}");
        foreach (var group in results.GroupBy(x => x.Group))
            output.WriteLine($"{group.Key}: {group.Count(x => x.Passed)}/{group.Count()}; failed: {string.Join(", ", group.Where(x => !x.Passed).Select(x => x.Id))}");

        // STEP-02: Assert results has CaseCount item(s) and results.Where(filtered).Select(filtered) is empty.
        results.Should().HaveCount(CaseCount, "the run must not stop early on provider failures");
        results.Where(x => x.MustRefuse && !x.Passed).Select(x => x.Id).Should().BeEmpty("a sentence outside ANGI must never be answered");
        results.Where(x => x.MustNotAccept && x.Chosen == "answer:accept").Select(x => x.Id).Should().BeEmpty("a refusal or a stray word must never save a plan or change conditions");
    }

    /// <summary>Reads the sentence set copied next to the test assembly.</summary>
    private static AgentEvaluationCase[] Load() => JsonSerializer.Deserialize<AgentEvaluationCase[]>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentEvaluationCases.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    /// <summary>Builds the real provider client from the environment; the key never comes from a file in the repository.</summary>
    private static OpenAiAgentLlmService Service()
    {
        var settings = new AiSettings { ApiKey = Environment.GetEnvironmentVariable(LiveAgentEvaluationFactAttribute.ApiKeyVariable)!,
            Model = Environment.GetEnvironmentVariable("ANGI_TEST_AI_MODEL") ?? "gpt-6-luna" };
        return new(new HttpClient { BaseAddress = new Uri(settings.BaseUrl), Timeout = TimeSpan.FromSeconds(60) }, Options.Create(settings), new AgentLimits(), TimeProvider.System);
    }

    /// <summary>Builds what the model sees at the stage the sentence is sent in: nothing yet, the budget question, a two-day plan, or that plan right after a suggestion.</summary>
    private static AgentContext Context(AgentEvaluationCase testCase)
    {
        var withPlan = testCase.Stage is "plan" or "offered" or "save_question" or "save_date" or "state_change";
        var plan = new AgentState { Province = "Hà Nội", NumDays = 2, BudgetAmount = 800000, MealSlots = ["breakfast", "lunch", "dinner"] };
        var dishes = new[] { ("Phở Bò", "Quán Phở Thìn"), ("Bún Chả", "Quán Hương Liên"), ("Bánh Mì", "Quán Bà Dần") };
        var items = Enumerable.Range(0, 6).Select(index => new AgentItem($"i{index}", null, (short)(index / 3 + 1), plan.MealSlots[index % 3], 0, index + 1,
            Guid.NewGuid(), 50000, dishes[index % 3].Item1, dishes[index % 3].Item2)).ToArray();
        return new AgentContext
        {
            ConversationId = 1, UserId = 1, TurnId = 1, AttemptId = 1, Phase = AgentTurnPhase.Read,
            State = testCase.Stage switch
            {
                "budget_question" => new AgentState { Province = "Hà Nội", NumDays = 3, PlanningIntake = AgentIntakePolicy.Details,
                    PendingQuestion = "Bạn cho mình biết ngân sách cho một người (tổng chuyến hoặc mỗi ngày) để lập lịch nhé?" },
                "plan" or "offered" or "save_question" or "save_date" => plan,
                "state_change" => plan with { PendingQuestion = ChangeQuestion, PendingStateChange = plan with { NumDays = 3 } },
                _ => new AgentState()
            },
            Source = withPlan ? new AgentSnapshot(plan, items, []) : null,
            CatalogProvinces = ["Hà Nội"],
            DishOffered = testCase.Stage == "offered",
            AwaitedAnswer = testCase.Stage switch { "save_question" => AgentConfirmationPolicy.AwaitingSave, "save_date" => AgentConfirmationPolicy.AwaitingSaveDate, "state_change" => AgentConfirmationPolicy.AwaitingStateChange, _ => null },
            History = testCase.Stage switch
            {
                "offered" => [new("user", "gợi ý cho tôi 1 món bất kì"), new("assistant", "Bạn thử Cơm Tấm ở Quán Bảy nhé, khoảng 45.000 đồng. Muốn cho vào lịch thì nhắn mình ngày và bữa, ví dụ bữa tối ngày 1 nha.")],
                "save_question" => [new("user", "chốt đi"), new("assistant", "Mình lưu lịch hiện tại (6 món, tổng 300.000 đồng/người) nhé?")],
                "save_date" => [new("user", "chốt đi"), new("assistant", "Lịch của bạn có 2 ngày. Bạn muốn bắt đầu từ ngày nào? Chưa biết ngày đi thì nhắn “để sau” nhé.")],
                "state_change" => [new("user", "đổi thành 3 ngày"), new("assistant", ChangeQuestion)],
                _ => []
            },
            Request = new SendAgentMessageRequestDto { Content = testCase.Message }
        };
    }

    /// <summary>Records what the model chose and whether it matches; a budget sentence also has to carry the right value in state.</summary>
    private static AgentEvaluationResult Result(AgentEvaluationCase testCase, AgentLlmStep? step)
    {
        var chosen = step switch
        {
            null => "no_response",
            { Failure: { } failure } => $"failure:{failure.Kind}",
            { Tools.Length: > 0 } => $"tool:{step.Tools[0].Name}",
            _ => step.Output is { Action: "use_tool", Tool: { } tool } ? $"tool:{tool}" : step.Output is { Action: "answer", PendingAnswer: { } reading } ? $"answer:{reading}" : step.Output?.Action ?? "no_output"
        };
        var state = step?.Output?.State;
        var passed = testCase.Expected.Contains(chosen) &&
            (testCase.BudgetUnrestricted is not true || state is { BudgetUnrestricted: true, BudgetAmount: null }) &&
            (testCase.BudgetAmount is not { } amount || state?.BudgetAmount == amount) &&
            (!testCase.Quoted || !string.IsNullOrWhiteSpace(state?.BudgetQuote));
        return new(testCase.Id, testCase.Group, testCase.Message, testCase.MustRefuse, testCase.MustNotAccept, passed, chosen, step?.Output?.Reply, state?.BudgetAmount, state?.BudgetUnrestricted, step?.Tokens);
    }

    /// <summary>One sentence and what a correct reading of it looks like.</summary>
    private sealed record AgentEvaluationCase(string Id, string Group, string Stage, string Message, string[] Expected,
        bool MustRefuse = false, bool MustNotAccept = false, bool Quoted = false, bool? BudgetUnrestricted = null, decimal? BudgetAmount = null);

    /// <summary>One sentence's outcome in the report, including the model's own wording for review.</summary>
    private sealed record AgentEvaluationResult(string Id, string Group, string Message, bool MustRefuse, bool MustNotAccept, bool Passed, string Chosen, string? Reply,
        decimal? BudgetAmount, bool? BudgetUnrestricted, int? Tokens);
}
