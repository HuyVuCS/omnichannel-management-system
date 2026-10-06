using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 023 Part C: what a signed webhook does with its order - validation, items through the channel's mappings, the
/// customer through the channel's customer mapping, and the order intake it ends in.
/// </summary>
public sealed partial class WebhookEndpointsTests
{
    // ---- normalised and handed to the intake ------------------------------------------------------------------

    [Fact]
    public async Task An_order_in_the_channel_s_words_becomes_a_task_and_a_message_of_the_common_order()
    {
        var byId = await MappedProductAsync(Website, externalProductId: $"WEB-ID-{_tag}", externalSku: $"LABEL-{_tag}", price: 12.5m);
        var bySku = await MappedProductAsync(Website, externalSku: $"WEB-SKU-{_tag}", price: 3m);
        var (orderId, customerId) = ($"WEB-ORDER-{_tag}", $"WEB-CUSTOMER-{_tag}");
        var correlationId = $"hook-{_tag}";
        // Ids with spaces around them are the same ids (they are stored trimmed).
        var body = WebhookTesting.Order(
            $"  {orderId} ",
            WebhookTesting.Customer($" {customerId}  ", "  Tran Thi B ", " 0901 234 567 ", $"b.{_tag}@example.com"),
            WebhookTesting.ById($" WEB-ID-{_tag} ", 2),
            WebhookTesting.BySku($"WEB-SKU-{_tag}", 5));

        var response = await _sender.SendAsync(WebhookTesting.Request(Website, body, ApiFactory.WebsiteWebhookSecret, correlationId: correlationId));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.ReadAsync<SubmitOrderResponse>();
        Assert.Equal((ProcessingTaskStatus.Pending, correlationId, false), (accepted.Status, accepted.CorrelationId, accepted.Duplicate));
        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-Id").Single());

        // The customer the channel named: created with what the webhook carried, by the webhook of that channel.
        var website = await ChannelAsync(Website);
        var (mapping, customer) = await CustomerOfAsync(Website, customerId);
        Assert.Equal((website.ChannelId, customerId, "webhook:website"), (mapping.ChannelId, mapping.ExternalCustomerId, mapping.CreatedBy));
        Assert.Equal(
            ("Tran Thi B", "0901 234 567", $"b.{_tag}@example.com", (string?)null, "webhook:website"),
            (customer.Name, customer.Phone, customer.Email, customer.Address, customer.CreatedBy));

        // The task of the intake, unchanged: its key, its hash, and the caller's correlation id.
        var task = Assert.Single(await TasksAsync(Website, orderId));
        Assert.Equal(
            (accepted.TaskId, ProcessingTaskType.OrderProcessing, ProcessingTaskStatus.Pending, correlationId, (Guid?)null),
            (task.TaskId, task.TaskType, task.Status, task.CorrelationId, task.OrderId));
        Assert.Equal(OrderIdempotencyKey.For(website.ChannelId, orderId), task.IdempotencyKey);
        Assert.Equal(
            OrderRequestHash.Compute(customer.CustomerId, [new SubmitOrderItem(byId.ProductId, 2), new SubmitOrderItem(bySku.ProductId, 5)]),
            task.RequestHash);

        // The message carries the common order: our product ids, the products' prices, our customer - and who received it.
        var message = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.AggregateId == task.TaskId.ToString()));
        Assert.Equal((EventTypes.OrderCreated, correlationId, task.MessageId), (message.EventType, message.CorrelationId, message.OutboxEventId.ToString()));
        var payload = MessageJson.Deserialize<OrderCreatedPayload>(message.Payload);
        Assert.Equal(
            (task.TaskId, website.ChannelId, Website, orderId, customer.CustomerId, "webhook:website"),
            (payload.TaskId, payload.ChannelId, payload.ChannelSlug, payload.ExternalOrderId, payload.CustomerId, payload.ReceivedBy));
        Assert.Equal(
            [(byId.ProductId, 2, 12.5m), (bySku.ProductId, 5, 3m)],
            payload.Items.Select(i => (i.ProductId, i.Stock, i.UnitPrice)));
        // The order itself is the Order Worker's to create.
        Assert.Equal(0, await factory.QueryDbAsync(db => db.Orders.CountAsync(o => o.ExternalOrderId == orderId)));
    }

    [Fact]
    public async Task The_same_webhook_again_is_a_duplicate_and_other_content_under_its_order_id_a_conflict()
    {
        var order = await NewOrderAsync(Website, quantity: 2);
        var first = await (await _sender.SendAsync(WebhookTesting.Request(Website, order.Body, ApiFactory.WebsiteWebhookSecret))).ReadAsync<SubmitOrderResponse>();

        // Sent again later (a new timestamp, a new signature), and written another way: the same order.
        var again = await _sender.SendAsync(WebhookTesting.Request(Website, order.Body, ApiFactory.WebsiteWebhookSecret, WebhookTesting.Now(5)));
        var rewritten = WebhookTesting.Order(
            order.ExternalOrderId,
            WebhookTesting.Customer(order.ExternalCustomerId, "Another name", "0999 999 999"),
            WebhookTesting.ById(order.ExternalProductId, 2));
        var otherWords = await _sender.SendAsync(WebhookTesting.Request(Website, rewritten, ApiFactory.WebsiteWebhookSecret));

        foreach (var response in new[] { again, otherWords })
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var duplicate = await response.ReadAsync<SubmitOrderResponse>();
            Assert.Equal((first.TaskId, true, first.CorrelationId), (duplicate.TaskId, duplicate.Duplicate, duplicate.CorrelationId));
        }

        // Another quantity, another product or another customer under the same order id: 409, nothing new.
        var other = await MappedProductAsync(Website, externalSku: $"OTHER-{_tag}");
        byte[][] changed =
        [
            WebhookTesting.Order(order.ExternalOrderId, WebhookTesting.Customer(order.ExternalCustomerId), WebhookTesting.ById(order.ExternalProductId, 3)),
            WebhookTesting.Order(order.ExternalOrderId, WebhookTesting.Customer(order.ExternalCustomerId), WebhookTesting.BySku($"OTHER-{_tag}", 2)),
            WebhookTesting.Order(order.ExternalOrderId, WebhookTesting.Customer($"SOMEBODY-ELSE-{_tag}"), WebhookTesting.ById(order.ExternalProductId, 2))
        ];
        foreach (var body in changed)
        {
            var conflict = await _sender.SendAsync(WebhookTesting.Request(Website, body, ApiFactory.WebsiteWebhookSecret));

            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal(OrderErrorCodes.OrderConflict, await conflict.ReadErrorCodeAsync());
        }

        Assert.Equal(first.TaskId, Assert.Single(await TasksAsync(order)).TaskId);
        Assert.Equal(1, await factory.QueryDbAsync(db => db.OutboxEvents.CountAsync(e => e.AggregateId == first.TaskId.ToString())));
        Assert.NotEqual(Guid.Empty, other.ProductId);
        // The customer of the refused order was real all the same: created before the intake said no, and kept.
        var kept = await CustomerOfAsync(Website, $"SOMEBODY-ELSE-{_tag}");
        Assert.Equal("webhook:website", kept.Customer.CreatedBy);

        // The same order id on another channel is another order.
        var onPos = await MappedProductAsync(Pos, externalProductId: order.ExternalProductId);
        var posOrder = await _sender.SendAsync(WebhookTesting.Request(Pos, order.Body, ApiFactory.PosWebhookSecret));
        var posAccepted = await posOrder.ReadAsync<SubmitOrderResponse>();
        Assert.Equal((HttpStatusCode.Accepted, false), (posOrder.StatusCode, posAccepted.Duplicate));
        Assert.NotEqual(first.TaskId, posAccepted.TaskId);
        Assert.Equal(onPos.ProductId, (await PayloadAsync(posAccepted.TaskId)).Items.Single().ProductId);
    }

    // ---- validation -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public async Task An_invalid_order_is_reported_under_the_json_paths_of_its_fields_in_the_caller_s_language(string language)
    {
        var body = Encoding.UTF8.GetBytes($$"""
            {
              "externalOrderId": "{{new string('x', 129)}}",
              "customer": { "externalCustomerId": "  ", "name": "", "phone": "090x 123", "email": "not-an-email" },
              "items": [
                { "externalProductId": "A", "externalSku": "B", "quantity": 1 },
                { "quantity": 2 },
                { "externalSku": "{{new string('s', 129)}}", "quantity": 0 },
                { "externalProductId": "OK", "quantity": 10001 }
              ]
            }
            """);

        var response = await _sender.SendAsync(WebhookTesting.Request(Website, body, ApiFactory.WebsiteWebhookSecret, language: language));

        var errors = await response.ErrorsAsync();
        Assert.Equal(language, Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.Equal(
            new[]
            {
                "customer.email", "customer.externalCustomerId", "customer.name", "customer.phone", "externalOrderId",
                "items[0].externalProductId", "items[1].externalProductId", "items[2].externalSku", "items[2].quantity", "items[3].quantity"
            },
            errors.Keys.Order(StringComparer.Ordinal));

        // The texts are the application's, in the language asked for, with the fields named as the UI names them.
        var vietnamese = language == "vi";
        Assert.Equal(
            vietnamese
                ? "Hãy nhập đúng một trong hai: 'Mã sản phẩm trên kênh' hoặc 'SKU trên kênh'."
                : "Give exactly one of 'External product ID' and 'External SKU'.",
            Assert.Single(errors["items[0].externalProductId"]));
        Assert.Equal(errors["items[0].externalProductId"], errors["items[1].externalProductId"]);
        Assert.Equal(
            vietnamese ? "'Mã đơn hàng bên ngoài' chỉ được dài tối đa 128 ký tự." : "'External order ID' must be at most 128 characters.",
            Assert.Single(errors["externalOrderId"]));
        Assert.Contains(vietnamese ? "Mã khách hàng trên kênh" : "External customer ID", Assert.Single(errors["customer.externalCustomerId"]));
        Assert.Contains(vietnamese ? "Số lượng" : "Quantity", Assert.Single(errors["items[2].quantity"]));
        Assert.StartsWith(vietnamese ? "'Điện thoại'" : "'Phone'", Assert.Single(errors["customer.phone"]));
        Assert.Equal(0, await factory.QueryDbAsync(db => db.ProcessingTasks.CountAsync(t => t.IdempotencyKey!.EndsWith(new string('x', 129)))));
    }

    [Fact]
    public async Task An_order_without_lines_with_too_many_lines_or_with_a_product_named_twice_is_reported_on_items()
    {
        var customer = WebhookTesting.Customer($"CUST-{_tag}");
        byte[][] bodies =
        [
            WebhookTesting.Order($"ORD-{_tag}", customer),
            WebhookTesting.Order($"ORD-{_tag}", customer, [.. Enumerable.Range(0, 101).Select(n => WebhookTesting.ById($"P-{n}", 1))]),
            WebhookTesting.Order($"ORD-{_tag}", customer, WebhookTesting.ById("SAME", 1), WebhookTesting.ById(" SAME ", 2)),
            Encoding.UTF8.GetBytes($$$"""{"externalOrderId":"ORD-{{{_tag}}}","customer":{"externalCustomerId":"C","name":"N"},"items":[null]}"""),
            Encoding.UTF8.GetBytes($$$"""{"externalOrderId":"ORD-{{{_tag}}}","customer":{"externalCustomerId":"C","name":"N"}}""")
        ];

        foreach (var body in bodies)
        {
            var response = await _sender.SendAsync(WebhookTesting.Request(Website, body, ApiFactory.WebsiteWebhookSecret));

            Assert.Equal(["items"], (await response.ErrorsAsync()).Keys);
        }

        // Missing altogether: the three parts of an order.
        var empty = await _sender.SendAsync(WebhookTesting.Request(Website, "{}"u8.ToArray(), ApiFactory.WebsiteWebhookSecret));
        Assert.Equal(["customer", "externalOrderId", "items"], (await empty.ErrorsAsync()).Keys.Order(StringComparer.Ordinal));
        // A value of the wrong JSON type is the parser's error, with its path.
        var wrongType = Encoding.UTF8.GetBytes($$$"""{"externalOrderId":"ORD-{{{_tag}}}","customer":{"externalCustomerId":"C","name":"N"},"items":[{"externalSku":"S","quantity":"two"}]}""");
        var parsed = await _sender.SendAsync(WebhookTesting.Request(Website, wrongType, ApiFactory.WebsiteWebhookSecret));
        Assert.Contains((await parsed.ErrorsAsync()).Keys, key => key.StartsWith("$.items[0].quantity", StringComparison.Ordinal));

        Assert.Empty(await TasksAsync(Website, $"ORD-{_tag}"));
        Assert.Equal(0, await factory.QueryDbAsync(db => db.ChannelCustomers.CountAsync(m => m.ExternalCustomerId == $"CUST-{_tag}")));
    }

    // ---- items ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en", "This item is not sold on this channel.")]
    [InlineData("vi", "Mặt hàng này không được bán trên kênh này.")]
    public async Task A_line_that_names_no_active_mapping_of_the_channel_is_not_sold_there(string language, string message)
    {
        // Sold on the website: one by its id, one by its SKU.
        var sold = await MappedProductAsync(Website, externalProductId: $"SOLD-ID-{_tag}", externalSku: $"LABEL-{_tag}");
        await MappedProductAsync(Website, externalSku: $"SOLD-SKU-{_tag}");
        // Not sold there: a mapping that is switched off (by id and by SKU), a mapping of another channel, and ids
        // nobody has.
        var offById = await ProductAsync();
        var offByIdMapping = await MapAsync(Website, offById.ProductId, $"OFF-ID-{_tag}", null);
        var offBySku = await ProductAsync();
        var offBySkuMapping = await MapAsync(Website, offBySku.ProductId, null, $"OFF-SKU-{_tag}");
        foreach (var mapping in new[] { offByIdMapping, offBySkuMapping })
        {
            var off = await _manager.PutAsJsonAsync(
                $"/api/channels/{mapping.ChannelId}/products/{mapping.ChannelProductId}",
                new UpdateChannelProductRequest(mapping.ExternalProductId, mapping.ExternalSku, ChannelProductStatus.Inactive), TestData.Json);
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        }

        await MappedProductAsync(Pos, externalProductId: $"POS-ONLY-ID-{_tag}");
        await MappedProductAsync(Pos, externalSku: $"POS-ONLY-SKU-{_tag}");

        var customer = $"CUST-ITEMS-{_tag}";
        var body = WebhookTesting.Order(
            $"ORD-ITEMS-{_tag}",
            WebhookTesting.Customer(customer),
            WebhookTesting.ById($"SOLD-ID-{_tag}", 1),
            WebhookTesting.BySku($"SOLD-SKU-{_tag}", 1),
            WebhookTesting.ById($"OFF-ID-{_tag}", 1),
            WebhookTesting.BySku($"OFF-SKU-{_tag}", 1),
            WebhookTesting.ById($"POS-ONLY-ID-{_tag}", 1),
            WebhookTesting.BySku($"POS-ONLY-SKU-{_tag}", 1),
            WebhookTesting.ById($"NOBODY-{_tag}", 1),
            // The SKU of a mapping that has an external product id: the channel knows that product by its id only.
            WebhookTesting.BySku($"LABEL-{_tag}", 1),
            // An id is not a SKU and a SKU is not an id, and they are compared exactly as stored.
            WebhookTesting.BySku($"SOLD-ID-{_tag}", 1),
            WebhookTesting.ById($"SOLD-SKU-{_tag}", 1),
            WebhookTesting.ById($"sold-id-{_tag}", 1));

        var response = await _sender.SendAsync(WebhookTesting.Request(Website, body, ApiFactory.WebsiteWebhookSecret, language: language));

        var errors = await response.ErrorsAsync();
        Assert.Equal(
            new[]
            {
                "items[10].externalProductId", "items[2].externalProductId", "items[3].externalSku", "items[4].externalProductId",
                "items[5].externalSku", "items[6].externalProductId", "items[7].externalSku", "items[8].externalSku", "items[9].externalProductId"
            },
            errors.Keys.Order(StringComparer.Ordinal));
        Assert.All(errors.Values, messages => Assert.Equal([message], messages));

        // All or nothing: no task for the two lines that are sold, and no customer.
        Assert.Empty(await TasksAsync(Website, $"ORD-ITEMS-{_tag}"));
        Assert.Equal(0, await factory.QueryDbAsync(db => db.ChannelCustomers.CountAsync(m => m.ExternalCustomerId == customer)));

        // The mapping that is switched off is sold again once it is switched on: the rule is the mapping's status.
        var on = await _manager.PutAsJsonAsync(
            $"/api/channels/{offByIdMapping.ChannelId}/products/{offByIdMapping.ChannelProductId}",
            new UpdateChannelProductRequest(offByIdMapping.ExternalProductId, null, ChannelProductStatus.Active), TestData.Json);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var accepted = await _sender.SendAsync(WebhookTesting.Request(
            Website,
            WebhookTesting.Order($"ORD-ITEMS-{_tag}", WebhookTesting.Customer(customer), WebhookTesting.ById($"OFF-ID-{_tag}", 4), WebhookTesting.ById($"SOLD-ID-{_tag}", 1)),
            ApiFactory.WebsiteWebhookSecret));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(
            [(offById.ProductId, 4), (sold.ProductId, 1)],
            (await PayloadAsync((await accepted.ReadAsync<SubmitOrderResponse>()).TaskId)).Items.Select(i => (i.ProductId, i.Stock)));
    }

    // ---- the customer -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_customer_is_created_when_the_channel_names_them_first_and_never_changed_by_a_later_webhook()
    {
        var product = await MappedProductAsync(Website, externalProductId: $"CUSTOMER-TEST-{_tag}");
        var externalCustomerId = $"LOYAL-{_tag}";
        byte[] OrderOf(string orderId, object customer) =>
            WebhookTesting.Order(orderId, customer, WebhookTesting.ById($"CUSTOMER-TEST-{_tag}", 1));

        var first = await _sender.SendAsync(WebhookTesting.Request(
            Website, OrderOf($"FIRST-{_tag}", WebhookTesting.Customer(externalCustomerId, "Le Van C", "0911 111 111", $"c.{_tag}@example.com")),
            ApiFactory.WebsiteWebhookSecret));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var (mapping, created) = await CustomerOfAsync(Website, externalCustomerId);

        // The same customer of the channel, with another name, phone and e-mail (and none at all).
        var second = await _sender.SendAsync(WebhookTesting.Request(
            Website, OrderOf($"SECOND-{_tag}", WebhookTesting.Customer($"  {externalCustomerId} ", $"Somebody Else {_tag}", "0922 222 222", $"other.{_tag}@example.com")),
            ApiFactory.WebsiteWebhookSecret));
        var third = await _sender.SendAsync(WebhookTesting.Request(
            Website, OrderOf($"THIRD-{_tag}", WebhookTesting.Customer(externalCustomerId, $"No Contact {_tag}")), ApiFactory.WebsiteWebhookSecret));
        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (second.StatusCode, third.StatusCode));

        // One mapping, one customer - exactly as first written (not even an UpdatedAt) - and all three orders are theirs.
        var (sameMapping, same) = await CustomerOfAsync(Website, externalCustomerId);
        Assert.Equal((mapping.ChannelCustomerId, mapping.CustomerId, (DateTimeOffset?)null), (sameMapping.ChannelCustomerId, sameMapping.CustomerId, sameMapping.UpdatedAt));
        Assert.Equal(
            ("Le Van C", "0911 111 111", $"c.{_tag}@example.com", created.CreatedAt, (DateTimeOffset?)null, (string?)null),
            (same.Name, same.Phone, same.Email, same.CreatedAt, same.UpdatedAt, same.UpdatedBy));
        // Nobody was created for the later names.
        Assert.Equal(0, await factory.QueryDbAsync(db => db.Customers.CountAsync(c => c.Name.EndsWith(_tag) && c.CustomerId != created.CustomerId)));
        foreach (var response in new[] { first, second, third })
        {
            Assert.Equal(created.CustomerId, (await PayloadAsync((await response.ReadAsync<SubmitOrderResponse>()).TaskId)).CustomerId);
        }

        // The id is the channel's: the same text on another channel is another customer, and another case another id.
        await MappedProductAsync(Pos, externalProductId: $"CUSTOMER-TEST-{_tag}");
        var onPos = await _sender.SendAsync(WebhookTesting.Request(
            Pos, OrderOf($"FIRST-{_tag}", WebhookTesting.Customer(externalCustomerId, "Le Van C on POS")), ApiFactory.PosWebhookSecret));
        var otherCase = await _sender.SendAsync(WebhookTesting.Request(
            Website, OrderOf($"FOURTH-{_tag}", WebhookTesting.Customer(externalCustomerId.ToLowerInvariant(), "lower case")), ApiFactory.WebsiteWebhookSecret));
        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (onPos.StatusCode, otherCase.StatusCode));
        var posCustomer = await CustomerOfAsync(Pos, externalCustomerId);
        var lowerCustomer = await CustomerOfAsync(Website, externalCustomerId.ToLowerInvariant());
        Assert.Equal(("Le Van C on POS", "webhook:pos", "webhook:pos"), (posCustomer.Customer.Name, posCustomer.Customer.CreatedBy, posCustomer.Mapping.CreatedBy));
        Assert.Equal(3, new[] { created.CustomerId, posCustomer.Customer.CustomerId, lowerCustomer.Customer.CustomerId }.Distinct().Count());
        Assert.NotEqual(Guid.Empty, product.ProductId);
    }

    [Fact]
    public async Task Webhooks_that_name_the_same_new_customer_at_once_create_one_customer()
    {
        await MappedProductAsync(Website, externalProductId: $"RACE-{_tag}");
        var externalCustomerId = $"RACE-CUSTOMER-{_tag}";
        var name = $"Race {_tag}";

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(n => _sender.SendAsync(WebhookTesting.Request(
            Website,
            WebhookTesting.Order($"RACE-ORDER-{n}-{_tag}", WebhookTesting.Customer(externalCustomerId, name), WebhookTesting.ById($"RACE-{_tag}", 1)),
            ApiFactory.WebsiteWebhookSecret))));

        // Every order is accepted - also those whose customer somebody else created a moment earlier - ...
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        // ... there is one mapping and one customer, and nobody else by that name was left behind ...
        var (mapping, customer) = await CustomerOfAsync(Website, externalCustomerId);
        Assert.Equal(1, await factory.QueryDbAsync(db => db.Customers.CountAsync(c => c.Name == name)));
        Assert.Equal(1, await factory.QueryDbAsync(db => db.ChannelCustomers.CountAsync(m => m.ExternalCustomerId == externalCustomerId)));
        // ... and all twelve orders are that customer's.
        var customers = new List<Guid>();
        foreach (var response in responses)
        {
            customers.Add((await PayloadAsync((await response.ReadAsync<SubmitOrderResponse>()).TaskId)).CustomerId);
        }

        Assert.Equal([customer.CustomerId], customers.Distinct());
        Assert.Equal(mapping.CustomerId, customer.CustomerId);
        Assert.Equal(12, customers.Count);
    }
}
