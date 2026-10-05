using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ANGI.Infrastructure.Persistences.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "core");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_uuid = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    event_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    aggregate_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false, defaultValue: "pending"),
                    attempt_count = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_event_type", "event_type IN ('DISH_UPSERTED', 'DISH_DEACTIVATED', 'FEEDBACK_CREATED')");
                    table.CheckConstraint("ck_outbox_messages_status", "status IN ('pending', 'processing', 'done', 'dead')");
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    description = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permissions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tag_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tags", x => x.id);
                    table.CheckConstraint("ck_tags_tag_type", "tag_type IN ('meal_time', 'dietary', 'category')");
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "core",
                columns: table => new
                {
                    role_id = table.Column<short>(type: "smallint", nullable: false),
                    permission_id = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permissions", x => new { x.role_id, x.permission_id });
                    table.ForeignKey(
                        name: "fk_role_permissions_permissions_permission_id",
                        column: x => x.permission_id,
                        principalSchema: "core",
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "core",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    actor_id = table.Column<int>(type: "integer", nullable: true),
                    actor_role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    entity_id = table.Column<long>(type: "bigint", nullable: true),
                    subject_user_id = table.Column<int>(type: "integer", nullable: true),
                    old_values = table.Column<string>(type: "jsonb", nullable: true),
                    new_values = table.Column<string>(type: "jsonb", nullable: true),
                    ip_address = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "blog_comments",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    blog_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "visible"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blog_comments", x => x.id);
                    table.CheckConstraint("ck_blog_comments_status", "status IN ('visible', 'hidden', 'removed')");
                });

            migrationBuilder.CreateTable(
                name: "blog_images",
                schema: "core",
                columns: table => new
                {
                    blog_id = table.Column<long>(type: "bigint", nullable: false),
                    media_id = table.Column<long>(type: "bigint", nullable: false),
                    sort_order = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blog_images", x => new { x.blog_id, x.media_id });
                });

            migrationBuilder.CreateTable(
                name: "blog_likes",
                schema: "core",
                columns: table => new
                {
                    blog_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blog_likes", x => new { x.blog_id, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "blogs",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    author_id = table.Column<int>(type: "integer", nullable: false),
                    roadmap_id = table.Column<long>(type: "bigint", nullable: true),
                    roadmap_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    slug = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    cover_media_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false, defaultValue: "published"),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    like_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    comment_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    view_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blogs", x => x.id);
                    table.CheckConstraint("ck_blogs_status", "status IN ('published', 'hidden', 'removed')");
                });

            migrationBuilder.CreateTable(
                name: "dish_feedbacks",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    event_uuid = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    dish_id = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<short>(type: "smallint", nullable: false),
                    roadmap_item_id = table.Column<long>(type: "bigint", nullable: true),
                    reco_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dish_feedbacks", x => x.id);
                    table.CheckConstraint("ck_dish_feedbacks_value", "value IN (1, -1)");
                });

            migrationBuilder.CreateTable(
                name: "dish_images",
                schema: "core",
                columns: table => new
                {
                    dish_id = table.Column<int>(type: "integer", nullable: false),
                    media_id = table.Column<long>(type: "bigint", nullable: false),
                    sort_order = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dish_images", x => new { x.dish_id, x.media_id });
                });

            migrationBuilder.CreateTable(
                name: "dish_rolls",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    dish_id = table.Column<int>(type: "integer", nullable: false),
                    reco_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rolled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dish_rolls", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dish_tags",
                schema: "core",
                columns: table => new
                {
                    dish_id = table.Column<int>(type: "integer", nullable: false),
                    tag_id = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dish_tags", x => new { x.dish_id, x.tag_id });
                    table.ForeignKey(
                        name: "fk_dish_tags_tags_tag_id",
                        column: x => x.tag_id,
                        principalSchema: "core",
                        principalTable: "tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dishes",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    restaurant_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    dish_form = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "other"),
                    like_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    dislike_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    price = table.Column<decimal>(type: "numeric(12,0)", precision: 12, scale: 0, nullable: false),
                    cover_media_id = table.Column<long>(type: "bigint", nullable: true),
                    is_available = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "active"),
                    sort_order = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dishes", x => x.id);
                    table.CheckConstraint("ck_dishes_dish_form", "dish_form IN ('soup', 'dry', 'other')");
                    table.CheckConstraint("ck_dishes_price", "price >= 0");
                    table.CheckConstraint("ck_dishes_status", "status IN ('active', 'hidden', 'removed')");
                });

            migrationBuilder.CreateTable(
                name: "media_files",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    uploader_id = table.Column<int>(type: "integer", nullable: true),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    email = table.Column<string>(type: "citext", nullable: false),
                    role_id = table.Column<short>(type: "smallint", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: true),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    avatar_media_id = table.Column<long>(type: "bigint", nullable: true),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    bio = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    email_verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending_verification"),
                    suspended_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_status", "status IN ('pending_verification', 'active', 'suspended', 'banned', 'deactivated')");
                    table.ForeignKey(
                        name: "fk_users_media_files_avatar_media_id",
                        column: x => x.avatar_media_id,
                        principalSchema: "core",
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_users_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "core",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    recipient_id = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    read_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_users_recipient_id",
                        column: x => x.recipient_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "restaurants",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(220)", maxLength: 220, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    email = table.Column<string>(type: "citext", nullable: true),
                    website = table.Column<string>(type: "text", nullable: true),
                    address_line = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ward = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    district = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    province_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    latitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    price_level = table.Column<short>(type: "smallint", nullable: true),
                    cover_media_id = table.Column<long>(type: "bigint", nullable: true),
                    verification_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "unverified"),
                    operating_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "open"),
                    moderation_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "visible"),
                    rating_avg = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: false, defaultValue: 0m),
                    rating_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_restaurants", x => x.id);
                    table.CheckConstraint("ck_restaurants_moderation_status", "moderation_status IN ('visible', 'hidden', 'suspended')");
                    table.CheckConstraint("ck_restaurants_operating_status", "operating_status IN ('open', 'temporarily_closed', 'permanently_closed')");
                    table.CheckConstraint("ck_restaurants_price_level", "price_level BETWEEN 1 AND 4");
                    table.CheckConstraint("ck_restaurants_verification_status", "verification_status IN ('unverified', 'pending', 'verified', 'rejected')");
                    table.ForeignKey(
                        name: "fk_restaurants_media_files_cover_media_id",
                        column: x => x.cover_media_id,
                        principalSchema: "core",
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_restaurants_users_owner_id",
                        column: x => x.owner_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roadmaps",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    num_days = table.Column<short>(type: "smallint", nullable: false),
                    budget_amount = table.Column<decimal>(type: "numeric(14,0)", precision: 14, scale: 0, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roadmaps", x => x.id);
                    table.CheckConstraint("ck_roadmaps_budget_amount", "budget_amount >= 0");
                    table.CheckConstraint("ck_roadmaps_num_days", "num_days BETWEEN 1 AND 30");
                    table.ForeignKey(
                        name: "fk_roadmaps_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_external_logins",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider_user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    linked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_external_logins", x => x.id);
                    table.CheckConstraint("ck_user_external_logins_provider", "provider IN ('google')");
                    table.ForeignKey(
                        name: "fk_user_external_logins_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_permissions",
                schema: "core",
                columns: table => new
                {
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    permission_id = table.Column<short>(type: "smallint", nullable: false),
                    effect = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false, defaultValue: "allow"),
                    granted_by = table.Column<int>(type: "integer", nullable: true),
                    granted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_permissions", x => new { x.user_id, x.permission_id });
                    table.CheckConstraint("ck_user_permissions_effect", "effect IN ('allow', 'deny')");
                    table.ForeignKey(
                        name: "fk_user_permissions_permissions_permission_id",
                        column: x => x.permission_id,
                        principalSchema: "core",
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_permissions_users_granted_by",
                        column: x => x.granted_by,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_permissions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_sessions",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    refresh_token_hash = table.Column<string>(type: "text", nullable: false),
                    user_agent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ip_address = table.Column<IPAddress>(type: "inet", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_sessions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tokens",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_tokens", x => x.id);
                    table.CheckConstraint("ck_user_tokens_purpose", "purpose IN ('email_verify', 'password_reset')");
                    table.ForeignKey(
                        name: "fk_user_tokens_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "menu_submissions",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    restaurant_id = table.Column<long>(type: "bigint", nullable: false),
                    created_by = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "draft"),
                    owner_note = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reviewed_by = table.Column<int>(type: "integer", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    review_note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_menu_submissions", x => x.id);
                    table.CheckConstraint("ck_menu_submissions_status", "status IN ('draft', 'pending', 'approved', 'rejected', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_menu_submissions_restaurants_restaurant_id",
                        column: x => x.restaurant_id,
                        principalSchema: "core",
                        principalTable: "restaurants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_menu_submissions_users_created_by",
                        column: x => x.created_by,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_menu_submissions_users_reviewed_by",
                        column: x => x.reviewed_by,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reports",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reporter_id = table.Column<int>(type: "integer", nullable: false),
                    target_type = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    restaurant_id = table.Column<long>(type: "bigint", nullable: true),
                    blog_id = table.Column<long>(type: "bigint", nullable: true),
                    reason_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    evidence = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false, defaultValue: "pending"),
                    handled_by = table.Column<int>(type: "integer", nullable: true),
                    handled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolution_action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    resolution_note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reports", x => x.id);
                    table.CheckConstraint("ck_reports_reason_code", "reason_code IN ('wrong_info', 'closed', 'spam', 'offensive', 'hygiene', 'scam', 'other')");
                    table.CheckConstraint("ck_reports_resolution_action", "resolution_action IN ('none', 'hide_blog', 'remove_blog', 'warn_user', 'suspend_user', 'ban_user', 'hide_restaurant', 'suspend_restaurant')");
                    table.CheckConstraint("ck_reports_status", "status IN ('pending', 'in_review', 'resolved', 'dismissed')");
                    table.CheckConstraint("ck_reports_target", "(target_type = 'restaurant' AND restaurant_id IS NOT NULL AND blog_id IS NULL) OR (target_type = 'blog' AND blog_id IS NOT NULL AND restaurant_id IS NULL)");
                    table.CheckConstraint("ck_reports_target_type", "target_type IN ('restaurant', 'blog')");
                    table.ForeignKey(
                        name: "fk_reports_blogs_blog_id",
                        column: x => x.blog_id,
                        principalSchema: "core",
                        principalTable: "blogs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reports_restaurants_restaurant_id",
                        column: x => x.restaurant_id,
                        principalSchema: "core",
                        principalTable: "restaurants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reports_users_handled_by",
                        column: x => x.handled_by,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reports_users_reporter_id",
                        column: x => x.reporter_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "restaurant_business_hours",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    restaurant_id = table.Column<long>(type: "bigint", nullable: false),
                    day_of_week = table.Column<short>(type: "smallint", nullable: false),
                    open_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    close_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_restaurant_business_hours", x => x.id);
                    table.CheckConstraint("ck_restaurant_business_hours_day_of_week", "day_of_week BETWEEN 0 AND 6");
                    table.ForeignKey(
                        name: "fk_restaurant_business_hours_restaurants_restaurant_id",
                        column: x => x.restaurant_id,
                        principalSchema: "core",
                        principalTable: "restaurants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "restaurant_images",
                schema: "core",
                columns: table => new
                {
                    restaurant_id = table.Column<long>(type: "bigint", nullable: false),
                    media_id = table.Column<long>(type: "bigint", nullable: false),
                    sort_order = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_restaurant_images", x => new { x.restaurant_id, x.media_id });
                    table.ForeignKey(
                        name: "fk_restaurant_images_media_files_media_id",
                        column: x => x.media_id,
                        principalSchema: "core",
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_restaurant_images_restaurants_restaurant_id",
                        column: x => x.restaurant_id,
                        principalSchema: "core",
                        principalTable: "restaurants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "restaurant_verifications",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    restaurant_id = table.Column<long>(type: "bigint", nullable: false),
                    submitted_by = table.Column<int>(type: "integer", nullable: false),
                    previous_id = table.Column<long>(type: "bigint", nullable: true),
                    legal_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    business_license_no = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    tax_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    owner_note = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    reviewed_by = table.Column<int>(type: "integer", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    review_note = table.Column<string>(type: "text", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_restaurant_verifications", x => x.id);
                    table.CheckConstraint("ck_restaurant_verifications_status", "status IN ('pending', 'approved', 'rejected', 'needs_more_info', 'cancelled')");
                    table.ForeignKey(
                        name: "fk_restaurant_verifications_restaurant_verifications_previous_",
                        column: x => x.previous_id,
                        principalSchema: "core",
                        principalTable: "restaurant_verifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_restaurant_verifications_restaurants_restaurant_id",
                        column: x => x.restaurant_id,
                        principalSchema: "core",
                        principalTable: "restaurants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_restaurant_verifications_users_reviewed_by",
                        column: x => x.reviewed_by,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_restaurant_verifications_users_submitted_by",
                        column: x => x.submitted_by,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roadmap_days",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    roadmap_id = table.Column<long>(type: "bigint", nullable: false),
                    day_number = table.Column<short>(type: "smallint", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roadmap_days", x => x.id);
                    table.ForeignKey(
                        name: "fk_roadmap_days_roadmaps_roadmap_id",
                        column: x => x.roadmap_id,
                        principalSchema: "core",
                        principalTable: "roadmaps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roadmap_generation_jobs",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    roadmap_id = table.Column<long>(type: "bigint", nullable: false),
                    requested_by = table.Column<int>(type: "integer", nullable: false),
                    scope = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "full"),
                    input = table.Column<string>(type: "jsonb", nullable: false),
                    reco_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    prompt_version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    raw_output = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false, defaultValue: "queued"),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roadmap_generation_jobs", x => x.id);
                    table.CheckConstraint("ck_roadmap_generation_jobs_scope", "scope IN ('full', 'day', 'slot')");
                    table.CheckConstraint("ck_roadmap_generation_jobs_status", "status IN ('queued', 'running', 'succeeded', 'failed')");
                    table.ForeignKey(
                        name: "fk_roadmap_generation_jobs_roadmaps_roadmap_id",
                        column: x => x.roadmap_id,
                        principalSchema: "core",
                        principalTable: "roadmaps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_roadmap_generation_jobs_users_requested_by",
                        column: x => x.requested_by,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "menu_submission_items",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    submission_id = table.Column<long>(type: "bigint", nullable: false),
                    source_dish_id = table.Column<int>(type: "integer", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    dish_form = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "other"),
                    price = table.Column<decimal>(type: "numeric(12,0)", precision: 12, scale: 0, nullable: false),
                    cover_media_id = table.Column<long>(type: "bigint", nullable: true),
                    tag_ids = table.Column<short[]>(type: "smallint[]", nullable: false, defaultValueSql: "'{}'::smallint[]"),
                    sort_order = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    result_dish_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_menu_submission_items", x => x.id);
                    table.CheckConstraint("ck_menu_submission_items_dish_form", "dish_form IN ('soup', 'dry', 'other')");
                    table.CheckConstraint("ck_menu_submission_items_price", "price >= 0");
                    table.ForeignKey(
                        name: "fk_menu_submission_items_dishes_result_dish_id",
                        column: x => x.result_dish_id,
                        principalSchema: "core",
                        principalTable: "dishes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_menu_submission_items_dishes_source_dish_id",
                        column: x => x.source_dish_id,
                        principalSchema: "core",
                        principalTable: "dishes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_menu_submission_items_media_files_cover_media_id",
                        column: x => x.cover_media_id,
                        principalSchema: "core",
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_menu_submission_items_menu_submissions_submission_id",
                        column: x => x.submission_id,
                        principalSchema: "core",
                        principalTable: "menu_submissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_sanctions",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    report_id = table.Column<long>(type: "bigint", nullable: true),
                    issued_by = table.Column<int>(type: "integer", nullable: false),
                    starts_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    lifted_by = table.Column<int>(type: "integer", nullable: true),
                    lifted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    lift_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_sanctions", x => x.id);
                    table.CheckConstraint("ck_user_sanctions_type", "type IN ('warn', 'suspend', 'ban')");
                    table.ForeignKey(
                        name: "fk_user_sanctions_reports_report_id",
                        column: x => x.report_id,
                        principalSchema: "core",
                        principalTable: "reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_sanctions_users_issued_by",
                        column: x => x.issued_by,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_sanctions_users_lifted_by",
                        column: x => x.lifted_by,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_user_sanctions_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "restaurant_verification_documents",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    verification_id = table.Column<long>(type: "bigint", nullable: false),
                    doc_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    media_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_restaurant_verification_documents", x => x.id);
                    table.CheckConstraint("ck_restaurant_verification_documents_doc_type", "doc_type IN ('business_license', 'food_safety_cert', 'id_card', 'storefront_photo', 'other')");
                    table.ForeignKey(
                        name: "fk_restaurant_verification_documents_media_files_media_id",
                        column: x => x.media_id,
                        principalSchema: "core",
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_restaurant_verification_documents_restaurant_verifications_",
                        column: x => x.verification_id,
                        principalSchema: "core",
                        principalTable: "restaurant_verifications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roadmap_items",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    roadmap_day_id = table.Column<long>(type: "bigint", nullable: false),
                    dish_id = table.Column<int>(type: "integer", nullable: false),
                    meal_slot = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    sort_order = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    reco_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roadmap_items", x => x.id);
                    table.CheckConstraint("ck_roadmap_items_meal_slot", "meal_slot IN ('breakfast', 'lunch', 'dinner', 'snack', 'late_night')");
                    table.CheckConstraint("ck_roadmap_items_source", "source IN ('manual', 'ai')");
                    table.ForeignKey(
                        name: "fk_roadmap_items_dishes_dish_id",
                        column: x => x.dish_id,
                        principalSchema: "core",
                        principalTable: "dishes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_roadmap_items_roadmap_days_roadmap_day_id",
                        column: x => x.roadmap_day_id,
                        principalSchema: "core",
                        principalTable: "roadmap_days",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "restaurant_reviews",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    restaurant_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<int>(type: "integer", nullable: false),
                    rating = table.Column<short>(type: "smallint", nullable: false),
                    content = table.Column<string>(type: "text", nullable: true),
                    roadmap_item_id = table.Column<long>(type: "bigint", nullable: true),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "visible"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_restaurant_reviews", x => x.id);
                    table.CheckConstraint("ck_restaurant_reviews_rating", "rating BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_restaurant_reviews_status", "status IN ('visible', 'hidden', 'removed')");
                    table.ForeignKey(
                        name: "fk_restaurant_reviews_restaurants_restaurant_id",
                        column: x => x.restaurant_id,
                        principalSchema: "core",
                        principalTable: "restaurants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_restaurant_reviews_roadmap_items_roadmap_item_id",
                        column: x => x.roadmap_item_id,
                        principalSchema: "core",
                        principalTable: "roadmap_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_restaurant_reviews_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_images",
                schema: "core",
                columns: table => new
                {
                    review_id = table.Column<long>(type: "bigint", nullable: false),
                    media_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_images", x => new { x.review_id, x.media_id });
                    table.ForeignKey(
                        name: "fk_review_images_media_files_media_id",
                        column: x => x.media_id,
                        principalSchema: "core",
                        principalTable: "media_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_review_images_restaurant_reviews_review_id",
                        column: x => x.review_id,
                        principalSchema: "core",
                        principalTable: "restaurant_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "review_replies",
                schema: "core",
                columns: table => new
                {
                    review_id = table.Column<long>(type: "bigint", nullable: false),
                    replier_id = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "visible"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_replies", x => x.review_id);
                    table.CheckConstraint("ck_review_replies_status", "status IN ('visible', 'hidden', 'removed')");
                    table.ForeignKey(
                        name: "fk_review_replies_restaurant_reviews_review_id",
                        column: x => x.review_id,
                        principalSchema: "core",
                        principalTable: "restaurant_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_review_replies_users_replier_id",
                        column: x => x.replier_id,
                        principalSchema: "core",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "core",
                table: "permissions",
                columns: new[] { "id", "code", "description" },
                values: new object[,]
                {
                    { (short)1, "USER_VIEW_AUDIT", "Xem nhật ký hoạt động của người dùng" },
                    { (short)2, "USER_SUSPEND", "Tạm khóa và mở khóa người dùng" },
                    { (short)3, "USER_BAN", "Cấm và gỡ cấm người dùng" },
                    { (short)4, "RESTAURANT_VERIFY", "Duyệt xác minh nhà hàng" },
                    { (short)5, "RESTAURANT_MODERATE", "Ẩn và đình chỉ nhà hàng" },
                    { (short)6, "MENU_APPROVE", "Duyệt menu nhà hàng" },
                    { (short)7, "REPORT_RESTAURANT_HANDLE", "Xử lý báo cáo nhà hàng" },
                    { (short)8, "REPORT_SOCIAL_HANDLE", "Xử lý báo cáo blog" },
                    { (short)9, "BLOG_MODERATE", "Ẩn và gỡ blog, bình luận" },
                    { (short)10, "REVIEW_MODERATE", "Ẩn và gỡ review, phản hồi" },
                    { (short)11, "MOD_MANAGE", "Quản lý tài khoản Mod và phân quyền" },
                    { (short)12, "SYSTEM_AUDIT_VIEW", "Xem nhật ký hệ thống" },
                    { (short)13, "DASHBOARD_VIEW", "Xem dashboard thống kê" }
                });

            migrationBuilder.InsertData(
                schema: "core",
                table: "roles",
                columns: new[] { "id", "code", "name" },
                values: new object[,]
                {
                    { (short)1, "TRAVELER", "Traveler" },
                    { (short)2, "RESTAURANT_OWNER", "Restaurant Owner" },
                    { (short)3, "MOD", "Mod" },
                    { (short)4, "ADMIN", "Admin" }
                });

            migrationBuilder.InsertData(
                schema: "core",
                table: "role_permissions",
                columns: new[] { "permission_id", "role_id" },
                values: new object[,]
                {
                    { (short)1, (short)3 },
                    { (short)2, (short)3 },
                    { (short)3, (short)3 },
                    { (short)4, (short)3 },
                    { (short)5, (short)3 },
                    { (short)6, (short)3 },
                    { (short)7, (short)3 },
                    { (short)8, (short)3 },
                    { (short)9, (short)3 },
                    { (short)10, (short)3 },
                    { (short)1, (short)4 },
                    { (short)2, (short)4 },
                    { (short)3, (short)4 },
                    { (short)4, (short)4 },
                    { (short)5, (short)4 },
                    { (short)6, (short)4 },
                    { (short)7, (short)4 },
                    { (short)8, (short)4 },
                    { (short)9, (short)4 },
                    { (short)10, (short)4 },
                    { (short)11, (short)4 },
                    { (short)12, (short)4 },
                    { (short)13, (short)4 }
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_actor_id",
                schema: "core",
                table: "audit_logs",
                column: "actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_subject_user_id",
                schema: "core",
                table: "audit_logs",
                column: "subject_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_blog_comments_blog_id",
                schema: "core",
                table: "blog_comments",
                column: "blog_id");

            migrationBuilder.CreateIndex(
                name: "ix_blog_comments_user_id",
                schema: "core",
                table: "blog_comments",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_blog_images_media_id",
                schema: "core",
                table: "blog_images",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_blog_likes_user_id",
                schema: "core",
                table: "blog_likes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_blogs_author_id",
                schema: "core",
                table: "blogs",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_blogs_cover_media_id",
                schema: "core",
                table: "blogs",
                column: "cover_media_id");

            migrationBuilder.CreateIndex(
                name: "ix_blogs_roadmap_id",
                schema: "core",
                table: "blogs",
                column: "roadmap_id");

            migrationBuilder.CreateIndex(
                name: "ix_blogs_slug",
                schema: "core",
                table: "blogs",
                column: "slug",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_dish_feedbacks_dish_id",
                schema: "core",
                table: "dish_feedbacks",
                column: "dish_id");

            migrationBuilder.CreateIndex(
                name: "ix_dish_feedbacks_event_uuid",
                schema: "core",
                table: "dish_feedbacks",
                column: "event_uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dish_feedbacks_roadmap_item_id",
                schema: "core",
                table: "dish_feedbacks",
                column: "roadmap_item_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dish_feedbacks_user_id",
                schema: "core",
                table: "dish_feedbacks",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dish_images_media_id",
                schema: "core",
                table: "dish_images",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_dish_rolls_dish_id",
                schema: "core",
                table: "dish_rolls",
                column: "dish_id");

            migrationBuilder.CreateIndex(
                name: "ix_dish_rolls_user_id",
                schema: "core",
                table: "dish_rolls",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_dish_tags_tag_id",
                schema: "core",
                table: "dish_tags",
                column: "tag_id");

            migrationBuilder.CreateIndex(
                name: "ix_dishes_cover_media_id",
                schema: "core",
                table: "dishes",
                column: "cover_media_id");

            migrationBuilder.CreateIndex(
                name: "ix_dishes_restaurant_id",
                schema: "core",
                table: "dishes",
                column: "restaurant_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_files_storage_key",
                schema: "core",
                table: "media_files",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_files_uploader_id",
                schema: "core",
                table: "media_files",
                column: "uploader_id");

            migrationBuilder.CreateIndex(
                name: "ix_menu_submission_items_cover_media_id",
                schema: "core",
                table: "menu_submission_items",
                column: "cover_media_id");

            migrationBuilder.CreateIndex(
                name: "ix_menu_submission_items_result_dish_id",
                schema: "core",
                table: "menu_submission_items",
                column: "result_dish_id");

            migrationBuilder.CreateIndex(
                name: "ix_menu_submission_items_source_dish_id",
                schema: "core",
                table: "menu_submission_items",
                column: "source_dish_id");

            migrationBuilder.CreateIndex(
                name: "ix_menu_submission_items_submission_id_source_dish_id",
                schema: "core",
                table: "menu_submission_items",
                columns: new[] { "submission_id", "source_dish_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_menu_submissions_created_by",
                schema: "core",
                table: "menu_submissions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_menu_submissions_restaurant_id",
                schema: "core",
                table: "menu_submissions",
                column: "restaurant_id",
                unique: true,
                filter: "status IN ('draft', 'pending')");

            migrationBuilder.CreateIndex(
                name: "ix_menu_submissions_reviewed_by",
                schema: "core",
                table: "menu_submissions",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_recipient_id",
                schema: "core",
                table: "notifications",
                column: "recipient_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_event_uuid",
                schema: "core",
                table: "outbox_messages",
                column: "event_uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_permissions_code",
                schema: "core",
                table: "permissions",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reports_blog_id",
                schema: "core",
                table: "reports",
                column: "blog_id");

            migrationBuilder.CreateIndex(
                name: "ix_reports_handled_by",
                schema: "core",
                table: "reports",
                column: "handled_by");

            migrationBuilder.CreateIndex(
                name: "ix_reports_reporter_id",
                schema: "core",
                table: "reports",
                column: "reporter_id");

            migrationBuilder.CreateIndex(
                name: "ix_reports_restaurant_id",
                schema: "core",
                table: "reports",
                column: "restaurant_id");

            migrationBuilder.CreateIndex(
                name: "ix_restaurant_business_hours_restaurant_id_day_of_week_open_ti",
                schema: "core",
                table: "restaurant_business_hours",
                columns: new[] { "restaurant_id", "day_of_week", "open_time" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_restaurant_images_media_id",
                schema: "core",
                table: "restaurant_images",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_restaurant_reviews_restaurant_id_user_id",
                schema: "core",
                table: "restaurant_reviews",
                columns: new[] { "restaurant_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_restaurant_reviews_roadmap_item_id",
                schema: "core",
                table: "restaurant_reviews",
                column: "roadmap_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_restaurant_reviews_user_id",
                schema: "core",
                table: "restaurant_reviews",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_restaurant_verification_documents_media_id",
                schema: "core",
                table: "restaurant_verification_documents",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_restaurant_verification_documents_verification_id",
                schema: "core",
                table: "restaurant_verification_documents",
                column: "verification_id");

            migrationBuilder.CreateIndex(
                name: "ix_restaurant_verifications_previous_id",
                schema: "core",
                table: "restaurant_verifications",
                column: "previous_id");

            migrationBuilder.CreateIndex(
                name: "ix_restaurant_verifications_restaurant_id",
                schema: "core",
                table: "restaurant_verifications",
                column: "restaurant_id");

            migrationBuilder.CreateIndex(
                name: "ix_restaurant_verifications_reviewed_by",
                schema: "core",
                table: "restaurant_verifications",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "ix_restaurant_verifications_submitted_by",
                schema: "core",
                table: "restaurant_verifications",
                column: "submitted_by");

            migrationBuilder.CreateIndex(
                name: "ix_restaurants_cover_media_id",
                schema: "core",
                table: "restaurants",
                column: "cover_media_id");

            migrationBuilder.CreateIndex(
                name: "ix_restaurants_owner_id",
                schema: "core",
                table: "restaurants",
                column: "owner_id",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_restaurants_slug",
                schema: "core",
                table: "restaurants",
                column: "slug",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_review_images_media_id",
                schema: "core",
                table: "review_images",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_replies_replier_id",
                schema: "core",
                table: "review_replies",
                column: "replier_id");

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_days_roadmap_id_day_number",
                schema: "core",
                table: "roadmap_days",
                columns: new[] { "roadmap_id", "day_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_generation_jobs_requested_by",
                schema: "core",
                table: "roadmap_generation_jobs",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_generation_jobs_roadmap_id",
                schema: "core",
                table: "roadmap_generation_jobs",
                column: "roadmap_id");

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_items_dish_id",
                schema: "core",
                table: "roadmap_items",
                column: "dish_id");

            migrationBuilder.CreateIndex(
                name: "ix_roadmap_items_roadmap_day_id",
                schema: "core",
                table: "roadmap_items",
                column: "roadmap_day_id");

            migrationBuilder.CreateIndex(
                name: "ix_roadmaps_user_id",
                schema: "core",
                table: "roadmaps",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_role_permissions_permission_id",
                schema: "core",
                table: "role_permissions",
                column: "permission_id");

            migrationBuilder.CreateIndex(
                name: "ix_roles_code",
                schema: "core",
                table: "roles",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tags_tag_type_code",
                schema: "core",
                table: "tags",
                columns: new[] { "tag_type", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_external_logins_user_id_provider_provider_user_id",
                schema: "core",
                table: "user_external_logins",
                columns: new[] { "user_id", "provider", "provider_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_permissions_granted_by",
                schema: "core",
                table: "user_permissions",
                column: "granted_by");

            migrationBuilder.CreateIndex(
                name: "ix_user_permissions_permission_id",
                schema: "core",
                table: "user_permissions",
                column: "permission_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_sanctions_issued_by",
                schema: "core",
                table: "user_sanctions",
                column: "issued_by");

            migrationBuilder.CreateIndex(
                name: "ix_user_sanctions_lifted_by",
                schema: "core",
                table: "user_sanctions",
                column: "lifted_by");

            migrationBuilder.CreateIndex(
                name: "ix_user_sanctions_report_id",
                schema: "core",
                table: "user_sanctions",
                column: "report_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_sanctions_user_id",
                schema: "core",
                table: "user_sanctions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_refresh_token_hash",
                schema: "core",
                table: "user_sessions",
                column: "refresh_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_user_id",
                schema: "core",
                table: "user_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_tokens_token_hash",
                schema: "core",
                table: "user_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_tokens_user_id",
                schema: "core",
                table: "user_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_avatar_media_id",
                schema: "core",
                table: "users",
                column: "avatar_media_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                schema: "core",
                table: "users",
                column: "email",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_role_id",
                schema: "core",
                table: "users",
                column: "role_id");

            migrationBuilder.AddForeignKey(
                name: "fk_audit_logs_users_actor_id",
                schema: "core",
                table: "audit_logs",
                column: "actor_id",
                principalSchema: "core",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_audit_logs_users_subject_user_id",
                schema: "core",
                table: "audit_logs",
                column: "subject_user_id",
                principalSchema: "core",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_blog_comments_blogs_blog_id",
                schema: "core",
                table: "blog_comments",
                column: "blog_id",
                principalSchema: "core",
                principalTable: "blogs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_blog_comments_users_user_id",
                schema: "core",
                table: "blog_comments",
                column: "user_id",
                principalSchema: "core",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_blog_images_blogs_blog_id",
                schema: "core",
                table: "blog_images",
                column: "blog_id",
                principalSchema: "core",
                principalTable: "blogs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_blog_images_media_files_media_id",
                schema: "core",
                table: "blog_images",
                column: "media_id",
                principalSchema: "core",
                principalTable: "media_files",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_blog_likes_blogs_blog_id",
                schema: "core",
                table: "blog_likes",
                column: "blog_id",
                principalSchema: "core",
                principalTable: "blogs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_blog_likes_users_user_id",
                schema: "core",
                table: "blog_likes",
                column: "user_id",
                principalSchema: "core",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_blogs_media_files_cover_media_id",
                schema: "core",
                table: "blogs",
                column: "cover_media_id",
                principalSchema: "core",
                principalTable: "media_files",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_blogs_roadmaps_roadmap_id",
                schema: "core",
                table: "blogs",
                column: "roadmap_id",
                principalSchema: "core",
                principalTable: "roadmaps",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_blogs_users_author_id",
                schema: "core",
                table: "blogs",
                column: "author_id",
                principalSchema: "core",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dish_feedbacks_dishes_dish_id",
                schema: "core",
                table: "dish_feedbacks",
                column: "dish_id",
                principalSchema: "core",
                principalTable: "dishes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dish_feedbacks_roadmap_items_roadmap_item_id",
                schema: "core",
                table: "dish_feedbacks",
                column: "roadmap_item_id",
                principalSchema: "core",
                principalTable: "roadmap_items",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_dish_feedbacks_users_user_id",
                schema: "core",
                table: "dish_feedbacks",
                column: "user_id",
                principalSchema: "core",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dish_images_dishes_dish_id",
                schema: "core",
                table: "dish_images",
                column: "dish_id",
                principalSchema: "core",
                principalTable: "dishes",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_dish_images_media_files_media_id",
                schema: "core",
                table: "dish_images",
                column: "media_id",
                principalSchema: "core",
                principalTable: "media_files",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dish_rolls_dishes_dish_id",
                schema: "core",
                table: "dish_rolls",
                column: "dish_id",
                principalSchema: "core",
                principalTable: "dishes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dish_rolls_users_user_id",
                schema: "core",
                table: "dish_rolls",
                column: "user_id",
                principalSchema: "core",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dish_tags_dishes_dish_id",
                schema: "core",
                table: "dish_tags",
                column: "dish_id",
                principalSchema: "core",
                principalTable: "dishes",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_dishes_media_files_cover_media_id",
                schema: "core",
                table: "dishes",
                column: "cover_media_id",
                principalSchema: "core",
                principalTable: "media_files",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_dishes_restaurants_restaurant_id",
                schema: "core",
                table: "dishes",
                column: "restaurant_id",
                principalSchema: "core",
                principalTable: "restaurants",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_media_files_users_uploader_id",
                schema: "core",
                table: "media_files",
                column: "uploader_id",
                principalSchema: "core",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Lookup rows were seeded with explicit ids; move the identity sequences past them.
            migrationBuilder.Sql("""
                SELECT setval(pg_get_serial_sequence('core.roles', 'id'), (SELECT max(id) FROM core.roles));
                SELECT setval(pg_get_serial_sequence('core.permissions', 'id'), (SELECT max(id) FROM core.permissions));
                """);

            // Transactional outbox (project_architecture.md §7): the row is written in the same
            // transaction as the change. Payloads match the Reco API request bodies (snake_case).
            migrationBuilder.Sql("""
                CREATE FUNCTION core.fn_dishes_outbox() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    occurred_at text := to_char(now() AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"');
                BEGIN
                    IF NEW.status = 'active' AND (
                        TG_OP = 'INSERT'
                        OR OLD.status <> 'active'
                        OR NEW.name IS DISTINCT FROM OLD.name
                        OR NEW.description IS DISTINCT FROM OLD.description
                        OR NEW.dish_form IS DISTINCT FROM OLD.dish_form)
                    THEN
                        -- RECO-01 POST /dishes/upsert
                        INSERT INTO core.outbox_messages (event_type, aggregate_key, payload)
                        VALUES ('DISH_UPSERTED', 'dish:' || NEW.id, jsonb_build_object(
                            'dish_id', NEW.id,
                            'name', NEW.name,
                            'description', NEW.description,
                            'dish_form', NEW.dish_form,
                            'restaurant_id', NEW.restaurant_id,
                            'occurred_at', occurred_at));
                    ELSIF TG_OP = 'UPDATE' AND OLD.status = 'active' AND NEW.status <> 'active' THEN
                        -- RECO-02 DELETE /dishes/{dish_id}
                        INSERT INTO core.outbox_messages (event_type, aggregate_key, payload)
                        VALUES ('DISH_DEACTIVATED', 'dish:' || NEW.id, jsonb_build_object(
                            'dish_id', NEW.id,
                            'occurred_at', occurred_at));
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE TRIGGER trg_dishes_outbox
                AFTER INSERT OR UPDATE ON core.dishes
                FOR EACH ROW EXECUTE FUNCTION core.fn_dishes_outbox();

                CREATE FUNCTION core.fn_dish_feedbacks_outbox() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    -- RECO-04 POST /feedback; event_uuid is shared with dish_feedbacks (Data Dictionary)
                    INSERT INTO core.outbox_messages (event_uuid, event_type, aggregate_key, payload)
                    VALUES (NEW.event_uuid, 'FEEDBACK_CREATED', 'user:' || NEW.user_id, jsonb_build_object(
                        'event_uuid', NEW.event_uuid,
                        'user_id', NEW.user_id,
                        'dish_id', NEW.dish_id,
                        'event_type', CASE NEW.value WHEN 1 THEN 'like' ELSE 'dislike' END,
                        'request_id', NEW.reco_request_id,
                        'occurred_at', to_char(NEW.created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')));
                    RETURN NULL;
                END;
                $$;

                CREATE TRIGGER trg_dish_feedbacks_outbox
                AFTER INSERT ON core.dish_feedbacks
                FOR EACH ROW EXECUTE FUNCTION core.fn_dish_feedbacks_outbox();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_dish_feedbacks_outbox ON core.dish_feedbacks;
                DROP FUNCTION IF EXISTS core.fn_dish_feedbacks_outbox();
                DROP TRIGGER IF EXISTS trg_dishes_outbox ON core.dishes;
                DROP FUNCTION IF EXISTS core.fn_dishes_outbox();
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_media_files_users_uploader_id",
                schema: "core",
                table: "media_files");

            migrationBuilder.DropTable(
                name: "audit_logs",
                schema: "core");

            migrationBuilder.DropTable(
                name: "blog_comments",
                schema: "core");

            migrationBuilder.DropTable(
                name: "blog_images",
                schema: "core");

            migrationBuilder.DropTable(
                name: "blog_likes",
                schema: "core");

            migrationBuilder.DropTable(
                name: "dish_feedbacks",
                schema: "core");

            migrationBuilder.DropTable(
                name: "dish_images",
                schema: "core");

            migrationBuilder.DropTable(
                name: "dish_rolls",
                schema: "core");

            migrationBuilder.DropTable(
                name: "dish_tags",
                schema: "core");

            migrationBuilder.DropTable(
                name: "menu_submission_items",
                schema: "core");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "core");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "core");

            migrationBuilder.DropTable(
                name: "restaurant_business_hours",
                schema: "core");

            migrationBuilder.DropTable(
                name: "restaurant_images",
                schema: "core");

            migrationBuilder.DropTable(
                name: "restaurant_verification_documents",
                schema: "core");

            migrationBuilder.DropTable(
                name: "review_images",
                schema: "core");

            migrationBuilder.DropTable(
                name: "review_replies",
                schema: "core");

            migrationBuilder.DropTable(
                name: "roadmap_generation_jobs",
                schema: "core");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "user_external_logins",
                schema: "core");

            migrationBuilder.DropTable(
                name: "user_permissions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "user_sanctions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "user_sessions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "user_tokens",
                schema: "core");

            migrationBuilder.DropTable(
                name: "tags",
                schema: "core");

            migrationBuilder.DropTable(
                name: "menu_submissions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "restaurant_verifications",
                schema: "core");

            migrationBuilder.DropTable(
                name: "restaurant_reviews",
                schema: "core");

            migrationBuilder.DropTable(
                name: "permissions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "reports",
                schema: "core");

            migrationBuilder.DropTable(
                name: "roadmap_items",
                schema: "core");

            migrationBuilder.DropTable(
                name: "blogs",
                schema: "core");

            migrationBuilder.DropTable(
                name: "dishes",
                schema: "core");

            migrationBuilder.DropTable(
                name: "roadmap_days",
                schema: "core");

            migrationBuilder.DropTable(
                name: "restaurants",
                schema: "core");

            migrationBuilder.DropTable(
                name: "roadmaps",
                schema: "core");

            migrationBuilder.DropTable(
                name: "users",
                schema: "core");

            migrationBuilder.DropTable(
                name: "media_files",
                schema: "core");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "core");
        }
    }
}
