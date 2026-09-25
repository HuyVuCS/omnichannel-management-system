using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Omnichannel.Domain.Entities;
using Omnichannel.Infrastructure.Identity;
using Omnichannel.Infrastructure.Persistence;

namespace Omnichannel.Tests.Persistence;

/// <summary>
/// Verifies the EF Core model that migrations are generated from. No database connection is opened.
/// Constraint behaviour itself (e.g. concurrent reservations) needs PostgreSQL integration tests.
/// </summary>
public sealed class ModelConfigurationTests : IDisposable
{
    private readonly OmnichannelDbContext _context;
    private readonly IModel _model;

    public ModelConfigurationTests()
    {
        var options = new DbContextOptionsBuilder<OmnichannelDbContext>()
            .UseNpgsql("Host=localhost;Database=model_tests") // never opened
            .Options;

        _context = new OmnichannelDbContext(options);
        // Design-time model keeps check constraints, computed SQL and seed data.
        _model = _context.GetService<IDesignTimeModel>().Model;
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public void Inventory_uses_ProductId_as_primary_and_foreign_key_without_InventoryId()
    {
        var inventory = Entity<Inventory>();

        var keyProperty = Assert.Single(inventory.FindPrimaryKey()!.Properties);
        Assert.Equal(nameof(Inventory.ProductId), keyProperty.Name);
        Assert.Null(inventory.FindProperty("InventoryId"));

        var foreignKey = Assert.Single(inventory.GetForeignKeys());
        Assert.Equal(typeof(Product), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(Inventory.ProductId), Assert.Single(foreignKey.Properties).Name);
    }

    [Fact]
    public void AvailableStock_is_a_stored_generated_column()
    {
        var availableStock = Entity<Inventory>().FindProperty(nameof(Inventory.AvailableStock))!;

        Assert.Equal("\"TotalStock\" - \"ReservedStock\"", availableStock.GetComputedColumnSql());
        Assert.True(availableStock.GetIsStored());
    }

    [Fact]
    public void Inventory_check_constraints_prevent_negative_and_over_reserved_stock()
    {
        var constraints = Entity<Inventory>().GetCheckConstraints().ToDictionary(c => c.Name!, c => c.Sql);

        Assert.Equal("\"TotalStock\" >= 0", constraints["CK_Inventories_TotalStock_NonNegative"]);
        Assert.Equal(
            "\"ReservedStock\" >= 0 AND \"ReservedStock\" <= \"TotalStock\"",
            constraints["CK_Inventories_ReservedStock_Range"]);
    }

    [Fact]
    public void Order_is_unique_per_channel_and_external_order_id()
    {
        var index = Entity<Order>().GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_Orders_ChannelId_ExternalOrderId");

        Assert.True(index.IsUnique);
        Assert.Equal(
            new[] { nameof(Order.ChannelId), nameof(Order.ExternalOrderId) },
            index.Properties.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Attempt_number_is_unique_per_processing_task()
    {
        var index = Entity<ProcessingAttempt>().GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { nameof(ProcessingAttempt.TaskId), nameof(ProcessingAttempt.AttemptNo) }));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Order_item_product_is_unique_per_order()
    {
        var index = Entity<OrderItem>().GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { nameof(OrderItem.OrderId), nameof(OrderItem.ProductId) }));

        Assert.True(index.IsUnique);
    }

    [Theory]
    [InlineData(typeof(Order), nameof(Order.Status))]
    [InlineData(typeof(ProcessingTask), nameof(ProcessingTask.Status))]
    [InlineData(typeof(ProcessingTask), nameof(ProcessingTask.TaskType))]
    [InlineData(typeof(InventoryMovement), nameof(InventoryMovement.Type))]
    public void Enums_are_stored_as_strings(Type entityType, string propertyName)
    {
        var property = _model.FindEntityType(entityType)!.FindProperty(propertyName)!;

        // HasConversion<string>() only records the provider type; the converter itself lives on the type mapping.
        Assert.Equal(typeof(string), property.GetTypeMapping().Converter?.ProviderClrType);
    }

    [Fact]
    public void Seeds_the_three_order_entry_channels()
    {
        var slugs = Entity<Channel>().GetSeedData()
            .Select(row => (string?)row[nameof(Channel.Slug)])
            .Order()
            .ToArray();

        Assert.Equal(new[] { "digital-platform", "pos", "website" }, slugs);
    }

    [Fact]
    public void Seeds_the_rbac_roles()
    {
        var roles = Entity<ApplicationRole>().GetSeedData()
            .Select(row => (string?)row[nameof(ApplicationRole.Name)])
            .Order()
            .ToArray();

        Assert.Equal(new[] { "Admin", "Manager", "Staff" }, roles);
    }

    private IEntityType Entity<T>() =>
        _model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped.");
}
