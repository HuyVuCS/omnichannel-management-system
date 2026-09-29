using System.Security.Cryptography;
using System.Text;

namespace Omnichannel.Application.Orders;

/// <summary>
/// Hash of the client-supplied content of an order submission, stored on its <c>ProcessingTask</c>:
/// SHA-256 (lower-case hex) of <c>{customerId}|{productId}:{stock},...</c> with items sorted by productId.
/// Item order in the request does not matter; prices are excluded (they are a snapshot taken by the server).
/// </summary>
public static class OrderRequestHash
{
    public static string Compute(Guid customerId, IEnumerable<SubmitOrderItem> items)
    {
        var canonical = $"{customerId:D}|" + string.Join(
            ',',
            items.OrderBy(i => i.ProductId).Select(i => $"{i.ProductId:D}:{i.Stock}"));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
