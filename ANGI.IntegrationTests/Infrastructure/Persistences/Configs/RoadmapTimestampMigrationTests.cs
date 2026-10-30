using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Domain.Enums;
using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ANGI.IntegrationTests.Infrastructure.Persistences.Configs;

/// <summary>Verifies upgrade of existing counters and nested JSON, rather than only an empty-schema migration.</summary>
public sealed class RoadmapTimestampMigrationTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    [PostgresEndToEndFact]
    public async Task Upgrade_PreservesHistoryAndCommittedReplay_AndInvalidatesLegacyPreviews()
    {
        await factory.ResetDatabaseAsync();
        await factory.ExecuteDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("DROP SCHEMA core CASCADE");
            await db.GetService<IMigrator>().MigrateAsync("20261027042013_DropRestaurantDistrict");
        });
        using var client = factory.CreateClient();
        var auth = await DiscoverySeed.RegisterAndLoginAsync(factory, client, "TRAVELER");
        await factory.ExecuteDbAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($$$"""
                INSERT INTO core.roadmaps (id, user_id, title, num_days) VALUES (701, {{{auth.User.Id}}}, 'Legacy roadmap', 1);
                INSERT INTO core.agent_conversations (id, user_id, roadmap_id, status, state, expires_at,
                    processing_turn_id, processing_attempt_id, processing_until, version)
                    VALUES (801, {{{auth.User.Id}}}, 701, 'active',
                        '{"sourceRoadmapVersion":1,"pendingStateChange":{"sourceRoadmapVersion":1}}',
                        now() + interval '7 days', 2, 3, now() + interval '1 minute', 3);
                INSERT INTO core.agent_proposals (id, conversation_id, proposal_version, operation, payload,
                    validation_result, status, result_roadmap_id, result_roadmap_version, commit_result, expires_at, committed_at)
                    VALUES (901, 801, 1, 'create',
                        '{"state":{"sourceRoadmapVersion":1},"items":[],"grants":[],"baseRoadmapId":null,"baseRoadmapVersion":null}',
                        '{"errors":[],"warnings":[],"totalCost":0,"routes":[]}', 'committed', 701, 1,
                        '{"roadmapId":701,"roadmapVersion":1,"committedProposalId":901,"itemIds":{}}',
                        now() + interval '7 days', '2026-10-26T01:02:03.123456Z');
                INSERT INTO core.agent_proposals (id, conversation_id, proposal_version, operation, base_roadmap_id,
                    base_roadmap_version, payload, validation_result, status, expires_at)
                    VALUES (902, 801, 2, 'update', 701, 1,
                        '{"state":{"sourceRoadmapVersion":1},"items":[],"grants":[],"baseRoadmapId":701,"baseRoadmapVersion":1}',
                        '{"errors":[],"warnings":[],"totalCost":0,"routes":[]}', 'ready', now() + interval '7 days');
                UPDATE core.agent_conversations SET current_proposal_id = 902 WHERE id = 801;
                INSERT INTO core.agent_messages (conversation_id, sequence_no, turn_id, client_message_id,
                    role, payload, processing_status, request_hash, response_payload)
                    SELECT 801, 1, 1, 11, 'user',
                        '{"content":"save","attempts":[],"confirmedState":{"sourceRoadmapVersion":1}}',
                        'completed', 'legacy-hash', jsonb_build_object('status', 'completed', 'reply', 'saved',
                            'conversationVersion', 3, 'proposal', jsonb_build_object('proposalId', p.id,
                                'version', p.proposal_version, 'operation', p.operation, 'status', p.status,
                                'snapshot', p.payload, 'validation', p.validation_result, 'expiresAt', p.expires_at),
                            'replayed', false, 'commit', (SELECT commit_result FROM core.agent_proposals WHERE id = 901),
                            'clientMessageId', 11)
                    FROM core.agent_proposals p WHERE p.id = 902;
                INSERT INTO core.agent_messages (conversation_id, sequence_no, turn_id, client_message_id,
                    role, payload, processing_status, request_hash)
                    VALUES (801, 2, 2, 12, 'user', '{"content":"in flight","attempts":[]}', 'processing', 'another-hash');
                """);
            // The old trigger advanced once per transaction. Use a later transaction to model a real
            // edit after the original commit, so the historical result cannot use the current token.
            await db.Database.ExecuteSqlRawAsync("UPDATE core.roadmaps SET title = 'Keep manual edit' WHERE id = 701");
            await db.GetService<IMigrator>().MigrateAsync();
            var roadmap = await db.Roadmaps.SingleAsync();
            roadmap.Title.Should().Be("Keep manual edit");
            var conversation = await db.AgentConversations.SingleAsync();
            conversation.CurrentProposalId.Should().BeNull();
            conversation.ProcessingAttemptId.Should().BeNull();
            conversation.Version.Should().Be(4);
            var state = AgentJson.Read<AgentState>(conversation.State);
            state.SourceRoadmapUpdatedAt.Should().BeNull();
            state.PendingStateChange!.SourceRoadmapUpdatedAt.Should().BeNull();
            var proposals = await db.AgentProposals.OrderBy(value => value.Id).ToArrayAsync();
            proposals[0].Status.Should().Be(AgentProposalStatus.Committed);
            proposals[1].Status.Should().Be(AgentProposalStatus.Superseded);
            var snapshot = AgentJson.Read<AgentSnapshot>(proposals[1].Payload);
            snapshot.BaseRoadmapUpdatedAt.Should().Be(roadmap.UpdatedAt);
            var commit = AgentJson.Read<AgentCommitDto>(proposals[0].CommitResult!);
            commit.CommittedProposalId.Should().Be(901);
            commit.RoadmapUpdatedAt.Should().Be(DateTime.Parse("2026-10-26T01:02:03.123456Z", null,
                System.Globalization.DateTimeStyles.AdjustToUniversal));
            var messages = await db.AgentMessages.OrderBy(value => value.SequenceNo).ToArrayAsync();
            messages.Should().HaveCount(2);
            messages[1].ProcessingStatus.Should().Be(AgentProcessingStatus.Failed);
            var replay = AgentJson.Read<AgentTurnDto>(messages[0].ResponsePayload!);
            replay.Commit.Should().BeEquivalentTo(commit);
            replay.Proposal!.Snapshot.BaseRoadmapUpdatedAt.Should().Be(roadmap.UpdatedAt);
            messages[0].Payload.Should().NotContain("sourceRoadmapVersion");
            (await db.Database.SqlQuery<int>($"""
                SELECT count(*)::integer AS "Value" FROM information_schema.columns
                WHERE table_schema = 'core' AND ((table_name = 'roadmaps' AND column_name = 'version')
                    OR (table_name = 'agent_proposals' AND column_name IN ('base_roadmap_version', 'result_roadmap_version')))
                """).SingleAsync()).Should().Be(0);
        });
    }
}
