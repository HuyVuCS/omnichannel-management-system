using System.Net;
using System.Net.Http.Json;
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
