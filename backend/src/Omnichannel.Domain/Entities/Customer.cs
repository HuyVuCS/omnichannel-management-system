namespace Omnichannel.Domain.Entities;

public class Customer : AuditableEntity
{
    private Customer()
    {
    }

    public Customer(string name, string? phone, string? email, string? address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        CustomerId = Guid.CreateVersion7();
        Name = name.Trim();
        Phone = phone?.Trim();
        Email = email?.Trim();
        Address = address?.Trim();
    }

    public Guid CustomerId { get; private set; }
    public string Name { get; private set; } = null!;
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public string? Address { get; private set; }
}
