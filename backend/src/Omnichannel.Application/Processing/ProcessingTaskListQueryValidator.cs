using FluentValidation;
using Omnichannel.Application.Common.Paging;
using Omnichannel.Application.Common.Validation;

namespace Omnichannel.Application.Processing;

public sealed class ProcessingTaskListQueryValidator : PageQueryValidator<ProcessingTaskListQuery>
{
    // Column lengths of ProcessingTasks.CorrelationId, ProcessingAttempts.WorkerName/WorkerInstanceId/ErrorCode.
    public const int MaxCorrelationIdLength = 100;
    public const int MaxWorkerNameLength = 100;
    public const int MaxWorkerInstanceIdLength = 100;
    public const int MaxErrorCodeLength = 64;

    public ProcessingTaskListQueryValidator()
        : base(ProcessingTaskListQuery.SortFields)
    {
        RuleFor(x => x.TaskType).IsInEnum();
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.CorrelationId).MaximumLength(MaxCorrelationIdLength);
        RuleFor(x => x.WorkerName).MaximumLength(MaxWorkerNameLength);
        RuleFor(x => x.WorkerInstanceId).MaximumLength(MaxWorkerInstanceIdLength);
        RuleFor(x => x.ErrorCode).MaximumLength(MaxErrorCodeLength);
        RuleFor(x => x.AttemptNo).GreaterThan(0);
        RuleFor(x => x.To)
            .GreaterThanOrEqualTo(x => x.From)
            .When(x => x.From.HasValue && x.To.HasValue)
            .WithMessage(_ => ValidationMessages.Get(
                ValidationMessages.Keys.DateRangeInverted,
                ("From", FieldNames.ForMember(nameof(ProcessingTaskListQuery.From)))));
    }
}
