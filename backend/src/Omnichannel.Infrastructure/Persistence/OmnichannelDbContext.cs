using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Domain.Entities;
using Omnichannel.Infrastructure.Identity;

namespace Omnichannel.Infrastructure.Persistence;

public sealed class OmnichannelDbContext(DbContextOptions<OmnichannelDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ChannelProduct> ChannelProducts => Set<ChannelProduct>();
    public DbSet<ChannelCustomer> ChannelCustomers => Set<ChannelCustomer>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<ProcessingTask> ProcessingTasks => Set<ProcessingTask>();
    public DbSet<ProcessingAttempt> ProcessingAttempts => Set<ProcessingAttempt>();
    public DbSet<SystemEventLog> SystemEventLogs => Set<SystemEventLog>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<ReportExport> ReportExports => Set<ReportExport>();
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Identity tables first, then our configurations (which also seed the RBAC roles).
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(OmnichannelDbContext).Assembly);
    }
}
