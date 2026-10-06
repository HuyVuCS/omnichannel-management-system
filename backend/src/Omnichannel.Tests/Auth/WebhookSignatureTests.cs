using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Omnichannel.Api.Auth;

namespace Omnichannel.Tests.Auth;

/// <summary>Task 023: the signature scheme of webhooks - parsing, HMAC, timestamp window - and the webhook settings.</summary>
public sealed class WebhookSignatureTests
{
    private const string Secret = "unit-test-webhook-secret-0123456789abcdef";
    private const string OtherSecret = "another-unit-test-secret-0123456789abcdef";
    private const string Timestamp = "1791201600";

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"externalOrderId":"WEB-1","items":[]}""");

    // ---- the signature ----------------------------------------------------------------------------------------

    [Fact]
    public void The_signature_is_hmac_sha256_over_the_timestamp_a_dot_and_the_raw_body()
    {
        // Computed here from the definition, not with the code under test.
        var message = Encoding.ASCII.GetBytes(Timestamp + ".").Concat(Body).ToArray();
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), message);

        Assert.Equal(expected, WebhookSignature.Compute(Secret, Timestamp, Body));
        Assert.Equal(32, expected.Length);
        Assert.Equal(("X-Webhook-Timestamp", "X-Webhook-Signature", "v1"),
            (WebhookSignature.TimestampHeader, WebhookSignature.SignatureHeader, WebhookSignature.Version));
    }

    [Fact]
    public void Another_secret_timestamp_or_body_byte_gives_another_signature()
    {
        var signature = WebhookSignature.Compute(Secret, Timestamp, Body);

        Assert.NotEqual(signature, WebhookSignature.Compute(OtherSecret, Timestamp, Body));
        Assert.NotEqual(signature, WebhookSignature.Compute(Secret, "1791201601", Body));
        var changed = (byte[])Body.Clone();
        changed[^1] ^= 0x01;
        Assert.NotEqual(signature, WebhookSignature.Compute(Secret, Timestamp, changed));
        // Whitespace is content: the same JSON written another way is another body.
        Assert.NotEqual(signature, WebhookSignature.Compute(Secret, Timestamp, Encoding.UTF8.GetBytes("""{ "externalOrderId": "WEB-1", "items": [] }""")));
        // The timestamp is taken as it was sent, not as a number.
        Assert.NotEqual(signature, WebhookSignature.Compute(Secret, "0" + Timestamp, Body));
        // The dot separates: moving a character across it changes what is signed.
        Assert.NotEqual(
            WebhookSignature.Compute(Secret, "12", "3"u8),
            WebhookSignature.Compute(Secret, "1", "23"u8));
    }

    [Fact]
    public void A_matching_signature_is_accepted_for_any_of_the_channel_s_secrets()
    {
        var current = WebhookSignature.Compute(Secret, Timestamp, Body);
        var previous = WebhookSignature.Compute(OtherSecret, Timestamp, Body);
        var foreign = WebhookSignature.Compute("a-third-secret-nobody-configured-0123456789", Timestamp, Body);

        Assert.True(WebhookSignature.Matches([Secret], Timestamp, Body, [current]));
        // Rotation: both secrets are configured, the sender signs with either.
        Assert.True(WebhookSignature.Matches([Secret, OtherSecret], Timestamp, Body, [previous]));
        Assert.True(WebhookSignature.Matches([Secret, OtherSecret], Timestamp, Body, [current]));
        // Several signatures: one that matches is enough, wherever it stands.
        Assert.True(WebhookSignature.Matches([Secret], Timestamp, Body, [foreign, current]));
        Assert.True(WebhookSignature.Matches([Secret], Timestamp, Body, [current, foreign]));

        Assert.False(WebhookSignature.Matches([Secret], Timestamp, Body, [previous]));
        Assert.False(WebhookSignature.Matches([Secret, OtherSecret], Timestamp, Body, [foreign]));
        Assert.False(WebhookSignature.Matches([Secret], Timestamp, Body, []));
        Assert.False(WebhookSignature.Matches([], Timestamp, Body, [current]));
        // A value that is no SHA-256 (a parser that let one through must not make it match).
        Assert.False(WebhookSignature.Matches([Secret], Timestamp, Body, [current[..31], []]));
    }

    [Fact]
    public void A_signature_for_another_timestamp_or_body_does_not_match()
    {
        var signature = WebhookSignature.Compute(Secret, Timestamp, Body);

        Assert.False(WebhookSignature.Matches([Secret], "1791201601", Body, [signature]));
        Assert.False(WebhookSignature.Matches([Secret], Timestamp, Body.AsSpan(1), [signature]));
        Assert.False(WebhookSignature.Matches([Secret], Timestamp, [], [signature]));
    }

    // ---- the signature header ---------------------------------------------------------------------------------

    [Fact]
    public void One_v1_value_is_read_in_either_case_of_hex()
    {
        var signature = WebhookSignature.Compute(Secret, Timestamp, Body);

        Assert.Equal([signature], WebhookSignature.ParseSignatures("v1=" + Convert.ToHexStringLower(signature)));
        Assert.Equal([signature], WebhookSignature.ParseSignatures("v1=" + Convert.ToHexString(signature)));
        Assert.Equal([signature], WebhookSignature.ParseSignatures("  v1=" + Convert.ToHexStringLower(signature) + "  "));
    }

    [Fact]
    public void Several_v1_values_are_all_read_and_other_versions_are_ignored()
    {
        var (first, second) = (Hex('a'), Hex('b'));

        Assert.Equal(
            [Convert.FromHexString(first), Convert.FromHexString(second)],
            WebhookSignature.ParseSignatures($"v1={first},v1={second}"));
        Assert.Equal(
            [Convert.FromHexString(first), Convert.FromHexString(second)],
            WebhookSignature.ParseSignatures($"v0={Hex('c')}, v1={first} ,v2={Hex('d')},t=1791201600 , v1={second},"));
        // Only other versions: nothing to compare.
        Assert.Empty(WebhookSignature.ParseSignatures($"v0={first},v2={second},sha256={first}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    [InlineData("v1")]
    [InlineData("v1=")]
    [InlineData("=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    // Not 64 hex digits: too short, too long, not hex, base64.
    [InlineData("v1=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("v1=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("v1=zaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("v1=qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq=")]
    // The version is exact.
    [InlineData("V1=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("v1 =aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("v11=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    // One value per comma: a space does not separate.
    [InlineData("v1=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void A_header_without_a_well_formed_v1_value_has_no_signature(string? header)
    {
        Assert.Empty(WebhookSignature.ParseSignatures(header));
    }

    [Fact]
    public void A_malformed_value_does_not_hide_a_well_formed_one_next_to_it()
    {
        var good = Hex('a');

        Assert.Equal([Convert.FromHexString(good)], WebhookSignature.ParseSignatures($"v1=nonsense,v1={good},v1="));
    }

    // ---- the timestamp ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1791201600", 1791201600L)]
    [InlineData("0001791201600", 1791201600L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void A_timestamp_is_a_number_of_whole_seconds_in_digits(string header, long expected)
    {
        Assert.True(WebhookSignature.TryParseTimestamp(header, out var seconds));
        Assert.Equal(expected, seconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("now")]
    [InlineData("-1791201600")]
    [InlineData("+1791201600")]
    [InlineData(" 1791201600")]
    [InlineData("1791201600 ")]
    [InlineData("1791201600.5")]
    [InlineData("1,791,201,600")]
    [InlineData("1791201600,1791201600")]
    [InlineData("0x6AC3A000")]
    [InlineData("1.7912016e9")]
    [InlineData("2026-10-05T12:00:00Z")]
    [InlineData("9223372036854775808")]
    [InlineData("١٧٩١٢٠١٦٠٠")]
    public void Anything_else_is_no_timestamp(string? header)
    {
        Assert.False(WebhookSignature.TryParseTimestamp(header, out _));
    }

    [Theory]
    // Exactly the tolerance, before and after: accepted.
    [InlineData(-300, true)]
    [InlineData(300, true)]
    [InlineData(0, true)]
    [InlineData(-299, true)]
    [InlineData(299, true)]
    // One second beyond: refused.
    [InlineData(-301, false)]
    [InlineData(301, false)]
    [InlineData(-86_400, false)]
    [InlineData(86_400, false)]
    public void The_timestamp_must_be_within_the_tolerance_of_the_clock_in_both_directions(int offsetSeconds, bool accepted)
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var sentAt = now.ToUnixTimeSeconds() + offsetSeconds;

        Assert.Equal(accepted, WebhookSignature.IsWithinTolerance(sentAt, now, TimeSpan.FromMinutes(5)));
        // The fraction of the clock's second does not move the edge.
        Assert.Equal(accepted, WebhookSignature.IsWithinTolerance(sentAt, now.AddMilliseconds(999), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void A_timestamp_far_from_any_clock_is_refused_without_overflow()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.False(WebhookSignature.IsWithinTolerance(0, now, TimeSpan.FromMinutes(15)));
        Assert.False(WebhookSignature.IsWithinTolerance(long.MaxValue, now, TimeSpan.FromMinutes(15)));
    }

    // ---- the settings -----------------------------------------------------------------------------------------

    private readonly WebhookOptionsValidator _validator = new();

    [Fact]
    public void The_defaults_are_valid_and_as_documented()
    {
        var options = new WebhookOptions();

        Assert.Equal(
            (TimeSpan.FromMinutes(5), 65_536, 120),
            (options.TimestampTolerance, options.MaxBodyBytes, options.PermitsPerMinute));
        Assert.Empty(options.Channels);
        Assert.Equal((32, 2), (WebhookOptions.MinSecretLength, WebhookOptions.MaxSecretsPerChannel));
        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("00:00:30")]
    [InlineData("00:05:00")]
    [InlineData("00:15:00")]
    public void A_tolerance_from_thirty_seconds_to_fifteen_minutes_is_valid(string tolerance)
    {
        Assert.True(_validator.Validate(null, new WebhookOptions { TimestampTolerance = TimeSpan.Parse(tolerance) }).Succeeded);
    }

    [Theory]
    [InlineData("00:00:29.999")]
    [InlineData("00:00:00")]
    [InlineData("-00:05:00")]
    [InlineData("00:15:00.001")]
    [InlineData("1.00:00:00")]
    public void A_tolerance_outside_that_range_fails_the_start(string tolerance)
    {
        var result = _validator.Validate(null, new WebhookOptions { TimestampTolerance = TimeSpan.Parse(tolerance) });

        Assert.True(result.Failed);
        Assert.Contains("Webhooks:TimestampTolerance", result.FailureMessage);
    }

    [Theory]
    [InlineData(0, 120, "Webhooks:MaxBodyBytes")]
    [InlineData(-1, 120, "Webhooks:MaxBodyBytes")]
    [InlineData(65_536, 0, "Webhooks:PermitsPerMinute")]
    [InlineData(65_536, -5, "Webhooks:PermitsPerMinute")]
    public void A_limit_below_one_fails_the_start(int maxBodyBytes, int permitsPerMinute, string setting)
    {
        var result = _validator.Validate(null, new WebhookOptions { MaxBodyBytes = maxBodyBytes, PermitsPerMinute = permitsPerMinute });

        Assert.True(result.Failed);
        Assert.Contains(setting, result.FailureMessage);
        Assert.True(_validator.Validate(null, new WebhookOptions { MaxBodyBytes = 1, PermitsPerMinute = 1 }).Succeeded);
    }

    [Fact]
    public void One_or_two_secrets_of_at_least_32_characters_are_valid()
    {
        var options = WithSecrets(("website", [new string('a', 32)]), ("pos", [new string('b', 32), new string('c', 64)]));

        Assert.True(_validator.Validate(null, options).Succeeded);
        Assert.Single(options.SecretsOf("website"));
        Assert.Equal(2, options.SecretsOf("pos").Count);
    }

    [Fact]
    public void A_channel_without_secrets_has_no_webhook_and_is_valid()
    {
        var options = WithSecrets(("website", []));
        options.Channels["pos"] = new WebhookChannelOptions { Secrets = null! };
        options.Channels["digital-platform"] = null!;

        Assert.True(_validator.Validate(null, options).Succeeded);
        Assert.Empty(options.SecretsOf("website"));
        Assert.Empty(options.SecretsOf("pos"));
        Assert.Empty(options.SecretsOf("digital-platform"));
        Assert.Empty(options.SecretsOf("never-configured"));
        Assert.Empty(options.SecretsOf(string.Empty));
    }

    [Theory]
    [InlineData("short-webhook-secret-of-31-char")]
    [InlineData("")]
    [InlineData("                                    ")]
    public void A_configured_secret_that_is_too_short_fails_the_start_naming_the_channel_and_never_the_value(string secret)
    {
        var result = _validator.Validate(null, WithSecrets(("website", [new string('a', 32)]), ("pos", [new string('b', 40), secret])));

        Assert.True(result.Failed);
        Assert.Contains("Webhooks:Channels:pos:Secrets", result.FailureMessage);
        Assert.DoesNotContain("Webhooks:Channels:website", result.FailureMessage);
        if (secret.Trim().Length > 0)
        {
            Assert.DoesNotContain(secret, result.FailureMessage);
        }

        Assert.DoesNotContain(new string('b', 40), result.FailureMessage);
        // Not even how long it was.
        Assert.DoesNotContain(secret.Length.ToString(), result.FailureMessage.Replace("32", string.Empty));
    }

    [Fact]
    public void More_than_two_secrets_fail_the_start()
    {
        var result = _validator.Validate(null, WithSecrets(("pos", [new string('a', 32), new string('b', 32), new string('c', 32)])));

        Assert.True(result.Failed);
        Assert.Contains("Webhooks:Channels:pos:Secrets", result.FailureMessage);
        Assert.DoesNotContain(new string('c', 32), result.FailureMessage);
    }

    [Fact]
    public void The_settings_bind_from_configuration_and_a_channel_is_found_whatever_the_case_of_its_key()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Webhooks:TimestampTolerance"] = "00:01:00",
                ["Webhooks:MaxBodyBytes"] = "1024",
                ["Webhooks:PermitsPerMinute"] = "7",
                ["Webhooks:Channels:WebSite:Secrets:0"] = Secret,
                ["Webhooks:Channels:digital-platform:Secrets:0"] = Secret,
                ["Webhooks:Channels:digital-platform:Secrets:1"] = OtherSecret
            })
            .Build();
        var options = new WebhookOptions();

        configuration.GetSection(WebhookOptions.SectionName).Bind(options);

        Assert.Equal((TimeSpan.FromMinutes(1), 1024, 7), (options.TimestampTolerance, options.MaxBodyBytes, options.PermitsPerMinute));
        Assert.Equal([Secret], options.SecretsOf("website"));
        Assert.Equal([Secret, OtherSecret], options.SecretsOf("digital-platform"));
        Assert.Empty(options.SecretsOf("pos"));
        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    // ---- the caller -------------------------------------------------------------------------------------------

    [Fact]
    public void The_caller_of_a_webhook_is_named_after_its_channel_and_is_no_authenticated_user()
    {
        var principal = WebhookCaller.PrincipalOf("digital-platform");

        Assert.Equal("webhook:digital-platform", WebhookCaller.ActorOf("digital-platform"));
        var claim = Assert.Single(principal.Claims);
        Assert.Equal((JwtClaimNames.Subject, "webhook:digital-platform"), (claim.Type, claim.Value));
        // Nobody to the authorization system: not authenticated, no role.
        Assert.False(principal.Identity!.IsAuthenticated);
        Assert.Null(principal.Identity.AuthenticationType);
        Assert.All(Omnichannel.Application.Auth.AppRoles.All, role => Assert.False(principal.IsInRole(role)));
        // The actor fits the audit columns (100) for the longest slug (64).
        Assert.True(WebhookCaller.ActorOf(new string('a', 64)).Length <= 100);
    }

    [Theory]
    [InlineData("website", "website")]
    [InlineData("  WebSite ", "website")]
    [InlineData("digital-platform", "digital-platform")]
    [InlineData(null, "(not a slug)")]
    [InlineData("", "(not a slug)")]
    [InlineData("web site", "(not a slug)")]
    [InlineData("website\nWebhook refused with 202", "(not a slug)")]
    [InlineData("../orders", "(not a slug)")]
    [InlineData("-website", "(not a slug)")]
    public void A_slug_is_logged_only_when_it_has_the_form_of_a_slug(string? routeValue, string logged)
    {
        Assert.Equal(logged, WebhookCaller.SlugForLog(routeValue));
        Assert.Equal("(not a slug)", WebhookCaller.SlugForLog(new string('a', 65)));
        Assert.Equal(new string('a', 64), WebhookCaller.SlugForLog(new string('a', 64)));
    }

    private static string Hex(char digit) => new(digit, 64);

    private static WebhookOptions WithSecrets(params (string Slug, string[] Secrets)[] channels)
    {
        var options = new WebhookOptions();
        foreach (var (slug, secrets) in channels)
        {
            options.Channels[slug] = new WebhookChannelOptions { Secrets = [.. secrets] };
        }

        return options;
    }
}
