using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
    {
        private const short ModRoleId = 3;
        private const short AdminRoleId = 4;
        private const short ModPermissionCount = 10;
        private const short PermissionCount = 13;

        public void Configure(EntityTypeBuilder<RolePermission> builder)
        {
            builder.ToTable("role_permissions");

            builder.HasKey(x => new { x.RoleId, x.PermissionId });

            builder.HasOne(x => x.Role).WithMany(x => x.RolePermissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);

            // Mod has every permission except the last 3; Admin has all (sheet "Enum")
            var modPermissions = Enumerable.Range(1, ModPermissionCount)
                .Select(id => new RolePermission { RoleId = ModRoleId, PermissionId = (short)id });
            var adminPermissions = Enumerable.Range(1, PermissionCount)
                .Select(id => new RolePermission { RoleId = AdminRoleId, PermissionId = (short)id });
            builder.HasData(modPermissions.Concat(adminPermissions));
        }
    }
}
