using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Customers;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Products;
using Omnichannel.Infrastructure.Identity;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.Persistence.Channels;
using Omnichannel.Infrastructure.Persistence.Customers;
using Omnichannel.Infrastructure.Persistence.Interceptors;
using Omnichannel.Infrastructure.Persistence.Inventories;
using Omnichannel.Infrastructure.Persistence.Products;

namespace Omnichannel.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    /// <summary>
    /// Persistence services shared by the API and the Workers.
    /// The host must register <see cref="Application.Common.Abstractions.ICurrentUser"/>.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString(ConnectionStringName) ?? string.Empty)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"ConnectionStrings:{ConnectionStringName} is not configured.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AuditingInterceptor>();

        services.AddDbContext<OmnichannelDbContext>((serviceProvider, options) =>
        {
            var database = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseNpgsql(database.ConnectionString);
            options.AddInterceptors(serviceProvider.GetRequiredService<AuditingInterceptor>());
        });

        services.AddScoped<IChannelService, ChannelService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IInventoryQueryService, InventoryQueryService>();

        return services;
    }

    /// <summary>ASP.NET Core Identity stores and the login service. API host only.</summary>
    public static IServiceCollection AddIdentityInfrastructure(this IServiceCollection services)
    {
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;

                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<OmnichannelDbContext>();

        services.AddScoped<IAuthService, IdentityAuthService>();

        services.AddOptions<BootstrapAdminOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.Email = configuration["BootstrapAdmin:Email"];
                options.Password = configuration["BootstrapAdmin:Password"];
            });
        services.AddScoped<BootstrapAdminSeeder>();

        return services;
    }
}
