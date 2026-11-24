using GrowGift.SharedKernel.Domain;
using GrowGift.SharedKernel.Results;

namespace GrowGift.Family.Domain;

public enum TaskRecurrence { OneTime = 0, Daily = 1, Weekly = 2, Monthly = 3 }
public enum TaskState { Open = 0, Submitted = 1, Approved = 2, Rejected = 3, Expired = 4 }

/// <summary>
/// Parent-created task with an approval workflow. Only the parent who owns the
/// family can approve; approval is the single point where money is created in
/// the child's wallet (via the RewardDeposited flow). Photo proof is stored in
/// S3 under a presigned-upload key and referenced here, never inlined.
/// </summary>
public sealed class ChoreTask : AggregateRoot<Guid>
{
    private ChoreTask() { }

    public Guid ChildId { get; private set; }
    public Guid CreatedByParentId { get; private set; }
    public string Title { get; private set; } = default!;
    public string? Description { get; private set; }
    public Money Reward { get; private set; } = default!;
    public DateOnly DueDate { get; private set; }
    public TaskRecurrence Recurrence { get; private set; }
    public bool RequiresPhotoProof { get; private set; }
    public string? PhotoProofObjectKey { get; private set; }
    public TaskState State { get; private set; }
    public DateTimeOffset? ApprovedAtUtc { get; private set; }

    public static Result<ChoreTask> Create(Guid childId, Guid parentId, string title, string? description,
        Money reward, DateOnly dueDate, TaskRecurrence recurrence, bool requiresPhotoProof)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120)
            return Result.Failure<ChoreTask>(Error.Validation("task.title", "Title is required (max 120 chars)."));
        if (reward.IsNegative || reward.IsZero || reward.Amount > 1000m)
            return Result.Failure<ChoreTask>(Error.Validation("task.reward", "Reward must be between $0.01 and $1,000."));

        return new ChoreTask
        {
            Id = Guid.NewGuid(),
            ChildId = childId,
            CreatedByParentId = parentId,
            Title = title.Trim(),
            Description = description,
            Reward = reward,
            DueDate = dueDate,
            Recurrence = recurrence,
            RequiresPhotoProof = requiresPhotoProof,
            State = TaskState.Open
        };
    }

    public Result SubmitForApproval(string? photoProofObjectKey)
    {
        if (State != TaskState.Open)
            return Result.Failure(Error.Conflict("task.state", $"Task is {State}; only Open tasks can be submitted."));
        if (RequiresPhotoProof && string.IsNullOrEmpty(photoProofObjectKey))
            return Result.Failure(Error.Validation("task.proof", "This task needs a photo before it can be submitted."));

        PhotoProofObjectKey = photoProofObjectKey;
        State = TaskState.Submitted;
        return Result.Success();
    }

    public Result Approve(Guid approvingParentId)
    {
        if (approvingParentId != CreatedByParentId)
            return Result.Failure(Error.Forbidden("task.approver", "Only the parent who created this task can approve it."));
        if (State != TaskState.Submitted)
            return Result.Failure(Error.Conflict("task.state", $"Task is {State}; only Submitted tasks can be approved."));

        State = TaskState.Approved;
        ApprovedAtUtc = DateTimeOffset.UtcNow;
        Raise(new Events.Approved(Id, ChildId, Reward.Amount, Reward.Currency));
        return Result.Success();
    }

    public Result Reject(Guid parentId, string reason)
    {
        if (parentId != CreatedByParentId)
            return Result.Failure(Error.Forbidden("task.approver", "Only the parent who created this task can reject it."));
        if (State != TaskState.Submitted)
            return Result.Failure(Error.Conflict("task.state", "Only submitted tasks can be rejected."));
        State = TaskState.Rejected;
        return Result.Success();
    }

    /// <summary>For recurring tasks, approval spawns the next occurrence.</summary>
    public ChoreTask? SpawnNextOccurrence()
    {
        if (Recurrence == TaskRecurrence.OneTime || State != TaskState.Approved) return null;
        var nextDue = Recurrence switch
        {
            TaskRecurrence.Daily => DueDate.AddDays(1),
            TaskRecurrence.Weekly => DueDate.AddDays(7),
            TaskRecurrence.Monthly => DueDate.AddMonths(1),
            _ => DueDate
        };
        return Create(ChildId, CreatedByParentId, Title, Description, Reward,
            nextDue, Recurrence, RequiresPhotoProof).Value;
    }

    public static class Events
    {
        public sealed record Approved(Guid TaskId, Guid ChildId, decimal Reward, string Currency) : DomainEvent;
    }
}
