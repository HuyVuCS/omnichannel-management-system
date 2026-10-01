using System.Linq.Expressions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Messaging;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Users;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Infrastructure.Identity;

/// <summary>
/// User management on ASP.NET Core Identity. Every write runs in one transaction together with its audit event
/// (<see cref="UserEvents"/>). Operations that change rights issue a new security stamp (old access tokens are rejected, see
/// <see cref="IUserSecurityStateProvider"/>) and evict the cached security state of the user in this process.
/// <para>
/// The "at least one unlocked Admin" rule is evaluated while holding a row lock on the Admin role, so concurrent demotions
/// or locks are serialised and cannot both pass.
/// </para>
/// Logs and audit payloads carry user ids only: never e-mail addresses, never passwords.
/// </summary>
internal sealed class IdentityUserService(
    OmnichannelDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IUserSecurityStateProvider securityStates,
    ICurrentUser currentUser,
    ICorrelationContext correlationContext,
    TimeProvider timeProvider,
    ILogger<IdentityUserService> logger) : IUserService
{
    /// <summary>Unique index on <c>AspNetUsers.NormalizedUserName</c> (the user name is the e-mail address).</summary>
    private const string UserNameIndexName = "UserNameIndex";

    private const string SystemActor = "system";

    public async Task<PagedResult<UserResponse>> ListAsync(UserListQuery query, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        IQueryable<ApplicationUser> users = dbContext.Users.AsNoTracking();

        if (query.SearchTerm is { } search)
        {
            var pattern = LikePattern.Contains(search);
            users = users.Where(u => EF.Functions.ILike(u.Email!, pattern, LikePattern.EscapeCharacter));
        }

        if (AppRoles.TryNormalize(query.Role, out var role))
        {
            users = users.Where(u => dbContext.UserRoles.Any(ur =>
                ur.UserId == u.Id && dbContext.Roles.Any(r => r.Id == ur.RoleId && r.Name == role)));
        }

        if (query.Locked is { } locked)
        {
            users = locked
                ? users.Where(u => u.LockoutEnabled && u.LockoutEnd != null && u.LockoutEnd >= now)
                : users.Where(u => !(u.LockoutEnabled && u.LockoutEnd != null && u.LockoutEnd >= now));
        }

        var totalCount = await users.CountAsync(cancellationToken);

        var items = await users
            .OrderBy(u => u.NormalizedEmail, query.IsDescending)
            .ThenBy(u => u.Id)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(ToResponse(now))
            .ToListAsync(cancellationToken);

        return new PagedResult<UserResponse>(items, query.Page, query.PageSize, totalCount);
    }

    public async Task<UserResponse> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(ToResponse(timeProvider.GetUtcNow()))
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw NotFound(userId);

    public async Task<UserResponse> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var role = RoleOf(request.Role, nameof(CreateUserRequest.Role));
        var email = request.Email.Trim();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };

        // User + role + audit event are atomic: never an account without its role.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        IdentityResult created;
        try
        {
            created = await userManager.CreateAsync(user, request.Password);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation(UserNameIndexName))
        {
            // Two requests passed Identity's own duplicate check at the same time: the unique index decides.
            throw EmailExists();
        }

        EnsureSucceeded(created, nameof(CreateUserRequest.Password));
        EnsureSucceeded(await userManager.AddToRoleAsync(user, role));

        AddAuditEvent(UserEvents.Created, new { targetUserId = user.Id, actorUserId = Actor, role });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("User {TargetUserId} created with role {Role} by {ActorUserId}", user.Id, role, Actor);
        return await GetAsync(user.Id, cancellationToken);
    }

    public async Task<UserResponse> ChangeRoleAsync(Guid userId, ChangeUserRoleRequest request, CancellationToken cancellationToken)
    {
        var newRole = RoleOf(request.Role, nameof(ChangeUserRoleRequest.Role));
        EnsureNotSelf(userId, "change their own role");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await LockAdminSetAsync(cancellationToken);

        var user = await FindAsync(userId);
        var roles = await userManager.GetRolesAsync(user);
        if (roles.Count == 1 && roles[0] == newRole)
        {
            // Nothing changes: no new stamp, no audit event.
            return await GetAsync(userId, cancellationToken);
        }

        if (roles.Contains(AppRoles.Admin) && newRole != AppRoles.Admin)
        {
            await EnsureNotLastAdminAsync(user, cancellationToken);
        }

        if (roles.Count > 0)
        {
            EnsureSucceeded(await userManager.RemoveFromRolesAsync(user, roles));
        }

        EnsureSucceeded(await userManager.AddToRoleAsync(user, newRole));
        EnsureSucceeded(await userManager.UpdateSecurityStampAsync(user));

        var from = roles.Count == 0 ? null : string.Join(",", roles.Order());
        AddAuditEvent(UserEvents.RoleChanged, new { targetUserId = userId, actorUserId = Actor, from, to = newRole });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        securityStates.Evict(userId);

        logger.LogInformation(
            "Role of user {TargetUserId} changed from {FromRole} to {ToRole} by {ActorUserId}", userId, from, newRole, Actor);
        return await GetAsync(userId, cancellationToken);
    }

    public async Task<UserResponse> LockAsync(Guid userId, CancellationToken cancellationToken)
    {
        EnsureNotSelf(userId, "lock themselves");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await LockAdminSetAsync(cancellationToken);

        var user = await FindAsync(userId);
        if (user.LockoutEnabled && user.LockoutEnd?.Year >= DateTimeOffset.MaxValue.Year)
        {
            // Already locked by an admin: nothing changes.
            return await GetAsync(userId, cancellationToken);
        }

        if (await userManager.IsInRoleAsync(user, AppRoles.Admin))
        {
            await EnsureNotLastAdminAsync(user, cancellationToken);
        }

        EnsureSucceeded(await userManager.SetLockoutEnabledAsync(user, true));
        EnsureSucceeded(await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue));
        EnsureSucceeded(await userManager.UpdateSecurityStampAsync(user));

        AddAuditEvent(UserEvents.Locked, new { targetUserId = userId, actorUserId = Actor });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        securityStates.Evict(userId);

        logger.LogInformation("User {TargetUserId} locked by {ActorUserId}", userId, Actor);
        return await GetAsync(userId, cancellationToken);
    }

    public async Task<UserResponse> UnlockAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var user = await FindAsync(userId);
        EnsureSucceeded(await userManager.SetLockoutEndDateAsync(user, null));
        EnsureSucceeded(await userManager.ResetAccessFailedCountAsync(user));

        // The security stamp is kept, so there is nothing to evict: tokens revoked by the lock stay revoked.
        AddAuditEvent(UserEvents.Unlocked, new { targetUserId = userId, actorUserId = Actor });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("User {TargetUserId} unlocked by {ActorUserId}", userId, Actor);
        return await GetAsync(userId, cancellationToken);
    }

    public async Task<UserResponse> ResetPasswordAsync(
        Guid userId,
        ResetUserPasswordRequest request,
        CancellationToken cancellationToken)
    {
        // Remove + add in one transaction: a password rejected by the policy rolls the removal back.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var user = await FindAsync(userId);
        if (await userManager.HasPasswordAsync(user))
        {
            EnsureSucceeded(await userManager.RemovePasswordAsync(user));
        }

        // AddPasswordAsync validates the policy and issues a new security stamp (tokens issued before are rejected).
        EnsureSucceeded(await userManager.AddPasswordAsync(user, request.NewPassword), nameof(ResetUserPasswordRequest.NewPassword));

        AddAuditEvent(UserEvents.PasswordReset, new { targetUserId = userId, actorUserId = Actor });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        securityStates.Evict(userId);

        logger.LogInformation("Password of user {TargetUserId} reset by {ActorUserId}", userId, Actor);
        return await GetAsync(userId, cancellationToken);
    }

    private string Actor => currentUser.UserId ?? SystemActor;

    /// <summary>One role per user; if legacy data has several, the alphabetically first is shown (Admin before Manager before Staff).</summary>
    private Expression<Func<ApplicationUser, UserResponse>> ToResponse(DateTimeOffset now) =>
        u => new UserResponse(
            u.Id,
            u.Email ?? u.UserName ?? string.Empty,
            dbContext.UserRoles
                .Where(ur => ur.UserId == u.Id)
                .Join(dbContext.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
                .OrderBy(name => name)
                .FirstOrDefault(),
            u.LockoutEnabled && u.LockoutEnd != null && u.LockoutEnd >= now,
            u.LockoutEnd,
            u.AccessFailedCount);

    private async Task<ApplicationUser> FindAsync(Guid userId) =>
        await userManager.FindByIdAsync(userId.ToString()) ?? throw NotFound(userId);

    /// <summary>
    /// Serialises every operation that can remove an unlocked Admin: the row lock on the Admin role is held until the
    /// transaction ends, so the admin count read afterwards cannot change underneath.
    /// </summary>
    private Task LockAdminSetAsync(CancellationToken cancellationToken)
    {
        var adminRole = userManager.NormalizeName(AppRoles.Admin);
        return dbContext.Database.ExecuteSqlAsync(
            $"""SELECT 1 FROM "AspNetRoles" WHERE "NormalizedName" = {adminRole} FOR UPDATE""",
            cancellationToken);
    }

    /// <summary>
    /// <paramref name="admin"/> is about to stop being an unlocked Admin. Requires <see cref="LockAdminSetAsync"/>.
    /// An admin who is locked already does not count, so demoting or locking them changes nothing.
    /// </summary>
    private async Task EnsureNotLastAdminAsync(ApplicationUser admin, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (admin.LockoutEnabled && admin.LockoutEnd >= now)
        {
            return;
        }

        var otherUnlockedAdminExists = await (
            from userRole in dbContext.UserRoles
            join role in dbContext.Roles on userRole.RoleId equals role.Id
            join user in dbContext.Users on userRole.UserId equals user.Id
            where role.Name == AppRoles.Admin
                && user.Id != admin.Id
                && !(user.LockoutEnabled && user.LockoutEnd != null && user.LockoutEnd >= now)
            select user.Id).AnyAsync(cancellationToken);

        if (!otherUnlockedAdminExists)
        {
            throw new ConflictException(
                UserErrorCodes.LastAdmin,
                "This is the last unlocked administrator; the operation would leave the system without one.");
        }
    }

    private void EnsureNotSelf(Guid userId, string action)
    {
        if (Guid.TryParse(currentUser.UserId, out var actorId) && actorId == userId)
        {
            throw new ConflictException(UserErrorCodes.CannotModifySelf, $"An administrator cannot {action}.");
        }
    }

    private void AddAuditEvent(string eventType, object payload) =>
        dbContext.SystemEventLogs.Add(new SystemEventLog(
            SystemEventLevel.Information,
            eventType,
            correlationContext.CorrelationId,
            taskId: null,
            orderId: null,
            workerName: null,
            MessageJson.Serialize(payload),
            timeProvider.GetUtcNow()));

    /// <summary>The validators reject unknown roles first; this guards callers that bypass them.</summary>
    private static string RoleOf(string role, string memberName) =>
        AppRoles.TryNormalize(role, out var normalized)
            ? normalized
            : throw new ValidationException(
            [
                new ValidationFailure(
                    ValidationConventions.JsonName(memberName),
                    ValidationMessages
                        .Get(ValidationMessages.Keys.RoleUnknown, ("Roles", string.Join(", ", AppRoles.All)))
                        .Replace("{PropertyName}", FieldNames.ForMember(memberName), StringComparison.Ordinal))
            ]);

    /// <summary>
    /// Maps Identity errors: password policy → 400 on <paramref name="passwordMember"/> (Identity's messages, which never
    /// contain the password), duplicate e-mail → 409, invalid e-mail → 400. Anything else is unexpected.
    /// </summary>
    private static void EnsureSucceeded(IdentityResult result, string? passwordMember = null)
    {
        if (result.Succeeded)
        {
            return;
        }

        var errors = result.Errors.ToList();
        if (errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateUserName) or nameof(IdentityErrorDescriber.DuplicateEmail)))
        {
            throw EmailExists();
        }

        if (errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
        {
            throw new ConflictException("concurrency_conflict", "The user was modified by another request. Reload and try again.");
        }

        var failures = new List<ValidationFailure>();
        foreach (var error in errors)
        {
            if (passwordMember is not null && error.Code.StartsWith("Password", StringComparison.Ordinal))
            {
                failures.Add(new ValidationFailure(ValidationConventions.JsonName(passwordMember), error.Description));
            }
            else if (error.Code is nameof(IdentityErrorDescriber.InvalidEmail) or nameof(IdentityErrorDescriber.InvalidUserName))
            {
                // Own message: Identity's description echoes the submitted value.
                failures.Add(new ValidationFailure(
                    ValidationConventions.JsonName(nameof(CreateUserRequest.Email)),
                    ValidationMessages.Get(ValidationMessages.Keys.EmailInvalid)));
            }
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        // Error codes only: descriptions may echo input values.
        throw new InvalidOperationException($"Identity operation failed ({string.Join(", ", errors.Select(e => e.Code))}).");
    }

    private static ConflictException EmailExists() =>
        new(UserErrorCodes.EmailExists, "A user with this e-mail address already exists.");

    private static NotFoundException NotFound(Guid userId) => new("User", userId, UserErrorCodes.UserNotFound);
}
