using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Omnichannel.Api.Auth;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Orders;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Serilog.Events;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 023 Part C: <c>POST /api/webhooks/{slug}</c> against the API host - who may call it (this file) and what it does
/// with an order (<c>WebhookEndpointsTests.Orders.cs</c>). Every test sends from a client address of its own: the rate
/// limit is per address and channel, and small in this host.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class WebhookEndpointsTests(ApiFactory factory)
{
    private const string Website = "website";
    private const string Pos = "pos";
    private const string Marketplace = "digital-platform";

    private readonly HttpClient _admin = factory.CreateClientAs(AppRoles.Admin);
    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);
    private readonly HttpClient _sender = factory.CreateAnonymousClient();
    private readonly string _tag = TestData.UniqueSuffix();

    // ---- the signature ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_webhook_signed_with_the_channel_s_secret_is_accepted()
    {
        var order = await NewOrderAsync(Website);

        var response = await _sender.SendAsync(WebhookTesting.Request(Website, order.Body, ApiFactory.WebsiteWebhookSecret));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.ReadAsync<SubmitOrderResponse>();
        Assert.Equal((ProcessingTaskStatus.Pending, false), (accepted.Status, accepted.Duplicate));
        Assert.Equal(accepted.TaskId, Assert.Single(await TasksAsync(order)).TaskId);
    }

    [Fact]
    public async Task A_webhook_that_is_not_signed_with_the_channel_s_secret_is_refused_and_writes_nothing()
    {
        var order = await NewOrderAsync(Website);
        var timestamp = WebhookTesting.Now();
        var valid = WebhookTesting.SignatureOf(ApiFactory.WebsiteWebhookSecret, timestamp, order.Body);
        var tampered = (byte[])order.Body.Clone();
        tampered[^2] ^= 0x01;
        // The same JSON, written with other whitespace: another body.
        var reserialised = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(order.Body).Replace(":", ": ", StringComparison.Ordinal));
        using (JsonDocument.Parse(reserialised))
        {
        }

        HttpRequestMessage[] requests =
        [
            // A secret nobody configured, and the secret of another channel.
            WebhookTesting.Request(Website, order.Body, "a-secret-that-is-not-the-channels-0123456789", timestamp),
            WebhookTesting.Request(Website, order.Body, ApiFactory.PosWebhookSecret, timestamp),
            // The right signature for another body or another timestamp.
            WebhookTesting.Request(Website, tampered, secret: null, timestamp, signature: $"v1={valid}"),
            WebhookTesting.Request(Website, reserialised, secret: null, timestamp, signature: $"v1={valid}"),
            WebhookTesting.Request(Website, order.Body, secret: null, (long.Parse(timestamp) + 1).ToString(), signature: $"v1={valid}"),
            WebhookTesting.Request(Website, order.Body, secret: null, "0" + timestamp, signature: $"v1={valid}"),
            // No signature, or none of the v1 scheme.
            WebhookTesting.Request(Website, order.Body, secret: null, timestamp),
            WebhookTesting.Request(Website, order.Body, secret: null, timestamp, signature: string.Empty),
            WebhookTesting.Request(Website, order.Body, secret: null, timestamp, signature: valid),
            WebhookTesting.Request(Website, order.Body, secret: null, timestamp, signature: $"v0={valid}"),
            WebhookTesting.Request(Website, order.Body, secret: null, timestamp, signature: $"sha256={valid}"),
            WebhookTesting.Request(Website, order.Body, secret: null, timestamp, signature: $"v1={valid[..63]}"),
            WebhookTesting.Request(Website, order.Body, secret: null, timestamp, signature: $"v1={new string('0', 64)}")
        ];

        foreach (var request in requests)
        {
            var response = await _sender.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(WebhookErrorCodes.SignatureInvalid, await response.ReadErrorCodeAsync());
            Assert.Equal("webhook_signature_invalid", WebhookErrorCodes.SignatureInvalid);
        }

        await AssertNothingWrittenAsync(order);

        // The control: the very same body, timestamp and signature are accepted when nothing was changed.
        var untouched = await _sender.SendAsync(WebhookTesting.Request(Website, order.Body, secret: null, timestamp, signature: $"v1={valid}"));
        Assert.Equal(HttpStatusCode.Accepted, untouched.StatusCode);
    }

    [Fact]
    public async Task A_webhook_whose_timestamp_is_missing_no_number_or_too_far_from_the_clock_is_refused_and_writes_nothing()
    {
        var order = await NewOrderAsync(Website);

        // Each is signed correctly for the timestamp it carries: it is the timestamp that is refused.
        HttpRequestMessage Signed(string timestamp) => WebhookTesting.Request(Website, order.Body, ApiFactory.WebsiteWebhookSecret, timestamp);
        HttpRequestMessage[] requests =
        [
            Signed(WebhookTesting.Now(-400)),
            Signed(WebhookTesting.Now(400)),
            Signed(WebhookTesting.Now(-86_400)),
            Signed("0"),
            Signed("not-a-number"),
            Signed(DateTimeOffset.UtcNow.ToString("O")),
            Signed("-" + WebhookTesting.Now()),
            Signed(WebhookTesting.Now() + ".5"),
            Signed(string.Empty),
            // Milliseconds instead of seconds: far in the future.
            Signed(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()),
            WebhookTesting.Request(Website, order.Body, ApiFactory.WebsiteWebhookSecret, sendTimestamp: false),
            // Neither header: the timestamp is looked at first.
            WebhookTesting.Request(Website, order.Body, secret: null, sendTimestamp: false)
        ];

        foreach (var request in requests)
        {
            var response = await _sender.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(WebhookErrorCodes.TimestampInvalid, await response.ReadErrorCodeAsync());
            Assert.Equal("webhook_timestamp_invalid", WebhookErrorCodes.TimestampInvalid);
        }

        await AssertNothingWrittenAsync(order);

        // Inside the window - a clock that is a few minutes off in either direction - it is accepted.
        var early = await _sender.SendAsync(WebhookTesting.Request(Website, order.Body, ApiFactory.WebsiteWebhookSecret, WebhookTesting.Now(-240)));
        Assert.Equal(HttpStatusCode.Accepted, early.StatusCode);
        var late = await NewOrderAsync(Website);
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await _sender.SendAsync(WebhookTesting.Request(Website, late.Body, ApiFactory.WebsiteWebhookSecret, WebhookTesting.Now(240)))).StatusCode);
    }

    [Fact]
    public async Task Both_secrets_of_a_channel_that_is_changing_its_secret_are_accepted_and_several_signatures_may_be_sent()
    {
        var (current, previous, both) = (await NewOrderAsync(Pos), await NewOrderAsync(Pos), await NewOrderAsync(Pos));

        Assert.Equal(HttpStatusCode.Accepted, (await _sender.SendAsync(WebhookTesting.Request(Pos, current.Body, ApiFactory.PosWebhookSecret))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await _sender.SendAsync(WebhookTesting.Request(Pos, previous.Body, ApiFactory.PosPreviousWebhookSecret))).StatusCode);

        // A sender in the middle of the change signs with the new and the old secret; a value of another scheme and a
        // signature that matches nothing do not stand in the way.
        var timestamp = WebhookTesting.Now();
        var header = string.Join(
            ",",
            $"v0={new string('a', 64)}",
            $"v1={WebhookTesting.SignatureOf("a-secret-that-is-not-the-channels-0123456789", timestamp, both.Body)}",
            $"v1={WebhookTesting.SignatureOf(ApiFactory.PosPreviousWebhookSecret, timestamp, both.Body)}");
        var response = await _sender.SendAsync(WebhookTesting.Request(Pos, both.Body, secret: null, timestamp, signature: header));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // website has one secret only: the previous secret of pos means nothing there.
        var onWebsite = await NewOrderAsync(Website);
        var refused = await _sender.SendAsync(WebhookTesting.Request(Website, onWebsite.Body, ApiFactory.PosPreviousWebhookSecret));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    // ---- a webhook is not a user ------------------------------------------------------------------------------

    [Fact]
    public async Task A_bearer_token_never_replaces_the_signature_and_never_acts_through_a_webhook()
    {
        var order = await NewOrderAsync(Website);
        var staff = factory.CreateClientAs(AppRoles.Staff);
        var admin = factory.CreateClientAs(AppRoles.Admin);

        // A valid token of any role, and no signature: refused like anybody else.
        foreach (var client in new[] { staff, admin })
        {
            var unsigned = await client.SendAsync(WebhookTesting.Request(Website, order.Body, secret: null));

            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
            Assert.Equal(WebhookErrorCodes.SignatureInvalid, await unsigned.ReadErrorCodeAsync());
        }

        await AssertNothingWrittenAsync(order);

        // With a signature the request is the webhook's, whatever token came along: the actor is the channel's webhook.
        var signed = await admin.SendAsync(WebhookTesting.Request(Website, order.Body, ApiFactory.WebsiteWebhookSecret));

        Assert.Equal(HttpStatusCode.Accepted, signed.StatusCode);
        Assert.Equal("webhook:website", await ReceivedByAsync(order));
        var customer = await CustomerOfAsync(order);
        Assert.Equal(("webhook:website", "webhook:website"), (customer.Mapping.CreatedBy, customer.Customer.CreatedBy));
        Assert.DoesNotContain(factory.UserIdOf(AppRoles.Admin).ToString(), new[] { customer.Mapping.CreatedBy, customer.Customer.CreatedBy });
    }

    [Fact]
    public async Task The_identity_of_a_webhook_passes_no_authorization_policy_of_the_api()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        var webhook = WebhookCaller.PrincipalOf(Website);

        string[] policies = [AuthorizationPolicies.AnyStaff, AuthorizationPolicies.ManagerOrAdmin, AuthorizationPolicies.AdminOnly];
        foreach (var policy in policies)
        {
            Assert.False((await authorization.AuthorizeAsync(webhook, resource: null, policy)).Succeeded, policy);
        }

        // Not even the policy of an endpoint that names none (any authenticated user).
        Assert.NotNull(options.FallbackPolicy);
        Assert.False((await authorization.AuthorizeAsync(webhook, resource: null, options.FallbackPolicy)).Succeeded);

        // The control: the same checks let a staff member through.
        var staff = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new(JwtClaimNames.Subject, Guid.NewGuid().ToString()), new(JwtClaimNames.Role, AppRoles.Staff)],
            "Bearer", JwtClaimNames.Subject, JwtClaimNames.Role));
        Assert.True((await authorization.AuthorizeAsync(staff, resource: null, AuthorizationPolicies.AnyStaff)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(staff, resource: null, options.FallbackPolicy)).Succeeded);
    }

    [Fact]
    public async Task A_valid_signature_opens_no_other_endpoint()
    {
        var order = await NewOrderAsync(Website);
        var website = await ChannelAsync(Website);

        // The headers of a webhook mean nothing anywhere else: every other endpoint wants a bearer token.
        string[] paths = [$"/api/orders/{Website}", "/api/customers", $"/api/channels/{website.ChannelId}/products", "/api/users"];
        foreach (var path in paths)
        {
            using var request = WebhookTesting.Request(Website, order.Body, ApiFactory.WebsiteWebhookSecret);
            request.RequestUri = new Uri(path, UriKind.Relative);

            Assert.Equal(HttpStatusCode.Unauthorized, (await _sender.SendAsync(request)).StatusCode);
        }

        using var read = new HttpRequestMessage(HttpMethod.Get, "/api/orders");
        read.Headers.TryAddWithoutValidation(WebhookTesting.TimestampHeader, WebhookTesting.Now());
        read.Headers.TryAddWithoutValidation(WebhookTesting.SignatureHeader, "v1=" + WebhookTesting.SignatureOf(ApiFactory.WebsiteWebhookSecret, WebhookTesting.Now(), []));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _sender.SendAsync(read)).StatusCode);

        // And a webhook is a POST: nothing can be read through its address.
        using var get = new HttpRequestMessage(HttpMethod.Get, $"/api/webhooks/{Website}");
        Assert.Contains((await _sender.SendAsync(get)).StatusCode, new[] { HttpStatusCode.MethodNotAllowed, HttpStatusCode.Unauthorized, HttpStatusCode.NotFound });

        await AssertNothingWrittenAsync(order);
    }

    // ---- 404, 413, 429 ----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_an_inactive_and_an_unconfigured_channel_look_the_same()
    {
        var order = await NewOrderAsync(Website);
        var off = await SwitchedOffChannelAsync();

        // Unknown: no such channel. Unconfigured: an Active channel without a webhook secret. Inactive: a channel with a
        // secret that is switched off. Each request is signed as well as it can be.
        var unknown = await _sender.SendAsync(WebhookTesting.Request($"no-such-{_tag}", order.Body, ApiFactory.WebsiteWebhookSecret));
        var unconfigured = await _sender.SendAsync(WebhookTesting.Request(Marketplace, order.Body, ApiFactory.WebsiteWebhookSecret));
        var inactive = await _sender.SendAsync(WebhookTesting.Request(off.Slug, order.Body, ApiFactory.SwitchedOffWebhookSecret));

        var answers = new List<(string?, string?, int, string?)>();
        foreach (var response in new[] { unknown, unconfigured, inactive })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = problem.RootElement;
            Assert.False(string.IsNullOrEmpty(root.GetProperty("correlationId").GetString()));
            answers.Add((root.GetProperty("title").GetString(), root.GetProperty("detail").GetString(), root.GetProperty("status").GetInt32(),
                root.GetProperty("errorCode").GetString()));
        }

        Assert.Single(answers.Distinct());
        Assert.Equal(("channel_not_found", 404), (answers[0].Item4, answers[0].Item3));
        Assert.Equal(WebhookErrorCodes.ChannelNotFound, OrderErrorCodes.ChannelNotFound);
        await AssertNothingWrittenAsync(order);

        // It was the status: switched on, the same channel answers the same request (the order names nothing it sells).
        await SetChannelStatusAsync(off, ChannelStatus.Active);
        try
        {
            var active = await _sender.SendAsync(WebhookTesting.Request(off.Slug, order.Body, ApiFactory.SwitchedOffWebhookSecret));
            Assert.Equal(HttpStatusCode.BadRequest, active.StatusCode);
        }
        finally
        {
            await SetChannelStatusAsync(off, ChannelStatus.Inactive);
        }
    }

    [Fact]
    public async Task A_body_above_the_limit_is_refused_whether_its_length_is_announced_or_not()
    {
        var limit = new WebhookOptions().MaxBodyBytes;
        var order = await NewOrderAsync(Website);
        // Valid JSON of any length: spaces after the object.
        byte[] Padded(int length) => [.. order.Body, .. Enumerable.Repeat((byte)' ', length - order.Body.Length)];
        var (atLimit, above) = (Padded(limit), Padded(limit + 1));
        // A client that does not buffer what it sends (the default test client does, to follow redirects - and a buffered
        // body has a known length): only this one sends a body without a Content-Length.
        var streaming = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        streaming.DefaultRequestHeaders.Add(ApiFactory.RemoteIpHeader, _sender.DefaultRequestHeaders.GetValues(ApiFactory.RemoteIpHeader));

        var announced = await _sender.SendAsync(WebhookTesting.Request(Website, above, ApiFactory.WebsiteWebhookSecret));
        var inChunks = await streaming.SendAsync(WebhookTesting.Request(
            Website, above, ApiFactory.WebsiteWebhookSecret, content: new WebhookTesting.UnknownLengthContent(above)));

        foreach (var response in new[] { announced, inChunks })
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal(WebhookErrorCodes.BodyTooLarge, await response.ReadErrorCodeAsync());
            Assert.Equal("webhook_body_too_large", WebhookErrorCodes.BodyTooLarge);
        }

        await AssertNothingWrittenAsync(order);

        // Exactly the limit is a body like any other, announced or not.
        var chunkedAtLimit = await NewOrderAsync(Website);
        byte[] other = [.. chunkedAtLimit.Body, .. Enumerable.Repeat((byte)' ', limit - chunkedAtLimit.Body.Length)];
        Assert.Equal(HttpStatusCode.Accepted, (await _sender.SendAsync(WebhookTesting.Request(Website, atLimit, ApiFactory.WebsiteWebhookSecret))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await streaming.SendAsync(WebhookTesting.Request(
                Website, other, ApiFactory.WebsiteWebhookSecret, content: new WebhookTesting.UnknownLengthContent(other)))).StatusCode);
    }

    [Fact]
    public async Task The_rate_limit_is_per_client_address_and_channel()
    {
        var order = await NewOrderAsync(Website);
        var limit = ApiFactory.WebhookPermitsPerMinute;

        // Every webhook counts, whatever its outcome: unsigned ones here.
        for (var n = 0; n < limit; n++)
        {
            var counted = await _sender.SendAsync(WebhookTesting.Request(Website, order.Body, secret: null));
            Assert.Equal(HttpStatusCode.Unauthorized, counted.StatusCode);
        }

        // One more - correctly signed - is refused before anything is looked at.
        var limited = await _sender.SendAsync(WebhookTesting.Request(Website, order.Body, ApiFactory.WebsiteWebhookSecret));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        using (var problem = JsonDocument.Parse(await limited.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                ("webhook_rate_limited", 429),
                (problem.RootElement.GetProperty("errorCode").GetString(), problem.RootElement.GetProperty("status").GetInt32()));
            Assert.False(string.IsNullOrEmpty(problem.RootElement.GetProperty("correlationId").GetString()));
        }

        await AssertNothingWrittenAsync(order);

        // The same address still reaches another channel, and another address this one.
        var onPos = await NewOrderAsync(Pos);
        Assert.Equal(HttpStatusCode.Accepted, (await _sender.SendAsync(WebhookTesting.Request(Pos, onPos.Body, ApiFactory.PosWebhookSecret))).StatusCode);
        var elsewhere = factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Accepted, (await elsewhere.SendAsync(WebhookTesting.Request(Website, order.Body, ApiFactory.WebsiteWebhookSecret))).StatusCode);
    }

    [Fact]
    public async Task Slugs_without_a_webhook_share_one_window_per_client_address()
    {
        var limit = ApiFactory.WebhookPermitsPerMinute;
        var body = "{}"u8.ToArray();

        // Made-up slugs do not multiply the limit: each of them counts toward the same window.
        for (var n = 0; n < limit; n++)
        {
            var counted = await _sender.SendAsync(WebhookTesting.Request($"made-up-{n}-{_tag}", body, secret: null));
            Assert.Equal(HttpStatusCode.NotFound, counted.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await _sender.SendAsync(WebhookTesting.Request($"another-{_tag}", body, secret: null))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _sender.SendAsync(WebhookTesting.Request(Marketplace, body, secret: null))).StatusCode);
        // A channel that has a webhook keeps its own window.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _sender.SendAsync(WebhookTesting.Request(Website, body, secret: null))).StatusCode);
    }

    // ---- the order of the steps -------------------------------------------------------------------------------

    [Fact]
    public async Task Each_step_is_reached_only_when_the_one_before_it_passed()
    {
        var limit = new WebhookOptions().MaxBodyBytes;
        var tooLarge = Enumerable.Repeat((byte)' ', limit + 1).ToArray();
        var notJson = "{ this is not json"u8.ToArray();
        var order = await NewOrderAsync(Website);

        // 2 before 3: a body that is too large is refused before the channel is looked up ...
        var largeToNowhere = await _sender.SendAsync(WebhookTesting.Request($"no-such-{_tag}", tooLarge, secret: null, sendTimestamp: false));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, largeToNowhere.StatusCode);

        // ... 3 before 4: an unknown channel before the headers are looked at ...
        var unsignedToNowhere = await _sender.SendAsync(WebhookTesting.Request($"no-such-{_tag}", order.Body, secret: null, sendTimestamp: false));
        Assert.Equal(HttpStatusCode.NotFound, unsignedToNowhere.StatusCode);

        // ... 4 before 6: a body nobody signed is never read as JSON ...
        var unsignedGarbage = await _sender.SendAsync(WebhookTesting.Request(Website, notJson, secret: null));
        Assert.Equal(HttpStatusCode.Unauthorized, unsignedGarbage.StatusCode);
        Assert.Equal(WebhookErrorCodes.SignatureInvalid, await unsignedGarbage.ReadErrorCodeAsync());

        // ... and only a signed one gets the answer of the parser (as every endpoint gives it).
        var signedGarbage = await _sender.SendAsync(WebhookTesting.Request(Website, notJson, ApiFactory.WebsiteWebhookSecret));
        Assert.Contains((await signedGarbage.ErrorsAsync()).Keys, key => key.StartsWith('$'));
        var signedNothing = await _sender.SendAsync(WebhookTesting.Request(Website, [], ApiFactory.WebsiteWebhookSecret));
        Assert.Equal(HttpStatusCode.BadRequest, signedNothing.StatusCode);

        // 6 before 7: an order that is not valid is not looked up - the line nobody sells is not mentioned ...
        var customer = $"CUST-STEPS-{_tag}";
        var invalid = WebhookTesting.Order($"ORD-STEPS-{_tag}", WebhookTesting.Customer(customer), WebhookTesting.ById($"NOBODY-SELLS-{_tag}", 0));
        var invalidAnswer = await _sender.SendAsync(WebhookTesting.Request(Website, invalid, ApiFactory.WebsiteWebhookSecret));
        Assert.Equal(["items[0].quantity"], (await invalidAnswer.ErrorsAsync()).Keys);

        // ... 7 before 8: and a valid order with such a line creates no customer.
        var unsold = WebhookTesting.Order($"ORD-STEPS-{_tag}", WebhookTesting.Customer(customer), WebhookTesting.ById($"NOBODY-SELLS-{_tag}", 1));
        var unsoldAnswer = await _sender.SendAsync(WebhookTesting.Request(Website, unsold, ApiFactory.WebsiteWebhookSecret));
        Assert.Equal(["items[0].externalProductId"], (await unsoldAnswer.ErrorsAsync()).Keys);
        Assert.Equal(0, await factory.QueryDbAsync(db => db.ChannelCustomers.CountAsync(m => m.ExternalCustomerId == customer)));
    }

    // ---- what is logged ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_refused_webhook_is_one_warning_and_no_log_line_carries_a_secret_a_signature_or_a_body()
    {
        var since = DateTimeOffset.Now;
        var clientIp = _sender.DefaultRequestHeaders.GetValues(ApiFactory.RemoteIpHeader).Single();
        var marker = $"Zq{_tag}";
        var product = await MappedProductAsync(Website, externalProductId: $"LOG-ID-{marker}");
        var customer = WebhookTesting.Customer($"LOG-CUSTOMER-{marker}", $"Name {marker}", "0909 123 456", $"{marker}@example.com");
        var body = WebhookTesting.Order($"LOG-ORDER-{marker}", customer, WebhookTesting.ById($"LOG-ID-{marker}", 2));
        var timestamp = WebhookTesting.Now();
        var signature = WebhookTesting.SignatureOf(ApiFactory.WebsiteWebhookSecret, timestamp, body);
        var wrong = WebhookTesting.SignatureOf("a-secret-that-is-not-the-channels-0123456789", timestamp, body);
        var limit = new WebhookOptions().MaxBodyBytes;

        // One of everything: accepted, a duplicate, a conflict, a validation failure, and each kind of refusal.
        (HttpRequestMessage Request, HttpStatusCode Status, string? Reason)[] sent =
        [
            (WebhookTesting.Request(Website, body, secret: null, timestamp, signature: $"v1={signature}"), HttpStatusCode.Accepted, null),
            (WebhookTesting.Request(Website, body, secret: null, timestamp, signature: $"v1={signature}"), HttpStatusCode.Accepted, null),
            (WebhookTesting.Request(Website, WebhookTesting.Order($"LOG-ORDER-{marker}", customer, WebhookTesting.ById($"LOG-ID-{marker}", 3)),
                ApiFactory.WebsiteWebhookSecret), HttpStatusCode.Conflict, null),
            (WebhookTesting.Request(Website, WebhookTesting.Order($"LOG-ORDER-2-{marker}", customer, WebhookTesting.BySku($"LOG-UNSOLD-{marker}", 1)),
                ApiFactory.WebsiteWebhookSecret), HttpStatusCode.BadRequest, null),
            (WebhookTesting.Request(Website, body, secret: null, timestamp, signature: $"v1={wrong}"), HttpStatusCode.Unauthorized, WebhookErrorCodes.SignatureInvalid),
            (WebhookTesting.Request(Website, body, ApiFactory.WebsiteWebhookSecret, WebhookTesting.Now(-3600)), HttpStatusCode.Unauthorized, WebhookErrorCodes.TimestampInvalid),
            (WebhookTesting.Request(Marketplace, body, ApiFactory.WebsiteWebhookSecret), HttpStatusCode.NotFound, WebhookErrorCodes.ChannelNotFound),
            (WebhookTesting.Request("Not%0Aa%20Slug", body, ApiFactory.WebsiteWebhookSecret), HttpStatusCode.NotFound, WebhookErrorCodes.ChannelNotFound),
            (WebhookTesting.Request(Website, [.. body, .. Enumerable.Repeat((byte)' ', limit)], ApiFactory.WebsiteWebhookSecret),
                HttpStatusCode.RequestEntityTooLarge, WebhookErrorCodes.BodyTooLarge)
        ];
        foreach (var (request, status, _) in sent)
        {
            Assert.Equal(status, (await _sender.SendAsync(request)).StatusCode);
        }

        // The rate limit, on a channel of its own window (pos): refused once.
        for (var n = 0; n < ApiFactory.WebhookPermitsPerMinute; n++)
        {
            await _sender.SendAsync(WebhookTesting.Request(Pos, body, secret: null, sendTimestamp: false));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await _sender.SendAsync(WebhookTesting.Request(Pos, body, ApiFactory.PosWebhookSecret))).StatusCode);

        var events = factory.Logs.EventsSince(since);
        var refusals = events.Where(e => e.MessageTemplate.Text.StartsWith("Webhook refused", StringComparison.Ordinal)).ToList();

        // One Warning per refused webhook, with the slug, the reason and the client address - and nothing else.
        Assert.All(refusals, e => Assert.Equal(LogEventLevel.Warning, e.Level));
        Assert.All(refusals, e => Assert.Equal(clientIp, Scalar(e, "ClientIp")));
        var refused = sent.Where(s => s.Reason is not null).Select(s => s.Reason!).ToList();
        Assert.Equal(
            refused.Concat(Enumerable.Repeat(WebhookErrorCodes.TimestampInvalid, ApiFactory.WebhookPermitsPerMinute)).Append(WebhookErrorCodes.RateLimited).Order(),
            refusals.Select(e => Scalar(e, "ReasonCode")).Order());
        Assert.Equal(
            new[] { "(not a slug)", Marketplace, Pos, Website }.Order(),
            refusals.Select(e => Scalar(e, "ChannelSlug")).Distinct().Order());
        Assert.Equal(
            (429, Pos),
            refusals.Where(e => Scalar(e, "ReasonCode") == WebhookErrorCodes.RateLimited).Select(e => (int.Parse(Scalar(e, "StatusCode")!), Scalar(e, "ChannelSlug"))).Single());
        Assert.All(refusals, e => Assert.Equal(
            ["ChannelSlug", "ClientIp", "ReasonCode", "StatusCode"],
            e.MessageTemplate.Tokens.OfType<Serilog.Parsing.PropertyToken>().Select(t => t.PropertyName).Order()));

        // Nothing the application logged - at any level, from any source - holds a secret, a signature, or the body or
        // the personal data in it.
        var rendered = factory.Logs.RenderedSince(since);
        Assert.NotEmpty(rendered);
        string[] forbidden =
        [
            ApiFactory.WebsiteWebhookSecret, ApiFactory.PosWebhookSecret, ApiFactory.PosPreviousWebhookSecret, ApiFactory.SwitchedOffWebhookSecret,
            signature, wrong, signature.ToUpperInvariant(),
            Encoding.UTF8.GetString(body), $"Name {marker}", "0909 123 456", $"{marker}@example.com", $"LOG-CUSTOMER-{marker}",
            $"LOG-ID-{marker}", $"LOG-UNSOLD-{marker}", "externalCustomerId"
        ];
        Assert.All(forbidden, text => Assert.DoesNotContain(rendered, line => line.Contains(text, StringComparison.Ordinal)));
        // The address a stranger typed ("Not<line break>a Slug") is not written into the refusal as it came: no second line.
        Assert.All(refusals, e => Assert.DoesNotContain('\n', e.RenderMessage()));
        Assert.All(refusals, e => Assert.DoesNotContain("Not", e.RenderMessage(), StringComparison.Ordinal));
        Assert.Equal(product.ProductId, (await CustomerOrderItemsAsync($"LOG-ORDER-{marker}")).Single().ProductId);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>A valid order of one mapped product for <paramref name="slug"/>, with ids of its own.</summary>
    private sealed record SentOrder(string Slug, string ExternalOrderId, string ExternalCustomerId, string ExternalProductId, Guid ProductId, byte[] Body);

    private async Task<SentOrder> NewOrderAsync(string slug, int quantity = 1)
    {
        var suffix = TestData.UniqueSuffix();
        var (orderId, customerId, externalProductId) = ($"ORD-{suffix}", $"CUST-{suffix}", $"EXT-{suffix}");
        var product = await MappedProductAsync(slug, externalProductId);
        return new SentOrder(
            slug, orderId, customerId, externalProductId, product.ProductId,
            WebhookTesting.Order(orderId, WebhookTesting.Customer(customerId), WebhookTesting.ById(externalProductId, quantity)));
    }

    private async Task<ChannelResponse> ChannelAsync(string slug) =>
        (await _manager.GetFromJsonAsync<List<ChannelResponse>>("/api/channels", TestData.Json))!.Single(c => c.Slug == slug);

    /// <summary>The channel whose webhook secret is configured and which is Inactive (created by the first test that asks).</summary>
    private async Task<ChannelResponse> SwitchedOffChannelAsync()
    {
        var existing = (await _manager.GetFromJsonAsync<List<ChannelResponse>>("/api/channels", TestData.Json))!
            .SingleOrDefault(c => c.Slug == ApiFactory.SwitchedOffWebhookSlug);
        if (existing is null)
        {
            var created = await _admin.PostAsJsonAsync(
                "/api/channels", new CreateChannelRequest("Switched-off webhook shop", ApiFactory.SwitchedOffWebhookSlug, ChannelType.Website), TestData.Json);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            existing = await created.ReadAsync<ChannelResponse>();
        }

        await SetChannelStatusAsync(existing, ChannelStatus.Inactive);
        return existing;
    }

    private async Task SetChannelStatusAsync(ChannelResponse channel, ChannelStatus status)
    {
        var response = await _admin.PutAsJsonAsync($"/api/channels/{channel.ChannelId}", new UpdateChannelRequest(channel.Name, status), TestData.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<ProductResponse> ProductAsync(decimal price = 9.99m)
    {
        var response = await _manager.PostAsJsonAsync(
            "/api/products", new CreateProductRequest($"WH-{TestData.UniqueSuffix()}".ToUpperInvariant(), "Webhook product", price, 0, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<ProductResponse>();
    }

    private async Task<ChannelProductSavedResponse> MapAsync(string slug, Guid productId, string? externalProductId, string? externalSku)
    {
        var channel = await ChannelAsync(slug);
        var response = await _manager.PostAsJsonAsync(
            $"/api/channels/{channel.ChannelId}/products", new CreateChannelProductRequest(productId, externalProductId, externalSku));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<ChannelProductSavedResponse>();
    }

    private async Task<ProductResponse> MappedProductAsync(string slug, string? externalProductId = null, string? externalSku = null, decimal price = 9.99m)
    {
        var product = await ProductAsync(price);
        await MapAsync(slug, product.ProductId, externalProductId, externalSku);
        return product;
    }

    private async Task<List<ProcessingTask>> TasksAsync(string slug, string externalOrderId)
    {
        var channel = await ChannelAsync(slug);
        var key = OrderIdempotencyKey.For(channel.ChannelId, externalOrderId);
        return await factory.QueryDbAsync(db => db.ProcessingTasks.AsNoTracking().Where(t => t.IdempotencyKey == key).ToListAsync());
    }

    private Task<List<ProcessingTask>> TasksAsync(SentOrder order) => TasksAsync(order.Slug, order.ExternalOrderId);

    private async Task<OrderCreatedPayload> PayloadAsync(Guid taskId)
    {
        var message = await factory.QueryDbAsync(db => db.OutboxEvents.AsNoTracking().SingleAsync(e => e.AggregateId == taskId.ToString()));
        Assert.Equal(EventTypes.OrderCreated, message.EventType);
        return MessageJson.Deserialize<OrderCreatedPayload>(message.Payload);
    }

    private async Task<string> ReceivedByAsync(SentOrder order) =>
        (await PayloadAsync(Assert.Single(await TasksAsync(order)).TaskId)).ReceivedBy;

    private async Task<IReadOnlyList<OrderCreatedItem>> CustomerOrderItemsAsync(string externalOrderId) =>
        (await PayloadAsync(Assert.Single(await TasksAsync(Website, externalOrderId)).TaskId)).Items;

    private async Task<(ChannelCustomer Mapping, Customer Customer)> CustomerOfAsync(string slug, string externalCustomerId)
    {
        var channel = await ChannelAsync(slug);
        var mapping = await factory.QueryDbAsync(db => db.ChannelCustomers.AsNoTracking()
            .SingleAsync(m => m.ChannelId == channel.ChannelId && m.ExternalCustomerId == externalCustomerId));
        var customer = await factory.QueryDbAsync(db => db.Customers.AsNoTracking().SingleAsync(c => c.CustomerId == mapping.CustomerId));
        return (mapping, customer);
    }

    private Task<(ChannelCustomer Mapping, Customer Customer)> CustomerOfAsync(SentOrder order) =>
        CustomerOfAsync(order.Slug, order.ExternalCustomerId);

    /// <summary>No task (and so no message) of the order, and nobody was created for its customer.</summary>
    private async Task AssertNothingWrittenAsync(SentOrder order)
    {
        Assert.Empty(await TasksAsync(order));
        Assert.Equal(0, await factory.QueryDbAsync(db => db.ChannelCustomers.CountAsync(m => m.ExternalCustomerId == order.ExternalCustomerId)));
    }

    private static string? Scalar(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out var value) && value is ScalarValue scalar ? scalar.Value?.ToString() : null;
}
