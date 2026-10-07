using ANGI.Domain.Common;
using ANGI.Domain.Entities;
using ANGI.Infrastructure.Persistences.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace ANGI.Infrastructure.Persistences
{
    public class ANGIContext : DbContext
    {
        public const string Schema = "core";

        public ANGIContext(DbContextOptions<ANGIContext> options) : base(options)
        {
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

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Soft-deleted rows stay in the table, so a required navigation to a soft-deleted
            // principal (e.g. Blog.Author) loads as null. Queries that need it must handle that.
            optionsBuilder.ConfigureWarnings(warnings =>
                warnings.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
        }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            // Cascade delete only where a configuration asks for it (owned child rows).
            configurationBuilder.Conventions.Remove(typeof(CascadeDeleteConvention));
        }

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

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            ApplyAuditRules();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            ApplyAuditRules();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        // Timestamps are set here so use cases never set them (coding_rule.md §15).
        // Soft delete is explicit: the use case sets DeletedAt instead of calling Remove().
        private void ApplyAuditRules()
        {
            var now = DateTime.UtcNow;

            foreach (var entry in ChangeTracker.Entries())
            {
                if (entry.State == EntityState.Added && entry.Entity is IHasCreatedAt created && created.CreatedAt == default)
                {
                    created.CreatedAt = now;
                }

                if (entry.State is EntityState.Added or EntityState.Modified && entry.Entity is IHasUpdatedAt updated)
                {
                    updated.UpdatedAt = now;
                }
            }
        }
    }
}
