using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Processing;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 014: channel product mapping API, the initial sync it requests (task + directed outbox row), and the additions to
/// the processing API. No Workers host runs here: tasks stay Pending and outbox rows stay unpublished.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ChannelProductEndpointsTests(ApiFactory factory)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly HttpClient _admin = factory.CreateClientAs(AppRoles.Admin);
    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);
    private readonly HttpClient _staff = factory.CreateClientAs(AppRoles.Staff);

    // ---- create -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_stores_the_mapping_and_requests_the_initial_sync_in_one_transaction()
    {
        var website = await ChannelAsync("website");
        var product = await CreateProductAsync();
        var correlationId = $"map-{TestData.UniqueSuffix()}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/channels/{website.ChannelId}/products")
        {
            Content = JsonContent.Create(new CreateChannelProductRequest(product.ProductId, "  EXT-1 ", " WEB-SKU "), options: TestData.Json)
        };
        request.Headers.Add("X-Correlation-Id", correlationId);

        var response = await _manager.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var mapping = await response.ReadAsync<ChannelProductSavedResponse>();
        Assert.Equal($"/api/channels/{website.ChannelId}/products/{mapping.ChannelProductId}", response.Headers.Location?.OriginalString);
        Assert.Equal((website.ChannelId, product.ProductId, product.Sku, product.Name),
            (mapping.ChannelId, mapping.ProductId, mapping.Sku, mapping.ProductName));
        Assert.Equal(("EXT-1", "WEB-SKU", ChannelProductStatus.Active), (mapping.ExternalProductId, mapping.ExternalSku, mapping.Status));
        Assert.NotNull(mapping.SyncTaskId);

        // The task: Pending stock sync of this channel and product, with the request's correlation id.
        var task = await factory.QueryDbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).SingleAsync(t => t.TaskId == mapping.SyncTaskId));
        Assert.Equal((ProcessingTaskType.StockSync, ProcessingTaskStatus.Pending, (Guid?)website.ChannelId, (Guid?)product.ProductId, (Guid?)null),
            (task.TaskType, task.Status, task.ChannelId, task.ProductId, task.OrderId));
        Assert.Equal((correlationId, (string?)null), (task.CorrelationId, task.IdempotencyKey));
        Assert.Empty(task.Attempts);

        // Its message: directed at the queue of the channel, the task id as aggregate, the task's MessageId = the row's id.
        var outbox = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.AggregateId == task.TaskId.ToString()));
        Assert.Equal((EventTypes.StockSyncRequested, "stock.sync.website", OutboxEventStatus.Pending, correlationId, (Guid?)null),
            (outbox.EventType, outbox.RoutingKey, outbox.Status, outbox.CorrelationId, outbox.InventoryMovementId));
        Assert.Equal(outbox.OutboxEventId.ToString(), task.MessageId);
        Assert.Equal(
            new StockSyncRequestedPayload(task.TaskId, website.ChannelId, product.ProductId),
            MessageJson.Deserialize<StockSyncRequestedPayload>(outbox.Payload));
        var envelope = MessageEnvelope.FromOutboxEvent(outbox);
        Assert.Equal((task.TaskId, correlationId), (envelope.TaskId, envelope.CorrelationId));

        // Visible through the processing API, with channel and product.
        var detail = await (await _staff.GetAsync($"/api/processing/tasks/{task.TaskId}")).ReadAsync<ProcessingTaskDetailResponse>();
        Assert.Equal((website.ChannelId, product.ProductId, EventTypes.StockSyncRequested),
            (detail.ChannelId, detail.ProductId, detail.Message!.EventType));
    }

    [Theory]
    [InlineData("website", "stock.sync.website")]
    [InlineData("pos", "stock.sync.pos")]
    [InlineData("digital-platform", "stock.sync.marketplace")]
    public async Task Initial_sync_is_directed_at_the_queue_of_the_channel(string slug, string queue)
    {
        var channel = await ChannelAsync(slug);
        var product = await CreateProductAsync();

        var mapping = await MapAsync(channel.ChannelId, product.ProductId, externalSku: $"X-{TestData.UniqueSuffix()}");

        var outbox = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.AggregateId == mapping.SyncTaskId.ToString()));
        Assert.Equal(queue, outbox.RoutingKey);
    }

    [Fact]
    public async Task Product_already_mapped_on_the_channel_is_a_conflict_and_leaves_no_task_behind()
    {
        var pos = await ChannelAsync("pos");
        var product = await CreateProductAsync();
        await MapAsync(pos.ChannelId, product.ProductId, externalProductId: $"P-{TestData.UniqueSuffix()}");

        var duplicate = await _manager.PostAsJsonAsync(
            $"/api/channels/{pos.ChannelId}/products",
            new CreateChannelProductRequest(product.ProductId, $"P-{TestData.UniqueSuffix()}", "OTHER"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(ChannelProductErrorCodes.MappingExists, await duplicate.ReadErrorCodeAsync());
        // Mapping, task and message are one transaction: the rejected request added none of them.
        Assert.Equal((1, 1, 1), await RowsOfAsync(pos.ChannelId, product.ProductId));

        // The same product can be mapped on another channel.
        var website = await ChannelAsync("website");
        await MapAsync(website.ChannelId, product.ProductId, externalSku: "WEB");
    }

    [Fact]
    public async Task External_product_id_of_another_product_on_the_channel_is_a_conflict()
    {
        var pos = await ChannelAsync("pos");
        var website = await ChannelAsync("website");
        var (first, second) = (await CreateProductAsync(), await CreateProductAsync());
        var externalId = $"EXT-{TestData.UniqueSuffix()}";
        await MapAsync(pos.ChannelId, first.ProductId, externalProductId: externalId);

        var taken = await _manager.PostAsJsonAsync(
            $"/api/channels/{pos.ChannelId}/products", new CreateChannelProductRequest(second.ProductId, externalId, null));

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(ChannelProductErrorCodes.ExternalProductIdTaken, await taken.ReadErrorCodeAsync());
        Assert.Equal((0, 0, 0), await RowsOfAsync(pos.ChannelId, second.ProductId));

        // The id is unique per channel only, and the external SKU is not unique at all.
        await MapAsync(website.ChannelId, second.ProductId, externalProductId: externalId, externalSku: "SAME");
        var third = await CreateProductAsync();
        await MapAsync(website.ChannelId, third.ProductId, externalSku: "SAME");
    }

    [Fact]
    public async Task Create_reports_invalid_input_on_its_fields()
    {
        var website = await ChannelAsync("website");
        var product = await CreateProductAsync();
        var path = $"/api/channels/{website.ChannelId}/products";

        var noExternalId = await _manager.PostAsJsonAsync(path, new CreateChannelProductRequest(product.ProductId, null, " "));
        Assert.Equal(HttpStatusCode.BadRequest, noExternalId.StatusCode);
        Assert.Equal(["externalProductId"], (await ErrorsAsync(noExternalId)).Keys);

        var tooLong = await _manager.PostAsJsonAsync(path, new CreateChannelProductRequest(Guid.Empty, null, new string('x', 129)));
        Assert.Equal(["externalSku", "productId"], (await ErrorsAsync(tooLong)).Keys.Order());

        // A product that does not exist: the same message as in stock import and order intake, on productId.
        var unknownProduct = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new CreateChannelProductRequest(unknownProduct, "EXT", null), options: TestData.Json)
        };
        request.Headers.Add("Accept-Language", "vi");
        var unknown = await _manager.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var errors = await ErrorsAsync(unknown);
        Assert.Equal(["productId"], errors.Keys);
        Assert.Equal([$"Sản phẩm '{unknownProduct}' không tồn tại."], errors["productId"]);

        Assert.Equal((0, 0, 0), await RowsOfAsync(website.ChannelId, product.ProductId));
    }

    [Fact]
    public async Task Missing_external_id_message_follows_the_request_language()
    {
        var website = await ChannelAsync("website");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/channels/{website.ChannelId}/products")
        {
            Content = JsonContent.Create(new CreateChannelProductRequest(Guid.NewGuid(), null, null), options: TestData.Json)
        };
        request.Headers.Add("Accept-Language", "vi");

        var response = await _manager.SendAsync(request);

        Assert.Equal(["Hãy nhập 'Mã sản phẩm trên kênh' hoặc 'SKU trên kênh'."], (await ErrorsAsync(response))["externalProductId"]);
    }

    [Fact]
    public async Task Location_of_a_created_mapping_resolves_to_that_mapping()
    {
        var website = await ChannelAsync("website");
        var pos = await ChannelAsync("pos");
        var product = await CreateProductAsync();

        var created = await _manager.PostAsJsonAsync(
            $"/api/channels/{website.ChannelId}/products", new CreateChannelProductRequest(product.ProductId, "EXT-GET", "WEB-GET"));
        var mapping = await created.ReadAsync<ChannelProductSavedResponse>();

        // The header as the API wrote it, followed by a Staff user.
        Assert.Equal($"/api/channels/{website.ChannelId}/products/{mapping.ChannelProductId}", created.Headers.Location?.OriginalString);
        var response = await _staff.GetAsync(created.Headers.Location);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = await response.ReadAsync<ChannelProductResponse>();
        Assert.Equal(
            new ChannelProductResponse(
                mapping.ChannelProductId, website.ChannelId, product.ProductId, product.Sku, product.Name,
                "EXT-GET", "WEB-GET", ChannelProductStatus.Active, item.CreatedAt, null),
            item);
        // The database keeps microseconds; the 201 body carries the value before it was stored.
        Assert.True((item.CreatedAt - mapping.CreatedAt).Duration() < TimeSpan.FromMilliseconds(1));
        // The same shape as an item of the list.
        Assert.Equal(item, (await ListAsync(website.ChannelId, $"search={product.Sku}")).Items.Single());

        // Unknown mapping, the mapping through another channel, an unknown channel: not found.
        foreach (var path in new[]
                 {
                     $"/api/channels/{website.ChannelId}/products/{Guid.NewGuid()}",
                     $"/api/channels/{pos.ChannelId}/products/{mapping.ChannelProductId}",
                     $"/api/channels/{Guid.NewGuid()}/products/{mapping.ChannelProductId}"
                 })
        {
            var missing = await _staff.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(ChannelProductErrorCodes.MappingNotFound, await missing.ReadErrorCodeAsync());
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(created.Headers.Location)).StatusCode);
    }

    [Fact]
    public async Task Unknown_channel_is_not_found()
    {
        var product = await CreateProductAsync();
        var unknown = Guid.NewGuid();

        var list = await _staff.GetAsync($"/api/channels/{unknown}/products");
        var create = await _manager.PostAsJsonAsync($"/api/channels/{unknown}/products", new CreateChannelProductRequest(product.ProductId, "EXT", null));

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal("not_found", await list.ReadErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        Assert.Equal("not_found", await create.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Staff_reads_mappings_but_cannot_write_them()
    {
        var website = await ChannelAsync("website");
        var product = await CreateProductAsync();
        var mapping = await MapAsync(website.ChannelId, product.ProductId, externalSku: "WEB");

        var list = await _staff.GetAsync($"/api/channels/{website.ChannelId}/products?search={product.Sku}");
        var create = await _staff.PostAsJsonAsync(
            $"/api/channels/{website.ChannelId}/products", new CreateChannelProductRequest(product.ProductId, "EXT", null));
        var update = await _staff.PutAsJsonAsync(
            $"/api/channels/{website.ChannelId}/products/{mapping.ChannelProductId}",
            new UpdateChannelProductRequest("EXT", null, ChannelProductStatus.Inactive), TestData.Json);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/channels/{website.ChannelId}/products")).StatusCode);
    }

    // ---- channels without a sync ------------------------------------------------------------------------------

    [Fact]
    public async Task Channel_created_through_the_api_has_no_stock_sync_queue_mapping_saved_without_a_task()
    {
        var channel = await CreateChannelAsync();
        var product = await CreateProductAsync();

        var mapping = await MapAsync(channel.ChannelId, product.ProductId, externalSku: "SHOP-1", expectSync: false);

        Assert.Null(mapping.SyncTaskId);
        Assert.Equal((1, 0, 0), await RowsOfAsync(channel.ChannelId, product.ProductId));

        // Re-activation on such a channel does not request a sync either.
        await UpdateAsync(channel.ChannelId, mapping.ChannelProductId, new UpdateChannelProductRequest(null, "SHOP-1", ChannelProductStatus.Inactive));
        var active = await UpdateAsync(channel.ChannelId, mapping.ChannelProductId, new UpdateChannelProductRequest(null, "SHOP-1", ChannelProductStatus.Active));
        Assert.Null(active.SyncTaskId);
    }

    [Fact]
    public async Task Inactive_channel_gets_the_mapping_but_no_sync()
    {
        // digital-platform is switched off for this test only (the API collection runs one test at a time).
        var marketplace = await ChannelAsync("digital-platform");
        var product = await CreateProductAsync();
        await SetChannelStatusAsync(marketplace, ChannelStatus.Inactive);
        try
        {
            var mapping = await MapAsync(marketplace.ChannelId, product.ProductId, externalSku: "MP-1", expectSync: false);

            Assert.Null(mapping.SyncTaskId);
            Assert.Equal((1, 0, 0), await RowsOfAsync(marketplace.ChannelId, product.ProductId));

            var changed = await UpdateAsync(
                marketplace.ChannelId, mapping.ChannelProductId, new UpdateChannelProductRequest("MP-EXT", "MP-2", ChannelProductStatus.Active));
            Assert.Null(changed.SyncTaskId);
        }
        finally
        {
            await SetChannelStatusAsync(marketplace, ChannelStatus.Active);
        }
    }

    // ---- update -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_requests_a_sync_for_changed_ids_and_re_activation_but_not_for_deactivation()
    {
        var pos = await ChannelAsync("pos");
        var product = await CreateProductAsync();
        var created = await MapAsync(pos.ChannelId, product.ProductId, externalProductId: $"E-{TestData.UniqueSuffix()}", externalSku: "POS-1");
        var path = (pos.ChannelId, created.ChannelProductId);

        // Nothing changed: no sync.
        var same = await UpdateAsync(path.ChannelId, path.ChannelProductId, new UpdateChannelProductRequest(created.ExternalProductId, " POS-1 ", ChannelProductStatus.Active));
        Assert.Null(same.SyncTaskId);

        // External SKU changed while Active: sync.
        var renamed = await UpdateAsync(path.ChannelId, path.ChannelProductId, new UpdateChannelProductRequest(created.ExternalProductId, "POS-2", ChannelProductStatus.Active));
        Assert.NotNull(renamed.SyncTaskId);
        Assert.Equal(("POS-2", ChannelProductStatus.Active), (renamed.ExternalSku, renamed.Status));
        Assert.NotNull(renamed.UpdatedAt);

        // Deactivation: nothing is sent to the channel.
        var inactive = await UpdateAsync(path.ChannelId, path.ChannelProductId, new UpdateChannelProductRequest(created.ExternalProductId, "POS-2", ChannelProductStatus.Inactive));
        Assert.Null(inactive.SyncTaskId);
        Assert.Equal(ChannelProductStatus.Inactive, inactive.Status);

        // A change while Inactive: stored, not synced.
        var changedWhileInactive = await UpdateAsync(path.ChannelId, path.ChannelProductId, new UpdateChannelProductRequest(null, "POS-3", ChannelProductStatus.Inactive));
        Assert.Null(changedWhileInactive.SyncTaskId);
        Assert.Equal(((string?)null, "POS-3"), (changedWhileInactive.ExternalProductId, changedWhileInactive.ExternalSku));

        // Re-activation: sync.
        var active = await UpdateAsync(path.ChannelId, path.ChannelProductId, new UpdateChannelProductRequest(null, "POS-3", ChannelProductStatus.Active));
        Assert.NotNull(active.SyncTaskId);
        Assert.NotEqual(renamed.SyncTaskId, active.SyncTaskId);

        // Three tasks in all (create, rename, re-activation), each with its own directed message; product and channel never changed.
        Assert.Equal((1, 3, 3), await RowsOfAsync(pos.ChannelId, product.ProductId));
        Assert.Equal((created.ChannelProductId, pos.ChannelId, product.ProductId, product.Sku),
            (active.ChannelProductId, active.ChannelId, active.ProductId, active.Sku));
        var messages = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking()
            .Where(e => e.EventType == EventTypes.StockSyncRequested && e.AggregateId == active.SyncTaskId.ToString())
            .ToListAsync());
        Assert.Equal("stock.sync.pos", Assert.Single(messages).RoutingKey);
    }

    [Fact]
    public async Task Update_rejects_an_external_product_id_of_another_product_and_keeps_the_mapping()
    {
        var pos = await ChannelAsync("pos");
        var (first, second) = (await CreateProductAsync(), await CreateProductAsync());
        var taken = $"EXT-{TestData.UniqueSuffix()}";
        await MapAsync(pos.ChannelId, first.ProductId, externalProductId: taken);
        var mapping = await MapAsync(pos.ChannelId, second.ProductId, externalSku: "MINE");

        var response = await _manager.PutAsJsonAsync(
            $"/api/channels/{pos.ChannelId}/products/{mapping.ChannelProductId}",
            new UpdateChannelProductRequest(taken, "MINE", ChannelProductStatus.Active), TestData.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ChannelProductErrorCodes.ExternalProductIdTaken, await response.ReadErrorCodeAsync());
        var stored = await factory.QueryDbAsync(db => db.ChannelProducts.AsNoTracking().SingleAsync(m => m.ChannelProductId == mapping.ChannelProductId));
        Assert.Equal(((string?)null, "MINE"), (stored.ExternalProductId, stored.ExternalSku));
        // The sync that the change would have requested was rolled back with it.
        Assert.Equal((1, 1, 1), await RowsOfAsync(pos.ChannelId, second.ProductId));
    }

    [Fact]
    public async Task Update_of_an_unknown_mapping_or_through_another_channel_is_not_found()
    {
        var pos = await ChannelAsync("pos");
        var website = await ChannelAsync("website");
        var product = await CreateProductAsync();
        var mapping = await MapAsync(pos.ChannelId, product.ProductId, externalSku: "POS");
        var body = new UpdateChannelProductRequest(null, "CHANGED", ChannelProductStatus.Active);

        var unknown = await _manager.PutAsJsonAsync($"/api/channels/{pos.ChannelId}/products/{Guid.NewGuid()}", body, TestData.Json);
        var otherChannel = await _manager.PutAsJsonAsync($"/api/channels/{website.ChannelId}/products/{mapping.ChannelProductId}", body, TestData.Json);
        var unknownChannel = await _manager.PutAsJsonAsync($"/api/channels/{Guid.NewGuid()}/products/{mapping.ChannelProductId}", body, TestData.Json);

        foreach (var response in new[] { unknown, otherChannel, unknownChannel })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(ChannelProductErrorCodes.MappingNotFound, await response.ReadErrorCodeAsync());
        }

        var stored = await factory.QueryDbAsync(db => db.ChannelProducts.AsNoTracking().SingleAsync(m => m.ChannelProductId == mapping.ChannelProductId));
        Assert.Equal("POS", stored.ExternalSku);
    }

    [Fact]
    public async Task Update_reports_invalid_input_on_its_fields()
    {
        var pos = await ChannelAsync("pos");
        var product = await CreateProductAsync();
        var mapping = await MapAsync(pos.ChannelId, product.ProductId, externalSku: "POS");
        var path = $"/api/channels/{pos.ChannelId}/products/{mapping.ChannelProductId}";

        var noId = await _manager.PutAsJsonAsync(path, new { externalProductId = "", externalSku = (string?)null, status = "Active" });
        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);
        Assert.Equal(["externalProductId"], (await ErrorsAsync(noId)).Keys);

        var badStatus = await _manager.PutAsJsonAsync(path, new { externalSku = "POS", status = 99 });
        Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        Assert.Equal(["status"], (await ErrorsAsync(badStatus)).Keys);
    }

    // ---- list -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_pages_searches_and_sorts_the_mappings_of_one_channel()
    {
        var channel = await CreateChannelAsync();
        var other = await CreateChannelAsync();
        var tag = TestData.UniqueSuffix().ToUpperInvariant();
        var a = await CreateProductAsync($"A-{tag}", $"Alpha {tag}");
        var b = await CreateProductAsync($"B-{tag}", $"Beta {tag}");
        var c = await CreateProductAsync($"C-{tag}", $"Gamma {tag}");
        var mapA = await MapAsync(channel.ChannelId, a.ProductId, externalProductId: $"ext-one-{tag}", expectSync: false);
        var mapB = await MapAsync(channel.ChannelId, b.ProductId, externalSku: $"shop-two-{tag}", expectSync: false);
        var mapC = await MapAsync(channel.ChannelId, c.ProductId, externalProductId: $"ext-three-{tag}", externalSku: "100%_real", expectSync: false);
        await MapAsync(other.ChannelId, a.ProductId, externalSku: "elsewhere", expectSync: false);

        var all = await ListAsync(channel.ChannelId, string.Empty);
        Assert.Equal(3, all.TotalCount);
        Assert.Equal([mapA.ChannelProductId, mapB.ChannelProductId, mapC.ChannelProductId], all.Items.Select(m => m.ChannelProductId));
        var first = all.Items[0];
        Assert.Equal((channel.ChannelId, a.ProductId, a.Sku, a.Name, $"ext-one-{tag}", (string?)null, ChannelProductStatus.Active),
            (first.ChannelId, first.ProductId, first.Sku, first.ProductName, first.ExternalProductId, first.ExternalSku, first.Status));

        // Default sort: SKU ascending; descending and createdAt on request.
        Assert.Equal([c.Sku, b.Sku, a.Sku], (await ListAsync(channel.ChannelId, "sortDir=desc")).Items.Select(m => m.Sku));
        Assert.Equal([mapC.ChannelProductId, mapB.ChannelProductId, mapA.ChannelProductId],
            (await ListAsync(channel.ChannelId, "sortBy=createdAt&sortDir=desc")).Items.Select(m => m.ChannelProductId));

        // Paging.
        var page2 = await ListAsync(channel.ChannelId, "page=2&pageSize=2");
        Assert.Equal((3, 2, 2), (page2.TotalCount, page2.Page, page2.PageSize));
        Assert.Equal([mapC.ChannelProductId], page2.Items.Select(m => m.ChannelProductId));

        // Search: SKU, product name, external product id, external SKU; LIKE wildcards are literal.
        Assert.Equal([mapB.ChannelProductId], (await ListAsync(channel.ChannelId, $"search=b-{tag.ToLowerInvariant()}")).Items.Select(m => m.ChannelProductId));
        Assert.Equal([mapC.ChannelProductId], (await ListAsync(channel.ChannelId, "search=gamma")).Items.Select(m => m.ChannelProductId));
        Assert.Equal([mapA.ChannelProductId], (await ListAsync(channel.ChannelId, "search=EXT-ONE")).Items.Select(m => m.ChannelProductId));
        Assert.Equal([mapB.ChannelProductId], (await ListAsync(channel.ChannelId, "search=shop-two")).Items.Select(m => m.ChannelProductId));
        Assert.Equal([mapC.ChannelProductId], (await ListAsync(channel.ChannelId, $"search={Uri.EscapeDataString("0%_r")}")).Items.Select(m => m.ChannelProductId));
        Assert.Empty((await ListAsync(channel.ChannelId, "search=elsewhere")).Items);

        var invalid = await _staff.GetAsync($"/api/channels/{channel.ChannelId}/products?sortBy=productName");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(["sortBy"], (await ErrorsAsync(invalid)).Keys);
    }

    // ---- processing API ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Task_list_filters_by_channel_and_shows_channel_and_product()
    {
        var (website, pos) = (await ChannelAsync("website"), await ChannelAsync("pos"));
        var product = await CreateProductAsync();
        var correlationId = $"filter-{TestData.UniqueSuffix()}";
        var onWebsite = ProcessingTask.ForStockSync(website.ChannelId, product.ProductId, correlationId, null);
        var onPos = ProcessingTask.ForStockSync(pos.ChannelId, product.ProductId, correlationId, null);
        var orderTask = new ProcessingTask(ProcessingTaskType.OrderProcessing, null, correlationId, null);
        await factory.QueryDbAsync(async db =>
        {
            db.ProcessingTasks.AddRange(onWebsite, onPos, orderTask);
            return await db.SaveChangesAsync();
        });

        var all = await TasksAsync($"correlationId={correlationId}");
        var website1 = await TasksAsync($"correlationId={correlationId}&channelId={website.ChannelId}");
        var stockSync = await TasksAsync($"correlationId={correlationId}&taskType=StockSync");

        Assert.Equal(3, all.TotalCount);
        var item = Assert.Single(website1.Items);
        Assert.Equal((onWebsite.TaskId, ProcessingTaskType.StockSync, (Guid?)website.ChannelId, (Guid?)product.ProductId, (Guid?)null),
            (item.TaskId, item.TaskType, item.ChannelId, item.ProductId, item.OrderId));
        Assert.Equal(new[] { onPos.TaskId, onWebsite.TaskId }.Order(), stockSync.Items.Select(t => t.TaskId).Order());
        var order = all.Items.Single(t => t.TaskId == orderTask.TaskId);
        Assert.Equal(((Guid?)null, (Guid?)null), (order.ChannelId, order.ProductId));

        var badFilter = await _staff.GetAsync("/api/processing/tasks?channelId=not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
        Assert.Equal(["channelId"], (await ErrorsAsync(badFilter)).Keys);
    }

    [Fact]
    public async Task Manual_retry_of_a_stock_sync_task_writes_a_directed_message_instead_of_a_copy_of_the_fanned_out_event()
    {
        var pos = await ChannelAsync("pos");
        var product = await CreateProductAsync();
        var correlationId = $"retry-{TestData.UniqueSuffix()}";
        // The task came from an inventory.changed event (which every channel receives) and failed.
        var source = new OutboxEvent(EventTypes.InventoryChanged, product.ProductId.ToString(), """{"seeded":true}""", correlationId, null);
        var task = ProcessingTask.ForStockSync(
            pos.ChannelId, product.ProductId, correlationId, source.OutboxEventId.ToString(),
            StockSyncIdempotencyKey.For(pos.ChannelId, source.OutboxEventId.ToString()));
        task.Fail(task.StartAttempt("StockSyncWorker", "inst-1", T0).AttemptId, ChannelApiErrorCodes.Rejected, "rejected (seeded)", T0);
        await factory.QueryDbAsync(async db =>
        {
            db.OutboxEvents.Add(source);
            db.ProcessingTasks.Add(task);
            return await db.SaveChangesAsync();
        });

        var staffAttempt = await _staff.PostAsync($"/api/processing/tasks/{task.TaskId}/retry", null);
        var response = await _manager.PostAsync($"/api/processing/tasks/{task.TaskId}/retry", null);

        Assert.Equal(HttpStatusCode.Forbidden, staffAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var retry = await response.ReadAsync<RetryProcessingTaskResponse>();
        Assert.Equal((task.TaskId, ProcessingTaskStatus.Pending), (retry.TaskId, retry.Status));

        var message = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.OutboxEventId == Guid.Parse(retry.MessageId)));
        Assert.Equal((EventTypes.StockSyncRequested, "stock.sync.pos", task.TaskId.ToString(), correlationId, OutboxEventStatus.Pending),
            (message.EventType, message.RoutingKey, message.AggregateId, message.CorrelationId, message.Status));
        Assert.Equal(
            new StockSyncRequestedPayload(task.TaskId, pos.ChannelId, product.ProductId),
            MessageJson.Deserialize<StockSyncRequestedPayload>(message.Payload));
        // No second inventory.changed: the other channels are not involved.
        Assert.Equal(1, await factory.QueryDbAsync(db => db.OutboxEvents.CountAsync(e => e.EventType == EventTypes.InventoryChanged && e.CorrelationId == correlationId)));

        var stored = await factory.QueryDbAsync(db => db.ProcessingTasks.AsNoTracking().Include(t => t.Attempts).SingleAsync(t => t.TaskId == task.TaskId));
        Assert.Equal((ProcessingTaskStatus.Pending, retry.MessageId, 1, 1), (stored.Status, stored.MessageId, stored.AttemptBudgetStart, stored.Attempts.Count));
        var audit = await factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .SingleAsync(l => l.EventType == ProcessingEvents.RetryRequested && l.TaskId == task.TaskId));
        using var payload = JsonDocument.Parse(audit.Payload!);
        Assert.Equal(("StockSync", source.OutboxEventId.ToString(), retry.MessageId),
            (payload.RootElement.GetProperty("taskType").GetString(),
                payload.RootElement.GetProperty("previousMessageId").GetString(),
                payload.RootElement.GetProperty("newMessageId").GetString()));

        // Pending now: a second retry is refused.
        var again = await _manager.PostAsync($"/api/processing/tasks/{task.TaskId}/retry", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(ProcessingErrorCodes.TaskNotFailed, await again.ReadErrorCodeAsync());
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private async Task<ChannelResponse> ChannelAsync(string slug) =>
        (await _staff.GetFromJsonAsync<List<ChannelResponse>>("/api/channels", TestData.Json))!.Single(c => c.Slug == slug);

    private async Task<ChannelResponse> CreateChannelAsync()
    {
        var response = await _admin.PostAsJsonAsync(
            "/api/channels",
            new CreateChannelRequest("Mapping test shop", $"map-{TestData.UniqueSuffix()}", ChannelType.Website), TestData.Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<ChannelResponse>();
    }

    private async Task SetChannelStatusAsync(ChannelResponse channel, ChannelStatus status)
    {
        var response = await _admin.PutAsJsonAsync($"/api/channels/{channel.ChannelId}", new UpdateChannelRequest(channel.Name, status), TestData.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<ProductResponse> CreateProductAsync(string? sku = null, string? name = null)
    {
        var response = await _manager.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest(sku ?? $"MAP-{TestData.UniqueSuffix()}".ToUpperInvariant(), name ?? "Mapping product", 1m, 0, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<ProductResponse>();
    }

    private async Task<ChannelProductSavedResponse> MapAsync(
        Guid channelId,
        Guid productId,
        string? externalProductId = null,
        string? externalSku = null,
        bool expectSync = true)
    {
        var response = await _manager.PostAsJsonAsync(
            $"/api/channels/{channelId}/products", new CreateChannelProductRequest(productId, externalProductId, externalSku));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var mapping = await response.ReadAsync<ChannelProductSavedResponse>();
        Assert.Equal(expectSync, mapping.SyncTaskId is not null);
        return mapping;
    }

    private async Task<ChannelProductSavedResponse> UpdateAsync(Guid channelId, Guid channelProductId, UpdateChannelProductRequest request)
    {
        var response = await _manager.PutAsJsonAsync($"/api/channels/{channelId}/products/{channelProductId}", request, TestData.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<ChannelProductSavedResponse>();
    }

    private async Task<PagedResult<ChannelProductResponse>> ListAsync(Guid channelId, string query)
    {
        var response = await _staff.GetAsync($"/api/channels/{channelId}/products?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<PagedResult<ChannelProductResponse>>();
    }

    private async Task<PagedResult<ProcessingTaskSummaryResponse>> TasksAsync(string query)
    {
        var response = await _staff.GetAsync($"/api/processing/tasks?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<PagedResult<ProcessingTaskSummaryResponse>>();
    }

    /// <summary>Mappings, stock-sync tasks and directed messages of one product on one channel.</summary>
    private Task<(int Mappings, int Tasks, int Messages)> RowsOfAsync(Guid channelId, Guid productId) =>
        factory.QueryDbAsync(async db =>
        {
            var mappings = await db.ChannelProducts.CountAsync(m => m.ChannelId == channelId && m.ProductId == productId);
            var taskIds = (await db.ProcessingTasks
                    .Where(t => t.ChannelId == channelId && t.ProductId == productId)
                    .Select(t => t.TaskId)
                    .ToListAsync())
                .Select(id => id.ToString())
                .ToList();
            var messages = await db.OutboxEvents.CountAsync(e => e.EventType == EventTypes.StockSyncRequested && taskIds.Contains(e.AggregateId));
            return (mappings, taskIds.Count, messages);
        });

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("errors").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToArray());
    }
}
