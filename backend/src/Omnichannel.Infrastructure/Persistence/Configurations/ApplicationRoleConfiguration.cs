using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Omnichannel.Infrastructure.Identity;

namespace Omnichannel.Infrastructure.Persistence.Configurations;

internal sealed class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        // Table/columns are configured by IdentityDbContext; only seed the RBAC roles here.
        builder.HasData(SeedData.Roles);
    }
}
