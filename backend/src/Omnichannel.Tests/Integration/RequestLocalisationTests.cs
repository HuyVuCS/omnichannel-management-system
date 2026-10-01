using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentValidation;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 010 Part A: validation messages follow <c>Accept-Language</c> (vi / en, default en); error keys and
/// <c>errorCode</c> values are the same in every language.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RequestLocalisationTests(ApiFactory factory)
{
    [Theory]
    [InlineData("vi", "vi")]
    [InlineData("vi-VN,vi;q=0.9,en;q=0.8", "vi")]
    [InlineData("en", "en")]
    [InlineData("en-US,en;q=0.9", "en")]
    [InlineData("fr-FR,fr;q=0.9", "en")] // not supported: default
    [InlineData(null, "en")]
    public async Task Invalid_login_body_is_answered_in_the_requested_language(string? acceptLanguage, string expectedLanguage)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("", ""))
        };
        if (acceptLanguage is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        var response = await factory.CreateAnonymousClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedLanguage, Assert.Single(response.Content.Headers.ContentLanguage));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Stable in every language: the error code and the keys.
        Assert.Equal("validation_failed", body.RootElement.GetProperty("errorCode").GetString());
        var errors = body.RootElement.GetProperty("errors");
        Assert.Equal(["email", "password"], errors.EnumerateObject().Select(p => p.Name).Order());

        // FluentValidation's own message for "not empty" in that language.
        var expected = ValidatorOptions.Global.LanguageManager
            .GetString("NotEmptyValidator", new CultureInfo(expectedLanguage))
            .Replace("{PropertyName}", "password", StringComparison.Ordinal); // the display name is the JSON name
        Assert.Equal(expected, errors.GetProperty("password")[0].GetString());
    }

    [Theory]
    [InlineData("vi", "Mật khẩu phải có ít nhất 12 ký tự.", "Mật khẩu phải có ít nhất một chữ số ('0'-'9').", "Mật khẩu phải có ít nhất một chữ hoa ('A'-'Z').")]
    [InlineData("en", "Passwords must be at least 12 characters.", "Passwords must have at least one digit ('0'-'9').", "Passwords must have at least one uppercase ('A'-'Z').")]
    public async Task Password_policy_messages_of_identity_follow_the_requested_language(string language, params string[] expected)
    {
        var admin = factory.CreateClientAs(AppRoles.Admin);

        async Task<string[]> MessagesAsync(HttpMethod method, string url, object body, string key)
        {
            using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("Accept-Language", language);
            var response = await admin.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("errors").GetProperty(key).EnumerateArray().Select(m => m.GetString()!).ToArray();
        }

        // "weakpass": too short, no digit, no upper-case letter.
        var email = $"policy.{TestData.UniqueSuffix()}@users.test";
        Assert.Equal(expected, await MessagesAsync(HttpMethod.Post, "/api/users", new { email, password = "weakpass", role = AppRoles.Staff }, "password"));

        // The same policy on a password reset, under the key of that request's field.
        var created = await admin.PostAsJsonAsync("/api/users", new { email, password = $"Passw0rd-{Guid.NewGuid():N}", role = AppRoles.Staff });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var user = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var userId = user.RootElement.GetProperty("userId").GetGuid();
        Assert.Equal(expected, await MessagesAsync(HttpMethod.Post, $"/api/users/{userId}/reset-password", new { newPassword = "weakpass" }, "newPassword"));
    }

    [Theory]
    [InlineData("vi")]
    [InlineData("en")]
    public async Task Custom_validation_messages_follow_the_requested_language(string language)
    {
        var culture = new CultureInfo(language);
        var manager = factory.CreateClientAs(AppRoles.Manager);

        async Task<string?> FirstErrorAsync(HttpRequestMessage request, string key)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", language);
            var response = await manager.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(language, Assert.Single(response.Content.Headers.ContentLanguage));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("validation_failed", body.RootElement.GetProperty("errorCode").GetString());
            return body.RootElement.GetProperty("errors").GetProperty(key)[0].GetString();
        }

        // SKU format (custom Must rule).
        using var product = new HttpRequestMessage(HttpMethod.Post, "/api/products")
        {
            Content = JsonContent.Create(new { sku = "bad sku!", name = "Localised", price = 1, lowStockThreshold = 0 })
        };
        var skuMessage = await FirstErrorAsync(product, "sku");
        Assert.Equal(
            ValidationMessages.Find(ValidationMessages.Keys.SkuFormat, culture)
                .Replace("{PropertyName}", "sku", StringComparison.Ordinal)
                .Replace("{MaxLength}", "64", StringComparison.Ordinal),
            skuMessage);

        // Inverted date range (custom message on a comparison rule).
        using var orders = new HttpRequestMessage(HttpMethod.Get, "/api/orders?from=2026-09-02T00:00:00Z&to=2026-09-01T00:00:00Z");
        Assert.Equal(ValidationMessages.Find(ValidationMessages.Keys.DateRangeInverted, culture), await FirstErrorAsync(orders, "to"));

        // A sort field outside the whitelist (message with a list argument).
        using var sort = new HttpRequestMessage(HttpMethod.Get, "/api/customers?sortBy=secret");
        Assert.Equal(
            ValidationMessages.Find(ValidationMessages.Keys.SortByNotAllowed, culture)
                .Replace("{PropertyName}", "sort By", StringComparison.Ordinal) // FluentValidation's display name
                .Replace("{Fields}", "name, createdAt", StringComparison.Ordinal),
            await FirstErrorAsync(sort, "sortBy"));

        // The two languages really differ (guards against comparing English with English).
        Assert.Equal(language == "vi", skuMessage!.Contains("dài tối đa", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Service_level_validation_failures_are_localised_too()
    {
        var staff = factory.CreateClientAs(AppRoles.Staff);
        var unknownCustomer = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders/website")
        {
            Content = JsonContent.Create(new
            {
                externalOrderId = $"LOC-{TestData.UniqueSuffix()}",
                customerId = unknownCustomer,
                items = new[] { new { productId = Guid.NewGuid(), stock = 1 } }
            })
        };
        request.Headers.TryAddWithoutValidation("Accept-Language", "vi");

        var response = await staff.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = body.RootElement.GetProperty("errors");
        Assert.Equal($"Khách hàng '{unknownCustomer}' không tồn tại.", errors.GetProperty("customerId")[0].GetString());
        Assert.StartsWith("Sản phẩm '", errors.GetProperty("items[0].productId")[0].GetString());
    }

    [Fact]
    public void Vietnamese_and_english_messages_differ()
    {
        var manager = ValidatorOptions.Global.LanguageManager;

        Assert.NotEqual(
            manager.GetString("NotEmptyValidator", new CultureInfo("en")),
            manager.GetString("NotEmptyValidator", new CultureInfo("vi")));
    }
}
