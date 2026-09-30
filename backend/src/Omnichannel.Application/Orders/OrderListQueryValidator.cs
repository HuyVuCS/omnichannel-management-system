using FluentValidation;
using Omnichannel.Application.Channels;
using Omnichannel.Application.Common.Paging;

namespace Omnichannel.Application.Orders;

public sealed class OrderListQueryValidator : PageQueryValidator<OrderListQuery>
{
    public OrderListQueryValidator()
        : base(OrderListQuery.SortFields)
    {
        RuleFor(x => x.Channel).MaximumLength(CreateChannelRequestValidator.MaxSlugLength);
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.To)
            .GreaterThanOrEqualTo(x => x.From)
            .When(x => x.From.HasValue && x.To.HasValue)
            .WithMessage("'To' must not be earlier than 'From'.");
    }
}
