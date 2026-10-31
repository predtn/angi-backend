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
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Agent:{property}"] = value }).Build();
        var act = () => new ServiceCollection().AddAgentInfrastructure(configuration);
        act.Should().Throw<InvalidOperationException>();
    }

    // TEST-02: Bind valid default limits without requiring provider credentials or a database connection.
    /// <summary>Checks optional deployment configuration leaves startup usable with controlled request-time availability errors.</summary>
    [Fact]
    public void AddAgentInfrastructure_Defaults_AreSafeWithoutSecrets()
    {
        var services = new ServiceCollection(); services.AddAgentInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var limits = provider.GetRequiredService<AgentLimits>(); limits.LeaseSeconds.Should().BeGreaterThan(limits.TimeoutSeconds);
        limits.MaxCandidates.Should().Be(2000); limits.MaxRanked.Should().Be(200); limits.RetentionDays.Should().Be(30);
        limits.MaxOutputTokens.Should().Be(16000); limits.ReasoningEffort.Should().BeNull();
    }

    /// <summary>Binds supported reasoning controls without requiring live provider credentials.</summary>
    [Theory]
    [InlineData("none")][InlineData("minimal")][InlineData("low")]
    [InlineData("medium")][InlineData("high")]
    public void AddAgentInfrastructure_ReasoningEffort_BindsConfiguredValue(string effort)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:ReasoningEffort"] = effort
        }).Build();
        var services = new ServiceCollection(); services.AddAgentInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<AgentLimits>().ReasoningEffort.Should().Be(effort);
    }

    // TEST-03: Agent's provider timeout is independent of shared roadmap AI settings and capped by the turn deadline.
    /// <summary>Checks configured typed HTTP clients without making a provider request.</summary>
    [Theory]
    [InlineData(60, 90, 60)][InlineData(180, 90, 90)]
    public void AddAgentInfrastructure_ProviderTimeout_IsIndependentAndCapped(int perCall, int perTurn, int expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:TimeoutSeconds"] = "1", ["Agent:LlmTimeoutSeconds"] = perCall.ToString(), ["Agent:TimeoutSeconds"] = perTurn.ToString()
        }).Build();
        var services = new ServiceCollection(); services.AddAiInfrastructure(configuration); services.AddAgentInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IAgentLlmService));
        client.Timeout.Should().Be(TimeSpan.FromSeconds(expected));
    }
}
