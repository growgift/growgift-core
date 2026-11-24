using FluentAssertions;
using GrowGift.Family.Domain;
using GrowGift.SharedKernel.Domain;
using Xunit;

namespace GrowGift.Domain.Tests;

public class ChoreTaskTests
{
    private static ChoreTask NewTask(Guid parentId, TaskRecurrence recurrence = TaskRecurrence.OneTime) =>
        ChoreTask.Create(Guid.NewGuid(), parentId, "Make your bed", null,
            Money.Of(3m, "AUD"), DateOnly.FromDateTime(DateTime.UtcNow), recurrence, false).Value;

    [Fact]
    public void Only_creating_parent_can_approve()
    {
        var parent = Guid.NewGuid();
        var task = NewTask(parent);
        task.SubmitForApproval(null);

        task.Approve(Guid.NewGuid()).Error.Code.Should().Be("task.approver");
        task.Approve(parent).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Approving_weekly_task_spawns_next_occurrence()
    {
        var parent = Guid.NewGuid();
        var task = NewTask(parent, TaskRecurrence.Weekly);
        task.SubmitForApproval(null);
        task.Approve(parent);

        var next = task.SpawnNextOccurrence();
        next.Should().NotBeNull();
        next!.DueDate.Should().Be(task.DueDate.AddDays(7));
        next.State.Should().Be(TaskState.Open);
    }

    [Fact]
    public void Photo_proof_required_blocks_submission_without_photo()
    {
        var task = ChoreTask.Create(Guid.NewGuid(), Guid.NewGuid(), "Clean room", null,
            Money.Of(5m, "AUD"), DateOnly.FromDateTime(DateTime.UtcNow), TaskRecurrence.OneTime, true).Value;

        task.SubmitForApproval(null).Error.Code.Should().Be("task.proof");
        task.SubmitForApproval("s3://gg-proofs/abc.jpg").IsSuccess.Should().BeTrue();
    }
}
