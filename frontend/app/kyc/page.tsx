"use client";

import { Suspense, useCallback, useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { api, ApiError } from "@/lib/api";
import { parseSession, useRequireAuth } from "@/lib/auth";
import { actionFor, needsBrokerageCheck, STATUS_UNAVAILABLE, waitForApproval, type GateAction } from "@/lib/brokerageGate";
import { safeInternalPath } from "@/lib/redirects";

export default function KycPage() {
  return (
    <Suspense>
      <Kyc />
    </Suspense>
  );
}

function Kyc() {
  const session = useRequireAuth();
  const router = useRouter();
  const params = useSearchParams();
  const then = safeInternalPath(params.get("then"), "/redeem?resume=1");

  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Same check the redeem page makes, repeated here because this page is also reached directly (and
  // after a claim bounced with 401/403): an existing, fully onboarded ViewTrade user must never be
  // shown "Start your brokerage application", and a failed check must never fall through to it.
  const [check, setCheck] = useState<"checking" | "ready" | Extract<GateAction, { kind: "stop" }>>("checking");
  const signedIn = !!session;
  const alreadyApproved = !!session && !needsBrokerageCheck(session);

  const runCheck = useCallback(async () => {
    setCheck("checking");
    try {
      const { decision } = await api.resolveExistingAccount();
      const action = actionFor(decision);
      if (action.kind === "onboard") return setCheck("ready");
      if (action.kind === "stop") return setCheck(action);
      const ok = await waitForApproval(async () => {
        await api.refreshSession();
        const s = parseSession();
        return !!s && !needsBrokerageCheck(s);
      });
      if (ok) return router.push(then);
      setCheck({
        kind: "stop",
        title: "Almost there",
        message: "We found your brokerage account and are finishing linking it. Please try again in a moment.",
        retryable: true,
      });
    } catch {
      setCheck(STATUS_UNAVAILABLE);
    }
  }, [router, then]);

  useEffect(() => {
    if (signedIn && !alreadyApproved) void runCheck();
  }, [signedIn, alreadyApproved, runCheck]);

  if (!session) return null;

  if (!needsBrokerageCheck(session)) {
    return (
      <div className="mx-auto max-w-xl px-5 py-20 text-center space-y-4">
        <span className="text-5xl">✅</span>
        <h1 className="font-display text-4xl font-semibold">You&apos;re already verified</h1>
        <button
          onClick={() => router.push(then)}
          className="rounded-full bg-leaf text-white px-8 py-3 font-semibold hover:bg-leaf-deep transition"
        >
          Continue
        </button>
      </div>
    );
  }

  if (check === "checking") {
    return (
      <div className="mx-auto max-w-xl px-5 py-20 text-center space-y-3">
        <h1 className="font-display text-3xl font-semibold">Checking your brokerage account…</h1>
        <p className="text-sm text-ink/60">This only takes a moment.</p>
      </div>
    );
  }
  if (check !== "ready") {
    return (
      <div className="mx-auto max-w-xl px-5 py-20 text-center space-y-4" role="alert">
        <h1 className="font-display text-3xl font-semibold">{check.title}</h1>
        <p className="text-ink/70">{check.message}</p>
        {check.retryable && (
          <button onClick={() => void runCheck()} className="rounded-full bg-ink text-canvas px-8 py-3 font-semibold">
            Try again
          </button>
        )}
        {/* Development-only escape hatch, same as the button below: lets local setups without ViewTrade
            credentials keep testing the redeem flow. Never rendered in a production build. */}
        {process.env.NODE_ENV !== "production" && (
          <button
            onClick={simulate}
            disabled={busy}
            className="w-full rounded-full border-[1.5px] border-dashed border-ribbon text-ink py-3 font-semibold hover:bg-ribbon/10 disabled:opacity-40 transition"
          >
            {busy ? "Verifying…" : "🛠 Simulate verification (dev only)"}
          </button>
        )}
        <p>
          <a href={then} className="text-sm font-semibold text-leaf-deep hover:underline">
            Back
          </a>
        </p>
      </div>
    );
  }

  async function simulate() {
    setBusy(true);
    setError(null);
    try {
      await api.devApproveKyc();
      router.push(then);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Verification failed — try again.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mx-auto max-w-xl px-5 py-16 space-y-8">
      <div className="text-center space-y-2">
        <h1 className="font-display text-5xl font-semibold">Verify your identity</h1>
        <p className="text-ink/70 max-w-md mx-auto">
          You&apos;re about to own real securities, so Australian law asks us to check who you are —
          once, and it takes about two minutes.
        </p>
      </div>

      <ol className="space-y-3">
        {[
          ["📄", "Have your photo ID ready", "Passport, driver licence, or national ID"],
          ["🤳", "Take a quick selfie", "Matched against your ID automatically"],
          ["🏦", "We open your brokerage account", "A fully disclosed account in your own name"],
        ].map(([emoji, title, sub]) => (
          <li
            key={title as string}
            className="flex gap-4 items-center rounded-2xl bg-white border-[1.5px] border-ink/15 px-5 py-4"
          >
            <span className="text-2xl">{emoji}</span>
            <div>
              <p className="font-semibold text-sm">{title}</p>
              <p className="text-xs text-ink/60">{sub}</p>
            </div>
          </li>
        ))}
      </ol>

      <div className="ticket p-6 space-y-4 text-center">
        <p className="text-sm text-ink/70">
          This opens a real brokerage account with <span className="font-semibold">ViewTrade</span>,
          our brokerage partner — it takes about 5–10 minutes and covers your details, a quick
          suitability questionnaire, and a photo of your ID.
        </p>
        <button
          onClick={() => router.push(`/onboarding?then=${encodeURIComponent(then)}`)}
          className="w-full rounded-full bg-ink text-canvas py-3 font-semibold hover:bg-leaf-deep transition"
        >
          Start your brokerage application
        </button>

        {/* Development-only shortcut; the backend endpoint does not exist outside Development */}
        <button
          onClick={simulate}
          disabled={busy}
          className="w-full rounded-full border-[1.5px] border-dashed border-ribbon text-ink py-3 font-semibold hover:bg-ribbon/10 disabled:opacity-40 transition"
        >
          {busy ? "Verifying…" : "🛠 Simulate verification (dev only)"}
        </button>
        {error && <p className="text-sm text-red-700">{error}</p>}
      </div>
    </div>
  );
}
