using System.Text.Json.Serialization;
using Omnichannel.Api.Auth;
using Omnichannel.Api.Filters;
using Omnichannel.Api.Middleware;
using Omnichannel.Application;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Infrastructure;
using Omnichannel.Infrastructure.Identity;
using Omnichannel.Infrastructure.Persistence;
using Serilog;
using Serilog.Formatting.Compact;

const string FrontendCorsPolicy = "Frontend";
const string DevelopmentLogTemplate =
    "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}{Exception}";

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Levels/overrides/enrichers come from the "Serilog" config section; the console sink is set here:
    // readable text in Development, structured JSON everywhere else (for log shipping).
    builder.Services.AddSerilog((services, logger) =>
    {
        logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext();

        if (builder.Environment.IsDevelopment())
        {
            logger.WriteTo.Console(outputTemplate: DevelopmentLogTemplate);
        }
        else
        {
            logger.WriteTo.Console(new RenderedCompactJsonFormatter());
        }
    });

    // Application / infrastructure
    builder.Services.AddApplication();
    builder.Services.AddInfrastructure();
    builder.Services.AddIdentityInfrastructure();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

    // Security
    builder.Services.AddJwtAuthentication(builder.Configuration);
    builder.Services.AddRbacAuthorization();
    builder.Services.AddApiRateLimiting();

    var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    builder.Services.AddCors(options => options.AddPolicy(FrontendCorsPolicy, policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()
        .WithExposedHeaders(CorrelationIdMiddleware.HeaderName)));

    // Web
    builder.Services
        .AddControllers(options => options.Filters.Add<ValidationFilter>())
        .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
        context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.GetCorrelationId();
    });

    builder.Services.AddOpenApi();
    builder.Services.AddHealthChecks().AddDbContextCheck<OmnichannelDbContext>("database");

    var app = builder.Build();

    // Pipeline order matters: correlation id first so request logs and error bodies carry it.
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();
    app.UseStatusCodePages();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().AllowAnonymous();
        // Static UI; registered before authentication so the fallback policy does not apply.
        app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Omnichannel API v1"));
    }
    else
    {
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseCors(FrontendCorsPolicy);
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHealthChecks("/health").AllowAnonymous();

    await using (var scope = app.Services.CreateAsyncScope())
    {
        await scope.ServiceProvider.GetRequiredService<BootstrapAdminSeeder>()
            .SeedAsync(app.Lifetime.ApplicationStopping);
    }

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // HostAbortedException is thrown on purpose by design-time tools (dotnet ef).
    Log.Fatal(ex, "API terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Exposed for WebApplicationFactory<Program> in integration tests.
public partial class Program;
