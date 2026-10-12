using System.Net;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences;
using ANGI.Infrastructure.Services.Audit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ANGI.Test.Infrastructure.Services.Audit
{
    public sealed class AuditLogServiceTests
    {
        private static readonly IPAddress _ipAddress = IPAddress.Parse("113.161.4.20");

        // TEST-01: Take the actor from the token and the IP address and User-Agent from the request.
        [Fact]
        public void Add_WithAuthenticatedRequest_ShouldUseTokenActorAndRequestMetadata()
        {
            using var context = CreateContext();
            var service = CreateService(context, userId: 3, role: "MOD");

            service.Add(new AuditLogEntry
            {
                Action = AuditActions.BlogHidden,
                EntityType = AuditEntityTypes.Blog,
                EntityId = 55,
                SubjectUserId = 12
            });

            var auditLog = SingleAdded(context);
            auditLog.ActorId.Should().Be(3);
            auditLog.ActorRole.Should().Be("MOD");
            auditLog.Action.Should().Be("BLOG_HIDDEN");
            auditLog.EntityType.Should().Be("blog");
            auditLog.EntityId.Should().Be(55);
            auditLog.SubjectUserId.Should().Be(12);
            auditLog.IpAddress.Should().Be(_ipAddress);
            auditLog.UserAgent.Should().Be("Mozilla/5.0 (Windows NT 10.0)");
        }

        // TEST-02: Use the explicit actor when the request has no token yet (login, register).
        [Fact]
        public void Add_WithExplicitActor_ShouldPreferItOverTheToken()
        {
            using var context = CreateContext();
            var service = CreateService(context, userId: null, role: null);

            service.Add(new AuditLogEntry
            {
                Action = AuditActions.UserLogin,
                EntityType = AuditEntityTypes.User,
                EntityId = 12,
                SubjectUserId = 12,
                ActorId = 12,
                ActorRole = "TRAVELER"
            });

            var auditLog = SingleAdded(context);
            auditLog.ActorId.Should().Be(12);
            auditLog.ActorRole.Should().Be("TRAVELER");
        }

        // TEST-03: Leave the actor null when there is neither a token nor an explicit actor (system).
        [Fact]
        public void Add_WithoutTokenOrExplicitActor_ShouldWriteSystemActor()
        {
            using var context = CreateContext();
            var service = CreateService(context, userId: null, role: null, hasRequest: false);

            service.Add(new AuditLogEntry { Action = AuditActions.UserUnbanned, EntityType = AuditEntityTypes.User, EntityId = 12 });

            var auditLog = SingleAdded(context);
            auditLog.ActorId.Should().BeNull();
            auditLog.ActorRole.Should().BeNull();
            auditLog.IpAddress.Should().BeNull();
            auditLog.UserAgent.Should().BeNull();
        }

        // TEST-04: Write old and new values as JSON with snake_case keys and snake_case enum values.
        [Fact]
        public void Add_WithValues_ShouldSerializeSnakeCaseJson()
        {
            using var context = CreateContext();
            var service = CreateService(context);

            service.Add(new AuditLogEntry
            {
                Action = AuditActions.UserSuspended,
                EntityType = AuditEntityTypes.User,
                EntityId = 12,
                OldValues = new { Status = UserStatus.Active },
                NewValues = new
                {
                    Status = UserStatus.PendingVerification,
                    SuspendedUntil = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc),
                    Reason = "Spam quảng cáo nhiều lần"
                }
            });

            var auditLog = SingleAdded(context);
            auditLog.OldValues.Should().Be("{\"status\":\"active\"}");
            using var newValues = JsonDocument.Parse(auditLog.NewValues!);
            newValues.RootElement.GetProperty("status").GetString().Should().Be("pending_verification");
            newValues.RootElement.GetProperty("suspended_until").GetString().Should().Be("2026-10-09T08:00:00Z");
            newValues.RootElement.GetProperty("reason").GetString().Should().Be("Spam quảng cáo nhiều lần");
        }

        // TEST-05: Store null old and new values as SQL NULL, not as the JSON literal null.
        [Fact]
        public void Add_WithoutValues_ShouldLeaveColumnsNull()
        {
            using var context = CreateContext();
            var service = CreateService(context);

            service.Add(new AuditLogEntry { Action = AuditActions.PasswordChanged, EntityType = AuditEntityTypes.User, EntityId = 12 });

            var auditLog = SingleAdded(context);
            auditLog.OldValues.Should().BeNull();
            auditLog.NewValues.Should().BeNull();
        }

        // TEST-06: Reject an action or entity type outside the catalog and add nothing.
        [Theory]
        [InlineData("USER_SANCTIONED", "user")]
        [InlineData("user_login", "user")]
        [InlineData("USER_LOGIN", "USER")]
        [InlineData("USER_LOGIN", "dish")]
        public void Add_WithUnknownActionOrEntityType_ShouldThrowAndAddNothing(string action, string entityType)
        {
            using var context = CreateContext();
            var service = CreateService(context);

            var act = () => service.Add(new AuditLogEntry { Action = action, EntityType = entityType });

            act.Should().Throw<ArgumentException>();
            context.ChangeTracker.Entries<AuditLog>().Should().BeEmpty();
        }

        // TEST-07: Cut a User-Agent longer than audit_logs.user_agent (500) and store a blank one as null.
        [Theory]
        [InlineData(600, 500)]
        [InlineData(500, 500)]
        [InlineData(0, null)]
        public void Add_ShouldFitUserAgentIntoColumn(int length, int? expectedLength)
        {
            using var context = CreateContext();
            var service = CreateService(context, userAgent: new string('a', length));

            service.Add(new AuditLogEntry { Action = AuditActions.UserLogin, EntityType = AuditEntityTypes.User });

            var userAgent = SingleAdded(context).UserAgent;
            if (expectedLength is null)
            {
                userAgent.Should().BeNull();
            }
            else
            {
                userAgent.Should().HaveLength(expectedLength.Value);
            }
        }

        // TEST-08: Link a registration audit row to a newly added user before the database generates its id.
        /// <summary>Verifies generated actor and subject ids can flow through EF relationships in one save.</summary>
        [Fact]
        public void Add_WithNewUser_ShouldUseTrackedActorAndSubjectNavigations()
        {
            using var context = CreateContext();
            var user = new User
            {
                Email = "new@angi.test",
                RoleId = 1,
                DisplayName = "New User",
                Status = UserStatus.PendingVerification
            };
            context.Users.Add(user);
            var service = CreateService(context, userId: null, role: null);

            service.Add(new AuditLogEntry
            {
                Action = AuditActions.UserRegistered,
                EntityType = AuditEntityTypes.User,
                ActorUser = user,
                ActorRole = "TRAVELER",
                SubjectUser = user
            });

            var auditLog = SingleAdded(context);
            auditLog.Actor.Should().BeSameAs(user);
            auditLog.SubjectUser.Should().BeSameAs(user);
            auditLog.ActorRole.Should().Be("TRAVELER");
        }

        private static AuditLog SingleAdded(ANGIContext context)
        {
            var entry = context.ChangeTracker.Entries<AuditLog>().Should().ContainSingle().Subject;
            entry.State.Should().Be(EntityState.Added);
            return entry.Entity;
        }

        private static AuditLogService CreateService(
            ANGIContext context,
            int? userId = 3,
            string? role = "MOD",
            string? userAgent = "Mozilla/5.0 (Windows NT 10.0)",
            bool hasRequest = true)
        {
            var currentUser = new Mock<ICurrentUserService>();
            currentUser.SetupGet(x => x.UserId).Returns(userId);
            currentUser.SetupGet(x => x.Role).Returns(role);
            currentUser.SetupGet(x => x.UserAgent).Returns(hasRequest ? userAgent : null);
            currentUser.SetupGet(x => x.IpAddress).Returns(hasRequest ? _ipAddress : null);
            return new AuditLogService(context, currentUser.Object);
        }

        // Add() only touches the change tracker, so the context never opens a connection.
        private static ANGIContext CreateContext() =>
            new(
                new DbContextOptionsBuilder<ANGIContext>()
                    .UseNpgsql("Host=localhost;Database=unused")
                    .UseSnakeCaseNamingConvention()
                    .Options,
                TimeProvider.System);
    }
}
