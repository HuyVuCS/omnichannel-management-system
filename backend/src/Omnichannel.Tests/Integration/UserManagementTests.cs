using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Users;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Identity;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 009: <c>/api/users</c> (Admin only), token revocation through the security stamp, audit events. Every test creates
/// its own users through the API; the factory's shared role users are only used as actors and are never modified.
/// The <c>last_admin</c> guard needs a database without other admins: see <see cref="UserGuardTests"/>.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UserManagementTests(ApiFactory factory)
{
    private const string AnyAuthenticatedEndpoint = "/api/auth/me";

    private readonly HttpClient _admin = factory.CreateClientAs(AppRoles.Admin);

    [Theory]
    [InlineData(AppRoles.Admin)]
    [InlineData(AppRoles.Manager)]
    [InlineData(AppRoles.Staff)]
    public async Task Admin_creates_a_user_who_can_log_in_with_that_role(string role)
    {
        var email = NewEmail();
        var password = NewPassword();

        var response = await _admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email, password, role.ToUpperInvariant()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadAsync<UserResponse>();
        Assert.Equal((email, role, false, (DateTimeOffset?)null, 0),
            (created.Email, created.Role, created.IsLocked, created.LockoutEnd, created.AccessFailedCount));
        Assert.EndsWith($"/api/users/{created.UserId}", response.Headers.Location!.AbsolutePath);
        Assert.Equal(created, await GetUserAsync(created.UserId));

        var login = await LoginAsync(email, password);
        Assert.Equal((created.UserId, role), (login.User.Id, Assert.Single(login.User.Roles)));

        var user = factory.CreateClientWithToken(login.AccessToken);
        var me = await (await user.GetAsync("/api/auth/me")).ReadAsync<AuthenticatedUser>();
        Assert.Equal((created.UserId, email, role), (me.Id, me.Email, Assert.Single(me.Roles)));

        // The token carries the role: only the new Admin may manage users.
        var expected = role == AppRoles.Admin ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
        Assert.Equal(expected, (await user.GetAsync("/api/users")).StatusCode);
    }

    public static TheoryData<string, string, bool> Endpoints()
    {
        const string id = "0199a1b0-0000-7000-8000-00000000abcd"; // no such user: authorization answers first
        return new TheoryData<string, string, bool>
        {
            { "GET", "/api/users", false },
            { "GET", $"/api/users/{id}", false },
            { "POST", "/api/users", true },
            { "PUT", $"/api/users/{id}/role", true },
            { "POST", $"/api/users/{id}/lock", false },
            { "POST", $"/api/users/{id}/unlock", false },
            { "POST", $"/api/users/{id}/reset-password", true }
        };
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Only_admins_reach_user_management(string method, string url, bool hasBody)
    {
        HttpRequestMessage Request() => new(new HttpMethod(method), url)
        {
            // A body that would be valid, so only authorization can be the reason for the answer.
            Content = hasBody
                ? JsonContent.Create(new { email = NewEmail(), password = NewPassword(), role = AppRoles.Staff, newPassword = NewPassword() })
                : null
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClientAs(AppRoles.Manager).SendAsync(Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClientAs(AppRoles.Staff).SendAsync(Request())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().SendAsync(Request())).StatusCode);
    }

    [Fact]
    public async Task Duplicate_email_weak_password_and_unknown_role_are_rejected()
    {
        var (user, email, _) = await CreateUserAsync(AppRoles.Staff);

        // Same address in another casing.
        var duplicate = await _admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email.ToUpperInvariant(), NewPassword(), AppRoles.Manager));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(UserErrorCodes.EmailExists, await duplicate.ReadErrorCodeAsync());
        Assert.Equal(AppRoles.Staff, (await GetUserAsync(user.UserId)).Role);

        // Identity's policy: at least 12 characters with a digit, a lower-case and an upper-case letter.
        const string weak = "short-pw";
        var weakPassword = await _admin.PostAsJsonAsync("/api/users", new CreateUserRequest(NewEmail(), weak, AppRoles.Staff));
        Assert.Equal(HttpStatusCode.BadRequest, weakPassword.StatusCode);
        var weakBody = await weakPassword.Content.ReadAsStringAsync();
        var passwordErrors = JsonNode.Parse(weakBody)!["errors"]!["password"]!.AsArray();
        Assert.True(passwordErrors.Count >= 3, weakBody); // too short, no digit, no upper-case letter
        Assert.DoesNotContain(weak, weakBody);

        var unknownRole = await _admin.PostAsJsonAsync("/api/users", new CreateUserRequest(NewEmail(), NewPassword(), "Owner"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownRole.StatusCode);
        Assert.Equal(["role"], await ErrorKeysAsync(unknownRole));

        var invalid = await _admin.PostAsJsonAsync("/api/users", new CreateUserRequest("not-an-email", "", ""));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(["email", "password", "role"], await ErrorKeysAsync(invalid));

        var unknownRoleChange = await _admin.PutAsJsonAsync($"/api/users/{user.UserId}/role", new ChangeUserRoleRequest("Root"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownRoleChange.StatusCode);
        Assert.Equal(["role"], await ErrorKeysAsync(unknownRoleChange));
    }

    [Fact]
    public async Task Concurrent_creates_with_the_same_email_store_one_user()
    {
        var email = NewEmail();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            factory.CreateClientAs(AppRoles.Admin).PostAsJsonAsync("/api/users", new CreateUserRequest(email, NewPassword(), AppRoles.Staff))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        var page = await ListAsync($"search={Uri.EscapeDataString(email)}");
        Assert.Equal(AppRoles.Staff, Assert.Single(page.Items).Role); // one user, and it has its role
    }

    [Fact]
    public async Task Users_are_listed_with_search_role_and_lock_filters()
    {
        var tag = TestData.UniqueSuffix();
        var (a, _, _) = await CreateUserAsync(AppRoles.Staff, $"a.{tag}@list.test");
        var (b, _, _) = await CreateUserAsync(AppRoles.Manager, $"b.{tag}@list.test");
        var (c, _, _) = await CreateUserAsync(AppRoles.Staff, $"c.{tag}@list.test");
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"/api/users/{c.UserId}/lock", null)).StatusCode);

        var all = await ListAsync($"search={tag.ToUpperInvariant()}");
        Assert.Equal([a.UserId, b.UserId, c.UserId], all.Items.Select(u => u.UserId));
        Assert.Equal(3, all.TotalCount);

        Assert.Equal([c.UserId, b.UserId, a.UserId], (await ListAsync($"search={tag}&sortBy=email&sortDir=desc")).Items.Select(u => u.UserId));
        Assert.Equal([a.UserId, c.UserId], (await ListAsync($"search={tag}&role=staff")).Items.Select(u => u.UserId));
        Assert.Equal([c.UserId], (await ListAsync($"search={tag}&locked=true")).Items.Select(u => u.UserId));
        Assert.Equal([a.UserId, b.UserId], (await ListAsync($"search={tag}&locked=false")).Items.Select(u => u.UserId));

        var page2 = await ListAsync($"search={tag}&pageSize=2&page=2");
        Assert.Equal([c.UserId], page2.Items.Select(u => u.UserId));
        Assert.Equal(3, page2.TotalCount);

        // "%" is a literal, not a wildcard.
        Assert.Empty((await ListAsync($"search={Uri.EscapeDataString($"%{tag}%list")}")).Items);

        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync("/api/users?role=Owner")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.GetAsync("/api/users?sortBy=role")).StatusCode);

        var unknown = await _admin.GetAsync($"/api/users/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(UserErrorCodes.UserNotFound, await unknown.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Role_change_rejects_the_old_token_and_a_new_login_carries_the_new_role()
    {
        var (user, email, password) = await CreateUserAsync(AppRoles.Staff);
        var oldToken = factory.CreateClientWithToken((await LoginAsync(email, password)).AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await oldToken.GetAsync(AnyAuthenticatedEndpoint)).StatusCode);

        var changed = await _admin.PutAsJsonAsync($"/api/users/{user.UserId}/role", new ChangeUserRoleRequest(AppRoles.Manager));

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(AppRoles.Manager, (await changed.ReadAsync<UserResponse>()).Role);

        // Rejected at once on this instance (cache entry evicted), not only after the cache window.
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldToken.GetAsync(AnyAuthenticatedEndpoint)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldToken.GetAsync("/api/products")).StatusCode);

        var login = await LoginAsync(email, password);
        Assert.Equal(AppRoles.Manager, Assert.Single(login.User.Roles));
        var newToken = factory.CreateClientWithToken(login.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await newToken.GetAsync(AnyAuthenticatedEndpoint)).StatusCode);

        // Setting the same role again changes nothing: the token stays valid.
        var same = await _admin.PutAsJsonAsync($"/api/users/{user.UserId}/role", new ChangeUserRoleRequest("manager"));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await newToken.GetAsync(AnyAuthenticatedEndpoint)).StatusCode);
    }

    [Fact]
    public async Task Change_made_by_another_instance_is_picked_up_within_the_cache_window()
    {
        var (user, email, password) = await CreateUserAsync(AppRoles.Staff);
        var token = factory.CreateClientWithToken((await LoginAsync(email, password)).AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync("/api/products")).StatusCode); // state is cached now

        // What another API instance would do: new stamp in the database, no eviction in this process.
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var stored = await userManager.FindByIdAsync(user.UserId.ToString());
            Assert.True((await userManager.UpdateSecurityStampAsync(stored!)).Succeeded);
        }

        // Still served from the cache (the window is the documented upper bound for revocation) ...
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync("/api/products")).StatusCode);

        // ... and rejected once the window has passed.
        await Eventually.WaitAsync(
            async () => (await token.GetAsync("/api/products")).StatusCode,
            status => status == HttpStatusCode.Unauthorized,
            ApiFactory.RevocationCacheDuration + TimeSpan.FromSeconds(10),
            "old token rejected after the cache window");
    }

    [Fact]
    public async Task Locked_user_cannot_log_in_or_use_the_old_token_until_unlocked()
    {
        var (user, email, password) = await CreateUserAsync(AppRoles.Staff);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync(email, "Wrong-passw0rd-1")).StatusCode);
        Assert.Equal(1, (await GetUserAsync(user.UserId)).AccessFailedCount);
        var oldToken = factory.CreateClientWithToken((await LoginAsync(email, password)).AccessToken);

        var locked = await _admin.PostAsync($"/api/users/{user.UserId}/lock", null);

        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        var lockedUser = await locked.ReadAsync<UserResponse>();
        Assert.True(lockedUser.IsLocked);
        Assert.Equal(9999, lockedUser.LockoutEnd!.Value.Year);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync(email, password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldToken.GetAsync(AnyAuthenticatedEndpoint)).StatusCode);

        // Locking again is a no-op.
        Assert.Equal(lockedUser, await (await _admin.PostAsync($"/api/users/{user.UserId}/lock", null)).ReadAsync<UserResponse>());

        // Failed attempts on record (e.g. from before the lock) are cleared by the unlock.
        await factory.QueryDbAsync(db => db.Users.Where(u => u.Id == user.UserId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.AccessFailedCount, 3)));

        var unlocked = await _admin.PostAsync($"/api/users/{user.UserId}/unlock", null);

        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        var unlockedUser = await unlocked.ReadAsync<UserResponse>();
        Assert.Equal((false, (DateTimeOffset?)null, 0), (unlockedUser.IsLocked, unlockedUser.LockoutEnd, unlockedUser.AccessFailedCount));

        var login = await LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClientWithToken(login.AccessToken).GetAsync(AnyAuthenticatedEndpoint)).StatusCode);

        // The token from before the lock stays revoked.
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldToken.GetAsync(AnyAuthenticatedEndpoint)).StatusCode);
    }

    [Fact]
    public async Task Lockout_after_failed_logins_also_rejects_the_existing_token_until_an_admin_unlocks()
    {
        var (user, email, password) = await CreateUserAsync(AppRoles.Staff);
        var token = factory.CreateClientWithToken((await LoginAsync(email, password)).AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync(AnyAuthenticatedEndpoint)).StatusCode);

        // Identity lockout: 5 failed attempts lock the account for 15 minutes (no new security stamp).
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync(email, "Wrong-passw0rd-1")).StatusCode);
        }

        var lockedOut = await GetUserAsync(user.UserId);
        Assert.True(lockedOut.IsLocked);
        Assert.InRange(lockedOut.LockoutEnd!.Value, DateTimeOffset.UtcNow.AddMinutes(10), DateTimeOffset.UtcNow.AddMinutes(16));
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync(email, password)).StatusCode);

        // The login path does not evict the cached state: the token is rejected within the cache window.
        await Eventually.WaitAsync(
            async () => (await token.GetAsync("/api/products")).StatusCode,
            status => status == HttpStatusCode.Unauthorized,
            ApiFactory.RevocationCacheDuration + TimeSpan.FromSeconds(10),
            "token of a locked-out user rejected");

        // Unlock keeps the security stamp, so the same token is accepted again - at once (eviction).
        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsync($"/api/users/{user.UserId}/unlock", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync("/api/products")).StatusCode);
        await LoginAsync(email, password);
    }

    [Fact]
    public async Task Password_reset_replaces_the_password_and_revokes_tokens()
    {
        var (user, email, oldPassword) = await CreateUserAsync(AppRoles.Manager);
        var oldToken = factory.CreateClientWithToken((await LoginAsync(email, oldPassword)).AccessToken);

        // A password the policy rejects changes nothing (the removal of the old one is rolled back).
        const string weak = "alllowercase";
        var rejected = await _admin.PostAsJsonAsync($"/api/users/{user.UserId}/reset-password", new ResetUserPasswordRequest(weak));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var rejectedBody = await rejected.Content.ReadAsStringAsync();
        Assert.NotNull(JsonNode.Parse(rejectedBody)!["errors"]!["newPassword"]);
        Assert.DoesNotContain(weak, rejectedBody);
        Assert.Equal(HttpStatusCode.OK, (await TryLoginAsync(email, oldPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await oldToken.GetAsync(AnyAuthenticatedEndpoint)).StatusCode);

        var newPassword = NewPassword();
        var reset = await _admin.PostAsJsonAsync($"/api/users/{user.UserId}/reset-password", new ResetUserPasswordRequest(newPassword));

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync(email, oldPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldToken.GetAsync(AnyAuthenticatedEndpoint)).StatusCode);
        var login = await LoginAsync(email, newPassword);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClientWithToken(login.AccessToken).GetAsync(AnyAuthenticatedEndpoint)).StatusCode);

        var unknown = await _admin.PostAsJsonAsync($"/api/users/{Guid.NewGuid()}/reset-password", new ResetUserPasswordRequest(NewPassword()));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(UserErrorCodes.UserNotFound, await unknown.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Admin_cannot_change_own_role_or_lock_self_but_can_reset_own_password()
    {
        var (self, email, password) = await CreateUserAsync(AppRoles.Admin);
        var client = factory.CreateClientWithToken((await LoginAsync(email, password)).AccessToken);

        var role = await client.PutAsJsonAsync($"/api/users/{self.UserId}/role", new ChangeUserRoleRequest(AppRoles.Staff));
        Assert.Equal(HttpStatusCode.Conflict, role.StatusCode);
        Assert.Equal(UserErrorCodes.CannotModifySelf, await role.ReadErrorCodeAsync());

        var locked = await client.PostAsync($"/api/users/{self.UserId}/lock", null);
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        Assert.Equal(UserErrorCodes.CannotModifySelf, await locked.ReadErrorCodeAsync());

        var unchanged = await GetUserAsync(self.UserId);
        Assert.Equal((AppRoles.Admin, false), (unchanged.Role, unchanged.IsLocked));

        var newPassword = NewPassword();
        var reset = await client.PostAsJsonAsync($"/api/users/{self.UserId}/reset-password", new ResetUserPasswordRequest(newPassword));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(AnyAuthenticatedEndpoint)).StatusCode); // own old token too
        await LoginAsync(email, newPassword);
    }

    [Fact]
    public async Task Every_operation_writes_an_audit_event_and_nothing_logs_passwords_or_emails()
    {
        var startedAt = DateTimeOffset.UtcNow;
        var correlationId = $"users-{TestData.UniqueSuffix()}";
        var actorId = factory.UserIdOf(AppRoles.Admin);
        var email = NewEmail();
        var (firstPassword, secondPassword) = (NewPassword(), NewPassword());

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Add("X-Correlation-Id", correlationId);
            return await _admin.SendAsync(request);
        }

        var user = await (await SendAsync(HttpMethod.Post, "/api/users", new CreateUserRequest(email, firstPassword, AppRoles.Staff))).ReadAsync<UserResponse>();
        await LoginAsync(email, firstPassword);
        await TryLoginAsync(email, "Wrong-passw0rd-1");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Put, $"/api/users/{user.UserId}/role", new ChangeUserRoleRequest(AppRoles.Manager))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/users/{user.UserId}/lock")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/users/{user.UserId}/unlock")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, $"/api/users/{user.UserId}/reset-password", new ResetUserPasswordRequest(secondPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, $"/api/users/{user.UserId}/reset-password", new ResetUserPasswordRequest("weak"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, "/api/users", new CreateUserRequest(email, firstPassword, AppRoles.Staff))).StatusCode);

        var events = await factory.QueryDbAsync(db => db.SystemEventLogs.AsNoTracking()
            .Where(l => l.CorrelationId == correlationId)
            .OrderBy(l => l.LogId)
            .ToListAsync());

        // Failed operations (weak password, duplicate) leave no event.
        Assert.Equal(
            [UserEvents.Created, UserEvents.RoleChanged, UserEvents.Locked, UserEvents.Unlocked, UserEvents.PasswordReset],
            events.Select(e => e.EventType));
        Assert.All(events, e =>
        {
            Assert.Equal(SystemEventLevel.Information, e.Level);
            var payload = JsonNode.Parse(e.Payload!)!;
            Assert.Equal(user.UserId, payload["targetUserId"]!.GetValue<Guid>());
            Assert.Equal(actorId.ToString(), payload["actorUserId"]!.GetValue<string>());
        });
        Assert.Equal(AppRoles.Staff, JsonNode.Parse(events[0].Payload!)!["role"]!.GetValue<string>());
        var roleChange = JsonNode.Parse(events[1].Payload!)!;
        Assert.Equal((AppRoles.Staff, AppRoles.Manager), (roleChange["from"]!.GetValue<string>(), roleChange["to"]!.GetValue<string>()));

        // Neither the audit rows nor anything the API logged contains a password or the e-mail address (ids only).
        string[] secrets = [firstPassword, secondPassword, "Wrong-passw0rd-1", email, email.ToUpperInvariant()];
        var written = events.Select(e => e.Payload!).Concat(factory.Logs.RenderedSince(startedAt)).ToList();
        Assert.NotEmpty(factory.Logs.RenderedSince(startedAt));
        Assert.Contains(written, line => line.Contains(user.UserId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.All(secrets, secret => Assert.DoesNotContain(written, line => line.Contains(secret, StringComparison.OrdinalIgnoreCase)));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static string NewEmail() => $"user.{TestData.UniqueSuffix()}@users.test";

    /// <summary>Satisfies the Identity policy (12+ characters, digit, lower- and upper-case letter); unique per call.</summary>
    private static string NewPassword() => $"Passw0rd-{Guid.NewGuid():N}";

    private async Task<(UserResponse User, string Email, string Password)> CreateUserAsync(string role, string? email = null)
    {
        email ??= NewEmail();
        var password = NewPassword();
        var response = await _admin.PostAsJsonAsync("/api/users", new CreateUserRequest(email, password, role));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadAsync<UserResponse>(), email, password);
    }

    private async Task<UserResponse> GetUserAsync(Guid userId)
    {
        var response = await _admin.GetAsync($"/api/users/{userId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<UserResponse>();
    }

    private async Task<PagedResult<UserResponse>> ListAsync(string query)
    {
        var response = await _admin.GetAsync($"/api/users?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<PagedResult<UserResponse>>();
    }

    /// <summary>Each login comes from its own "IP", so the login rate limit never interferes.</summary>
    private Task<HttpResponseMessage> TryLoginAsync(string email, string password) =>
        factory.CreateAnonymousClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));

    private async Task<LoginResult> LoginAsync(string email, string password)
    {
        var response = await TryLoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<LoginResult>();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order().ToArray();
    }
}
