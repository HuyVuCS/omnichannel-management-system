using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Omnichannel.Api.Hubs;
using Omnichannel.Application.Auth;

namespace Omnichannel.Api.Auth;

public static class AuthServiceCollectionExtensions
{
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured lazily from validated JwtOptions (not read from IConfiguration at registration time).
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;

                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = JwtAccessTokenIssuer.CreateSigningKey(jwt.SigningKey),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtClaimNames.Subject,
                    RoleClaimType = JwtClaimNames.Role
                };

                bearer.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ReadHubTokenFromQuery,
                    // Stateless tokens live for up to an hour: reject those of users whose rights changed meanwhile.
                    OnTokenValidated = TokenRevocation.ValidateAsync
                };
            });

        return services;
    }

    /// <summary>
    /// Browsers cannot set an <c>Authorization</c> header on WebSocket and server-sent-event requests, so the realtime hub -
    /// and only the hub - also accepts the token as the <c>access_token</c> query parameter. Every other path reads the
    /// header only. The value must never be logged (<see cref="Middleware.AccessTokenRedactionEnricher"/>).
    /// </summary>
    private static Task ReadHubTokenFromQuery(MessageReceivedContext context)
    {
        var request = context.HttpContext.Request;
        if (request.Path.StartsWithSegments(NotificationsHub.Path)
            && !request.Headers.ContainsKey("Authorization")
            && request.Query.TryGetValue(HubAccessTokenParameter, out var token)
            && !string.IsNullOrEmpty(token))
        {
            context.Token = token;
        }

        return Task.CompletedTask;
    }

    public const string HubAccessTokenParameter = "access_token";

    public static IServiceCollection AddRbacAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(AuthorizationPolicies.AdminOnly, policy => policy.RequireRole(AppRoles.Admin))
            .AddPolicy(AuthorizationPolicies.ManagerOrAdmin, policy => policy.RequireRole(AppRoles.Admin, AppRoles.Manager))
            .AddPolicy(AuthorizationPolicies.AnyStaff, policy => policy.RequireRole(AppRoles.Admin, AppRoles.Manager, AppRoles.Staff));

        return services;
    }
}
