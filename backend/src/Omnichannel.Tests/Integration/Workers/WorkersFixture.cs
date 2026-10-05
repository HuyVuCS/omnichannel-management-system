using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Omnichannel.Infrastructure.Persistence;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers;
using Omnichannel.Workers.ReportExportWorker;
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

    // Fixed as well: the broker-outage test restarts the container, which would re-map a random port.
    private readonly int _managementHostPort = GetFreeTcpPort();
    private readonly RabbitMqContainer _rabbit;
    private IHost? _tools;
    private IConnection? _assertionConnection;
    private int _isolatedDatabases;

    public WorkersFixture()
    {
        _rabbit = new RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername(RabbitUser)
            .WithPassword(RabbitPassword)
            .WithPortBinding(_amqpHostPort, AmqpPort)
            .WithPortBinding(_managementHostPort, ManagementPort)
            .Build();
    }

    public HttpClient Management { get; private set; } = null!;

    /// <summary>The Workers test database (for a separate connection, e.g. a <c>LISTEN</c> probe).</summary>
    public string DatabaseConnectionString => _postgres.GetConnectionString();

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
            BaseAddress = new Uri($"http://{_rabbit.Hostname}:{_managementHostPort}/")
        };
        Management.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{RabbitUser}:{RabbitPassword}")));

        await using var channel = await OpenChannelAsync();
        await RabbitMqTopology.DeclareAsync(channel, CancellationToken.None);
        await RabbitMqTopology.DeclareAsync(channel, TestRetryDelays, CancellationToken.None);
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
    /// <param name="databaseConnectionString">Another database of this server (<see cref="CreateIsolatedDatabaseAsync"/>); the shared one when null.</param>
    public IHost CreateHost(
        string instanceId,
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null,
        string? databaseConnectionString = null) =>
        BuildWorkersHost(
            databaseConnectionString ?? _postgres.GetConnectionString(), _rabbit.Hostname, _amqpHostPort, instanceId, overrides, configureServices);

    /// <summary>
    /// A migrated database of its own on the same server, for tests whose subject looks at <b>every</b> row of a table
    /// (a reconciliation run reads all mappings): in the shared database the rows of every other test would take part.
    /// Seeded like any migrated database (the three channels, the roles), otherwise empty. Never dropped: the container
    /// goes away with the fixture.
    /// </summary>
    public async Task<string> CreateIsolatedDatabaseAsync()
    {
        var name = $"isolated_{Interlocked.Increment(ref _isolatedDatabases)}";
        await using (var server = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await server.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", server);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<OmnichannelDbContext>().UseNpgsql(connectionString).Options;
        await using var dbContext = new OmnichannelDbContext(options);
        await dbContext.Database.MigrateAsync();
        return connectionString;
    }

    /// <summary>
    /// Workers host with the production registrations against the given database and broker (test credentials).
    /// <para>
    /// Two things differ from production unless a test asks for them: the object storage is configuration only (valid, at
    /// an address nothing listens on - a test that stores export files overrides <c>ObjectStorage:*</c> with a started
    /// emulator), and the retention sweep of exports does not run (<paramref name="retentionSweep"/>): in the background
    /// it would add statements to tests that count them and expire the exports of other tests. Reconciliation is
    /// switched off the same way (<c>Reconciliation:Enabled</c>; a test of the service overrides it): a run would create
    /// stock-sync tasks for the mappings of every test in the database.
    /// </para>
    /// </summary>
    public static IHost BuildWorkersHost(
        string databaseConnectionString,
        string rabbitHostName,
        int rabbitPort,
        string instanceId,
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null,
        bool retentionSweep = false)
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
            // Short retry tiers (their own delay queues: omnichannel.delay.1s ... 4s).
            ["RabbitMq:RetryDelays:0"] = "00:00:01",
            ["RabbitMq:RetryDelays:1"] = "00:00:02",
            ["RabbitMq:RetryDelays:2"] = "00:00:03",
            ["RabbitMq:RetryDelays:3"] = "00:00:04",
            ["Worker:InstanceId"] = instanceId,
            ["OutboxPublisher:PollInterval"] = "00:00:00.100",
            ["OutboxPublisher:InitialBackoff"] = "00:00:00.200",
            ["OutboxPublisher:MaxBackoff"] = "00:00:02",
            ["Reconciliation:Enabled"] = "false",
            // Never connected (port 9, "discard"): only the options must be valid for the host to start.
            ["ObjectStorage:ServiceUrl"] = "http://127.0.0.1:9",
            ["ObjectStorage:BucketName"] = "not-used",
            ["ObjectStorage:AccessKeyId"] = "not-used",
            ["ObjectStorage:SecretAccessKey"] = "not-used",
            ["ObjectStorage:RequestTimeout"] = "00:00:03"
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
        if (!retentionSweep)
        {
            builder.Services.RemoveHostedService<ReportExportCleanupService>();
        }

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

        foreach (var delayQueue in DelayQueues)
        {
            await channel.QueuePurgeAsync(delayQueue);
        }
    }

    /// <summary>The short retry tiers every test host uses (see <see cref="BuildWorkersHost"/>).</summary>
    public static readonly TimeSpan[] TestRetryDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)];

    /// <summary>Delay queues of the test tiers and of the default tiers (both are declared on this broker).</summary>
    public static IEnumerable<string> DelayQueues =>
        TestRetryDelays.Concat(RabbitMqTopology.DefaultRetryDelays).Select(RabbitMqTopology.DelayNameOf).Distinct();

    /// <summary>A separate connection to <paramref name="virtualHost"/> (topology tests on a fresh vhost).</summary>
    public Task<IConnection> ConnectAsync(string virtualHost) =>
        new ConnectionFactory
        {
            HostName = _rabbit.Hostname,
            Port = _amqpHostPort,
            VirtualHost = virtualHost,
            UserName = RabbitUser,
            Password = RabbitPassword,
            AutomaticRecoveryEnabled = false
        }.CreateConnectionAsync();

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
