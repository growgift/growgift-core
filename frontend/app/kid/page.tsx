"use client";

// Kids' read-only view. Big, friendly, zero buttons that move money — every
// mutating endpoint rejects the child role server-side regardless of what
// this UI shows, so nothing here needs its own protection against that.
// Keeps this page's original jar names/emoji/"money garden" framing, now
// wired to GET /me/child-view instead of the hardcoded "Maya" placeholder
// that was here before.

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { api, ApiError } from "@/lib/api";
import { parseSession } from "@/lib/auth";

type ChildView = Awaited<ReturnType<typeof api.getChildView>>;

const jarMeta = [
  { key: "shortTerm", emoji: "🫙", label: "Soon Jar", sub: "you can use this in 1 year" },
  { key: "mediumTerm", emoji: "🌿", label: "Growing Jar", sub: "unlocks in 3 years" },
  { key: "longTerm", emoji: "🌳", label: "Future You Jar", sub: "for 18-year-old you" },
] as const;

export default function KidView() {
  const router = useRouter();
  const [view, setView] = useState<ChildView | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Not useRequireAuth — its redirect targets (/login, /signup) are built
  // for adult flows and don't fit here. A child who isn't signed in, or
  // isn't actually a child account, goes to the kids' login page instead.
  useEffect(() => {
    const session = parseSession();
    if (!session || session.role !== "child") {
      router.replace("/kid-login");
      return;
    }
    api
      .getChildView()
      .then(setView)
      .catch((e) => setError(e instanceof ApiError ? e.message : "Couldn't load your jars right now."));
  }, [router]);

  if (error) {
    return (
      <div className="mx-auto max-w-md px-5 py-20 text-center">
        <p className="text-red-700">{error}</p>
      </div>
    );
  }

  if (!view) {
    return (
      <div className="mx-auto max-w-md px-5 py-20 text-center">
        <p className="text-ink/50">Loading your money garden…</p>
      </div>
    );
  }

  const total = view.jars.shortTerm + view.jars.mediumTerm + view.jars.longTerm;

  return (
    <div className="mx-auto max-w-3xl px-5 py-14 space-y-12">
      <div className="text-center space-y-3">
        <p className="text-sm font-bold uppercase tracking-[0.2em] text-leaf-deep">
          {view.name}&apos;s money garden
        </p>
        <h1 className="font-display text-6xl font-semibold">${total.toFixed(2)}</h1>
        <p className="text-ink/60">and it&apos;s all growing while you sleep 😴</p>
      </div>

      <div className="grid gap-5 sm:grid-cols-3">
        {jarMeta.map((j) => (
          <div key={j.key} className="rounded-3xl border-[1.5px] border-ink bg-white p-6 text-center shadow-card">
            <span className="text-5xl">{j.emoji}</span>
            <p className="font-display text-2xl mt-3">{j.label}</p>
            <p className="font-mono text-2xl font-semibold text-leaf-deep mt-1">${view.jars[j.key].toFixed(2)}</p>
            <p className="text-xs text-ink/60 mt-1">{j.sub}</p>
          </div>
        ))}
      </div>

      {view.pendingTasks.length > 0 && (
        <section className="space-y-4">
          <h2 className="font-display text-3xl font-semibold">Chores waiting for a grown-up to check</h2>
          <div className="space-y-3">
            {view.pendingTasks.map((t) => (
              <div
                key={t.id}
                className="flex items-center justify-between rounded-2xl bg-white border-[1.5px] border-ink/15 px-5 py-4"
              >
                <div>
                  <p className="font-semibold">{t.title}</p>
                  <p className="text-xs text-ink/60">{t.state}</p>
                </div>
                <span className="rounded-full bg-mint px-3 py-1 font-mono text-sm font-semibold">
                  ${t.reward.toFixed(2)}
                </span>
              </div>
            ))}
          </div>
        </section>
      )}

      <section className="space-y-4">
        <h2 className="font-display text-3xl font-semibold">Companies you own a piece of</h2>
        {view.giftedShares.length === 0 ? (
          <p className="text-sm text-ink/60">Nothing yet — ask a grown-up to redeem a gift for you!</p>
        ) : (
          view.giftedShares.map((s, i) => (
            <div
              key={i}
              className="flex items-center justify-between rounded-2xl bg-white border-[1.5px] border-ink/15 px-5 py-4"
            >
              <div>
                <p className="font-semibold">{s.symbol}</p>
                <p className="text-xs text-ink/60">
                  ${s.amount.toFixed(2)} {s.currency} · {new Date(s.redeemedAtUtc).toLocaleDateString()}
                </p>
              </div>
              <span className="rounded-full bg-mint px-3 py-1 font-mono text-sm font-semibold">{s.symbol}</span>
            </div>
          ))
        )}
      </section>

      <p className="rounded-2xl bg-ribbon/20 border-[1.5px] border-ribbon px-5 py-4 text-sm font-semibold text-center">
        On your 18th birthday, all of this becomes officially, legally yours. 🎓
      </p>
    </div>
  );
}
