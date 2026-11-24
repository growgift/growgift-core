"use client";

import { Suspense, useEffect, useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { api } from "@/lib/api";
import { useRequireAuth } from "@/lib/auth";
import { canSkipSubscribe, cardRuleNote, planSummary, type SubscriptionView } from "@/lib/billing";
import { safeInternalPath } from "@/lib/redirects";
import { SubscriptionCard } from "@/components/SubscriptionCard";

export default function SubscribePage() {
  return (
    <Suspense>
      <Subscribe />
    </Suspense>
  );
}

function Subscribe() {
  const session = useRequireAuth();
  const params = useSearchParams();
  const then = safeInternalPath(params.get("then"), "/");
  const canceled = params.get("canceled") === "1";
  const [view, setView] = useState<SubscriptionView | null>(null);
  const [error, setError] = useState(false);

  useEffect(() => {
    if (!session) return;
    api.subscription().then(setView).catch(() => setError(true));
  }, [session]);

  if (!session) return null;

  return (
    <div className="mx-auto max-w-xl px-5 py-16 space-y-6">
      <h1 className="font-display text-4xl font-semibold">
        {view?.cardRequired ? "One last step to finish signing up" : "Your GrowGift subscription"}
      </h1>

      {canceled && (
        <p className="rounded-xl bg-ribbon/15 border-[1.5px] border-ribbon px-4 py-3 text-sm">
          Checkout was cancelled — nothing was charged. You can start again whenever you&apos;re ready.
        </p>
      )}

      {error ? (
        <p className="text-sm text-red-700">We couldn&apos;t load your subscription just now. Please refresh to try again.</p>
      ) : !view ? (
        <p className="text-sm text-ink/50">Loading…</p>
      ) : (
        <>
          <p className="text-ink/70">{view.required ? planSummary(view) : "Your account type doesn't need a subscription."}</p>
          <SubscriptionCard view={view} />
          {view.required && <p className="text-xs text-ink/60">{cardRuleNote(view.minCardMonths)}</p>}
          <ul className="text-sm text-ink/70 list-disc pl-5 space-y-1">
            <li>Payment is taken securely by Stripe. We never see or store your card number.</li>
            <li>If your subscription ever lapses, you can still sign in, redeem gifts and sell your shares. Only new gift purchases and new family features pause.</li>
          </ul>
        </>
      )}

      {(!view || canSkipSubscribe(view)) && (
        <p className="text-sm">
          <Link href={then} className="underline text-ink/70 hover:text-leaf-deep">
            Not now — continue
          </Link>
        </p>
      )}
    </div>
  );
}
