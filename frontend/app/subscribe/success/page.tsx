"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { api } from "@/lib/api";
import { useRequireAuth } from "@/lib/auth";
import { describeSubscription, type SubscriptionView } from "@/lib/billing";

// Stripe sends people back here after Checkout. The webhook usually has already updated us, but it can lag by a
// few seconds, so we ask the server to re-read Stripe (never trusting the URL itself) and wait briefly.
export default function SubscribeSuccessPage() {
  const session = useRequireAuth();
  const [view, setView] = useState<SubscriptionView | null>(null);
  const [waited, setWaited] = useState(false);

  useEffect(() => {
    if (!session) return;
    let cancelled = false;
    (async () => {
      for (let attempt = 0; attempt < 8 && !cancelled; attempt++) {
        try {
          await api.syncSubscription(); // re-reads Stripe and re-issues the token
          const v = await api.subscription();
          if (cancelled) return;
          setView(v);
          if (v.entitled || v.status === "card_rejected") return;
        } catch {
          /* keep trying briefly */
        }
        await new Promise((r) => setTimeout(r, 1500));
      }
      if (!cancelled) setWaited(true);
    })();
    return () => {
      cancelled = true;
    };
  }, [session]);

  if (!session) return null;

  const rejected = view?.status === "card_rejected";
  const ok = view?.entitled === true;
  const d = view ? describeSubscription(view) : null;

  return (
    <div className="mx-auto max-w-xl px-5 py-20 text-center space-y-6">
      {ok ? (
        <>
          <span className="text-6xl">🎉</span>
          <h1 className="font-display text-5xl font-semibold">You&apos;re subscribed!</h1>
          <p className="text-ink/70">{d?.detail}</p>
          <Link href="/profile" className="inline-block rounded-full bg-leaf text-white px-8 py-3 font-semibold hover:bg-leaf-deep transition">
            Continue
          </Link>
        </>
      ) : rejected ? (
        <>
          <h1 className="font-display text-4xl font-semibold">That card can&apos;t be used</h1>
          <p className="text-ink/70">{d?.detail} Nothing has been charged.</p>
          <Link href="/subscribe" className="inline-block rounded-full bg-leaf text-white px-8 py-3 font-semibold hover:bg-leaf-deep transition">
            Try another card
          </Link>
        </>
      ) : waited ? (
        <>
          <h1 className="font-display text-4xl font-semibold">Still confirming…</h1>
          <p className="text-ink/70">Your payment provider hasn&apos;t confirmed yet. This can take a minute. Check your profile shortly — you haven&apos;t lost anything.</p>
          <Link href="/profile" className="inline-block rounded-full bg-ink text-canvas px-8 py-3 font-semibold">
            Go to my profile
          </Link>
        </>
      ) : (
        <>
          <h1 className="font-display text-4xl font-semibold">Confirming your subscription…</h1>
          <p className="text-ink/60 text-sm">One moment.</p>
        </>
      )}
    </div>
  );
}
