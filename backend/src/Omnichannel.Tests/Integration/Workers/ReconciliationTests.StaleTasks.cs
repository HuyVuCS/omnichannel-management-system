using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Omnichannel.Application.Processing;
using Omnichannel.Domain.Enums;
using Omnichannel.Workers.StaleTaskWorker;

namespace Omnichannel.Tests.Integration.Workers;

/// <summary>
/// Task 026b Part C: reconciliation trusts an unfinished stock-sync task and skips its pair. A task whose message is
/// lost would be trusted for good; once the stale-task sweep failed it, the next run pushes the pair again.
/// </summary>
public sealed partial class ReconciliationTests
{
    [Fact]
    public async Task A_pair_whose_sync_task_stands_still_is_synced_again_once_the_sweep_failed_the_task()
    {
        var product = await MappedAsync(Website, centralStock: 7, channelStock: 9);

        // Drift: one task for the pair. Its message is never delivered (no Workers run here).
        var first = await RunAsync();
        Assert.Equal((true, 1, 0, 1, 1, 0, 0, 0), Counts(first));
        var lost = Assert.Single(await ReconciliationTasksAsync());

        // As long as the task is unfinished the pair is skipped - however old the task is.
        await DbAsync(db => db.Database.ExecuteSqlAsync(
            $"""UPDATE "ProcessingTasks" SET "CreatedAt" = now() - interval '2 hours' WHERE "TaskId" = {lost.TaskId}"""));
        var second = await RunAsync();
        Assert.Equal((true, 0, 0, 0, 0, 0, 1, 0), Counts(second));
        Assert.Single(await ReconciliationTasksAsync());

        var sweep = await ServicesAsync(services => services.GetRequiredService<IStaleTaskSweep>()
            .RunAsync(Guid.CreateVersion7(), StaleTaskSweepService.WorkerName, "reconciliation-tools", CancellationToken.None));
        Assert.Equal([lost.TaskId], sweep.Failed);

        // The pair still differs and nothing is unfinished any more: a new task (the failed one was not rejected by the channel).
        var third = await RunAsync();
        Assert.Equal((true, 1, 0, 1, 1, 0, 0, 0), Counts(third));
        var tasks = await ReconciliationTasksAsync();
        Assert.Equal(
            [(lost.TaskId, ProcessingTaskStatus.Failed), (tasks[1].TaskId, ProcessingTaskStatus.Pending)],
            tasks.Select(t => (t.TaskId, t.Status)));
        Assert.Equal((product.ProductId, lost.ChannelId), (tasks[1].ProductId!.Value, tasks[1].ChannelId));
        Assert.Equal("stale_no_progress", Assert.Single(tasks[0].Attempts).ErrorCode);
    }
}
