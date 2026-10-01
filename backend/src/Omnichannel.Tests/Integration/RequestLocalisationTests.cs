using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentValidation;
using Omnichannel.Application.Auth;

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

    [Fact]
    public void Vietnamese_and_english_messages_differ()
    {
        var manager = ValidatorOptions.Global.LanguageManager;

        Assert.NotEqual(
            manager.GetString("NotEmptyValidator", new CultureInfo("en")),
            manager.GetString("NotEmptyValidator", new CultureInfo("vi")));
    }
}
