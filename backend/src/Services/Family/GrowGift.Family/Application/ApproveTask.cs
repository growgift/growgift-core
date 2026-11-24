using GrowGift.Contracts.IntegrationEvents;
using GrowGift.Family.Domain;
using GrowGift.Family.Infrastructure.Persistence;
using GrowGift.Infrastructure.Common.Messaging;
using GrowGift.SharedKernel.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Family.Application;

/// <summary>
/// Approving a task deposits the reward into the child's jars in the SAME
/// transaction — money creation and approval can never diverge. Recurring
/// tasks spawn their next occurrence here too.
/// </summary>
public sealed record ApproveTaskCommand(Guid TaskId, Guid ParentUserId) : IRequest<Result>;

public sealed class ApproveTaskHandler(FamilyDbContext db, IEventPublisher publisher)
    : IRequestHandler<ApproveTaskCommand, Result>
{
    public async Task<Result> Handle(ApproveTaskCommand request, CancellationToken ct)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == request.TaskId, ct);
        if (task is null)
            return Result.Failure(Error.NotFound("task.not_found", "Task not found."));

        var approved = task.Approve(request.ParentUserId);
        if (approved.IsFailure) return approved;

        var wallet = await db.Wallets
            .Include("_ledger")
            .FirstOrDefaultAsync(w => w.ChildId == task.ChildId, ct);
        if (wallet is null)
            return Result.Failure(Error.NotFound("wallet.not_found", "The child's wallet was not found."));

        var deposit = wallet.DepositRewardSplit(task.Reward, task.Id);
        if (deposit.IsFailure) return deposit;

        if (task.SpawnNextOccurrence() is { } next)
            db.Tasks.Add(next);

        await publisher.EnqueueAsync(new TaskRewardApprovedV1(
            task.Id, task.ChildId, task.Reward.Amount, task.Reward.Currency),
            partitionKey: task.ChildId.ToString(), ct);

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
