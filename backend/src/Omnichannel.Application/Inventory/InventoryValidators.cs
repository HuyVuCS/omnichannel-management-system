using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Inventory;

public sealed class InventoryListQueryValidator() : PageQueryValidator<InventoryListQuery>(InventoryListQuery.SortFields);
