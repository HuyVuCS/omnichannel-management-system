using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Omnichannel.Api.Auth;

namespace Omnichannel.Api.Hubs;

/// <summary>
/// Realtime hints for the dashboard (doc §12). Server to client only: clients receive <c>hints</c>
/// (<see cref="Application.Realtime.RealtimeHintMessage"/>) and refetch through the API; there is nothing a client can call.
/// SignalR is a notification channel, never the source of truth: after a reconnect, or when a message says
/// <c>resync</c>, the client refetches what it shows.
/// </summary>
[Authorize(Policy = AuthorizationPolicies.AnyStaff)]
public sealed class NotificationsHub : Hub
{
    /// <summary>Under <c>/api</c>, so the dev proxy and a reverse proxy forward one prefix.</summary>
    public const string Path = "/api/hubs/notifications";
}
