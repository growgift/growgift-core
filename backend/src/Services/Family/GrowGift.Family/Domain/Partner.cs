using GrowGift.SharedKernel.Domain;
using GrowGift.SharedKernel.Results;

namespace GrowGift.Family.Domain;

/// <summary>
/// Links an existing GrowGift account as a co-parent on the same family
/// tree. Unlike Child, a Partner isn't read-only — Identity promotes their
/// account to role=parent when linked (see the internal
/// /internal/v1/users/link-as-parent call in FamilyEndpoints), so once
/// linked they pass the existing "Parent" authorization policy on their own
/// account, with full access to the family's children and tasks. This
/// record exists so family-scoped endpoints can resolve "which family does
/// this caller belong to" for a partner the same way they already do for
/// the master parent (whose own user id doubles as the family id — see
/// Child.Create's doc).
/// </summary>
public sealed class Partner : AggregateRoot<Guid>
{
    private Partner() { }

    public Guid FamilyId { get; private set; }
    public Guid UserId { get; private set; }
    public string Email { get; private set; } = default!;
    public DateTimeOffset AddedAtUtc { get; private set; }

    public static Result<Partner> Create(Guid familyId, Guid userId, string email)
    {
        if (userId == familyId)
            return Result.Failure<Partner>(Error.Validation(
                "partner.self", "The family's own master account can't also be added as a partner."));

        return new Partner
        {
            Id = Guid.NewGuid(),
            FamilyId = familyId,
            UserId = userId,
            Email = email.Trim().ToLowerInvariant(),
            AddedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
