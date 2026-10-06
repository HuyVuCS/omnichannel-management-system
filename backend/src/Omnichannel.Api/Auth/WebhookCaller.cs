using System.Security.Claims;
using System.Text.RegularExpressions;
using Omnichannel.Application.Channels;

namespace Omnichannel.Api.Auth;

/// <summary>
/// Who a request is once its webhook signature has been verified: <c>webhook:{slug}</c>, the actor in the audit fields of
/// everything the request writes (<see cref="HttpCurrentUser"/> reads the subject of the request's principal).
/// <para>
/// The identity lives in that one request and nowhere else: it is set by the signature filter of the webhook action
/// (<see cref="Filters.WebhookSignatureFilter"/>) and replaces whatever principal the request arrived with - a bearer
/// token never acts through a webhook. It is deliberately <b>not an authenticated identity</b> and carries no role: every
/// authorization policy of the API, the fallback policy included, refuses it. No token is issued for it, and no other
/// endpoint looks at the signature headers.
/// </para>
/// </summary>
public static partial class WebhookCaller
{
    public const string ActorPrefix = "webhook:";

    public static string ActorOf(string channelSlug) => ActorPrefix + channelSlug;

    /// <summary>A principal whose only claim is its subject, <see cref="ActorOf"/>: no authentication type, no role.</summary>
    public static ClaimsPrincipal PrincipalOf(string channelSlug) =>
        new(new ClaimsIdentity(
            [new Claim(JwtClaimNames.Subject, ActorOf(channelSlug))],
            authenticationType: null,
            JwtClaimNames.Subject,
            JwtClaimNames.Role));

    /// <summary>The slug of the address as the channels store theirs: trimmed, lower case.</summary>
    public static string NormaliseSlug(string? routeValue) => (routeValue ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// The slug for a log line. The address is whatever a stranger typed: only something that has the form of a slug is
    /// logged as it is.
    /// </summary>
    public static string SlugForLog(string? routeValue)
    {
        var slug = NormaliseSlug(routeValue);
        return slug.Length <= CreateChannelRequestValidator.MaxSlugLength && SlugPattern().IsMatch(slug) ? slug : "(not a slug)";
    }

    [GeneratedRegex(CreateChannelRequestValidator.SlugPattern)]
    private static partial Regex SlugPattern();
}
