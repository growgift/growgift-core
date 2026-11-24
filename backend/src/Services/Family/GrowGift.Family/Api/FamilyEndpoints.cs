using System.Net.Http.Json;
using System.Security.Claims;
using GrowGift.Family.Application;
using GrowGift.Family.Domain;
using GrowGift.Family.Infrastructure.Persistence;
using GrowGift.SharedKernel.Domain;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GrowGift.Family.Api;

public static class FamilyEndpoints
{
    // A partner's own user id is never the family id their children are
    // stored under (that's always the master parent's own id — see
    // Child.Create's doc) — so any endpoint that needs "which family does
    // this caller belong to" has to check the Partners link first, falling
    // back to the caller's own id only when no such link exists. Every
    // family-scoped query below goes through this rather than comparing
    // against the raw caller id directly, which is exactly the gap that
    // meant a linked partner couldn't see their own family's children
    // before this existed.
    private static async Task<Guid> GetEffectiveFamilyIdAsync(Guid callerId, FamilyDbContext db, CancellationToken ct)
    {
        var partner = await db.Partners.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == callerId, ct);
        return partner?.FamilyId ?? callerId;
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapPost("/children", async ([FromBody] CreateChildRequest body,
            ClaimsPrincipal user, FamilyDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (GrowGift.SharedKernel.Billing.BillingGate.IsBlocked(user))
                return Results.Json(GrowGift.SharedKernel.Billing.BillingGate.BlockedBody, statusCode: StatusCodes.Status402PaymentRequired);
            var callerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var familyId = await GetEffectiveFamilyIdAsync(callerId, db, ct);

            // Retry/refresh/double-click safety: the same name + date of birth
            // in the same family is the same child, not a new one. Returns the
            // existing record (and its login code) instead of creating a
            // duplicate child with a second Identity login.
            var wantedName = (body.DisplayName ?? "").Trim().ToLower();
            var existing = await db.Children.AsNoTracking().FirstOrDefaultAsync(c =>
                c.FamilyId == familyId && c.DateOfBirth == body.DateOfBirth &&
                c.DisplayName.ToLower() == wantedName, ct);
            if (existing is not null)
                return Results.Ok(new { childId = existing.Id, loginCode = existing.LoginCode, alreadyExisted = true });

            var child = Child.Create(familyId, callerId, body.DisplayName, body.DateOfBirth);
            if (child.IsFailure) return Results.BadRequest(new { error = new { code = child.Error.Code, message = child.Error.Message } });

            // The child's login is created in Identity, not here — Family
            // owns the family tree, Identity owns authentication, and this
            // is a synchronous internal call rather than an event because
            // the parent needs the generated login code back immediately
            // to write it down/share with the child (see User.RegisterChildLogin's
            // doc for why this reuses the plain email/password mechanism).
            var identity = httpFactory.CreateClient("identity");
            var accountResponse = await identity.PostAsJsonAsync("/api/v1/auth/internal/v1/child-accounts",
                new { pin = body.Pin }, ct);
            if (!accountResponse.IsSuccessStatusCode)
                return Results.Problem(statusCode: 502, detail: "Couldn't create the child's login right now — try again.");

            var account = await accountResponse.Content.ReadFromJsonAsync<ChildAccountResponse>(ct)
                ?? throw new InvalidOperationException("Empty child-account response from Identity.");
            child.Value.AttachReadOnlyLogin(account.UserId, account.LoginCode);

            db.Children.Add(child.Value);
            db.Wallets.Add(ChildWallet.Open(child.Value.Id, body.Currency ?? "AUD"));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/children/{child.Value.Id}",
                new { childId = child.Value.Id, loginCode = account.LoginCode });
        }).RequireAuthorization("Parent");

        // A partner is an existing GrowGift account, found by email and
        // promoted to role=parent in Identity (see link-as-parent's own
        // doc) — not a new invite/signup flow. If they don't have an
        // account yet, this tells the caller so rather than silently
        // creating one with no password the person never set.
        v1.MapPost("/partners", async ([FromBody] AddPartnerRequest body,
            ClaimsPrincipal user, FamilyDbContext db, IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            if (GrowGift.SharedKernel.Billing.BillingGate.IsBlocked(user))
                return Results.Json(GrowGift.SharedKernel.Billing.BillingGate.BlockedBody, statusCode: StatusCodes.Status402PaymentRequired);
            var callerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var familyId = await GetEffectiveFamilyIdAsync(callerId, db, ct);

            var identity = httpFactory.CreateClient("identity");
            var linkResponse = await identity.PostAsJsonAsync("/api/v1/auth/internal/v1/users/link-as-parent",
                new { email = body.Email }, ct);
            if (linkResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Results.BadRequest(new { error = new { code = "partner.no_account", message = "No GrowGift account exists for that email yet — ask them to sign up first." } });
            if (!linkResponse.IsSuccessStatusCode)
                return Results.Problem(statusCode: 502, detail: "Couldn't link that account right now — try again.");

            var linked = await linkResponse.Content.ReadFromJsonAsync<LinkedUserResponse>(ct)
                ?? throw new InvalidOperationException("Empty link-as-parent response from Identity.");

            var partner = Partner.Create(familyId, linked.UserId, body.Email);
            if (partner.IsFailure) return Results.BadRequest(new { error = new { code = partner.Error.Code, message = partner.Error.Message } });

            db.Partners.Add(partner.Value);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Unique index on Partners.UserId — that account is already
                // linked to a (possibly different) family.
                return Results.Conflict(new { error = new { code = "partner.already_linked", message = "That account is already linked to a family." } });
            }

            return Results.Created($"/api/v1/partners/{partner.Value.Id}", new { partnerId = partner.Value.Id });
        }).RequireAuthorization("Parent");

        // The whole tree in one call — self, partner (if any), and
        // children — for the family management page and the redemption
        // flow's "who is this for?" picker.
        v1.MapGet("/family", async (ClaimsPrincipal user, FamilyDbContext db, CancellationToken ct) =>
        {
            var callerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var familyId = await GetEffectiveFamilyIdAsync(callerId, db, ct);

            var partners = await db.Partners.AsNoTracking().Where(p => p.FamilyId == familyId).ToListAsync(ct);
            var children = await db.Children.AsNoTracking().Where(c => c.FamilyId == familyId)
                .OrderBy(c => c.DisplayName).ToListAsync(ct);

            return Results.Ok(new
            {
                familyId,
                partners = partners.Select(p => new { p.Id, p.Email, p.AddedAtUtc }),
                children = children.Select(c => new
                {
                    c.Id,
                    c.DisplayName,
                    c.DateOfBirth,
                    loginCode = c.LoginCode,
                    ownershipTransferred = c.OwnershipTransferred
                })
            });
        }).RequireAuthorization("Parent");

        v1.MapPost("/tasks", async ([FromBody] CreateTaskRequest body,
            ClaimsPrincipal user, FamilyDbContext db, CancellationToken ct) =>
        {
            if (GrowGift.SharedKernel.Billing.BillingGate.IsBlocked(user))
                return Results.Json(GrowGift.SharedKernel.Billing.BillingGate.BlockedBody, statusCode: StatusCodes.Status402PaymentRequired);
            var callerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var familyId = await GetEffectiveFamilyIdAsync(callerId, db, ct);
            var owned = await db.Children.AnyAsync(c => c.Id == body.ChildId && c.FamilyId == familyId, ct);
            if (!owned) return Results.Forbid();

            var task = ChoreTask.Create(body.ChildId, callerId, body.Title, body.Description,
                Money.Of(body.RewardAmount, body.Currency ?? "AUD"),
                body.DueDate, body.Recurrence, body.RequiresPhotoProof);
            if (task.IsFailure) return Results.BadRequest(new { error = new { code = task.Error.Code, message = task.Error.Message } });

            db.Tasks.Add(task.Value);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/tasks/{task.Value.Id}", new { taskId = task.Value.Id });
        }).RequireAuthorization("Parent");

        v1.MapPost("/tasks/{taskId:guid}/approve", async (Guid taskId,
            ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
        {
            var parentId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var result = await sender.Send(new ApproveTaskCommand(taskId, parentId), ct);
            return result.IsFailure
                ? Results.Conflict(new { error = new { code = result.Error.Code, message = result.Error.Message } })
                : Results.NoContent();
        }).RequireAuthorization("Parent");

        // Parent dashboard: all children with jar balances and open/submitted
        // tasks, one round trip.
        v1.MapGet("/children", async (ClaimsPrincipal user, FamilyDbContext db, CancellationToken ct) =>
        {
            var callerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var familyId = await GetEffectiveFamilyIdAsync(callerId, db, ct);
            var children = await db.Children.Where(c => c.FamilyId == familyId)
                .OrderBy(c => c.DisplayName).ToListAsync(ct);
            var childIds = children.Select(c => c.Id).ToList();
            var wallets = await db.Wallets.Where(w => childIds.Contains(w.ChildId)).ToListAsync(ct);
            var tasks = await db.Tasks.Where(t => childIds.Contains(t.ChildId)
                && (t.State == TaskState.Open || t.State == TaskState.Submitted)).ToListAsync(ct);

            return Results.Ok(children.Select(c =>
            {
                var wallet = wallets.First(w => w.ChildId == c.Id);
                return new
                {
                    id = c.Id,
                    name = c.DisplayName,
                    dateOfBirth = c.DateOfBirth,
                    loginCode = c.LoginCode,
                    jars = new
                    {
                        shortTerm = wallet.BalanceOf(JarKind.ShortTerm).Amount,
                        mediumTerm = wallet.BalanceOf(JarKind.MediumTerm).Amount,
                        longTerm = wallet.BalanceOf(JarKind.LongTerm).Amount
                    },
                    pendingTasks = tasks.Where(t => t.ChildId == c.Id).Select(t => new
                    {
                        id = t.Id,
                        title = t.Title,
                        reward = t.Reward.Amount,
                        state = t.State.ToString()
                    })
                };
            }));
        }).RequireAuthorization("Parent");

        v1.MapGet("/children/{childId:guid}/jars", async (Guid childId,
            ClaimsPrincipal user, FamilyDbContext db, CancellationToken ct) =>
        {
            var callerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var child = await db.Children.AsNoTracking().FirstOrDefaultAsync(c => c.Id == childId, ct);
            if (child is null) return Results.NotFound();

            // Covers three legitimate viewers: the child's own read-only
            // login, the master parent who created them, or a linked
            // partner in the same family — checked via family id rather
            // than the original ParentUserId so a partner isn't wrongly
            // excluded from a family they were actually added to.
            var callerFamilyId = await GetEffectiveFamilyIdAsync(callerId, db, ct);
            if (child.ChildLoginUserId != callerId && child.FamilyId != callerFamilyId) return Results.Forbid();

            var wallet = await db.Wallets.Include("_ledger").FirstAsync(w => w.ChildId == childId, ct);
            return Results.Ok(new
            {
                shortTerm = wallet.BalanceOf(JarKind.ShortTerm).Amount,
                mediumTerm = wallet.BalanceOf(JarKind.MediumTerm).Amount,
                longTerm = wallet.BalanceOf(JarKind.LongTerm).Amount,
                currency = wallet.Currency
            });
        }).RequireAuthorization("ChildReadOnly");

        // A logged-in child's own dashboard, found via their login user id
        // rather than requiring a childId in the URL (which the child has
        // no way to know — their JWT only carries ChildLoginUserId, a
        // different id than Child.Id). One call for everything the
        // read-only view needs: jars, pending chores, and gifted shares
        // (via a call to Gifting, which has no concept of families at all
        // and so can't answer "what's this child's" on its own).
        v1.MapGet("/me/child-view", async (ClaimsPrincipal user, FamilyDbContext db,
            IHttpClientFactory httpFactory, CancellationToken ct) =>
        {
            var callerId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var child = await db.Children.AsNoTracking().FirstOrDefaultAsync(c => c.ChildLoginUserId == callerId, ct);
            if (child is null) return Results.NotFound(new { error = new { code = "child.not_found", message = "No child profile is linked to this login." } });

            var wallet = await db.Wallets.Include("_ledger").FirstAsync(w => w.ChildId == child.Id, ct);
            var tasks = await db.Tasks.AsNoTracking()
                .Where(t => t.ChildId == child.Id && (t.State == TaskState.Open || t.State == TaskState.Submitted))
                .ToListAsync(ct);

            var gifting = httpFactory.CreateClient("gifting");
            var giftsResponse = await gifting.GetAsync($"/api/v1/internal/v1/gifts/for-child/{child.Id}", ct);
            var gifts = giftsResponse.IsSuccessStatusCode
                ? await giftsResponse.Content.ReadFromJsonAsync<List<ChildGiftResponse>>(ct) ?? []
                : []; // non-fatal — the rest of the dashboard (jars, chores) still renders fine without it

            return Results.Ok(new
            {
                name = child.DisplayName,
                jars = new
                {
                    shortTerm = wallet.BalanceOf(JarKind.ShortTerm).Amount,
                    mediumTerm = wallet.BalanceOf(JarKind.MediumTerm).Amount,
                    longTerm = wallet.BalanceOf(JarKind.LongTerm).Amount,
                    currency = wallet.Currency
                },
                pendingTasks = tasks.Select(t => new { id = t.Id, title = t.Title, reward = t.Reward.Amount, state = t.State.ToString() }),
                giftedShares = gifts.Select(g => new { g.Symbol, g.Amount, g.Currency, g.RedeemedAtUtc })
            });
        }).RequireAuthorization("ChildReadOnly");
    }

    private sealed record ChildAccountResponse(Guid UserId, string LoginCode);
    private sealed record ChildGiftResponse(Guid GiftId, string Symbol, decimal Amount, string Currency, DateTimeOffset RedeemedAtUtc);
    private sealed record LinkedUserResponse(Guid UserId);

    public sealed record CreateChildRequest(string DisplayName, DateOnly DateOfBirth, string Pin, string? Currency, Guid FamilyId = default);
    public sealed record AddPartnerRequest(string Email);
    public sealed record CreateTaskRequest(Guid ChildId, string Title, string? Description,
        decimal RewardAmount, string? Currency, DateOnly DueDate, TaskRecurrence Recurrence, bool RequiresPhotoProof);
}
