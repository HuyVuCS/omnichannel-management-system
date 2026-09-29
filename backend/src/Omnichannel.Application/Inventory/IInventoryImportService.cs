namespace Omnichannel.Application.Inventory;

public interface IInventoryImportService
{
    /// <summary>
    /// Adds stock for every item atomically: inventory update, movement and <c>inventory.changed</c> outbox event
    /// per item, all in one transaction. Either every item is applied or none.
    /// </summary>
    /// <exception cref="FluentValidation.ValidationException">An item refers to a product that does not exist.</exception>
    /// <exception cref="Common.Exceptions.ConflictException">
    /// The document was already applied (<see cref="InventoryErrorCodes.ImportAlreadyApplied"/>), or a product's stock
    /// would exceed the maximum (<see cref="InventoryErrorCodes.StockLimitExceeded"/>).
    /// </exception>
    Task<ImportInventoryResponse> ImportAsync(ImportInventoryRequest request, CancellationToken cancellationToken);
}
