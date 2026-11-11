using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Infrastructure.Services.Agent;
using ANGI.Infrastructure.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.UnitTests.Infrastructure.Services.Agent;

/// <summary>Verifies that configuration cannot disable cost, schema, lease or retention safeguards.</summary>
public sealed class AgentModuleTests
{
    // TEST-01: Reject every out-of-range backend configuration and an unsafe lease/timeout relationship.
    /// <summary>Checks each resource limit below and above its allowed interval.</summary>
    [Theory]
    [InlineData("MaxToolRounds", "0")][InlineData("MaxToolRounds", "11")]
    [InlineData("MaxToolCalls", "0")][InlineData("MaxToolCalls", "21")]
    [InlineData("MaxLlmCalls", "1")][InlineData("MaxLlmCalls", "21")]
    [InlineData("MaxMessageLength", "0")][InlineData("MaxMessageLength", "4001")]
    [InlineData("MaxContextCharacters", "3999")][InlineData("MaxContextCharacters", "500001")]
    [InlineData("MaxHistoryMessages", "0")][InlineData("MaxHistoryMessages", "101")]
    [InlineData("MaxTranscriptMessages", "19")][InlineData("MaxTranscriptMessages", "5001")]
    [InlineData("MaxCandidates", "0")][InlineData("MaxCandidates", "2001")]
    [InlineData("MaxRanked", "0")][InlineData("MaxRanked", "201")]
    [InlineData("TimeoutSeconds", "0")][InlineData("TimeoutSeconds", "301")]
    [InlineData("LlmTimeoutSeconds", "0")][InlineData("LlmTimeoutSeconds", "301")]
    [InlineData("LeaseSeconds", "90")][InlineData("LeaseSeconds", "601")]
    [InlineData("MessagesPerMinute", "0")][InlineData("MessagesPerDay", "9")]
    [InlineData("ActiveDays", "0")][InlineData("RetentionDays", "6")][InlineData("RetentionDays", "366")]
    [InlineData("CleanupSeconds", "0")][InlineData("CleanupSeconds", "86401")]
    [InlineData("MaxOutputTokens", "99")][InlineData("MaxOutputTokens", "16001")]
    [InlineData("ReasoningEffort", "unbounded")][InlineData("ReasoningEffort", "")]
    public void AddAgentInfrastructure_InvalidLimit_RejectsStartup(string property, string value)
    {
        // STEP-01: Create configuration through AddInMemoryCollection().Build.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Agent:{property}"] = value }).Build();
        // STEP-02: Wrap new ServiceCollection().AddAgentInfrastructure with configuration in act so its outcome can be asserted.
        var act = () => new ServiceCollection().AddAgentInfrastructure(configuration);
        // STEP-03: Assert act throws InvalidOperationException.
        act.Should().Throw<InvalidOperationException>();
    }

    // TEST-02: Bind valid default limits without requiring provider credentials or a database connection.
    /// <summary>Checks optional deployment configuration leaves startup usable with controlled request-time availability errors.</summary>
    [Fact]
    public void AddAgentInfrastructure_Defaults_AreSafeWithoutSecrets()
    {
        // STEP-01: Create services as ServiceCollection, call services.AddAgentInfrastructure, create provider through services.BuildServiceProvider and 1 more.
        var services = new ServiceCollection(); services.AddAgentInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        // STEP-02: Assert limits.LeaseSeconds is greater than limits.TimeoutSeconds, limits.MaxCandidates is 2000, limits.MaxRanked is 200 and 3 more.
        var limits = provider.GetRequiredService<AgentLimits>(); limits.LeaseSeconds.Should().BeGreaterThan(limits.TimeoutSeconds);
        limits.MaxCandidates.Should().Be(2000); limits.MaxRanked.Should().Be(200); limits.RetentionDays.Should().Be(30);
        limits.MaxOutputTokens.Should().Be(16000); limits.ReasoningEffort.Should().BeNull();
    }

    // TEST-03: Every supported reasoning effort binds from configuration; covers none, minimal, low, medium and high.
    /// <summary>Binds supported reasoning controls without requiring live provider credentials.</summary>
    [Theory]
    [InlineData("none")][InlineData("minimal")][InlineData("low")]
    [InlineData("medium")][InlineData("high")]
    public void AddAgentInfrastructure_ReasoningEffort_BindsConfiguredValue(string effort)
    {
        // STEP-01: Create configuration through AddInMemoryCollection().Build, create services as ServiceCollection, call services.AddAgentInfrastructure and 1 more.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:ReasoningEffort"] = effort
        }).Build();
        var services = new ServiceCollection(); services.AddAgentInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        // STEP-02: Assert provider.GetRequiredService().ReasoningEffort is effort.
        provider.GetRequiredService<AgentLimits>().ReasoningEffort.Should().Be(effort);
    }

    // TEST-04: Agent's provider timeout is independent of shared roadmap AI settings and capped by the turn deadline.
    /// <summary>Checks configured typed HTTP clients without making a provider request.</summary>
    [Theory]
    [InlineData(60, 90, 60)][InlineData(180, 90, 90)]
    public void AddAgentInfrastructure_ProviderTimeout_IsIndependentAndCapped(int perCall, int perTurn, int expected)
    {
        // STEP-01: Create configuration through AddInMemoryCollection().Build, create services as ServiceCollection, call services.AddAiInfrastructure and 2 more.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:TimeoutSeconds"] = "1", ["Agent:LlmTimeoutSeconds"] = perCall.ToString(), ["Agent:TimeoutSeconds"] = perTurn.ToString()
        }).Build();
        var services = new ServiceCollection(); services.AddAiInfrastructure(configuration); services.AddAgentInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        // STEP-02: Create the HTTP client.
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IAgentLlmService));
        // STEP-03: Assert client.Timeout is TimeSpan.FromSeconds().
        client.Timeout.Should().Be(TimeSpan.FromSeconds(expected));
    }
}
