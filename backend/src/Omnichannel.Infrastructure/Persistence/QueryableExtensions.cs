using System.Linq.Expressions;

namespace Omnichannel.Infrastructure.Persistence;

internal static class QueryableExtensions
{
    public static IOrderedQueryable<T> OrderBy<T, TKey>(
        this IQueryable<T> source,
        Expression<Func<T, TKey>> keySelector,
        bool descending) =>
        descending ? source.OrderByDescending(keySelector) : source.OrderBy(keySelector);
}
