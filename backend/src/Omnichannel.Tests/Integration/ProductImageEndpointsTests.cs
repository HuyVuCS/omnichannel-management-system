using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Products;
using Omnichannel.Infrastructure.S3;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 019: product images. The API hands out presigned links, the "browser" of these tests (an <see cref="HttpClient"/>
/// without the API's token) talks to the storage emulator directly, and the confirmation checks what was stored.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProductImageEndpointsTests(ApiFactory factory) : IAsyncLifetime
{
    private const long MaxImageBytes = 5 * 1024 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF, 0xE0];
    private static readonly byte[] WebPSignature = [.. "RIFF"u8, 0x24, 0x00, 0x00, 0x00, .. "WEBP"u8];

    private readonly HttpClient _manager = factory.CreateClientAs(AppRoles.Manager);

    /// <summary>What a browser is for the storage: no <c>Authorization</c> header, no cookie, nothing of the API.</summary>
    private readonly HttpClient _browser = new();

    private StorageEmulator _storage = null!;

    public async Task InitializeAsync() => _storage = await factory.StorageAsync();

    public Task DisposeAsync()
    {
        _browser.Dispose();
        return Task.CompletedTask;
    }

    // ---- the whole flow ---------------------------------------------------------------------------------------

    public static TheoryData<string, byte[]> Images() => new()
    {
        { ProductImageRules.Png, Image(PngSignature) },
        { ProductImageRules.Jpeg, Image(JpegSignature) },
        { ProductImageRules.WebP, Image(WebPSignature) }
    };

    [Theory]
    [MemberData(nameof(Images))]
    public async Task Presign_upload_and_confirm_make_the_object_the_image_and_its_link_returns_the_same_bytes(string contentType, byte[] image)
    {
        var product = await CreateProductAsync();
        var before = DateTimeOffset.UtcNow;

        var presigned = await PresignAsync(product.ProductId, contentType, image.Length);

        Assert.True(ProductImageKey.BelongsTo(presigned.ObjectKey, product.ProductId));
        Assert.Equal("PUT", presigned.Method);
        Assert.Equal(new Dictionary<string, string> { ["Content-Type"] = contentType }, presigned.Headers);
        Assert.InRange(presigned.ExpiresAt, before.AddMinutes(4), DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(1));
        Assert.StartsWith(_storage.ServiceUrl, presigned.UploadUrl, StringComparison.Ordinal);

        using var upload = await UploadAsync(presigned, image);
        Assert.True(upload.IsSuccessStatusCode, $"upload: HTTP {(int)upload.StatusCode}");

        var confirm = await ConfirmAsync(product.ProductId, presigned.ObjectKey);

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var confirmed = await confirm.ReadAsync<ProductResponse>();
        Assert.NotNull(confirmed.Image);
        Assert.Equal((contentType, (long)image.Length), (confirmed.Image.ContentType, confirmed.Image.SizeBytes));
        Assert.InRange(confirmed.Image.UpdatedAt, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.InRange(confirmed.Image.ExpiresAt, before.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(15).AddSeconds(1));
        // The rest of the product is what it was.
        Assert.Equal(product with { UpdatedAt = confirmed.UpdatedAt, Image = confirmed.Image }, confirmed);

        using var download = await _browser.GetAsync(confirmed.Image.Url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(contentType, download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(image, await download.Content.ReadAsByteArrayAsync());

        var row = await factory.QueryDbAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.ProductId == product.ProductId));
        Assert.Equal(
            (presigned.ObjectKey, contentType, (long?)image.Length),
            (row.ImageKey, row.ImageContentType, row.ImageSizeBytes));
    }

    [Fact]
    public async Task The_image_is_on_every_product_response_with_a_link_of_its_own()
    {
        var withImage = await CreateProductAsync();
        var image = await SetImageAsync(withImage.ProductId, ProductImageRules.Png, Image(PngSignature));
        var without = await CreateProductAsync();
        Assert.Null(without.Image); // create

        var detail = await _manager.GetFromJsonAsync<ProductResponse>($"/api/products/{withImage.ProductId}", TestData.Json);
        var list = await _manager.GetFromJsonAsync<PagedResult<ProductResponse>>($"/api/products?search={withImage.Sku}", TestData.Json);
        var updated = await (await _manager.PutAsJsonAsync(
            $"/api/products/{withImage.ProductId}", new UpdateProductRequest("Renamed", 2m, 1, null))).ReadAsync<ProductResponse>();
        var staff = await factory.CreateClientAs(AppRoles.Staff)
            .GetFromJsonAsync<ProductResponse>($"/api/products/{withImage.ProductId}", TestData.Json);

        foreach (var response in new[] { detail!, Assert.Single(list!.Items), updated, staff! })
        {
            Assert.NotNull(response.Image);
            // The same image, whoever asks and however often; only the link is new each time.
            Assert.Equal(
                (image.ContentType, image.SizeBytes, image.UpdatedAt),
                (response.Image.ContentType, response.Image.SizeBytes, response.Image.UpdatedAt));
            using var download = await _browser.GetAsync(response.Image.Url);
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        }

        // An update of the product's fields leaves the image alone.
        Assert.Equal("Renamed", updated.Name);
        Assert.Null((await _manager.GetFromJsonAsync<ProductResponse>($"/api/products/{without.ProductId}", TestData.Json))!.Image);
    }

    // ---- presign ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("image/svg+xml", 100L, "contentType", "en", "The image must be a JPEG, PNG or WebP file.")]
    [InlineData("image/svg+xml", 100L, "contentType", "vi", "Ảnh phải là tệp JPEG, PNG hoặc WebP.")]
    [InlineData("image/gif", 100L, "contentType", "en", "The image must be a JPEG, PNG or WebP file.")]
    [InlineData("IMAGE/PNG", 100L, "contentType", "en", "The image must be a JPEG, PNG or WebP file.")]
    [InlineData("", 100L, "contentType", "en", "'Image type' must not be empty.")]
    [InlineData("", 100L, "contentType", "vi", "'Loại ảnh' không được rỗng.")]
    [InlineData("image/png", 0L, "sizeBytes", "en", "'Image size' must be greater than or equal to '1'.")]
    [InlineData("image/png", -5L, "sizeBytes", "vi", "'Kích thước ảnh' phải lớn hơn hoặc bằng với '1'.")]
    [InlineData("image/png", MaxImageBytes + 1, "sizeBytes", "en", "The image must not be larger than 5 MB.")]
    [InlineData("image/png", MaxImageBytes + 1, "sizeBytes", "vi", "Ảnh không được lớn hơn 5 MB.")]
    public async Task Presign_rejects_what_cannot_become_an_image(string contentType, long sizeBytes, string field, string language, string message)
    {
        var product = await CreateProductAsync();

        var response = await SendAsync(
            HttpMethod.Post, $"/api/products/{product.ProductId}/image/presign", new PresignProductImageRequest(contentType, sizeBytes), language);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(new Dictionary<string, string[]> { [field] = [message] }, await ErrorsAsync(response));
    }

    [Fact]
    public async Task Presign_accepts_the_largest_size_writes_nothing_and_answers_404_for_an_unknown_product()
    {
        var product = await CreateProductAsync();

        var response = await _manager.PostAsJsonAsync(
            $"/api/products/{product.ProductId}/image/presign", new PresignProductImageRequest(ProductImageRules.Jpeg, MaxImageBytes));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Nothing in the database, nothing in the bucket: a link is only a link.
        var row = await factory.QueryDbAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.ProductId == product.ProductId));
        Assert.Equal((null, product.UpdatedAt), (row.ImageKey, row.UpdatedAt));
        Assert.Empty(await _storage.KeysAsync($"products/{product.ProductId}/"));

        var unknown = await _manager.PostAsJsonAsync(
            $"/api/products/{Guid.NewGuid()}/image/presign", new PresignProductImageRequest(ProductImageRules.Png, 10));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Two_presigns_give_two_keys_under_the_product()
    {
        var product = await CreateProductAsync();

        var first = await PresignAsync(product.ProductId, ProductImageRules.Png, 10);
        var second = await PresignAsync(product.ProductId, ProductImageRules.Png, 10);

        Assert.NotEqual(first.ObjectKey, second.ObjectKey);
        Assert.All(new[] { first, second }, p => Assert.StartsWith($"products/{product.ProductId}/", p.ObjectKey, StringComparison.Ordinal));
    }

    // ---- what the storage enforces ------------------------------------------------------------------------------

    [Fact]
    public async Task The_upload_link_takes_only_the_signed_content_type_and_expires()
    {
        var product = await CreateProductAsync();
        var image = Image(PngSignature);

        // Another Content-Type than the one the link was signed for.
        var presigned = await PresignAsync(product.ProductId, ProductImageRules.Png, image.Length);
        using (var wrongType = await UploadAsync(presigned, image, "text/html"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrongType.StatusCode);
        }

        // An expired link (the lifetime is configuration: two seconds for this test).
        var options = factory.Services.GetRequiredService<IOptions<ObjectStorageOptions>>().Value;
        var lifetime = options.UploadUrlLifetime;
        PresignProductImageResponse shortLived;
        try
        {
            options.UploadUrlLifetime = TimeSpan.FromSeconds(2);
            shortLived = await PresignAsync(product.ProductId, ProductImageRules.Png, image.Length);
        }
        finally
        {
            options.UploadUrlLifetime = lifetime;
        }

        Assert.InRange(shortLived.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(3));
        await Task.Delay(TimeSpan.FromSeconds(4));
        using (var expired = await UploadAsync(shortLived, image))
        {
            Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        }

        Assert.Empty(await _storage.KeysAsync($"products/{product.ProductId}/"));
        // Nothing was uploaded, so nothing can be confirmed.
        var confirm = await ConfirmAsync(product.ProductId, shortLived.ObjectKey);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
    }

    [Fact]
    public async Task The_bucket_is_private_an_object_is_read_with_its_link_only()
    {
        var product = await CreateProductAsync();
        var image = await SetImageAsync(product.ProductId, ProductImageRules.Png, Image(PngSignature));
        var key = (await factory.QueryDbAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.ProductId == product.ProductId))).ImageKey;

        using var unsigned = await _browser.GetAsync($"{_storage.ServiceUrl}/{StorageEmulator.Bucket}/{key}");
        using var tampered = await _browser.GetAsync(image.Url.Replace("X-Amz-Signature=", "X-Amz-Signature=0", StringComparison.Ordinal));
        // The API's token is not a key of the storage either.
        using var withApiToken = new HttpRequestMessage(HttpMethod.Get, $"{_storage.ServiceUrl}/{StorageEmulator.Bucket}/{key}");
        withApiToken.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueTokenFor(AppRoles.Admin));
        using var bearer = await _browser.SendAsync(withApiToken);

        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, tampered.StatusCode);
        Assert.Contains(bearer.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.BadRequest });
    }

    [Fact]
    public async Task The_storage_answers_a_browser_preflight_of_the_frontend_origin_and_of_no_other()
    {
        var product = await CreateProductAsync();
        var presigned = await PresignAsync(product.ProductId, ProductImageRules.Png, 10);

        using var allowed = await PreflightAsync(presigned.UploadUrl, StorageEmulator.AllowedOrigin);
        using var other = await PreflightAsync(presigned.UploadUrl, "http://evil.example");

        Assert.True(allowed.IsSuccessStatusCode);
        Assert.Equal(StorageEmulator.AllowedOrigin, Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // ---- confirm ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en", "This is not an upload key of this product.")]
    [InlineData("vi", "Đây không phải khoá tải lên của sản phẩm này.")]
    public async Task Confirm_rejects_a_key_of_another_product_and_anything_that_is_no_key(string language, string message)
    {
        var product = await CreateProductAsync();
        var other = await CreateProductAsync();
        var otherKey = await UploadNewAsync(other.ProductId, ProductImageRules.Png, Image(PngSignature));
        var objectId = Guid.CreateVersion7();

        string[] keys =
        [
            otherKey,
            "products",
            $"products/{product.ProductId}",
            $"products/{product.ProductId}/",
            $"products/{product.ProductId}/{objectId}/extra",
            $"products/{product.ProductId}/../{other.ProductId}/{objectId}",
            $"products/{product.ProductId.ToString().ToUpperInvariant()}/{objectId}",
            $"products/{product.ProductId:N}/{objectId:N}",
            $"/products/{product.ProductId}/{objectId}",
            $"images/{product.ProductId}/{objectId}",
            $"products/{product.ProductId}/not-a-guid",
            $"{_storage.ServiceUrl}/{StorageEmulator.Bucket}/products/{product.ProductId}/{objectId}"
        ];
        foreach (var key in keys)
        {
            var response = await SendAsync(HttpMethod.Put, $"/api/products/{product.ProductId}/image", new ConfirmProductImageRequest(key), language);

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{key}: {(int)response.StatusCode}");
            Assert.Equal(new Dictionary<string, string[]> { ["objectKey"] = [message] }, await ErrorsAsync(response));
        }

        // The other product's upload is untouched and still its own.
        Assert.True(await _storage.ExistsAsync(otherKey));
        Assert.Null((await GetAsync(product.ProductId)).Image);
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(other.ProductId, otherKey)).StatusCode);
    }

    [Theory]
    [InlineData("en", "No file has been uploaded for this key.")]
    [InlineData("vi", "Chưa có tệp nào được tải lên cho khoá này.")]
    public async Task Confirm_without_an_upload_is_rejected(string language, string message)
    {
        var product = await CreateProductAsync();
        var presigned = await PresignAsync(product.ProductId, ProductImageRules.Png, 10);

        var response = await SendAsync(
            HttpMethod.Put, $"/api/products/{product.ProductId}/image", new ConfirmProductImageRequest(presigned.ObjectKey), language);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(new Dictionary<string, string[]> { ["objectKey"] = [message] }, await ErrorsAsync(response));
    }

    [Fact]
    public async Task Confirm_requires_a_key()
    {
        var product = await CreateProductAsync();

        var response = await SendAsync(HttpMethod.Put, $"/api/products/{product.ProductId}/image", new ConfirmProductImageRequest(""), "en");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(new Dictionary<string, string[]> { ["objectKey"] = ["'Upload key' must not be empty."] }, await ErrorsAsync(response));
    }

    public enum BadUpload
    {
        /// <summary>One byte above the limit (the declared size was a lie).</summary>
        TooLarge,

        /// <summary>Stored with a content type no presigned link of the API would sign.</summary>
        TypeNotAllowed,

        /// <summary>A text file uploaded as PNG.</summary>
        TextAsPng,

        /// <summary>A PNG uploaded as JPEG.</summary>
        PngAsJpeg,

        /// <summary>Shorter than any signature.</summary>
        Truncated,

        Empty
    }

    [Theory]
    [InlineData(BadUpload.TooLarge, "en", "The image must not be larger than 5 MB.")]
    [InlineData(BadUpload.TooLarge, "vi", "Ảnh không được lớn hơn 5 MB.")]
    [InlineData(BadUpload.TypeNotAllowed, "en", "The image must be a JPEG, PNG or WebP file.")]
    [InlineData(BadUpload.TypeNotAllowed, "vi", "Ảnh phải là tệp JPEG, PNG hoặc WebP.")]
    [InlineData(BadUpload.TextAsPng, "en", "The content of the file is not an image of the type it was uploaded as.")]
    [InlineData(BadUpload.TextAsPng, "vi", "Nội dung tệp không phải là ảnh thuộc loại đã khai báo khi tải lên.")]
    [InlineData(BadUpload.PngAsJpeg, "en", "The content of the file is not an image of the type it was uploaded as.")]
    [InlineData(BadUpload.Truncated, "en", "The content of the file is not an image of the type it was uploaded as.")]
    [InlineData(BadUpload.Empty, "en", "The content of the file is not an image of the type it was uploaded as.")]
    public async Task Confirm_rejects_and_deletes_an_upload_that_is_no_product_image(BadUpload kind, string language, string message)
    {
        var product = await CreateProductAsync();
        var current = await SetImageAsync(product.ProductId, ProductImageRules.Png, Image(PngSignature));
        var currentKey = await ImageKeyAsync(product.ProductId);

        var key = kind switch
        {
            BadUpload.TooLarge => await UploadNewAsync(product.ProductId, ProductImageRules.Png, Image(PngSignature, (int)MaxImageBytes + 1), declaredSize: 100),
            BadUpload.TextAsPng => await UploadNewAsync(product.ProductId, ProductImageRules.Png, Encoding.UTF8.GetBytes("<script>alert(1)</script> not an image")),
            BadUpload.PngAsJpeg => await UploadNewAsync(product.ProductId, ProductImageRules.Jpeg, Image(PngSignature)),
            BadUpload.Truncated => await UploadNewAsync(product.ProductId, ProductImageRules.WebP, WebPSignature[..8]),
            BadUpload.Empty => await PutDirectlyAsync(product.ProductId, [], ProductImageRules.Png),
            _ => await PutDirectlyAsync(product.ProductId, Image(PngSignature), "image/svg+xml")
        };
        Assert.True(await _storage.ExistsAsync(key));

        var response = await SendAsync(HttpMethod.Put, $"/api/products/{product.ProductId}/image", new ConfirmProductImageRequest(key), language);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(new Dictionary<string, string[]> { ["objectKey"] = [message] }, await ErrorsAsync(response));
        // The rejected object is gone; the image the product had is still there and still its image.
        Assert.False(await _storage.ExistsAsync(key));
        Assert.True(await _storage.ExistsAsync(currentKey!));
        Assert.Equal(current.UpdatedAt, (await GetAsync(product.ProductId)).Image?.UpdatedAt);
    }

    [Fact]
    public async Task An_image_of_exactly_the_largest_size_is_accepted()
    {
        var product = await CreateProductAsync();

        var image = await SetImageAsync(product.ProductId, ProductImageRules.Jpeg, Image(JpegSignature, (int)MaxImageBytes));

        Assert.Equal(MaxImageBytes, image.SizeBytes);
    }

    [Fact]
    public async Task Replacing_the_image_deletes_the_previous_object_and_its_old_link_dies()
    {
        var product = await CreateProductAsync();
        var first = await SetImageAsync(product.ProductId, ProductImageRules.Png, Image(PngSignature));
        var firstKey = await ImageKeyAsync(product.ProductId);

        var second = await SetImageAsync(product.ProductId, ProductImageRules.Jpeg, Image(JpegSignature, 300));

        var secondKey = await ImageKeyAsync(product.ProductId);
        Assert.NotEqual(firstKey, secondKey);
        Assert.Equal((ProductImageRules.Jpeg, 300L), (second.ContentType, second.SizeBytes));
        Assert.True(second.UpdatedAt > first.UpdatedAt);
        Assert.Equal([secondKey!], await _storage.KeysAsync($"products/{product.ProductId}/"));
        using var oldLink = await _browser.GetAsync(first.Url);
        Assert.Equal(HttpStatusCode.NotFound, oldLink.StatusCode);
    }

    [Fact]
    public async Task Confirming_the_current_image_again_changes_nothing_and_deletes_nothing()
    {
        var product = await CreateProductAsync();
        var image = await SetImageAsync(product.ProductId, ProductImageRules.Png, Image(PngSignature));
        var key = await ImageKeyAsync(product.ProductId);
        var updatedAt = (await GetAsync(product.ProductId)).UpdatedAt;

        var again = await ConfirmAsync(product.ProductId, key!);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var product2 = await again.ReadAsync<ProductResponse>();
        Assert.Equal((image.UpdatedAt, updatedAt), (product2.Image?.UpdatedAt, product2.UpdatedAt));
        Assert.True(await _storage.ExistsAsync(key!));
        using var download = await _browser.GetAsync(product2.Image!.Url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    }

    [Fact]
    public async Task Concurrent_confirms_of_two_uploads_leave_one_image_and_delete_the_other_object()
    {
        var product = await CreateProductAsync();
        var keys = new[]
        {
            await UploadNewAsync(product.ProductId, ProductImageRules.Png, Image(PngSignature, 111)),
            await UploadNewAsync(product.ProductId, ProductImageRules.Jpeg, Image(JpegSignature, 222)),
            await UploadNewAsync(product.ProductId, ProductImageRules.WebP, Image(WebPSignature, 333))
        };

        var responses = await Task.WhenAll(keys.Select(key => ConfirmAsync(product.ProductId, key)));

        // Every confirmation succeeded in its turn (the product row is locked); the last one is the image.
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var winner = await ImageKeyAsync(product.ProductId);
        Assert.Contains(winner, keys);
        // The objects of the confirmations that were replaced are deleted, not orphaned.
        Assert.Equal([winner!], await _storage.KeysAsync($"products/{product.ProductId}/"));
        var final = await GetAsync(product.ProductId);
        using var download = await _browser.GetAsync(final.Image!.Url);
        Assert.Equal(final.Image.SizeBytes, (await download.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Confirm_of_an_unknown_product_is_404()
    {
        var unknown = Guid.NewGuid();

        var response = await ConfirmAsync(unknown, $"products/{unknown}/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- remove -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Remove_clears_the_image_deletes_its_object_and_is_204_without_an_image_too()
    {
        var product = await CreateProductAsync();
        var image = await SetImageAsync(product.ProductId, ProductImageRules.WebP, Image(WebPSignature));
        var key = await ImageKeyAsync(product.ProductId);

        var removed = await _manager.DeleteAsync($"/api/products/{product.ProductId}/image");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Null((await GetAsync(product.ProductId)).Image);
        Assert.False(await _storage.ExistsAsync(key!));
        using var oldLink = await _browser.GetAsync(image.Url);
        Assert.Equal(HttpStatusCode.NotFound, oldLink.StatusCode);
        var row = await factory.QueryDbAsync(db => db.Products.AsNoTracking().SingleAsync(p => p.ProductId == product.ProductId));
        Assert.Equal((null, null, null, null), (row.ImageKey, row.ImageContentType, row.ImageSizeBytes, row.ImageUpdatedAt));

        // No image (any more): the same answer, nothing to do.
        Assert.Equal(HttpStatusCode.NoContent, (await _manager.DeleteAsync($"/api/products/{product.ProductId}/image")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _manager.DeleteAsync($"/api/products/{Guid.NewGuid()}/image")).StatusCode);
    }

    // ---- roles ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AppRoles.Staff, HttpStatusCode.Forbidden)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task Staff_is_forbidden_and_an_anonymous_caller_unauthorized_on_all_three(string? role, HttpStatusCode expected)
    {
        var product = await CreateProductAsync();
        await SetImageAsync(product.ProductId, ProductImageRules.Png, Image(PngSignature));
        var key = await ImageKeyAsync(product.ProductId);
        var client = role is null ? factory.CreateClient() : factory.CreateClientAs(role);

        var presign = await client.PostAsJsonAsync(
            $"/api/products/{product.ProductId}/image/presign", new PresignProductImageRequest(ProductImageRules.Png, 10));
        var confirm = await client.PutAsJsonAsync($"/api/products/{product.ProductId}/image", new ConfirmProductImageRequest(key!));
        var remove = await client.DeleteAsync($"/api/products/{product.ProductId}/image");

        Assert.Equal((expected, expected, expected), (presign.StatusCode, confirm.StatusCode, remove.StatusCode));
        Assert.Equal(key, await ImageKeyAsync(product.ProductId));
        Assert.True(await _storage.ExistsAsync(key!));
    }

    [Fact]
    public async Task An_admin_may_do_all_three()
    {
        var admin = factory.CreateClientAs(AppRoles.Admin);
        var product = await CreateProductAsync();
        var image = Image(PngSignature);

        var presigned = await (await admin.PostAsJsonAsync(
            $"/api/products/{product.ProductId}/image/presign", new PresignProductImageRequest(ProductImageRules.Png, image.Length)))
            .ReadAsync<PresignProductImageResponse>();
        using var upload = await UploadAsync(presigned, image);
        var confirm = await admin.PutAsJsonAsync($"/api/products/{product.ProductId}/image", new ConfirmProductImageRequest(presigned.ObjectKey));
        var remove = await admin.DeleteAsync($"/api/products/{product.ProductId}/image");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.NoContent), (confirm.StatusCode, remove.StatusCode));
    }

    // ---- the storage is down ----------------------------------------------------------------------------------

    [Fact]
    public async Task While_the_storage_is_unreachable_products_are_still_listed_and_a_confirmation_is_a_503_that_changes_nothing()
    {
        var marker = TestData.UniqueSuffix().ToUpperInvariant();
        var withImage = await CreateProductAsync($"IMG-{marker}-A");
        var image = await SetImageAsync(withImage.ProductId, ProductImageRules.Png, Image(PngSignature));
        var currentKey = await ImageKeyAsync(withImage.ProductId);
        var pending = await CreateProductAsync($"IMG-{marker}-B");
        var uploadedKey = await UploadNewAsync(pending.ProductId, ProductImageRules.Png, Image(PngSignature));
        var replacementKey = await UploadNewAsync(withImage.ProductId, ProductImageRules.Jpeg, Image(JpegSignature));
        var removable = await CreateProductAsync($"IMG-{marker}-C");
        await SetImageAsync(removable.ProductId, ProductImageRules.WebP, Image(WebPSignature));
        var removedKey = await ImageKeyAsync(removable.ProductId);
        var since = DateTimeOffset.UtcNow;

        HttpResponseMessage list, detail, presign, confirm, replace, repeated, remove;
        await _storage.StopAsync();
        try
        {
            // Reading products makes no call to the storage: the links are computed locally.
            list = await _manager.GetAsync($"/api/products?search=IMG-{marker}");
            detail = await _manager.GetAsync($"/api/products/{withImage.ProductId}");
            presign = await _manager.PostAsJsonAsync(
                $"/api/products/{pending.ProductId}/image/presign", new PresignProductImageRequest(ProductImageRules.Png, 10));

            confirm = await ConfirmAsync(pending.ProductId, uploadedKey);
            replace = await ConfirmAsync(withImage.ProductId, replacementKey);
            // The image it already has: no check of the storage is needed.
            repeated = await ConfirmAsync(withImage.ProductId, currentKey!);
            // The row is cleared; the object could not be deleted and stays behind, unreferenced.
            remove = await _manager.DeleteAsync($"/api/products/{removable.ProductId}/image");
        }
        finally
        {
            await _storage.StartAsync();
        }

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK), (list.StatusCode, detail.StatusCode, presign.StatusCode));
        var listed = (await list.ReadAsync<PagedResult<ProductResponse>>()).Items;
        Assert.Equal(3, listed.Count);
        Assert.Equal(image.UpdatedAt, listed.Single(p => p.ProductId == withImage.ProductId).Image?.UpdatedAt);

        foreach (var failed in new[] { confirm, replace })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Equal(ObjectStorageException.ErrorCode, await failed.ReadErrorCodeAsync());
            // Nothing of the storage in the answer.
            var body = await failed.Content.ReadAsStringAsync();
            Assert.DoesNotContain("127.0.0.1", body);
            Assert.DoesNotContain(StorageEmulator.Bucket, body);
            Assert.DoesNotContain(_storage.AccessKeyId, body);
        }

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.NoContent), (repeated.StatusCode, remove.StatusCode));
        // Removed while the storage was down: the product has no image, its object is an orphan in the bucket.
        Assert.Null(await ImageKeyAsync(removable.ProductId));
        Assert.True(await _storage.ExistsAsync(removedKey!));

        // Unchanged: no image where none was confirmed, the old image where a replacement failed.
        Assert.Null(await ImageKeyAsync(pending.ProductId));
        Assert.Equal(currentKey, await ImageKeyAsync(withImage.ProductId));
        // The storage is back: both uploads are still there and can be confirmed now.
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(pending.ProductId, uploadedKey)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ConfirmAsync(withImage.ProductId, replacementKey)).StatusCode);

        AssertNothingOfTheStorageWasLogged(since);
    }

    [Fact]
    public async Task Links_and_keys_of_the_storage_are_never_logged()
    {
        var since = DateTimeOffset.UtcNow;
        var product = await CreateProductAsync();
        await SetImageAsync(product.ProductId, ProductImageRules.Png, Image(PngSignature));
        await SetImageAsync(product.ProductId, ProductImageRules.Jpeg, Image(JpegSignature));
        await ConfirmAsync(product.ProductId, await UploadNewAsync(product.ProductId, ProductImageRules.Png, "text"u8.ToArray()));
        await _manager.GetAsync($"/api/products/{product.ProductId}");
        await _manager.DeleteAsync($"/api/products/{product.ProductId}/image");

        AssertNothingOfTheStorageWasLogged(since);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Neither a presigned link (it is a credential until it expires) nor the key id or the secret appear in anything the
    /// API host logged since <paramref name="since"/>.
    /// </summary>
    private void AssertNothingOfTheStorageWasLogged(DateTimeOffset since)
    {
        var logged = factory.Logs.RenderedSince(since);
        Assert.NotEmpty(logged);
        Assert.All(logged, line =>
        {
            Assert.DoesNotContain("X-Amz-", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_storage.AccessKeyId, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_storage.SecretAccessKey, line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_storage.ServiceUrl, line, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary><paramref name="signature"/> followed by filler, <paramref name="length"/> bytes in all.</summary>
    private static byte[] Image(byte[] signature, int length = 2048)
    {
        var image = new byte[length];
        Random.Shared.NextBytes(image);
        signature.CopyTo(image, 0);
        return image;
    }

    private async Task<ProductResponse> CreateProductAsync(string? sku = null)
    {
        var response = await _manager.PostAsJsonAsync(
            "/api/products",
            new CreateProductRequest(sku ?? $"IMG-{TestData.UniqueSuffix()}".ToUpperInvariant(), "Product with image", 12.5m, 3, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<ProductResponse>();
    }

    private async Task<ProductResponse> GetAsync(Guid productId) =>
        (await _manager.GetFromJsonAsync<ProductResponse>($"/api/products/{productId}", TestData.Json))!;

    private Task<string?> ImageKeyAsync(Guid productId) =>
        factory.QueryDbAsync(db => db.Products.AsNoTracking().Where(p => p.ProductId == productId).Select(p => p.ImageKey).SingleAsync());

    private async Task<PresignProductImageResponse> PresignAsync(Guid productId, string contentType, long sizeBytes)
    {
        var response = await _manager.PostAsJsonAsync(
            $"/api/products/{productId}/image/presign", new PresignProductImageRequest(contentType, sizeBytes));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<PresignProductImageResponse>();
    }

    /// <summary>The upload as a browser sends it: the method and the headers the presign answer names, nothing else.</summary>
    private async Task<HttpResponseMessage> UploadAsync(PresignProductImageResponse presigned, byte[] content, string? contentType = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(presigned.Method), presigned.UploadUrl) { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? presigned.Headers["Content-Type"]);
        Assert.Null(request.Headers.Authorization);
        return await _browser.SendAsync(request);
    }

    /// <summary>Presign and upload, not confirmed. Returns the key.</summary>
    private async Task<string> UploadNewAsync(Guid productId, string contentType, byte[] content, long? declaredSize = null)
    {
        var presigned = await PresignAsync(productId, contentType, declaredSize ?? Math.Max(content.Length, 1));
        using var upload = await UploadAsync(presigned, content);
        Assert.True(upload.IsSuccessStatusCode, $"upload: HTTP {(int)upload.StatusCode}");
        return presigned.ObjectKey;
    }

    /// <summary>An object under a key of the product that no presigned link of the API produced.</summary>
    private async Task<string> PutDirectlyAsync(Guid productId, byte[] content, string contentType)
    {
        var key = ProductImageKey.NewFor(productId);
        await _storage.PutAsync(key, content, contentType);
        return key;
    }

    private Task<HttpResponseMessage> ConfirmAsync(Guid productId, string key) =>
        _manager.PutAsJsonAsync($"/api/products/{productId}/image", new ConfirmProductImageRequest(key));

    /// <summary>Presign, upload, confirm. Returns the image of the product.</summary>
    private async Task<ProductImageResponse> SetImageAsync(Guid productId, string contentType, byte[] content)
    {
        var key = await UploadNewAsync(productId, contentType, content);
        var confirm = await ConfirmAsync(productId, key);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        return (await confirm.ReadAsync<ProductResponse>()).Image!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object body, string language)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: TestData.Json) };
        request.Headers.AcceptLanguage.ParseAdd(language);
        return await _manager.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PreflightAsync(string url, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "PUT");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return await _browser.SendAsync(request);
    }

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("validation_failed", document.RootElement.GetProperty("errorCode").GetString());
        return document.RootElement.GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;
    }
}
