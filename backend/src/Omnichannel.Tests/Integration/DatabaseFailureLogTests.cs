using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Omnichannel.Application.Auth;
using Omnichannel.Application.Customers;
using Omnichannel.Application.Inventory;
using Omnichannel.Application.Products;
using Omnichannel.Domain.Entities;
using Omnichannel.Domain.Enums;
using Omnichannel.Infrastructure.Persistence;
using Serilog.Events;

namespace Omnichannel.Tests.Integration;

/// <summary>
/// Task 014 Part A1: a unique violation that a service maps to a 409 is not logged by EF Core as an error; every other
/// database failure still is. All exceptions come from the test database, through the API host's real logging pipeline.
/// </summary>
[Collection(ApiCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DatabaseFailureLogTests(ApiFactory factory)
{
    private const string EfCorePrefix = "Microsoft.EntityFrameworkCore";
    private const string CommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";
    private const string UpdateCategory = "Microsoft.EntityFrameworkCore.Update";
    private const int CommandErrorEventId = 20102;
    private const int SaveChangesFailedEventId = 10000;

    /// <summary>Exists only in the test database, so no service knows it; partial, so it affects no other test.</summary>
    private const string UnregisteredIndex = "UX_Test_Customers_Name";

    [Fact]
    public async Task Registered_conflict_is_not_logged_by_ef_core_at_warning_or_above()
    {
        var manager = factory.CreateClientAs(AppRoles.Manager);
        var product = await (await manager.PostAsJsonAsync(
                "/api/products",
                new CreateProductRequest($"LOG-{TestData.UniqueSuffix()}".ToUpperInvariant(), "Log filter product", 1m, 0, null)))
            .ReadAsync<ProductResponse>();
        var import = new ImportInventoryRequest($"GRN-{TestData.UniqueSuffix()}", null, [new ImportInventoryItem(product.ProductId, 1)]);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync("/api/inventory/import", import)).StatusCode);

        var (response, events) = await SendAsync(manager, "/api/inventory/import", import);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(InventoryErrorCodes.ImportAlreadyApplied, await response.ReadErrorCodeAsync());
        Assert.DoesNotContain(events, e => IsEfCore(e) && e.Level >= LogEventLevel.Warning);
        Assert.DoesNotContain(events, e => e.Level >= LogEventLevel.Warning);

        // The mapped outcome stays visible once, at Information.
        var outcome = Assert.Single(events, e => SourceOf(e) == "Omnichannel.Api.Middleware.GlobalExceptionHandler");
        Assert.Equal(LogEventLevel.Information, outcome.Level);
        Assert.Equal(
            $"Request failed with 409: {InventoryErrorCodes.ImportAlreadyApplied}",
            outcome.RenderMessage().Replace("\"", string.Empty));
    }

    [Fact]
    public async Task Unique_violation_on_an_unregistered_constraint_is_still_logged_as_an_error()
    {
        await EnsureUnregisteredIndexAsync();
        var staff = factory.CreateClientAs(AppRoles.Staff);
        var customer = new CustomerRequest($"unregistered-{TestData.UniqueSuffix()}", null, null, null);
        Assert.Equal(HttpStatusCode.Created, (await staff.PostAsJsonAsync("/api/customers", customer)).StatusCode);

        var (response, events) = await SendAsync(staff, "/api/customers", customer);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var command = Assert.Single(events, e => SourceOf(e) == CommandCategory);
        Assert.Equal((LogEventLevel.Error, CommandErrorEventId), (command.Level, EventIdOf(command)));
        Assert.StartsWith("Failed executing DbCommand", command.RenderMessage());
        Assert.Contains("INSERT INTO \"Customers\"", command.RenderMessage());
        Assert.Null(command.Exception); // as EF Core writes it

        var save = Assert.Single(events, e => SourceOf(e) == UpdateCategory);
        Assert.Equal((LogEventLevel.Error, SaveChangesFailedEventId), (save.Level, EventIdOf(save)));
        Assert.StartsWith("An exception occurred in the database while saving changes for context type", save.RenderMessage());
        var postgres = Assert.IsType<PostgresException>(Assert.IsType<DbUpdateException>(save.Exception).InnerException);
        Assert.Equal((PostgresErrorCodes.UniqueViolation, UnregisteredIndex), (postgres.SqlState, postgres.ConstraintName));
    }

    [Fact]
    public async Task A_name_outside_the_registry_never_matches_a_catch_clause()
    {
        await EnsureUnregisteredIndexAsync();
        var name = $"unregistered-{TestData.UniqueSuffix()}";

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => factory.QueryDbAsync(async db =>
        {
            db.Customers.AddRange(new Customer(name, null, null, null), new Customer(name, null, null, null));
            return await db.SaveChangesAsync();
        }));

        Assert.Equal(UnregisteredIndex, Assert.IsType<PostgresException>(exception.InnerException).ConstraintName);
        Assert.False(exception.IsUniqueViolation(UnregisteredIndex));
        Assert.False(ExpectedUniqueViolations.Contains(exception));
    }

    [Fact]
    public async Task Failed_save_that_is_not_a_unique_violation_is_still_logged_as_an_error()
    {
        var since = DateTimeOffset.Now;

        // Foreign-key violation (23503): a movement for a product that does not exist.
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => factory.QueryDbAsync(async db =>
        {
            db.InventoryMovements.Add(new InventoryMovement(
                Guid.NewGuid(), InventoryMovementType.Import, 1, InventoryReferenceType.StockImport,
                $"LOG-{TestData.UniqueSuffix()}", documentNumber: null, documentSeries: null));
            return await db.SaveChangesAsync();
        }));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);

        var events = factory.Logs.EventsSince(since).Where(IsEfCore).ToList();
        var command = Assert.Single(events, e => SourceOf(e) == CommandCategory);
        Assert.Equal((LogEventLevel.Error, CommandErrorEventId), (command.Level, EventIdOf(command)));
        var save = Assert.Single(events, e => SourceOf(e) == UpdateCategory);
        Assert.Equal((LogEventLevel.Error, SaveChangesFailedEventId), (save.Level, EventIdOf(save)));
        Assert.Same(exception, save.Exception);
    }

    [Fact]
    public async Task Failed_command_outside_save_changes_is_still_logged_as_an_error()
    {
        var since = DateTimeOffset.Now;

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            factory.QueryDbAsync(db => db.Database.ExecuteSqlRawAsync("SELECT 1 / 0")));
        Assert.Equal(PostgresErrorCodes.DivisionByZero, exception.SqlState);

        var entry = Assert.Single(factory.Logs.EventsSince(since), IsEfCore);
        Assert.Equal((CommandCategory, LogEventLevel.Error, CommandErrorEventId), (SourceOf(entry), entry.Level, EventIdOf(entry)));
        Assert.Contains("SELECT 1 / 0", entry.RenderMessage());
    }

    [Fact]
    public void Every_constant_of_the_registry_is_registered()
    {
        Assert.Equal(10, ExpectedUniqueViolations.ConstraintNames.Count);
        Assert.Superset(
            new HashSet<string>
            {
                ExpectedUniqueViolations.ChannelSlug,
                ExpectedUniqueViolations.ProductSku,
                ExpectedUniqueViolations.UserName,
                ExpectedUniqueViolations.InventoryMovementReference,
                ExpectedUniqueViolations.ProcessingTaskIdempotencyKey,
                ExpectedUniqueViolations.OrderIdempotency,
                ExpectedUniqueViolations.ChannelProductMapping,
                ExpectedUniqueViolations.ChannelProductExternalProductId,
                ExpectedUniqueViolations.ChannelProductExternalSku,
                ExpectedUniqueViolations.ChannelCustomerExternalId
            },
            ExpectedUniqueViolations.ConstraintNames.ToHashSet());
        Assert.False(ExpectedUniqueViolations.Contains(new InvalidOperationException("no database error inside")));
        Assert.False(ExpectedUniqueViolations.Contains(null));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>Posts one request with its own correlation id and returns everything the host logged for it.</summary>
    private async Task<(HttpResponseMessage Response, List<LogEvent> Events)> SendAsync(HttpClient client, string path, object body)
    {
        var correlationId = $"log-{TestData.UniqueSuffix()}";
        var since = DateTimeOffset.Now;
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: TestData.Json) };
        request.Headers.Add("X-Correlation-Id", correlationId);

        var response = await client.SendAsync(request);

        var events = factory.Logs.EventsSince(since)
            .Where(e => e.Properties.TryGetValue("CorrelationId", out var value) && value is ScalarValue { Value: string id } && id == correlationId)
            .ToList();
        Assert.NotEmpty(events);
        return (response, events);
    }

    private Task EnsureUnregisteredIndexAsync() =>
        factory.QueryDbAsync(db => db.Database.ExecuteSqlRawAsync(
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{UnregisteredIndex}\" ON \"Customers\" (\"Name\") WHERE \"Name\" LIKE 'unregistered-%'"));

    private static bool IsEfCore(LogEvent logEvent) => SourceOf(logEvent)?.StartsWith(EfCorePrefix, StringComparison.Ordinal) == true;

    private static string? SourceOf(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string source } ? source : null;

    private static int? EventIdOf(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("EventId", out var value)
        && value is StructureValue structure
        && structure.Properties.FirstOrDefault(p => p.Name == "Id")?.Value is ScalarValue { Value: int id }
            ? id
            : null;
}
