using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Omnichannel.Application.Common.Exceptions;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Customers;
using Omnichannel.Domain.Entities;

namespace Omnichannel.Infrastructure.Persistence.Customers;

internal sealed class CustomerService(OmnichannelDbContext dbContext) : ICustomerService
{
    private static readonly Expression<Func<Customer, CustomerResponse>> ToResponse = c => new CustomerResponse(
        c.CustomerId, c.Name, c.Phone, c.Email, c.Address, c.CreatedAt, c.UpdatedAt);

    private static readonly Func<Customer, CustomerResponse> ToResponseCompiled = ToResponse.Compile();

    public async Task<PagedResult<CustomerResponse>> ListAsync(CustomerListQuery query, CancellationToken cancellationToken)
    {
        var customers = dbContext.Customers.AsNoTracking();

        if (query.SearchTerm is { } search)
        {
            var pattern = LikePattern.Contains(search);
            customers = customers.Where(c =>
                EF.Functions.ILike(c.Name, pattern, LikePattern.EscapeCharacter)
                || EF.Functions.ILike(c.Phone!, pattern, LikePattern.EscapeCharacter)
                || EF.Functions.ILike(c.Email!, pattern, LikePattern.EscapeCharacter));
        }

        var totalCount = await customers.CountAsync(cancellationToken);

        var ordered = query.IsSortedBy(CustomerListQuery.SortByCreatedAt)
            ? customers.OrderBy(c => c.CreatedAt, query.IsDescending)
            : customers.OrderBy(c => c.Name, query.IsDescending);

        var items = await ordered
            .ThenBy(c => c.CustomerId)
            .Skip(query.Skip)
            .Take(query.PageSize)
            .Select(ToResponse)
            .ToListAsync(cancellationToken);

        return new PagedResult<CustomerResponse>(items, query.Page, query.PageSize, totalCount);
    }

    public async Task<CustomerResponse> GetAsync(Guid customerId, CancellationToken cancellationToken) =>
        await dbContext.Customers
            .AsNoTracking()
            .Where(c => c.CustomerId == customerId)
            .Select(ToResponse)
            .SingleOrDefaultAsync(cancellationToken)
        ?? throw new NotFoundException(nameof(Customer), customerId);

    public async Task<CustomerResponse> CreateAsync(CustomerRequest request, CancellationToken cancellationToken)
    {
        var customer = new Customer(request.Name, request.Phone, request.Email, request.Address);
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponseCompiled(customer);
    }

    public async Task<CustomerResponse> UpdateAsync(Guid customerId, CustomerRequest request, CancellationToken cancellationToken)
    {
        var customer = await dbContext.Customers.SingleOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), customerId);

        customer.Update(request.Name, request.Phone, request.Email, request.Address);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponseCompiled(customer);
    }
}
