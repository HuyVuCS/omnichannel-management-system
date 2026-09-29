namespace Omnichannel.Domain.Entities;

public class Customer : AuditableEntity
{
    private Customer()
    {
    }

    public Customer(string name, string? phone, string? email, string? address)
    {
        CustomerId = Guid.CreateVersion7();
        Update(name, phone, email, address);
    }

    public Guid CustomerId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public string? Address { get; private set; }

    /// <summary>Optional fields that are blank are stored as null.</summary>
    public void Update(string name, string? phone, string? email, string? address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim();
        Phone = NullIfBlank(phone);
        Email = NullIfBlank(email);
        Address = NullIfBlank(address);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
