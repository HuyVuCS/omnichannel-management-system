namespace Omnichannel.Application.Auth;

public sealed record LoginRequest(string Email, string Password);

public sealed record AuthenticatedUser(Guid Id, string Email, IReadOnlyList<string> Roles);

public sealed record LoginResult(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, AuthenticatedUser User);

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);
