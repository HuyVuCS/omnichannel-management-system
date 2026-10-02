using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.ExternalChannels;
using Omnichannel.Infrastructure.RabbitMQ;
using Omnichannel.Workers.Processing;
using Omnichannel.Workers.StockSyncWorker;

namespace Omnichannel.Tests.Workers;

/// <summary>Task 014: what a stock-sync queue accepts; everything else is poison.</summary>
public sealed class StockSyncMessageParserTests
{
    private static readonly Guid ProductId = Guid.NewGuid();

    [Fact]
    public void Inventory_changed_is_parsed_to_its_product_without_a_task()
    {
        var body = Envelope(EventTypes.InventoryChanged, null, new InventoryChangedPayload(
            ProductId, Guid.NewGuid(), InventoryMovementType.Import, 9, 2, 7, DateTimeOffset.UtcNow));

        Assert.True(StockSyncMessageParser.TryParse(body, out var message, out var reason));

        Assert.Null(reason);
        Assert.Equal((ProductId, (Guid?)null, false), (message!.ProductId, message.TaskId, message.IsDirected));
        Assert.Equal(("msg-1", "corr-1"), (message.Envelope.MessageId, message.Envelope.CorrelationId));
    }

    [Fact]
    public void Stock_sync_requested_is_parsed_to_its_task()
    {
        var (taskId, channelId) = (Guid.NewGuid(), Guid.NewGuid());
        var body = Envelope(EventTypes.StockSyncRequested, taskId, new StockSyncRequestedPayload(taskId, channelId, ProductId));

        Assert.True(StockSyncMessageParser.TryParse(body, out var message, out _));

        Assert.Equal((ProductId, (Guid?)taskId, (Guid?)channelId, true), (message!.ProductId, message.TaskId, message.ChannelId, message.IsDirected));
    }

    public static TheoryData<string, byte[]> PoisonMessages()
    {
        var taskId = Guid.NewGuid();
        var change = new InventoryChangedPayload(ProductId, Guid.NewGuid(), InventoryMovementType.Import, 1, 0, 1, DateTimeOffset.UtcNow);
        return new TheoryData<string, byte[]>
        {
            { "not a valid message envelope", Encoding.UTF8.GetBytes("{ not json") },
            { "null envelope", Encoding.UTF8.GetBytes("null") },
            { "Unexpected event type 'order.created'", Envelope(EventTypes.OrderCreated, taskId, change) },
            { "no messageId or correlationId", Envelope(EventTypes.InventoryChanged, null, change, messageId: " ") },
            { "no messageId or correlationId", Envelope(EventTypes.InventoryChanged, null, change, correlationId: "") },
            { "longer than 100 characters", Envelope(EventTypes.InventoryChanged, null, change, messageId: new string('m', 101)) },
            { "payload is not a JSON object", Envelope(EventTypes.InventoryChanged, null, "text") },
            { "no productId", Envelope(EventTypes.InventoryChanged, null, new { productId = Guid.Empty }) },
            { "not a valid inventory change", Envelope(EventTypes.InventoryChanged, null, new { productId = "x" }) },
            { "no taskId", Envelope(EventTypes.StockSyncRequested, null, new StockSyncRequestedPayload(taskId, Guid.NewGuid(), ProductId)) },
            { "does not match envelope taskId", Envelope(EventTypes.StockSyncRequested, taskId, new StockSyncRequestedPayload(Guid.NewGuid(), Guid.NewGuid(), ProductId)) },
            { "lacks channelId or productId", Envelope(EventTypes.StockSyncRequested, taskId, new StockSyncRequestedPayload(taskId, Guid.Empty, ProductId)) },
            { "lacks channelId or productId", Envelope(EventTypes.StockSyncRequested, taskId, new StockSyncRequestedPayload(taskId, Guid.NewGuid(), Guid.Empty)) }
        };
    }

