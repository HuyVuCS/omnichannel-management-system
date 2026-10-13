namespace Omnichannel.Application.Products;

/// <summary>
/// The image of a product (one per product). The image is uploaded by the client straight to the object storage with a
/// presigned link; the API never receives the bytes. Nothing is trusted until the upload is confirmed: the confirmation
/// checks the stored object and only then records it on the product.
/// </summary>
public interface IProductImageService
{
    /// <summary>
    /// A link for one upload of exactly the declared type and size to a new staging key of this product. Writes
    /// nothing to the database.
    /// </summary>
    /// <exception cref="Common.Exceptions.NotFoundException">The product does not exist.</exception>
    /// <exception cref="FluentValidation.ValidationException">The declared size is above the limit.</exception>
    Task<PresignProductImageResponse> PresignAsync(Guid productId, PresignProductImageRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the uploaded object the product's image: the upload (a staging key of this product) is copied to a final
    /// key of its own inside the storage, the copy is checked (exists, size, content type, first bytes) and the product
    /// then refers to the copy - never to the key the upload link can still write. An upload that fails a check is
    /// deleted with its copy. The previous image's object and the staging object are deleted after the commit.
    /// Confirming the upload of the current image again changes nothing.
    /// </summary>
    /// <exception cref="Common.Exceptions.NotFoundException">The product does not exist.</exception>
    /// <exception cref="FluentValidation.ValidationException">A check failed (field <c>objectKey</c>).</exception>
    /// <exception cref="Common.Exceptions.ObjectStorageException">The storage could not be asked; nothing was changed.</exception>
    Task<ProductResponse> ConfirmAsync(Guid productId, ConfirmProductImageRequest request, CancellationToken cancellationToken);

    /// <summary>Removes the image from the product and deletes its object after the commit. No image: nothing to do.</summary>
    /// <exception cref="Common.Exceptions.NotFoundException">The product does not exist.</exception>
    Task RemoveAsync(Guid productId, CancellationToken cancellationToken);
}
