using Omnichannel.Application;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Infrastructure;
using Omnichannel.Workers;
using Serilog;
using Serilog.Formatting.Compact;

const string DevelopmentLogTemplate =
    "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}{Exception}";

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

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

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure();
    builder.Services.AddSingleton<ICurrentUser, WorkerCurrentUser>();
    builder.Services.AddScoped<WorkerCorrelationContext>();
    builder.Services.AddScoped<ICorrelationContext>(sp => sp.GetRequiredService<WorkerCorrelationContext>());

    // Hosted workers (OrderWorker, OutboxPublisher, StockSyncWorker, ...) are registered from
    // implementation step 6 onwards, together with RabbitMQ.

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Workers host terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