    [Theory]
    [MemberData(nameof(PoisonMessages))]
    public void Anything_else_is_poison_with_a_reason(string expectedReason, byte[] body)
    {
        Assert.False(StockSyncMessageParser.TryParse(body, out var message, out var reason));

        Assert.Null(message);
        Assert.Contains(expectedReason, reason, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Envelope(string eventType, Guid? taskId, object payload, string messageId = "msg-1", string correlationId = "corr-1") =>
        MessageJson.SerializeToUtf8Bytes(new MessageEnvelope(
            messageId, correlationId, taskId, eventType, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(payload, MessageJson.Options)));
}

/// <summary>Task 014: channel errors in the shared classifier (the order cases are in <see cref="ProcessingErrorClassifierTests"/>).</summary>
public sealed class ChannelErrorClassificationTests
{
    [Theory]
    [InlineData(ChannelApiErrorCodes.Timeout, true)]
    [InlineData(ChannelApiErrorCodes.Unavailable, true)]
    [InlineData(ChannelApiErrorCodes.RateLimited, true)]
    [InlineData(ChannelApiErrorCodes.CircuitOpen, true)]
    [InlineData(ChannelApiErrorCodes.Rejected, false)]
    public void A_channel_error_keeps_its_code_and_decides_itself_whether_a_retry_can_help(string errorCode, bool transient)
    {
        var error = ProcessingErrorClassifier.Classify(new ChannelApiException(errorCode, "the channel said no", transient));

        Assert.Equal((errorCode, "the channel said no", !transient), (error.ErrorCode, error.ErrorMessage, error.IsPermanent));
    }

    [Fact]
    public void A_wrapped_channel_error_is_found()
    {
        var wrapped = new InvalidOperationException(
            "outer", new ChannelApiException(ChannelApiErrorCodes.Rejected, "unknown external product", isTransient: false));

        var error = ProcessingErrorClassifier.Classify(wrapped);

        Assert.Equal((ChannelApiErrorCodes.Rejected, true), (error.ErrorCode, error.IsPermanent));
    }

    [Fact]
    public void A_channel_message_is_sanitised_and_bounded()
    {
        var error = ProcessingErrorClassifier.Classify(new ChannelApiException(
            ChannelApiErrorCodes.Unavailable, "failed: password=s3cr3t " + new string('x', 2000), isTransient: true));

        Assert.DoesNotContain("s3cr3t", error.ErrorMessage);
        Assert.True(error.ErrorMessage.Length <= 500);
    }

    [Fact]
    public void A_database_error_during_stock_sync_is_classified_as_for_orders()
    {
        Assert.Equal(
            (ProcessingErrorClassifier.Timeout, false),
            (ProcessingErrorClassifier.Classify(new DbUpdateException("wrapped", new TimeoutException("slow"))).ErrorCode,
                ProcessingErrorClassifier.Classify(new DbUpdateException("wrapped", new TimeoutException("slow"))).IsPermanent));
    }
}

public sealed class StockSyncWorkerOptionsTests
{
    private readonly StockSyncWorkerOptionsValidator _validator = new();

    [Fact]
    public void Defaults_are_valid_and_as_documented()
    {
        var options = new StockSyncWorkerOptions();

        Assert.True(_validator.Validate(null, options).Succeeded);
        Assert.Equal((1, 5, 3, TimeSpan.FromSeconds(10)),
            ((int)options.Concurrency, options.MaxAttempts, options.MaxPushesPerAttempt, options.ChannelCallTimeout));
        Assert.Equal((0.5, 5, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)),
            (options.CircuitBreaker.FailureRatio, options.CircuitBreaker.MinimumThroughput,
                options.CircuitBreaker.SamplingDuration, options.CircuitBreaker.BreakDuration));
    }

    public static TheoryData<string, Action<StockSyncWorkerOptions>> InvalidSettings() => new()
    {
        { "Concurrency", o => o.Concurrency = 0 },
        { "Concurrency", o => o.Concurrency = 17 },
        { "MaxAttempts", o => o.MaxAttempts = 0 },
        { "MaxPushesPerAttempt", o => o.MaxPushesPerAttempt = 0 },
        { "MaxPushesPerAttempt", o => o.MaxPushesPerAttempt = 11 },
        { "ChannelCallTimeout", o => o.ChannelCallTimeout = TimeSpan.Zero },
        { "CircuitBreaker:FailureRatio", o => o.CircuitBreaker.FailureRatio = 0 },
        { "CircuitBreaker:FailureRatio", o => o.CircuitBreaker.FailureRatio = 1.1 },
        { "CircuitBreaker:MinimumThroughput", o => o.CircuitBreaker.MinimumThroughput = 1 },
        { "CircuitBreaker:SamplingDuration", o => o.CircuitBreaker.SamplingDuration = TimeSpan.FromMilliseconds(100) },
        { "CircuitBreaker:BreakDuration", o => o.CircuitBreaker.BreakDuration = TimeSpan.FromMilliseconds(100) },
        { "CircuitBreaker", o => o.CircuitBreaker = null! },
        { "InitialBackoff", o => o.InitialBackoff = TimeSpan.Zero }
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void Values_outside_their_range_stop_the_host_with_the_name_of_the_setting(string setting, Action<StockSyncWorkerOptions> change)
    {
        var options = new StockSyncWorkerOptions();
        change(options);

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.StartsWith($"StockSyncWorker:{setting} ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_limits_of_concurrency_are_1_and_16()
    {
        Assert.True(_validator.Validate(null, new StockSyncWorkerOptions { Concurrency = 16 }).Succeeded);
        Assert.True(_validator.Validate(null, new StockSyncWorkerOptions { Concurrency = 1 }).Succeeded);
    }
}

/// <summary>
/// Task 014: the definitions of the queues that exist since tasks 005/007 are compared with literal values, so a change of
/// an argument (which a running broker would refuse) fails a test instead of stopping the Workers host.
/// </summary>
public sealed class StockSyncTopologyTests
{
    [Fact]
    public void Channels_map_to_the_queues_of_the_architecture_document()
    {
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["website"] = "stock.sync.website",
                ["pos"] = "stock.sync.pos",
                ["digital-platform"] = "stock.sync.marketplace"
            },
            RabbitMqTopology.StockSyncQueues.ToDictionary(entry => entry.Key, entry => entry.Value));

        Assert.Equal("stock.sync.marketplace", RabbitMqTopology.StockSyncQueueOf("digital-platform"));
        Assert.Null(RabbitMqTopology.StockSyncQueueOf("my-new-shop"));
        Assert.Null(RabbitMqTopology.StockSyncQueueOf("Website")); // slugs are lower-case
    }

    [Fact]
    public void Event_bindings_are_unchanged()
    {
        Assert.Equal(
            [
                ("order.processing", "order.created"),
                ("stock.sync.website", "inventory.changed"),
                ("stock.sync.pos", "inventory.changed"),
                ("stock.sync.marketplace", "inventory.changed"),
                ("invoice.processing", "invoice.requested"),
                ("notification.email", "email.requested")
            ],
            RabbitMqTopology.Bindings);
    }

    [Theory]
    [InlineData("order.processing")]
    [InlineData("stock.sync.website")]
    [InlineData("stock.sync.pos")]
    [InlineData("stock.sync.marketplace")]
    [InlineData("invoice.processing")]
    [InlineData("notification.email")]
    public void Main_queue_arguments_equal_the_task_005_definition(string queue)
    {
        var arguments = RabbitMqTopology.MainQueueArguments(queue);

        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = "omnichannel.dlx",
                ["x-dead-letter-routing-key"] = queue + ".dlq",
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish",
                ["x-delivery-limit"] = 10
            },
            arguments);
    }

