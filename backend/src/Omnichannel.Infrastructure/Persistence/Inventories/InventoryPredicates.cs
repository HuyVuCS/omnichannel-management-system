using System.Linq.Expressions;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Inventories;

/// <summary>Inventory rules expressed once as SQL-translatable expressions.</summary>
public static class InventoryPredicates
{
    /// <summary>
    /// Low stock: <c>AvailableStock &lt;= LowStockThreshold</c>, inclusive, no special case
    /// (threshold 0 warns when the product is out of stock). Used by the inventory list/filter and step 11 alerts.
    /// </summary>
    public static readonly Expression<Func<Inventory, bool>> IsLowStock =
        i => i.AvailableStock <= i.Product.LowStockThreshold;

    /// <summary>
    /// <see cref="IsLowStock"/> as SQL text for the set-based alert statements (alert store, backfill of the
    /// <c>Alerts</c> migration), which cannot be written with LINQ (<c>INSERT … ON CONFLICT</c>, <c>UPDATE … RETURNING</c>).
    /// <c>i</c> is <c>"Inventories"</c>, <c>p</c> its <c>"Products"</c> row. The only second wording of the rule; a test
    /// compares both on the same rows.
    /// </summary>
    public const string IsLowStockSql = "i.\"AvailableStock\" <= p.\"LowStockThreshold\"";

    /// <summary>
    /// <see cref="IsLowStock"/> seen from the product: the same expression applied to the product's inventory row
    /// (false for a product without one, which cannot exist through the API).
    /// </summary>
    public static readonly Expression<Func<Product, bool>> IsLowStockProduct = ForProduct(IsLowStock);

    private static Expression<Func<Product, bool>> ForProduct(Expression<Func<Inventory, bool>> inventoryPredicate)
    {
        Expression<Func<Product, Inventory?>> inventoryOf = p => p.Inventory;
        var product = inventoryOf.Parameters[0];
        var body = new ParameterReplacer(inventoryPredicate.Parameters[0], inventoryOf.Body).Visit(inventoryPredicate.Body);

        return Expression.Lambda<Func<Product, bool>>(
            Expression.AndAlso(Expression.NotEqual(inventoryOf.Body, Expression.Constant(null, typeof(Inventory))), body),
            product);
    }

    /// <summary>
    /// Inlines <paramref name="predicate"/> (a lambda over the same entity) into <paramref name="template"/>'s second
    /// parameter, so a shared rule can be projected into a DTO and still translate to SQL.
    /// </summary>
    public static Expression<Func<T, TResult>> Inline<T, TResult>(
        Expression<Func<T, bool, TResult>> template,
        Expression<Func<T, bool>> predicate)
    {
        var entity = template.Parameters[0];
        var predicateBody = new ParameterReplacer(predicate.Parameters[0], entity).Visit(predicate.Body);
        var body = new ParameterReplacer(template.Parameters[1], predicateBody).Visit(template.Body);

        return Expression.Lambda<Func<T, TResult>>(body, entity);
    }

    private sealed class ParameterReplacer(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == parameter ? replacement : base.VisitParameter(node);
    }
}
