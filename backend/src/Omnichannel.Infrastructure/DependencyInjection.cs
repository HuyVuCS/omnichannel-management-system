using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Alerts;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Customers;
using Omnichannel.Application.Dashboard;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Processing;
using Omnichannel.Application.Products;
using Omnichannel.Application.Reports;
using Omnichannel.Application.SystemEvents;
using Omnichannel.Application.Users;
using Omnichannel.Infrastructure.Identity;
using Omnichannel.Infrastructure.Logging;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.Persistence.Alerts;
using Omnichannel.Infrastructure.Persistence.Channels;
using Omnichannel.Infrastructure.Persistence.Customers;
using Omnichannel.Infrastructure.Persistence.Dashboard;
using Omnichannel.Infrastructure.Persistence.Interceptors;
using Omnichannel.Infrastructure.Persistence.Inventories;
using Omnichannel.Infrastructure.Persistence.Orders;
using Omnichannel.Infrastructure.Persistence.Processing;
using Omnichannel.Infrastructure.Persistence.Products;
using Omnichannel.Infrastructure.Persistence.Reports;
using Omnichannel.Infrastructure.Persistence.SystemEvents;
using Omnichannel.Infrastructure.Realtime;
using Omnichannel.Infrastructure.S3;

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
        services.AddSingleton<DatabaseFailureLogInterceptor>();
        services.AddScoped<RealtimeHintInterceptor>();

        services.AddDbContext<OmnichannelDbContext>((serviceProvider, options) =>
        {
            var database = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseNpgsql(database.ConnectionString);
            // Order matters: audit stamps first, then the hints of what is about to be saved.
            options.AddInterceptors(
                serviceProvider.GetRequiredService<AuditingInterceptor>(),
                serviceProvider.GetRequiredService<RealtimeHintInterceptor>());
            DatabaseFailureLogInterceptor.Configure(options, serviceProvider.GetRequiredService<DatabaseFailureLogInterceptor>());
        });

        services.AddScoped<ProcessingAttemptStore>();
        services.AddScoped<AlertStore>();
        services.AddScoped<IAlertService, AlertService>();
        services.AddScoped<IChannelService, ChannelService>();
        services.AddScoped<IStockSyncService, StockSyncService>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IProductService, ProductService>();
        // Product images and export files need the object storage; a host without one (AddObjectStorage not called)
        // serves products that have no image and fails loudly on anything else.
        services.TryAddSingleton<IObjectStorage, UnconfiguredObjectStorage>();
        services.AddScoped<ProductImageLinks>();
        services.AddScoped<IProductImageService, ProductImageService>();
        services.AddScoped<IInventoryQueryService, InventoryQueryService>();
        services.AddScoped<IInventoryImportService, InventoryImportService>();
        services.AddScoped<IOrderIntakeService, OrderIntakeService>();
        services.AddScoped<IWebhookOrderService, WebhookOrderService>();
        services.AddScoped<IOrderReservationService, OrderReservationService>();
        services.AddScoped<IOrderQueryService, OrderQueryService>();
        services.AddScoped<IProcessingTaskService, ProcessingTaskService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<ISystemEventService, SystemEventService>();

        // Exports: requested through the API, built and swept by the Workers host; both read the same limits.
        services.AddOptions<ReportsOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                configuration.GetSection(ReportsOptions.SectionName).Bind(options))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ReportsOptions>, ReportsOptionsValidator>();
        services.AddScoped<IReportExportService, ReportExportService>();
        services.AddScoped<IReportExportProcessingService, ReportExportProcessingService>();
        services.AddScoped<ReportExportCleanup>();

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
            .AddErrorDescriber<LocalizedIdentityErrorDescriber>()
            .AddEntityFrameworkStores<OmnichannelDbContext>();

        services.AddScoped<IAuthService, IdentityAuthService>();

        services.AddMemoryCache();
        services.AddOptions<TokenRevocationOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                var configured = configuration[$"{TokenRevocationOptions.SectionName}:{nameof(TokenRevocationOptions.CacheDuration)}"];
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    // An unparsable value becomes negative and fails validation instead of silently using the default.
                    options.CacheDuration = TimeSpan.TryParse(configured, System.Globalization.CultureInfo.InvariantCulture, out var duration)
                        ? duration
                        : TimeSpan.MinValue;
                }
            })
            .Validate(
                options => options.CacheDuration >= TimeSpan.Zero && options.CacheDuration <= TokenRevocationOptions.MaxCacheDuration,
                $"{TokenRevocationOptions.SectionName}:{nameof(TokenRevocationOptions.CacheDuration)} must be between 00:00:00 and {TokenRevocationOptions.MaxCacheDuration:c}.")
            .ValidateOnStart();
        services.AddScoped<IUserSecurityStateProvider, UserSecurityStateProvider>();
        services.AddScoped<IUserService, IdentityUserService>();

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
