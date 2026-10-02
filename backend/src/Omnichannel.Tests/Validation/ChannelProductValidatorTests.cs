using System.Globalization;
using Omnichannel.Application.Channels;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Validation;

/// <summary>Task 014: request validators of the channel product mapping API (keys, messages, limits).</summary>
public sealed class ChannelProductValidatorTests
{
    private static readonly string TooLong = new('x', ChannelProduct.MaxExternalIdLength + 1);
    private static readonly string Longest = new('x', ChannelProduct.MaxExternalIdLength);

    private readonly CreateChannelProductRequestValidator _create = new();
    private readonly UpdateChannelProductRequestValidator _update = new();

    [Theory]
    [InlineData("EXT-1", null)]
    [InlineData(null, "SKU-1")]
    [InlineData("EXT-1", "SKU-1")]
    public void One_external_id_is_enough(string? externalProductId, string? externalSku)
    {
        Assert.True(_create.Validate(new CreateChannelProductRequest(Guid.NewGuid(), externalProductId, externalSku)).IsValid);
        Assert.True(_update.Validate(new UpdateChannelProductRequest(externalProductId, externalSku, ChannelProductStatus.Inactive)).IsValid);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "  ")]
    public void A_mapping_without_any_external_id_is_reported_on_the_external_product_id(string? externalProductId, string? externalSku)
    {
        var created = _create.Validate(new CreateChannelProductRequest(Guid.NewGuid(), externalProductId, externalSku));
        var updated = _update.Validate(new UpdateChannelProductRequest(externalProductId, externalSku, ChannelProductStatus.Active));

        Assert.Equal("externalProductId", Assert.Single(created.Errors).PropertyName);
        Assert.Equal("externalProductId", Assert.Single(updated.Errors).PropertyName);
    }

    [Fact]
    public void The_missing_id_message_names_both_fields_in_the_request_language()
    {
        var request = new CreateChannelProductRequest(Guid.NewGuid(), null, null);
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");
            Assert.Equal("Enter 'External product ID' or 'External SKU'.", _create.Validate(request).Errors.Single().ErrorMessage);

            CultureInfo.CurrentUICulture = new CultureInfo("vi");
            Assert.Equal("Hãy nhập 'Mã sản phẩm trên kênh' hoặc 'SKU trên kênh'.", _create.Validate(request).Errors.Single().ErrorMessage);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void External_ids_are_limited_to_128_characters()
    {
        Assert.True(_create.Validate(new CreateChannelProductRequest(Guid.NewGuid(), Longest, Longest)).IsValid);

        Assert.Equal(
            ["externalProductId", "externalSku"],
            _create.Validate(new CreateChannelProductRequest(Guid.NewGuid(), TooLong, TooLong)).Errors.Select(e => e.PropertyName).Order());
        Assert.Equal(
            ["externalProductId", "externalSku"],
            _update.Validate(new UpdateChannelProductRequest(TooLong, TooLong, ChannelProductStatus.Active)).Errors.Select(e => e.PropertyName).Order());
    }

    [Fact]
    public void Product_is_required_and_status_must_be_known()
    {
        Assert.Equal("productId", Assert.Single(_create.Validate(new CreateChannelProductRequest(Guid.Empty, "EXT-1", null)).Errors).PropertyName);
        Assert.Equal("status", Assert.Single(_update.Validate(new UpdateChannelProductRequest("EXT-1", null, (ChannelProductStatus)99)).Errors).PropertyName);
    }

    [Fact]
    public void List_query_sorts_by_sku_or_created_at_only()
    {
        var validator = new ChannelProductListQueryValidator();

        Assert.True(validator.Validate(new ChannelProductListQuery()).IsValid);
        Assert.True(validator.Validate(new ChannelProductListQuery { SortBy = "createdAt", SortDir = "desc" }).IsValid);
        Assert.True(validator.Validate(new ChannelProductListQuery { SortBy = "SKU" }).IsValid);
        Assert.Equal("sortBy", Assert.Single(validator.Validate(new ChannelProductListQuery { SortBy = "name" }).Errors).PropertyName);
        Assert.Equal("pageSize", Assert.Single(validator.Validate(new ChannelProductListQuery { PageSize = 101 }).Errors).PropertyName);
    }
}
