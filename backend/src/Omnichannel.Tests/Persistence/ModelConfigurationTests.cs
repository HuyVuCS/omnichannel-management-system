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

    [Fact]
    public void Movement_reference_is_unique_per_product_for_import_reserve_and_release()
    {
        var index = Entity<InventoryMovement>().GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_InventoryMovements_Reference");

        Assert.True(index.IsUnique);
        Assert.Equal(
            new[]
            {
                nameof(InventoryMovement.ReferenceType), nameof(InventoryMovement.ReferenceId),
                nameof(InventoryMovement.ProductId), nameof(InventoryMovement.Type)
            },
            index.Properties.Select(p => p.Name).ToArray());
        Assert.Equal(
            "\"ReferenceId\" IS NOT NULL AND \"Type\" IN ('Import', 'Reserve', 'Release')",
            index.GetFilter());
        Assert.DoesNotContain(
            Entity<InventoryMovement>().GetIndexes(),
            i => i.GetDatabaseName() == "UX_InventoryMovements_Reference_Reservation");
    }

    [Theory]
    [InlineData(typeof(Order), nameof(Order.Status))]
    [InlineData(typeof(ProcessingTask), nameof(ProcessingTask.Status))]
    [InlineData(typeof(ProcessingTask), nameof(ProcessingTask.TaskType))]
    [InlineData(typeof(InventoryMovement), nameof(InventoryMovement.Type))]
    [InlineData(typeof(ReportExport), nameof(ReportExport.ReportType))]
    public void Enums_are_stored_as_strings(Type entityType, string propertyName)
    {
        var property = _model.FindEntityType(entityType)!.FindProperty(propertyName)!;

        // HasConversion<string>() only records the provider type; the converter itself lives on the type mapping.
        Assert.Equal(typeof(string), property.GetTypeMapping().Converter?.ProviderClrType);
    }

    [Fact]
    public void An_export_is_one_to_one_with_its_task_and_has_no_id_of_its_own()
    {
        var export = Entity<ReportExport>();

        Assert.Equal(nameof(ReportExport.TaskId), Assert.Single(export.FindPrimaryKey()!.Properties).Name);
        var foreignKey = Assert.Single(export.GetForeignKeys());
        Assert.Equal((typeof(ProcessingTask), nameof(ReportExport.TaskId), true, DeleteBehavior.Restrict),
            (foreignKey.PrincipalEntityType.ClrType, Assert.Single(foreignKey.Properties).Name, foreignKey.IsUnique, foreignKey.DeleteBehavior));
        Assert.Equal("jsonb", export.FindProperty(nameof(ReportExport.Parameters))!.GetColumnType());
        Assert.Equal(
            (100, 49),
            (export.FindProperty(nameof(ReportExport.FileName))!.GetMaxLength(), export.FindProperty(nameof(ReportExport.ObjectKey))!.GetMaxLength()));
        Assert.False(export.FindProperty(nameof(ReportExport.ExpiresAt))!.IsNullable);
    }

    [Fact]
    public void The_result_of_an_export_is_all_or_nothing_and_its_two_indexes_serve_the_list_and_the_sweep()
    {
        var export = Entity<ReportExport>();

        var constraint = Assert.Single(export.GetCheckConstraints());
        Assert.Equal("CK_ReportExports_Result_AllOrNone", constraint.Name);
        Assert.Equal(
            "(\"ObjectKey\" IS NULL AND \"SizeBytes\" IS NULL AND \"RowCount\" IS NULL AND \"CompletedAt\" IS NULL) "
            + "OR (\"ObjectKey\" IS NOT NULL AND \"SizeBytes\" IS NOT NULL AND \"RowCount\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL)",
            constraint.Sql);

        var list = export.GetIndexes().Single(i => i.Properties.Count == 2);
        Assert.Equal([nameof(ReportExport.CreatedBy), nameof(ReportExport.CreatedAt)], list.Properties.Select(p => p.Name));
        Assert.Equal([false, true], list.IsDescending);

        var sweep = export.GetIndexes().Single(i => i.Properties.Count == 1);
        Assert.Equal((nameof(ReportExport.ExpiresAt), "\"DeletedAt\" IS NULL"), (sweep.Properties.Single().Name, sweep.GetFilter()));
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
