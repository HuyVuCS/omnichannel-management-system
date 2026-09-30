using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// PostgreSQL + RabbitMQ for Workers-side tests, shared by the "workers" collection (tests run sequentially).
/// No WebApplicationFactory: Workers hosts are built with the same <see cref="WorkerHostSetup.AddWorkerServices"/> as
/// <c>Program</c>. RabbitMQ's AMQP port is bound to a fixed host port so the broker can be stopped and started again
/// at the same address.
/// </summary>
public sealed class WorkersFixture : IAsyncLifetime
{
    // Test-only credentials of a throw-away container.
    public const string RabbitUser = "omni-test";
    public const string RabbitPassword = "omni-test-password";
    private const int AmqpPort = 5672;
    private const int ManagementPort = 15672;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly int _amqpHostPort = GetFreeTcpPort();
    private readonly RabbitMqContainer _rabbit;
    private IHost? _tools;
    private IConnection? _assertionConnection;

    public WorkersFixture()
    {
        _rabbit = new RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername(RabbitUser)
            .WithPassword(RabbitPassword)
            .WithPortBinding(_amqpHostPort, AmqpPort)
            .WithPortBinding(ManagementPort, true)
            .Build();
    }

    public HttpClient Management { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        var options = new DbContextOptionsBuilder<OmnichannelDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options;
        await using (var dbContext = new OmnichannelDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        // Not started: only its services are used (DbContext with the auditing interceptor, connection provider).
        _tools = CreateHost("tools");

        Management = new HttpClient
        {
            BaseAddress = new Uri($"http://{_rabbit.Hostname}:{_rabbit.GetMappedPublicPort(ManagementPort)}/")
        };
        Management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{RabbitUser}:{RabbitPassword}")));

        await using var channel = await OpenChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        Management?.Dispose();
        if (_assertionConnection is not null)
        {
            await _assertionConnection.DisposeAsync();
        }

        _tools?.Dispose();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    /// <summary>
    /// A Workers host (not started) with test configuration; <paramref name="overrides"/> win.
    /// <paramref name="configureServices"/> runs after the production registrations (test seams, log capture).
    /// </summary>
    public IHost CreateHost(
        string instanceId,
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null) =>
        BuildWorkersHost(
            _postgres.GetConnectionString(), _rabbit.Hostname, _amqpHostPort, instanceId, overrides, configureServices);

    /// <summary>Workers host with the production registrations against the given database and broker (test credentials).</summary>
    public static IHost BuildWorkersHost(
        string databaseConnectionString,
        string rabbitHostName,
        int rabbitPort,
        string instanceId,
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = databaseConnectionString,
            ["RabbitMq:HostName"] = rabbitHostName,
            ["RabbitMq:Port"] = rabbitPort.ToString(),
            ["RabbitMq:UserName"] = RabbitUser,
            ["RabbitMq:Password"] = RabbitPassword,
            ["RabbitMq:ClientProvidedName"] = $"tests-{instanceId}",
            ["RabbitMq:NetworkRecoveryInterval"] = "00:00:01",
            ["Worker:InstanceId"] = instanceId,
            ["OutboxPublisher:PollInterval"] = "00:00:00.100",
            ["OutboxPublisher:InitialBackoff"] = "00:00:00.200",
            ["OutboxPublisher:MaxBackoff"] = "00:00:02"
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        // No default sources: the referenced projects' appsettings.json files in the test output must not leak in.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            EnvironmentName = "Testing"
        });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddWorkerServices(builder.Configuration);
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }

    public async Task<T> WithDbAsync<T>(Func<OmnichannelDbContext, Task<T>> action)
    {
        await using var scope = _tools!.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<OmnichannelDbContext>());
    }

    /// <summary>Runs application services (intake, import, ...) of a non-started Workers host in a fresh scope.</summary>
    public async Task<T> WithServicesAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _tools!.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>Publishes a raw message to the main exchange, as the Outbox Publisher would.</summary>
    public async Task PublishAsync(string routingKey, byte[] body, string? messageId = null)
    {
        await using var channel = await OpenChannelAsync();
        var properties = new BasicProperties
        {
            MessageId = messageId,
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent
        };
        await channel.BasicPublishAsync(RabbitMqTopology.Exchange, routingKey, mandatory: false, properties, body);
    }

    /// <summary>Channel on a separate connection used only by test assertions.</summary>
    public async Task<IChannel> OpenChannelAsync()
    {
        if (_assertionConnection is not { IsOpen: true })
        {
            if (_assertionConnection is not null)
            {
                await _assertionConnection.DisposeAsync();
            }

            var factory = new ConnectionFactory
            {
                HostName = _rabbit.Hostname,
                Port = _amqpHostPort,
                UserName = RabbitUser,
                Password = RabbitPassword,
                AutomaticRecoveryEnabled = false
            };
            _assertionConnection = await factory.CreateConnectionAsync();
        }

        return await _assertionConnection.CreateChannelAsync();
    }

    /// <summary>Empties every main queue and DLQ so each test sees only its own messages.</summary>
    public async Task PurgeQueuesAsync()
    {
        await using var channel = await OpenChannelAsync();
        foreach (var (queue, _) in RabbitMqTopology.Bindings)
        {
            await channel.QueuePurgeAsync(queue);
            await channel.QueuePurgeAsync(RabbitMqTopology.DeadLetterQueueOf(queue));
        }
    }

    public Task StopBrokerAsync() => _rabbit.StopAsync();

    public Task StartBrokerAsync() => _rabbit.StartAsync();

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

[CollectionDefinition(Name)]
public sealed class WorkersCollection : ICollectionFixture<WorkersFixture>
{
    public const string Name = "workers";
}
