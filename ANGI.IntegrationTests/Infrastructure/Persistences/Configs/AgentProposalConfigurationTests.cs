using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ANGI.IntegrationTests.Infrastructure.Persistences.Configs;

/// <summary>Verifies proposal invariants against PostgreSQL's nullable CHECK semantics.</summary>
public sealed class AgentProposalConfigurationTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    // TEST-01: Reject missing and infinite source timestamps for update proposals at the database boundary.
    /// <summary>Ensures SQL NULL cannot bypass the mandatory finite timestamp or source roadmap.</summary>
    [PostgresEndToEndFact]
    public async Task UpdateProposal_RequiresCompleteFiniteSource()
    {
        var (conversationId, roadmapId) = await SetupAsync();
        foreach (var timestamp in new DateTime?[] { null, DateTime.MinValue, DateTime.MaxValue })
        {
            var proposal = Proposal(conversationId);
            proposal.Operation = AgentProposalOperation.Update;
            proposal.BaseRoadmapId = roadmapId;
            proposal.BaseRoadmapUpdatedAt = timestamp;
            await RejectAsync(proposal, "ck_agent_proposals_base");
        }
        var missingRoadmap = Proposal(conversationId);
        missingRoadmap.Operation = AgentProposalOperation.Update;
        missingRoadmap.BaseRoadmapUpdatedAt = DateTime.UnixEpoch.AddSeconds(1);
        await RejectAsync(missingRoadmap, "ck_agent_proposals_base");
        await factory.ExecuteDbAsync(async db =>
        {
            var valid = Proposal(conversationId);
            valid.Operation = AgentProposalOperation.Update;
            valid.BaseRoadmapId = roadmapId;
            valid.BaseRoadmapUpdatedAt = DateTime.UnixEpoch.AddSeconds(1);
            db.AgentProposals.Add(valid);
            await db.SaveChangesAsync();
            (await db.AgentProposals.CountAsync()).Should().Be(1);
        });
    }

    // TEST-02: Reject every incomplete committed result, including a NULL or infinite result timestamp.
    /// <summary>Ensures replay metadata is complete before a proposal can enter committed state.</summary>
    [PostgresEndToEndFact]
    public async Task CommittedProposal_RequiresCompleteFiniteResult()
    {
        var (conversationId, roadmapId) = await SetupAsync();
        foreach (var missing in new[] { "roadmap", "timestamp-null", "timestamp-min", "timestamp-max", "result", "time" })
        {
            var proposal = Proposal(conversationId);
            proposal.Status = AgentProposalStatus.Committed;
            proposal.ResultRoadmapId = missing == "roadmap" ? null : roadmapId;
            proposal.ResultRoadmapUpdatedAt = missing switch { "timestamp-null" => null, "timestamp-min" => DateTime.MinValue, "timestamp-max" => DateTime.MaxValue, _ => DateTime.UnixEpoch };
            proposal.CommitResult = missing == "result" ? null : "{}";
            proposal.CommittedAt = missing == "time" ? null : DateTime.UtcNow;
            await RejectAsync(proposal, "ck_agent_proposals_commit");
        }
        await factory.ExecuteDbAsync(async db =>
        {
            var valid = Proposal(conversationId);
            valid.Status = AgentProposalStatus.Committed;
            valid.ResultRoadmapId = roadmapId;
            valid.ResultRoadmapUpdatedAt = DateTime.UnixEpoch.AddSeconds(1);
            valid.CommitResult = "{}";
            valid.CommittedAt = DateTime.UtcNow;
            db.AgentProposals.Add(valid);
            await db.SaveChangesAsync();
            (await db.AgentProposals.CountAsync()).Should().Be(1);
        });
    }

    // TEST-03: Reject source metadata on create proposals and commit metadata on uncommitted proposals.
    /// <summary>Verifies the inverse source and result invariants as well as a valid empty-metadata create proposal.</summary>
    [PostgresEndToEndFact]
    public async Task UncommittedCreate_RejectsSourceAndResultMetadata()
    {
        var (conversationId, roadmapId) = await SetupAsync();
        foreach (var source in new[] { "roadmap", "timestamp", "both" })
        {
            var proposal = Proposal(conversationId);
            proposal.BaseRoadmapId = source == "timestamp" ? null : roadmapId;
            proposal.BaseRoadmapUpdatedAt = source == "roadmap" ? null : DateTime.UnixEpoch;
            await RejectAsync(proposal, "ck_agent_proposals_base");
        }
        foreach (var result in new[] { "roadmap", "timestamp", "result", "time" })
        {
            var proposal = Proposal(conversationId);
            proposal.ResultRoadmapId = result == "roadmap" ? roadmapId : null;
            proposal.ResultRoadmapUpdatedAt = result == "timestamp" ? DateTime.UnixEpoch : null;
            proposal.CommitResult = result == "result" ? "{}" : null;
            proposal.CommittedAt = result == "time" ? DateTime.UtcNow : null;
            await RejectAsync(proposal, "ck_agent_proposals_commit");
        }
        await factory.ExecuteDbAsync(async db =>
        {
            db.AgentProposals.Add(Proposal(conversationId));
            await db.SaveChangesAsync();
            (await db.AgentProposals.CountAsync()).Should().Be(1);
        });
    }

    /// <summary>Creates one owned roadmap and conversation in the dedicated migrated test database.</summary>
    private async Task<(long ConversationId, long RoadmapId)> SetupAsync()
    {
        await factory.ResetDatabaseAsync();
        using var client = factory.CreateClient();
        await DiscoverySeed.RegisterAndLoginAsync(factory, client, "TRAVELER");
        return await factory.ExecuteDbAsync(async db =>
        {
            var userId = await db.Users.Select(user => user.Id).SingleAsync();
            var roadmap = new ANGI.Domain.Entities.Roadmap { UserId = userId, Title = "Constraint test", NumDays = 1 };
            var conversation = new AgentConversation { UserId = userId, ExpiresAt = DateTime.UtcNow.AddDays(7) };
            db.Roadmaps.Add(roadmap);
            db.AgentConversations.Add(conversation);
            await db.SaveChangesAsync();
            return (conversation.Id, roadmap.Id);
        });
    }

    /// <summary>Builds a valid uncommitted proposal so each rejection isolates one database invariant.</summary>
    private static AgentProposal Proposal(long conversationId) => new()
    {
        ConversationId = conversationId, ProposalVersion = 1, Operation = AgentProposalOperation.Create,
        Status = AgentProposalStatus.Ready, ExpiresAt = DateTime.UtcNow.AddDays(7)
    };

    /// <summary>Checks the named database constraint and disposes failed tracking before the next boundary case.</summary>
    private Task RejectAsync(AgentProposal proposal, string constraint) => factory.ExecuteDbAsync(async db =>
    {
        db.AgentProposals.Add(proposal);
        var act = () => db.SaveChangesAsync();
        var failure = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        var postgres = failure.InnerException.Should().BeOfType<PostgresException>().Subject;
        postgres.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        postgres.ConstraintName.Should().Be(constraint);
    });
}
