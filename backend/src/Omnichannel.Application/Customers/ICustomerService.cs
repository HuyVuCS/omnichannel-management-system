using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Customers;

public interface ICustomerService
{
    Task<PagedResult<CustomerResponse>> ListAsync(CustomerListQuery query, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException">The customer does not exist.</exception>
    Task<CustomerResponse> GetAsync(Guid customerId, CancellationToken cancellationToken);

    Task<CustomerResponse> CreateAsync(CustomerRequest request, CancellationToken cancellationToken);

    /// <exception cref="Common.Exceptions.NotFoundException">The customer does not exist.</exception>
    Task<CustomerResponse> UpdateAsync(Guid customerId, CustomerRequest request, CancellationToken cancellationToken);
}