    [Fact]
    public void Dead_letter_and_delay_queue_arguments_equal_the_task_005_and_007_definitions()
    {
        Assert.Equal(new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, RabbitMqTopology.DeadLetterQueueArguments());

        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-message-ttl"] = 5_000,
                ["x-dead-letter-exchange"] = "omnichannel.retry",
                ["x-dead-letter-strategy"] = "at-least-once",
                ["x-overflow"] = "reject-publish"
            },
            RabbitMqTopology.DelayQueueArguments(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            ["omnichannel.delay.5s", "omnichannel.delay.30s", "omnichannel.delay.2m", "omnichannel.delay.10m"],
            RabbitMqTopology.DefaultRetryDelays.Select(RabbitMqTopology.DelayNameOf));
    }

    [Fact]
    public void Simulation_settings_are_found_whatever_the_case_of_the_slug_and_default_to_success()
    {
        var options = new ChannelSimulationOptions();
        options.Channels["Website"] = new SimulatedChannelSettings { Mode = "Timeout", LatencyMs = 5 };

        Assert.Equal(("Timeout", 5), (options.For("website").Mode, options.For("website").LatencyMs));
        Assert.Equal(("Succeed", 0), (options.For("pos").Mode, options.For("pos").LatencyMs));
        Assert.False(options.SkuRules);
    }
}
