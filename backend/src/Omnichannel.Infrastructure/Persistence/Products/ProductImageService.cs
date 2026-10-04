using System.Globalization;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Common.Abstractions;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Validation;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Entities;
using Omnichannel.Infrastructure.S3;

namespace Omnichannel.Infrastructure.Persistence.Products;

/// <summary>
/// The <c>image</c> of a product response: a fresh presigned download link per response, computed locally (no call to
/// the storage - a product list never depends on it).
/// </summary>
internal sealed class ProductImageLinks(IObjectStorage storage, IOptions<ObjectStorageOptions> options)
{
    public ProductImageResponse? For(string? key, string? contentType, long? sizeBytes, DateTimeOffset? updatedAt)
    {
        if (key is null || contentType is null || sizeBytes is null || updatedAt is null)
        {
            return null;
        }

        var link = storage.PresignGet(key, options.Value.DownloadUrlLifetime);
        return new ProductImageResponse(link.Url, link.ExpiresAt, contentType, sizeBytes.Value, updatedAt.Value);
    }
}

internal sealed class ProductImageService(
    OmnichannelDbContext dbContext,
    IObjectStorage storage,
    IProductService products,
    IOptions<ObjectStorageOptions> options,
    TimeProvider timeProvider,
    ILogger<ProductImageService> logger) : IProductImageService
{
    private const string Put = "PUT";
    private const string ContentTypeHeader = "Content-Type";
    private const long BytesPerMegabyte = 1024 * 1024;

    public async Task<PresignProductImageResponse> PresignAsync(
        Guid productId,
        PresignProductImageRequest request,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (request.SizeBytes > settings.MaxImageBytes)
        {
            throw Invalid(nameof(PresignProductImageRequest.SizeBytes), TooLarge(settings.MaxImageBytes));
        }

        if (!await dbContext.Products.AnyAsync(p => p.ProductId == productId, cancellationToken))
        {
            throw new NotFoundException(nameof(Product), productId);
        }

        var key = ProductImageKey.NewFor(productId);
        var link = storage.PresignPut(key, request.ContentType, settings.UploadUrlLifetime);
        return new PresignProductImageResponse(
            key,
            link.Url,
            Put,
            new Dictionary<string, string> { [ContentTypeHeader] = request.ContentType },
            link.ExpiresAt);
    }

    public async Task<ProductResponse> ConfirmAsync(
        Guid productId,
        ConfirmProductImageRequest request,
        CancellationToken cancellationToken)
    {
        var key = request.ObjectKey;
        var current = await dbContext.Products
            .AsNoTracking()
            .Where(p => p.ProductId == productId)
            .Select(p => new { p.ImageKey })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(Product), productId);

        if (!ProductImageKey.BelongsTo(key, productId))
        {
            throw InvalidKey(ValidationMessages.Keys.ImageKeyInvalid);
        }

        if (current.ImageKey == key)
        {
            // A repeated confirmation (the client did not see the first answer): the image is already this object.
            return await products.GetAsync(productId, cancellationToken);
        }

        // The checks talk to the storage: all of them before the transaction, none inside it.
        var uploaded = await CheckUploadAsync(productId, key, cancellationToken);

        string? previousKey;
        // No EnableRetryOnFailure: a retried transaction would need the execution strategy around it.
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var product = await LockAsync(productId, cancellationToken);
            previousKey = product.ImageKey;
            if (previousKey != key)
            {
                product.SetImage(key, uploaded.ContentType, uploaded.Length, timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (previousKey is not null && previousKey != key)
        {
            // Nothing refers to the replaced object any more. If this fails it stays as an orphan: never served.
            await DeleteQuietlyAsync(productId, previousKey, "replaced", cancellationToken);
        }

        return await products.GetAsync(productId, cancellationToken);
    }

    public async Task RemoveAsync(Guid productId, CancellationToken cancellationToken)
    {
        string? removedKey;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            var product = await LockAsync(productId, cancellationToken);
            removedKey = product.ImageKey;
            if (removedKey is not null)
            {
                product.ClearImage();
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (removedKey is not null)
        {
            await DeleteQuietlyAsync(productId, removedKey, "removed", cancellationToken);
        }
    }

    /// <summary>
    /// What is stored under the key, if it may be a product image: it exists, is not too large, has an allowed content
    /// type, and its first bytes are the signature of that type. An object that fails is deleted.
    /// </summary>
    private async Task<(string ContentType, long Length)> CheckUploadAsync(Guid productId, string key, CancellationToken cancellationToken)
    {
        var stored = await storage.HeadAsync(key, cancellationToken)
            ?? throw InvalidKey(ValidationMessages.Keys.ImageNotUploaded);

        var maxBytes = options.Value.MaxImageBytes;
        ValidationException? rejection = null;
        if (stored.Length > maxBytes)
        {
            rejection = Invalid(nameof(ConfirmProductImageRequest.ObjectKey), TooLarge(maxBytes));
        }
        else if (!ProductImageRules.IsAllowed(stored.ContentType))
        {
            rejection = InvalidKey(ValidationMessages.Keys.ImageTypeNotAllowed);
        }
        else if (!ProductImageRules.StartsLike(
            stored.ContentType!,
            await storage.ReadStartAsync(key, ProductImageRules.SignatureLength, cancellationToken)))
        {
            rejection = InvalidKey(ValidationMessages.Keys.ImageContentMismatch);
        }

        if (rejection is not null)
        {
            await DeleteQuietlyAsync(productId, key, "rejected", cancellationToken);
            throw rejection;
        }

        return (stored.ContentType!, stored.Length);
    }

    /// <summary>The product, with its row locked until the transaction ends: concurrent image changes run one after the other.</summary>
    private async Task<Product> LockAsync(Guid productId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlAsync(
            $"""SELECT 1 FROM "Products" WHERE "ProductId" = {productId} FOR UPDATE""",
            cancellationToken);

        return await dbContext.Products.SingleOrDefaultAsync(p => p.ProductId == productId, cancellationToken)
            ?? throw new NotFoundException(nameof(Product), productId);
    }

    /// <summary>Best effort: the request has done its work; an object that cannot be deleted stays in the bucket unreferenced.</summary>
    private async Task DeleteQuietlyAsync(Guid productId, string key, string why, CancellationToken cancellationToken)
    {
        try
        {
            await storage.DeleteAsync(key, cancellationToken);
        }
        catch (ObjectStorageException)
        {
            logger.LogWarning(
                "The {Why} image object {ObjectKey} of product {ProductId} could not be deleted; it stays in the bucket unreferenced",
                why, key, productId);
        }
    }

    private static string TooLarge(long maxBytes) =>
        ValidationMessages.Get(ValidationMessages.Keys.ImageTooLarge, ("MaxSize", SizeText(maxBytes)));

    /// <summary>"5 MB" for whole megabytes (the default), otherwise the bytes.</summary>
    private static string SizeText(long bytes) =>
        bytes % BytesPerMegabyte == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / BytesPerMegabyte} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes} B");

    private static ValidationException InvalidKey(string messageKey) =>
        Invalid(nameof(ConfirmProductImageRequest.ObjectKey), ValidationMessages.Get(messageKey));

    private static ValidationException Invalid(string memberName, string message) =>
        new([new ValidationFailure(ValidationConventions.JsonName(memberName), message)]);
}
