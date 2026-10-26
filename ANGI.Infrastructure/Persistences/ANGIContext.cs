using ANGI.Domain.Common;
using ANGI.Domain.Entities;
using ANGI.Infrastructure.Persistences.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace ANGI.Infrastructure.Persistences
{
    /// <summary>Represents the EF Core unit of persistence for the core schema.</summary>
    public class ANGIContext : DbContext
    {
        public const string Schema = "core";

        private readonly TimeProvider _timeProvider;

        /// <summary>Initializes the context with database options and a testable UTC clock.</summary>
        public ANGIContext(DbContextOptions<ANGIContext> options, TimeProvider timeProvider) : base(options)
        {
            _timeProvider = timeProvider;
        }

        public DbSet<Role> Roles { get; set; } = null!;
        public DbSet<User> Users { get; set; } = null!;
        public DbSet<Permission> Permissions { get; set; } = null!;
        public DbSet<RolePermission> RolePermissions { get; set; } = null!;
        public DbSet<UserPermission> UserPermissions { get; set; } = null!;
        public DbSet<UserExternalLogin> UserExternalLogins { get; set; } = null!;
        public DbSet<UserToken> UserTokens { get; set; } = null!;
        public DbSet<UserSession> UserSessions { get; set; } = null!;
        public DbSet<MediaFile> MediaFiles { get; set; } = null!;
        public DbSet<Restaurant> Restaurants { get; set; } = null!;
        public DbSet<RestaurantImage> RestaurantImages { get; set; } = null!;
        public DbSet<RestaurantBusinessHour> RestaurantBusinessHours { get; set; } = null!;
        public DbSet<RestaurantVerification> RestaurantVerifications { get; set; } = null!;
        public DbSet<RestaurantVerificationDocument> RestaurantVerificationDocuments { get; set; } = null!;
        public DbSet<Tag> Tags { get; set; } = null!;
        public DbSet<Dish> Dishes { get; set; } = null!;
        public DbSet<DishTag> DishTags { get; set; } = null!;
        public DbSet<DishImage> DishImages { get; set; } = null!;
        public DbSet<MenuSubmission> MenuSubmissions { get; set; } = null!;
        public DbSet<MenuSubmissionItem> MenuSubmissionItems { get; set; } = null!;
        public DbSet<RestaurantReview> RestaurantReviews { get; set; } = null!;
        public DbSet<ReviewImage> ReviewImages { get; set; } = null!;
        public DbSet<ReviewReply> ReviewReplies { get; set; } = null!;
        public DbSet<DishFeedback> DishFeedbacks { get; set; } = null!;
        public DbSet<DishRoll> DishRolls { get; set; } = null!;
        public DbSet<Roadmap> Roadmaps { get; set; } = null!;
        public DbSet<RoadmapDay> RoadmapDays { get; set; } = null!;
        public DbSet<RoadmapItem> RoadmapItems { get; set; } = null!;
        public DbSet<RoadmapGenerationJob> RoadmapGenerationJobs { get; set; } = null!;
        public DbSet<Blog> Blogs { get; set; } = null!;
        public DbSet<BlogImage> BlogImages { get; set; } = null!;
        public DbSet<BlogLike> BlogLikes { get; set; } = null!;
        public DbSet<BlogComment> BlogComments { get; set; } = null!;
        public DbSet<Report> Reports { get; set; } = null!;
        public DbSet<UserSanction> UserSanctions { get; set; } = null!;
        public DbSet<Notification> Notifications { get; set; } = null!;
        public DbSet<AuditLog> AuditLogs { get; set; } = null!;
        public DbSet<OutboxMessage> OutboxMessages { get; set; } = null!;
        public DbSet<AgentConversation> AgentConversations { get; set; } = null!;
        public DbSet<AgentMessage> AgentMessages { get; set; } = null!;
        public DbSet<AgentProposal> AgentProposals { get; set; } = null!;

        /// <summary>Configures warnings for required navigations whose principals may be soft deleted.</summary>
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Soft-deleted rows stay in the table, so a required navigation to a soft-deleted
            // principal (e.g. Blog.Author) loads as null. Queries that need it must handle that.
            optionsBuilder.ConfigureWarnings(warnings =>
                warnings.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
        }

        /// <summary>Disables implicit cascade deletes so each relationship declares its intended behavior.</summary>
        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            // Cascade delete only where a configuration asks for it (owned child rows).
            configurationBuilder.Conventions.Remove(typeof(CascadeDeleteConvention));
        }

        /// <summary>Applies the core schema, entity configurations, and project-wide model conventions.</summary>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema(Schema);
            modelBuilder.HasPostgresExtension("citext");

            modelBuilder.ApplyConfigurationsFromAssembly(typeof(ANGIContext).Assembly);

            modelBuilder.ApplyEnumConventions();
            modelBuilder.ApplyTimestampConventions();
            modelBuilder.ApplySoftDeleteFilters();
            modelBuilder.ApplyDefaultValueAndDeleteConventions();
        }

        /// <summary>Applies automatic timestamps before synchronously persisting tracked changes.</summary>
        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            ApplyAuditRules();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        /// <summary>Applies automatic timestamps before asynchronously persisting tracked changes.</summary>
        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            ApplyAuditRules();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        /// <summary>
        /// Sets creation and update timestamps from the injected clock; soft deletion remains explicit in use cases.
        /// </summary>
        private void ApplyAuditRules()
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;

            foreach (var entry in ChangeTracker.Entries())
            {
                if (entry.State == EntityState.Added && entry.Entity is IHasCreatedAt created && created.CreatedAt == default)
                {
                    created.CreatedAt = now;
                }

                if (entry.State is EntityState.Added or EntityState.Modified && entry.Entity is IHasUpdatedAt updated &&
                    entry.Entity is not AgentConversation { Status: ANGI.Domain.Enums.AgentConversationStatus.Expired })
                {
                    updated.UpdatedAt = now;
                }
            }
        }
    }
}
