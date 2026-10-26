using ANGI.Application.Common.Models.Agent;
using ANGI.Infrastructure.Services.Agent;
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
    [InlineData("LeaseSeconds", "90")][InlineData("LeaseSeconds", "601")]
    [InlineData("MessagesPerMinute", "0")][InlineData("MessagesPerDay", "9")]
    [InlineData("ActiveDays", "0")][InlineData("RetentionDays", "6")][InlineData("RetentionDays", "366")]
    [InlineData("CleanupSeconds", "0")][InlineData("CleanupSeconds", "86401")]
    [InlineData("MaxOutputTokens", "99")][InlineData("MaxOutputTokens", "16001")]
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
    }
}
