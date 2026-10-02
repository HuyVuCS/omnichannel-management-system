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

    // RabbitMQ topology, Outbox Publisher, Order Worker, Stock Sync Worker; further workers follow in later steps.
    builder.Services.AddWorkerServices(builder.Configuration);

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
