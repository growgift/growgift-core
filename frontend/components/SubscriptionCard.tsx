"use client";

import { useState } from "react";
import { api, ApiError } from "@/lib/api";
import { checkoutErrorMessage, describeSubscription, planSummary, type SubscriptionView } from "@/lib/billing";

const TONE: Record<string, string> = {
  good: "border-leaf/50 bg-leaf/10",
  info: "border-ink/20 bg-white",
  warn: "border-ribbon bg-ribbon/10",
  bad: "border-red-300 bg-red-50",
};

/**
 * Shows where the signed-in person's subscription stands and the one button that makes sense for it.
 * Both buttons hand off to Stripe-hosted pages; no card detail ever touches this site.
 */
export function SubscriptionCard({ view }: { view: SubscriptionView }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const d = describeSubscription(view);

  async function go(kind: "subscribe" | "manage") {
    setBusy(true);
    setError(null);
    try {
      const url = kind === "subscribe" ? await api.startSubscriptionCheckout() : await api.openBillingPortal();
      window.location.assign(url);
    } catch (e) {
      setError(e instanceof ApiError ? checkoutErrorMessage(e.code) : "Something went wrong. Please try again.");
      setBusy(false);
    }
  }

  return (
    <div className={`rounded-2xl border-[1.5px] p-5 space-y-3 ${TONE[d.tone]}`}>
      <div>
        <p className="font-display text-xl font-semibold">{d.headline}</p>
        <p className="text-sm text-ink/70">{d.detail}</p>
      </div>
      {d.action === "subscribe" && view.available && <p className="text-sm font-semibold">{planSummary(view)}</p>}
      {d.action !== "none" && view.available && (
        <button
          onClick={() => go(d.action === "manage" ? "manage" : "subscribe")}
          disabled={busy}
          className="rounded-full bg-leaf text-white px-6 py-2.5 text-sm font-semibold hover:bg-leaf-deep transition disabled:opacity-50"
        >
          {busy ? "Opening…" : d.action === "manage" ? "Manage subscription" : view.trialEligible ? "Start free trial" : "Subscribe"}
        </button>
      )}
      {d.action !== "none" && !view.available && <p className="text-xs text-ink/60">Subscriptions aren&apos;t available just yet.</p>}
      {error && <p className="text-sm text-red-700">{error}</p>}
    </div>
  );
}
