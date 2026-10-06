namespace Omnichannel.Domain.Entities;

/// <summary>
/// Maps a customer as a channel knows them (its own customer id) to the central customer. Written when a webhook of the
/// channel names an external customer for the first time, and never changed afterwards: what the channel sends later
/// about the same customer does not overwrite the central record.
/// </summary>
public class ChannelCustomer : AuditableEntity
{
    public const int MaxExternalCustomerIdLength = 128;

    private ChannelCustomer()
    {
    }

    public ChannelCustomer(Guid channelId, string externalCustomerId, Guid customerId)
    {
        if (channelId == Guid.Empty) throw new ArgumentException("ChannelId is required.", nameof(channelId));
        if (customerId == Guid.Empty) throw new ArgumentException("CustomerId is required.", nameof(customerId));
        ArgumentException.ThrowIfNullOrWhiteSpace(externalCustomerId);

        var trimmed = externalCustomerId.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trimmed.Length, MaxExternalCustomerIdLength, nameof(externalCustomerId));

        ChannelCustomerId = Guid.CreateVersion7();
        ChannelId = channelId;
        ExternalCustomerId = trimmed;
        CustomerId = customerId;
    }

    public Guid ChannelCustomerId { get; private set; }
    public Guid ChannelId { get; private set; }

    /// <summary>The channel's id of the customer, trimmed; compared exactly as stored.</summary>
    public string ExternalCustomerId { get; private set; } = null!;

    public Guid CustomerId { get; private set; }

    public Channel Channel { get; private set; } = null!;
    public Customer Customer { get; private set; } = null!;
}
