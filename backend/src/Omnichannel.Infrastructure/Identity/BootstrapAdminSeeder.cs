using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Auth;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Infrastructure.Identity;

public sealed class BootstrapAdminSeeder(
    OmnichannelDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IOptions<BootstrapAdminOptions> options,
    ILogger<BootstrapAdminSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Email) || string.IsNullOrWhiteSpace(settings.Password))
        {
            logger.LogDebug("Bootstrap admin not configured; skipping");
            return;
        }

        if (await userManager.FindByEmailAsync(settings.Email) is not null)
        {
            logger.LogInformation("Bootstrap admin already exists; skipping");
            return;
        }

        // User + role assignment must be atomic: never leave an admin account without its role.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var user = new ApplicationUser
        {
            UserName = settings.Email,
            Email = settings.Email,
            EmailConfirmed = true
        };

        var created = await userManager.CreateAsync(user, settings.Password);
        EnsureSucceeded(created, "create user");

        var roleAdded = await userManager.AddToRoleAsync(user, AppRoles.Admin);
        EnsureSucceeded(roleAdded, "assign Admin role");

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Bootstrap admin {UserId} created", user.Id);
    }

    private static void EnsureSucceeded(IdentityResult result, string step)
    {
        if (!result.Succeeded)
        {
            // Error codes only: descriptions may echo input values.
            var codes = string.Join(", ", result.Errors.Select(e => e.Code));
            throw new InvalidOperationException($"Bootstrap admin: failed to {step} ({codes}).");
        }
    }
}
