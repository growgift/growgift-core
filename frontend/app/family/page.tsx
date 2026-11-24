"use client";

import { useCallback, useEffect, useState } from "react";
import { api, ApiError } from "@/lib/api";
import { useRequireAuth } from "@/lib/auth";
import { Modal, AddChildModal } from "@/components/FamilyModals";

type ChildRow = Awaited<ReturnType<typeof api.listChildren>>[number];

const jarMeta = [
  { key: "shortTerm", label: "Short-Term", lock: "1 yr", tint: "bg-mint" },
  { key: "mediumTerm", label: "Medium-Term", lock: "3 yrs", tint: "bg-ribbon/30" },
  { key: "longTerm", label: "Long-Term", lock: "10 yrs", tint: "bg-leaf/20" },
] as const;

export default function FamilyDashboard() {
  // Deliberately no requireKyc. Building a family tree is just organizational
  // data and doesn't need a brokerage account — the backend's "Parent"
  // policy already dropped its KYC requirement for exactly that reason, and
  // the intended order is: build the tree, pick who a gift is for, THEN
  // onboard with ViewTrade. This frontend gate was left over from before
  // that change, and it bounced anyone not yet onboarded straight into the
  // broker application the moment they tried to add a family member.
  const session = useRequireAuth();
  const [children, setChildren] = useState<ChildRow[] | null>(null);
  const [partners, setPartners] = useState<{ id: string; email: string; addedAtUtc: string }[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showAddChild, setShowAddChild] = useState(false);
  const [showAddPartner, setShowAddPartner] = useState(false);
  const [addTaskFor, setAddTaskFor] = useState<ChildRow | null>(null);
  const [becomingParent, setBecomingParent] = useState(false);
  // Tracks the promotion locally rather than waiting for a full page
  // reload — api.becomeParent() already refreshes the stored token, but
  // this component's own session variable (from useRequireAuth, read once
  // on mount) wouldn't otherwise reflect the change until next navigation.
  const [justBecameParent, setJustBecameParent] = useState(false);

  const load = useCallback(async () => {
    try {
      const [childList, family] = await Promise.all([api.listChildren(), api.getFamily()]);
      setChildren(childList);
      setPartners(family.partners);
      setError(null);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Couldn't load your family right now.");
    }
  }, []);

  const isParent = session?.role === "parent" || justBecameParent;

  useEffect(() => {
    if (isParent) void load();
  }, [isParent, load]);

  async function becomeParent() {
    setBecomingParent(true);
    setError(null);
    try {
      await api.becomeParent();
      setJustBecameParent(true);
    } catch {
      setError("Couldn't set up your family right now — try again.");
    } finally {
      setBecomingParent(false);
    }
  }

  async function approve(taskId: string) {
    try {
      await api.approveTask(taskId);
      await load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Approval failed — try again.");
    }
  }

  if (!session) return null; // redirecting to login

  if (!isParent) {
    return (
      <div className="mx-auto max-w-lg px-5 py-24 text-center space-y-6">
        <span className="text-6xl">🌳</span>
        <h1 className="font-display text-5xl font-semibold">Set up your family</h1>
        <p className="text-ink/70">
          You&apos;re signed in — no need to create another account. This just adds family features to the one
          you already have: add children (each gets their own read-only login and savings jars), link a partner,
          and redeem gifts directly into whoever they&apos;re for.
        </p>
        {error && <p className="text-sm text-red-700">{error}</p>}
        <button
          onClick={becomeParent}
          disabled={becomingParent}
          className="rounded-full bg-leaf text-white px-8 py-3 font-semibold hover:bg-leaf-deep disabled:opacity-40 transition"
        >
          {becomingParent ? "Setting up…" : "Get started"}
        </button>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-6xl px-5 py-14 space-y-10">
      <div className="flex items-end justify-between flex-wrap gap-4">
        <div>
          <h1 className="font-display text-5xl font-semibold">Your family tree</h1>
          <p className="text-ink/70 mt-2">
            Approve chores, watch jars grow, and hand it all over — officially — at 18.
          </p>
        </div>
        <button
          onClick={() => setShowAddChild(true)}
          className="rounded-full border-[1.5px] border-ink px-5 py-2.5 font-semibold hover:bg-mint transition"
        >
          + Add a child
        </button>
        <button
          onClick={() => setShowAddPartner(true)}
          className="rounded-full border-[1.5px] border-ink px-5 py-2.5 font-semibold hover:bg-mint transition"
        >
          + Add a partner
        </button>
      </div>

      {partners !== null && partners.length > 0 && (
        <div className="flex flex-wrap gap-2">
          {partners.map((p) => (
            <span key={p.id} className="rounded-full bg-mint px-4 py-1.5 text-sm font-semibold">
              {p.email}
            </span>
          ))}
        </div>
      )}

      {error && <p className="rounded-xl bg-red-50 border border-red-200 px-4 py-3 text-sm text-red-800">{error}</p>}

      {children === null && <p className="text-ink/50">Loading your family…</p>}

      {children?.length === 0 && (
        <div className="rounded-2xl border-[1.5px] border-dashed border-ink/30 p-12 text-center space-y-3">
          <span className="text-5xl">🌱</span>
          <p className="font-display text-2xl">Plant the first branch</p>
          <p className="text-ink/60 text-sm">Add your first child to open their three savings jars.</p>
          <button
            onClick={() => setShowAddChild(true)}
            className="rounded-full bg-leaf text-white px-6 py-2.5 font-semibold hover:bg-leaf-deep transition"
          >
            Add a child
          </button>
        </div>
      )}

      <div className="grid gap-8 lg:grid-cols-2">
        {children?.map((child) => {
          const total = child.jars.shortTerm + child.jars.mediumTerm + child.jars.longTerm;
          const age = Math.floor(
            (Date.now() - new Date(child.dateOfBirth).getTime()) / (365.25 * 24 * 3600 * 1000),
          );
          return (
            <section
              key={child.id}
              className="rounded-2xl border-[1.5px] border-ink bg-white shadow-card overflow-hidden"
            >
              <header className="flex items-center justify-between px-6 py-4 border-b-[1.5px] border-ink/10">
                <div>
                  <h2 className="font-display text-2xl">{child.name}</h2>
                  <p className="text-xs text-ink/60">{age} yrs · transfers at 18</p>
                  {child.loginCode && (
                    <p className="text-xs text-ink/50 font-mono mt-0.5">Kids&apos; login code: {child.loginCode}</p>
                  )}
                </div>
                <p className="font-mono text-xl font-semibold text-leaf-deep">${total.toFixed(2)}</p>
              </header>

              <div className="grid grid-cols-3 divide-x divide-ink/10">
                {jarMeta.map((j) => (
                  <div key={j.key} className={`p-4 text-center ${j.tint}`}>
                    <p className="text-[11px] font-bold uppercase tracking-wider">{j.label}</p>
                    <p className="font-mono text-lg font-semibold">${child.jars[j.key].toFixed(2)}</p>
                    <p className="text-[11px] text-ink/60">locked {j.lock}</p>
                  </div>
                ))}
              </div>

              <div className="px-6 py-4 space-y-3">
                <div className="flex items-center justify-between">
                  <p className="text-xs font-bold uppercase tracking-[0.15em] text-ink/50">Chores</p>
                  <button
                    onClick={() => setAddTaskFor(child)}
                    className="text-xs font-bold text-leaf-deep hover:underline"
                  >
                    + Add a chore
                  </button>
                </div>
                {child.pendingTasks.length === 0 && (
                  <p className="text-sm text-ink/50">No open chores. 🌱</p>
                )}
                {child.pendingTasks.map((t) => (
                  <div key={t.id} className="flex items-center justify-between gap-3">
                    <div>
                      <p className="text-sm font-semibold">{t.title}</p>
                      <p className="text-[11px] text-ink/50">{t.state}</p>
                    </div>
                    <div className="flex items-center gap-2 shrink-0">
                      <span className="font-mono text-sm">${t.reward.toFixed(2)}</span>
                      <button
                        onClick={() => approve(t.id)}
                        className="rounded-full bg-leaf text-white px-4 py-1.5 text-xs font-bold hover:bg-leaf-deep transition"
                      >
                        Approve
                      </button>
                    </div>
                  </div>
                ))}
              </div>
            </section>
          );
        })}
      </div>

      <p className="text-xs text-ink/50 max-w-2xl">
        Approved rewards split equally into the three jars (any leftover cent goes short → medium →
        long). Withdrawals respect each jar&apos;s lock.
      </p>

      {showAddChild && <AddChildModal onClose={() => setShowAddChild(false)} onDone={load} />}
      {showAddPartner && <AddPartnerModal onClose={() => setShowAddPartner(false)} onDone={load} />}
      {addTaskFor && (
        <AddTaskModal child={addTaskFor} onClose={() => setAddTaskFor(null)} onDone={load} />
      )}
    </div>
  );
}

function AddPartnerModal({ onClose, onDone }: { onClose: () => void; onDone: () => Promise<void> }) {
  const [email, setEmail] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit() {
    setBusy(true);
    setError(null);
    try {
      await api.addPartner({ email });
      await onDone();
      onClose();
    } catch (e) {
      setError(
        e instanceof ApiError
          ? e.message
          : "Couldn't link that account — check the email and try again.",
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal title="Add a partner" onClose={onClose}>
      <label className="block">
        <span className="text-sm font-bold">Their GrowGift email</span>
        <input
          type="email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf outline-none"
          placeholder="partner@example.com"
        />
        <span className="text-xs text-ink/50">
          They need an existing GrowGift account already — if they haven&apos;t signed up yet, ask them to do
          that first.
        </span>
      </label>
      {error && <p className="text-sm text-red-700">{error}</p>}
      <button
        onClick={submit}
        disabled={!email || busy}
        className="w-full rounded-full bg-leaf text-white py-3 font-semibold hover:bg-leaf-deep disabled:opacity-40 transition"
      >
        {busy ? "Linking…" : "Add to family"}
      </button>
    </Modal>
  );
}

function AddTaskModal({
  child,
  onClose,
  onDone,
}: {
  child: ChildRow;
  onClose: () => void;
  onDone: () => Promise<void>;
}) {
  const [title, setTitle] = useState("");
  const [reward, setReward] = useState("2.00");
  const [dueDate, setDueDate] = useState(new Date().toISOString().slice(0, 10));
  const [recurrence, setRecurrence] = useState<"OneTime" | "Daily" | "Weekly" | "Monthly">("OneTime");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit() {
    setBusy(true);
    setError(null);
    try {
      await api.createTask({
        childId: child.id,
        title,
        rewardAmount: Number(reward),
        dueDate,
        recurrence,
        requiresPhotoProof: false,
      });
      await onDone();
      onClose();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Couldn't create the chore.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal title={`New chore for ${child.name}`} onClose={onClose}>
      <label className="block">
        <span className="text-sm font-bold">What needs doing?</span>
        <input
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          maxLength={100}
          className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf outline-none"
          placeholder="Water the garden"
        />
      </label>
      <div className="grid grid-cols-2 gap-3">
        <label className="block">
          <span className="text-sm font-bold">Reward ($)</span>
          <input
            inputMode="decimal"
            value={reward}
            onChange={(e) => setReward(e.target.value.replace(/[^\d.]/g, ""))}
            className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 font-mono focus:border-leaf outline-none"
          />
        </label>
        <label className="block">
          <span className="text-sm font-bold">Due</span>
          <input
            type="date"
            value={dueDate}
            onChange={(e) => setDueDate(e.target.value)}
            className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf outline-none"
          />
        </label>
      </div>
      <label className="block">
        <span className="text-sm font-bold">Repeats</span>
        <select
          value={recurrence}
          onChange={(e) => setRecurrence(e.target.value as typeof recurrence)}
          className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf outline-none"
        >
          <option value="OneTime">Just once</option>
          <option value="Daily">Daily</option>
          <option value="Weekly">Weekly</option>
          <option value="Monthly">Monthly</option>
        </select>
      </label>
      {error && <p className="text-sm text-red-700">{error}</p>}
      <button
        onClick={submit}
        disabled={!title || !Number(reward) || busy}
        className="w-full rounded-full bg-leaf text-white py-3 font-semibold hover:bg-leaf-deep disabled:opacity-40 transition"
      >
        {busy ? "Creating…" : "Create chore"}
      </button>
      <p className="text-xs text-ink/50">
        When you approve it, the reward splits equally across {child.name}&apos;s three jars.
      </p>
    </Modal>
  );
}
