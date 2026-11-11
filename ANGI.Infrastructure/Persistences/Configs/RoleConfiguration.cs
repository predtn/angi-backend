using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ANGI.Infrastructure.Persistences.Configs
{
    public class RoleConfiguration : IEntityTypeConfiguration<Role>
    {
        public void Configure(EntityTypeBuilder<Role> builder)
        {
            builder.ToTable("roles");

            builder.Property(x => x.Code).HasMaxLength(30);
            builder.Property(x => x.Name).HasMaxLength(100);

            builder.HasIndex(x => x.Code).IsUnique();

            builder.HasData(
                new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" },
                new Role { Id = 2, Code = "RESTAURANT_OWNER", Name = "Restaurant Owner" },
                new Role { Id = 3, Code = "MOD", Name = "Mod" },
                new Role { Id = 4, Code = "ADMIN", Name = "Admin" });
        }
    }
}
