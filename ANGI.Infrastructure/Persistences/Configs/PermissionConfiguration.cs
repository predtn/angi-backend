using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
    {
        public void Configure(EntityTypeBuilder<Permission> builder)
        {
            builder.ToTable("permissions");

            builder.Property(x => x.Code).HasMaxLength(60);
            builder.Property(x => x.Description).HasMaxLength(255);

            builder.HasIndex(x => x.Code).IsUnique();

            // Codes from sheet "Enum" of ANGI_API_Design_Ver1.6
            builder.HasData(
                new Permission { Id = 1, Code = "USER_VIEW_AUDIT", Description = "Xem nhật ký hoạt động của người dùng" },
                new Permission { Id = 2, Code = "USER_SUSPEND", Description = "Tạm khóa và mở khóa người dùng" },
                new Permission { Id = 3, Code = "USER_BAN", Description = "Cấm và gỡ cấm người dùng" },
                new Permission { Id = 4, Code = "RESTAURANT_VERIFY", Description = "Duyệt xác minh nhà hàng" },
                new Permission { Id = 5, Code = "RESTAURANT_MODERATE", Description = "Ẩn và đình chỉ nhà hàng" },
                new Permission { Id = 6, Code = "MENU_APPROVE", Description = "Duyệt menu nhà hàng" },
                new Permission { Id = 7, Code = "REPORT_RESTAURANT_HANDLE", Description = "Xử lý báo cáo nhà hàng" },
                new Permission { Id = 8, Code = "REPORT_SOCIAL_HANDLE", Description = "Xử lý báo cáo blog" },
                new Permission { Id = 9, Code = "BLOG_MODERATE", Description = "Ẩn và gỡ blog, bình luận" },
                new Permission { Id = 10, Code = "REVIEW_MODERATE", Description = "Ẩn và gỡ review, phản hồi" },
                new Permission { Id = 11, Code = "MOD_MANAGE", Description = "Quản lý tài khoản Mod và phân quyền" },
                new Permission { Id = 12, Code = "SYSTEM_AUDIT_VIEW", Description = "Xem nhật ký hệ thống" },
                new Permission { Id = 13, Code = "DASHBOARD_VIEW", Description = "Xem dashboard thống kê" });
        }
    }
}
