using System.Collections.Frozen;
using System.Reflection;
using Npgsql;

namespace Omnichannel.Infrastructure.Persistence;

/// <summary>
/// The one registry of unique indexes whose violation (PostgreSQL 23505) is an expected outcome: a service maps it to a
/// 409 or to an idempotent result instead of check-then-insert. The index configurations, the catch clauses
/// (<see cref="DbUpdateExceptionExtensions.IsUniqueViolation"/>) and the log level of the failure
/// (<see cref="Logging.DatabaseFailureLogInterceptor"/>) all use these constants, so the three cannot drift apart: every
/// constant declared here is registered, and a name that is not declared here is never treated as expected.
/// </summary>
public static class ExpectedUniqueViolations
{
    /// <summary><c>Channels.Slug</c> → 409 <c>channel_slug_exists</c>.</summary>
    public const string ChannelSlug = "IX_Channels_Slug";

    /// <summary><c>Products.Sku</c> → 409 <c>product_sku_exists</c>.</summary>
    public const string ProductSku = "IX_Products_Sku";

    /// <summary><c>AspNetUsers.NormalizedUserName</c> (named by ASP.NET Core Identity) → 409 <c>user_email_exists</c>.</summary>
    public const string UserName = "UserNameIndex";

    /// <summary>Movement reference (import / reserve / release applied once) → 409 <c>stock_import_already_applied</c>.</summary>
    public const string InventoryMovementReference = "UX_InventoryMovements_Reference";

    /// <summary><c>ProcessingTasks.IdempotencyKey</c> → the existing task (duplicate order submission, duplicate stock-sync delivery).</summary>
    public const string ProcessingTaskIdempotencyKey = "UX_ProcessingTasks_IdempotencyKey";

    /// <summary><c>Orders (ChannelId, ExternalOrderId)</c> → the existing order (final idempotency guard of the Order Worker).</summary>
    public const string OrderIdempotency = "UX_Orders_ChannelId_ExternalOrderId";

    /// <summary><c>ChannelProducts (ChannelId, ProductId)</c> → 409 <c>channel_product_exists</c>.</summary>
    public const string ChannelProductMapping = "IX_ChannelProducts_ChannelId_ProductId";

    /// <summary><c>ChannelProducts (ChannelId, ExternalProductId)</c> → 409 <c>external_product_id_taken</c>.</summary>
    public const string ChannelProductExternalProductId = "IX_ChannelProducts_ChannelId_ExternalProductId";

    /// <summary>
    /// <c>ChannelProducts (ChannelId, ExternalSku)</c> among the mappings without an external product id → 409
    /// <c>external_sku_taken</c>.
    /// </summary>
    public const string ChannelProductExternalSku = "IX_ChannelProducts_ChannelId_ExternalSku";

    /// <summary>Every constant of this class (PostgreSQL identifiers are compared case-sensitively).</summary>
    public static readonly FrozenSet<string> ConstraintNames = typeof(ExpectedUniqueViolations)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="exception"/> is, or wraps (<c>DbUpdateException</c>), a unique violation on a registered
    /// constraint.
    /// </summary>
    public static bool Contains(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: { } constraintName })
            {
                return ConstraintNames.Contains(constraintName);
            }
        }

        return false;
    }
}
