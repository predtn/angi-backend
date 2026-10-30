using ANGI.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ANGI.IntegrationTests.Infrastructure.Persistences.Configs;

public sealed class ConversationPreferencesMigrationTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    [PostgresEndToEndFact]
    public async Task Upgrade_DetectsDuplicatesWithoutDeletingData_ThenBackfillsCounters()
    {
        await factory.ResetDatabaseAsync();
        using var client = factory.CreateClient();
        var auth = await DiscoverySeed.RegisterAndLoginAsync(factory, client, "TRAVELER");
        await factory.ExecuteDbAsync(async db =>
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261029020050_ReplaceRoadmapVersionWithUpdatedAt");
            await db.Database.ExecuteSqlInterpolatedAsync($$$"""
                INSERT INTO core.roadmaps(id,user_id,title,num_days) VALUES (701,{{{auth.User.Id}}},'Migration test',1);
                INSERT INTO core.agent_conversations(id,user_id,roadmap_id,status,state,expires_at)
                    VALUES (801,{{{auth.User.Id}}},701,'active','{}',now()+interval '7 days'),
                           (802,{{{auth.User.Id}}},701,'active','{}',now()+interval '7 days');
                INSERT INTO core.agent_messages(conversation_id,sequence_no,turn_id,client_message_id,role,payload,processing_status,request_hash)
                    VALUES (801,42,1,10,'user','{}','failed','test-hash');
                INSERT INTO core.agent_proposals(conversation_id,proposal_version,operation,payload,validation_result,status,expires_at)
                    VALUES (801,9,'create','{}','{}','ready',now()+interval '7 days');
                """);
            Func<Task> upgrade = () => migrator.MigrateAsync();
            var failure = await upgrade.Should().ThrowAsync<PostgresException>();
            failure.Which.MessageText.Should().Contain("701").And.Contain("801,802");
            (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM core.agent_conversations").SingleAsync()).Should().Be(2);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM core.agent_conversations WHERE id=802");
            await migrator.MigrateAsync();
            var conversation = await db.AgentConversations.SingleAsync();
            conversation.LastMessageSequence.Should().Be(42); conversation.LastProposalVersion.Should().Be(9);
            conversation.PurgedClientMessageId.Should().Be(0);
            Func<Task> duplicate = () => db.Database.ExecuteSqlInterpolatedAsync($$$"""
                INSERT INTO core.agent_conversations(user_id,roadmap_id,status,state,expires_at)
                VALUES ({{{auth.User.Id}}},701,'active','{}',now()+interval '7 days')
                """);
            (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        });
    }
}
