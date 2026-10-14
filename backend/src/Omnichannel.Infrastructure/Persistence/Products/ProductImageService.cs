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

/// <summary>
/// <see cref="IProductImageService"/>. An upload goes to a staging key; the confirmation copies it inside the storage
/// to a final key of its own, checks <b>the copy</b> and makes the product refer to it (review 019: the upload link is
/// valid for some minutes more and could replace the confirmed object - now it can only write the staging key, which
/// nothing refers to).
/// <para>
/// Why the copy is what is checked: the staging object can be rewritten through the link between a check and the
/// copy; the copy cannot - its key is new for this confirmation (<see cref="ProductImageKey.NewFinalFor"/>), no link
/// leads to it and no client can name it. So what a product refers to is exactly what was checked, also when two
/// confirmations of the same upload run at once: each has its own copy, the product row lock decides which one is
/// referred to, and the other one is deleted.
/// </para>
/// <para>
/// Every call to the storage happens outside the database transaction. A copy whose confirmation does not finish is
/// deleted when the failure is known, and is otherwise left to the image clean-up
/// (<see cref="IProductImageCleanup"/>), like a staging object that could not be deleted.
/// </para>
/// </summary>
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

        var key = ProductImageKey.NewStagingFor(productId);
        // The declared size is signed into the link. The browser sends the length of the file by itself, so the
        // answer names the Content-Type header only (a script cannot set Content-Length).
        var link = storage.PresignPut(key, request.ContentType, request.SizeBytes, settings.UploadUrlLifetime);
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
        var stagingKey = request.ObjectKey;
        var current = await dbContext.Products
            .AsNoTracking()
            .Where(p => p.ProductId == productId)
            .Select(p => new { p.ImageKey })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(Product), productId);

        if (!ProductImageKey.TryParseStaging(stagingKey, out var owner, out var uploadId) || owner != productId)
        {
            throw InvalidKey(ValidationMessages.Keys.ImageKeyInvalid);
        }

        if (ProductImageKey.IsFinalOf(current.ImageKey, uploadId))
        {
            // A repeated confirmation (the client did not see the first answer): the image is already a copy of this
            // upload. No call to the storage - whatever is under the staging key now is not looked at.
            return await products.GetAsync(productId, cancellationToken);
        }

        // The storage is asked before the transaction, never inside it.
        var staged = await CheckStagedAsync(productId, stagingKey, cancellationToken);
        if (staged is null)
        {
            return await AlreadyConfirmedOrNotUploadedAsync(productId, uploadId, cancellationToken);
        }

        var finalKey = ProductImageKey.NewFinalFor(productId, uploadId);
        var copiedAt = timeProvider.GetUtcNow();
        if (!await storage.CopyAsync(stagingKey, finalKey, staged.Value.ContentType, cancellationToken))
        {
            // Deleted between the look and the copy (a concurrent confirmation of the same upload, finished or rejected).
            return await AlreadyConfirmedOrNotUploadedAsync(productId, uploadId, cancellationToken);
        }

        string? previousKey;
        bool alreadyConfirmed;
        (string ContentType, long Length) copy;
        var committing = false;
        try
        {
            copy = await CheckCopyAsync(productId, stagingKey, finalKey, cancellationToken);

            // No EnableRetryOnFailure: a retried transaction would need the execution strategy around it.
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var product = await LockAsync(productId, cancellationToken);
            previousKey = product.ImageKey;
            // A concurrent confirmation of the same upload came first: its copy is the image, this one is not needed.
            alreadyConfirmed = ProductImageKey.IsFinalOf(previousKey, uploadId);
            if (!alreadyConfirmed)
            {
                if (timeProvider.GetUtcNow() - copiedAt > ProductImageRules.ConfirmationDeadline)
                {
                    // The clean-up may take an unreferenced copy for an orphan once it is old; a confirmation that
                    // was held up this long does not refer to its copy any more. The upload is still there: try again.
                    throw new ObjectStorageException("confirm");
                }

                product.SetImage(finalKey, copy.ContentType, copy.Length, timeProvider.GetUtcNow());
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            committing = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch when (!committing)
        {
            // Nothing refers to the copy and nothing will. (A failure of the commit itself says nothing about its
            // outcome: then the copy stays, and the clean-up removes it only if no product refers to it.)
            await DeleteQuietlyAsync(productId, finalKey, "unconfirmed", CancellationToken.None);
            throw;
        }

        if (alreadyConfirmed)
        {
            await DeleteQuietlyAsync(productId, finalKey, "duplicate", cancellationToken);
        }
        else if (previousKey is not null)
        {
            // Nothing refers to the replaced object any more. If this fails it stays as an orphan: never served.
            await DeleteQuietlyAsync(productId, previousKey, "replaced", cancellationToken);
        }

        // The upload has done its work; what the link writes there afterwards is never looked at.
        await DeleteQuietlyAsync(productId, stagingKey, "confirmed upload", cancellationToken);

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
    /// The upload is gone. Either nothing was uploaded, or a concurrent confirmation of the same upload committed and
    /// deleted the staging object after this one read the product (review 026c F1: a double click). The product is read
    /// again: when its image is a copy of this upload, the answer is the one of a repeated confirmation.
    /// </summary>
    private async Task<ProductResponse> AlreadyConfirmedOrNotUploadedAsync(
        Guid productId,
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        var imageKey = await dbContext.Products
            .AsNoTracking()
            .Where(p => p.ProductId == productId)
            .Select(p => p.ImageKey)
            .SingleOrDefaultAsync(cancellationToken);

        if (ProductImageKey.IsFinalOf(imageKey, uploadId))
        {
            return await products.GetAsync(productId, cancellationToken);
        }

        throw InvalidKey(ValidationMessages.Keys.ImageNotUploaded);
    }

    /// <summary>
    /// The upload as it is now, before anything is copied: it exists (null when it does not), is not too large and has
    /// an allowed content type. The cheap part of the checks - an upload that can never become an image is deleted
    /// without being copied.
    /// </summary>
    private async Task<(string ContentType, long Length)?> CheckStagedAsync(Guid productId, string stagingKey, CancellationToken cancellationToken)
    {
        var staged = await storage.HeadAsync(stagingKey, cancellationToken);
        if (staged is null)
        {
            return null;
        }

        if (RejectionOf(staged) is { } rejection)
        {
            await DeleteQuietlyAsync(productId, stagingKey, "rejected", cancellationToken);
            throw rejection;
        }

        return (staged.ContentType!, staged.Length);
    }

    /// <summary>
    /// The copy, which nothing but this confirmation can write: size and content type again (the upload may have been
    /// replaced between the first look and the copy) and its first bytes, which must be the signature of its type. A
    /// copy that fails is deleted together with the upload.
    /// </summary>
    private async Task<(string ContentType, long Length)> CheckCopyAsync(
        Guid productId,
        string stagingKey,
        string finalKey,
        CancellationToken cancellationToken)
    {
        // The storage confirmed the copy a moment ago: not finding it is a failure of the storage, not of the client.
        var copy = await storage.HeadAsync(finalKey, cancellationToken) ?? throw new ObjectStorageException("head");

        var rejection = RejectionOf(copy);
        if (rejection is null
            && !ProductImageRules.StartsLike(
                copy.ContentType!,
                await storage.ReadStartAsync(finalKey, ProductImageRules.SignatureLength, cancellationToken)))
        {
            rejection = InvalidKey(ValidationMessages.Keys.ImageContentMismatch);
        }

        if (rejection is not null)
        {
            await DeleteQuietlyAsync(productId, stagingKey, "rejected", cancellationToken);
            // The copy is deleted by the caller, like every copy of a confirmation that does not finish.
            throw rejection;
        }

        return (copy.ContentType!, copy.Length);
    }

    /// <summary>Why an object cannot be a product image by its size or content type; null when it can.</summary>
    private ValidationException? RejectionOf(StoredObject stored)
    {
        var maxBytes = options.Value.MaxImageBytes;
        if (stored.Length > maxBytes)
        {
            return Invalid(nameof(ConfirmProductImageRequest.ObjectKey), TooLarge(maxBytes));
        }

        return ProductImageRules.IsAllowed(stored.ContentType) ? null : InvalidKey(ValidationMessages.Keys.ImageTypeNotAllowed);
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
