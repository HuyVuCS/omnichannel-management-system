using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Stamps CreatedAt/By and UpdatedAt/By on tracked entities.
/// NOTE: set-based ExecuteUpdate/ExecuteDelete and raw SQL bypass the change tracker, so those
/// statements must set UpdatedAt/UpdatedBy explicitly.
/// </summary>
public sealed class AuditingInterceptor(ICurrentUser currentUser, TimeProvider timeProvider) : SaveChangesInterceptor
{
    private const string SystemActor = "system";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var actor = currentUser.UserId ?? SystemActor;

        foreach (var entry in context.ChangeTracker.Entries<IHasTimestamps>())
        {
            var isAuditable = entry.Entity is AuditableEntity;

            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(nameof(IHasTimestamps.CreatedAt)).CurrentValue = now;
                    entry.Property(nameof(IHasTimestamps.UpdatedAt)).CurrentValue = null;
                    if (isAuditable)
                    {
                        entry.Property(nameof(AuditableEntity.CreatedBy)).CurrentValue = actor;
                        entry.Property(nameof(AuditableEntity.UpdatedBy)).CurrentValue = null;
                    }

                    break;

                case EntityState.Modified:
                    // Creation stamps are immutable.
                    entry.Property(nameof(IHasTimestamps.CreatedAt)).IsModified = false;
                    entry.Property(nameof(IHasTimestamps.UpdatedAt)).CurrentValue = now;
                    if (isAuditable)
                    {
                        entry.Property(nameof(AuditableEntity.CreatedBy)).IsModified = false;
                        entry.Property(nameof(AuditableEntity.UpdatedBy)).CurrentValue = actor;
                    }

                    break;
            }
        }
    }
}
