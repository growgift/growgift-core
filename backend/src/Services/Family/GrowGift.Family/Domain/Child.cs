using GrowGift.SharedKernel.Domain;
using GrowGift.SharedKernel.Results;

namespace GrowGift.Family.Domain;

/// <summary>
/// A child in the family tree. Children get a read-only login: their identity
/// role is "child" and no API policy grants a child any mutating scope. On the
/// 18th birthday a scheduled job raises ChildTurned18, which starts the
/// custodial → own-account transfer workflow at the broker.
/// </summary>
public sealed class Child : AggregateRoot<Guid>
{
    private Child() { }

    public Guid FamilyId { get; private set; }
    public Guid ParentUserId { get; private set; }
    public string DisplayName { get; private set; } = default!;
    public DateOnly DateOfBirth { get; private set; }
    public Guid? ChildLoginUserId { get; private set; }
    public string? LoginCode { get; private set; }
    public bool OwnershipTransferred { get; private set; }
    public int TransferAgeYears { get; private set; }

    public static Result<Child> Create(Guid familyId, Guid parentUserId, string displayName,
        DateOnly dateOfBirth, int transferAgeYears = 18)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (dateOfBirth > today)
            return Result.Failure<Child>(Error.Validation("child.dob", "Date of birth cannot be in the future."));
        if (dateOfBirth < today.AddYears(-transferAgeYears))
            return Result.Failure<Child>(Error.Validation("child.age",
                $"A child must be under {transferAgeYears}. Adults redeem gifts into their own account."));

        return new Child
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId,
            ParentUserId = parentUserId,
            DisplayName = displayName.Trim(),
            DateOfBirth = dateOfBirth,
            TransferAgeYears = transferAgeYears
        };
    }

    public void AttachReadOnlyLogin(Guid userId, string loginCode)
    {
        ChildLoginUserId = userId;
        LoginCode = loginCode;
    }

    public bool HasReachedTransferAge(DateOnly today) =>
        DateOnly.FromDateTime(DateOfBirth.ToDateTime(TimeOnly.MinValue).AddYears(TransferAgeYears)) <= today;

    public Result MarkOwnershipTransferred()
    {
        if (OwnershipTransferred)
            return Result.Failure(Error.Conflict("child.transferred", "Ownership already transferred."));
        OwnershipTransferred = true;
        Raise(new Events.OwnershipTransferred(Id, ParentUserId));
        return Result.Success();
    }

    public static class Events
    {
        public sealed record OwnershipTransferred(Guid ChildId, Guid ParentUserId) : DomainEvent;
    }
}
