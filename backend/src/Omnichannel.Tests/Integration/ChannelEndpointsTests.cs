using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Channels;
using Omnichannel.Domain.Enums;

namespace Omnichannel.Tests.Integration;

[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ChannelEndpointsTests(ApiFactory factory)
{
    private readonly HttpClient _admin = factory.CreateClientAs(AppRoles.Admin);

    [Fact]
    public async Task Admin_creates_channel_and_duplicate_slug_is_a_conflict()
    {
        var request = new CreateChannelRequest("Shop", $"shop-{TestData.UniqueSuffix()}", ChannelType.Website);

        var created = await _admin.PostAsJsonAsync("/api/channels", request);
        var duplicate = await _admin.PostAsJsonAsync("/api/channels", request with { Name = "Other shop" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var channel = await created.ReadAsync<ChannelResponse>();
        Assert.Equal(request.Slug, channel.Slug);
        Assert.Equal(ChannelStatus.Active, channel.Status);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(ChannelErrorCodes.SlugExists, await duplicate.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Invalid_slug_is_rejected()
    {
        var response = await _admin.PostAsJsonAsync(
            "/api/channels",
            new CreateChannelRequest("Web Site", "Web Site", ChannelType.Website));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await response.ReadErrorCodeAsync());
    }

    [Fact]
    public async Task Staff_cannot_create_channels()
    {
        var staff = factory.CreateClientAs(AppRoles.Staff);

        var response = await staff.PostAsJsonAsync(
            "/api/channels",
            new CreateChannelRequest("Shop", $"shop-{TestData.UniqueSuffix()}", ChannelType.Pos));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Staff_lists_channels_including_the_seeded_ones()
    {
        var staff = factory.CreateClientAs(AppRoles.Staff);

        var channels = await staff.GetFromJsonAsync<List<ChannelResponse>>("/api/channels", TestData.Json);

        Assert.Contains(channels!, c => c.Slug == "website");
        Assert.Contains(channels!, c => c.Slug == "pos");
        Assert.Contains(channels!, c => c.Slug == "digital-platform");
    }

    [Theory]
    [InlineData("website")]
    [InlineData("pos")]
    [InlineData("digital-platform")]
    public async Task Seeded_channel_has_stock_sync_in_the_list_and_on_its_own(string slug)
    {
        var staff = factory.CreateClientAs(AppRoles.Staff);

        var channels = await staff.GetFromJsonAsync<List<ChannelResponse>>("/api/channels", TestData.Json);
        var listed = Assert.Single(channels!, c => c.Slug == slug);
        var single = await staff.GetFromJsonAsync<ChannelResponse>($"/api/channels/{listed.ChannelId}", TestData.Json);

        Assert.True(listed.StockSyncEnabled);
        Assert.True(single!.StockSyncEnabled);
    }

    [Fact]
    public async Task Stock_sync_flag_is_part_of_the_json_contract()
    {
        var channels = await _admin.GetFromJsonAsync<List<JsonElement>>("/api/channels");

        var website = Assert.Single(channels!, c => c.GetProperty("slug").GetString() == "website");
        Assert.Equal(JsonValueKind.True, website.GetProperty("stockSyncEnabled").ValueKind);
    }

    [Fact]
    public async Task Channel_created_through_the_api_has_no_stock_sync_and_an_update_keeps_that()
    {
        var slug = $"nosync-{TestData.UniqueSuffix()}";

        var created = await (await _admin.PostAsJsonAsync(
            "/api/channels",
            new CreateChannelRequest("No sync", slug, ChannelType.Website))).ReadAsync<ChannelResponse>();
        var channels = await _admin.GetFromJsonAsync<List<ChannelResponse>>("/api/channels", TestData.Json);
        var single = await _admin.GetFromJsonAsync<ChannelResponse>($"/api/channels/{created.ChannelId}", TestData.Json);
        var updated = await (await _admin.PutAsJsonAsync(
            $"/api/channels/{created.ChannelId}",
            new UpdateChannelRequest("Still no sync", ChannelStatus.Active))).ReadAsync<ChannelResponse>();

        // No queue for this slug: the type (Website) does not matter.
        Assert.False(created.StockSyncEnabled);
        Assert.False(Assert.Single(channels!, c => c.Slug == slug).StockSyncEnabled);
        Assert.False(single!.StockSyncEnabled);
        Assert.False(updated.StockSyncEnabled);
    }

    [Fact]
    public async Task Stock_sync_flag_is_the_capability_of_the_channel_not_its_status()
    {
        // digital-platform is switched off for this test only (the API collection runs one test at a time).
        var channels = await _admin.GetFromJsonAsync<List<ChannelResponse>>("/api/channels", TestData.Json);
        var marketplace = Assert.Single(channels!, c => c.Slug == "digital-platform");
        var address = $"/api/channels/{marketplace.ChannelId}";
        try
        {
            var deactivated = await (await _admin.PutAsJsonAsync(
                address, new UpdateChannelRequest(marketplace.Name, ChannelStatus.Inactive), TestData.Json)).ReadAsync<ChannelResponse>();
            var read = await _admin.GetFromJsonAsync<ChannelResponse>(address, TestData.Json);

            Assert.Equal((ChannelStatus.Inactive, true), (deactivated.Status, deactivated.StockSyncEnabled));
            Assert.Equal((ChannelStatus.Inactive, true), (read!.Status, read.StockSyncEnabled));
        }
        finally
        {
            await _admin.PutAsJsonAsync(address, new UpdateChannelRequest(marketplace.Name, ChannelStatus.Active), TestData.Json);
        }
    }

    [Fact]
    public async Task Update_changes_name_and_status_but_keeps_slug()
    {
        var slug = $"upd-{TestData.UniqueSuffix()}";
        var created = await (await _admin.PostAsJsonAsync(
            "/api/channels",
            new CreateChannelRequest("Before", slug, ChannelType.DigitalPlatform))).ReadAsync<ChannelResponse>();

        var response = await _admin.PutAsJsonAsync(
            $"/api/channels/{created.ChannelId}",
            new UpdateChannelRequest("After", ChannelStatus.Inactive));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.ReadAsync<ChannelResponse>();
        Assert.Equal(("After", ChannelStatus.Inactive, slug), (updated.Name, updated.Status, updated.Slug));
    }
}
