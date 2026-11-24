"use client";

import { Suspense, useEffect, useRef, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import Link from "next/link";
import { api, ApiError } from "@/lib/api";
import { parseSession } from "@/lib/auth";
import { actionFor, needsBrokerageCheck, STATUS_UNAVAILABLE, waitForApproval, type GateAction } from "@/lib/brokerageGate";
import { AddChildModal } from "@/components/FamilyModals";
import { clearPending, loadPending, savePending } from "@/lib/redemption";
import { buildLoginUrl, buildSignupUrl, REDEEM_RESUME_PATH } from "@/lib/redirects";

// Mirrors backend normalization: uppercase, O→0, I/L→1, strip separators.
function normalize(raw: string) {
  return raw
    .toUpperCase()
    .replace(/[OIL]/g, (c) => (c === "O" ? "0" : "1"))
    .replace(/[^0-9A-Z]/g, "");
}

function format(digits: string) {
  const body = digits.startsWith("GG") ? digits.slice(2) : digits;
  const groups = body.match(/.{1,4}/g) ?? [];
  return ["GG", ...groups].slice(0, 4).join("-");
}

type Preview = {
  symbol: string;
  amount: number;
  currency: string;
  boxSku: string | null;
  greetingMessage: string | null;
  recipientName: string;
  status: string;
};

type Phase = "code" | "preview" | "recipient" | "done";

export default function Redeem() {
  return (
    <Suspense>
      <RedeemForm />
    </Suspense>
  );
}

function RedeemForm() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const [raw, setRaw] = useState("");
  const [phase, setPhase] = useState<Phase>("code");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [familyChildren, setFamilyChildren] = useState<{ id: string; displayName: string }[] | null>(null);
  const [showAddMember, setShowAddMember] = useState(false);
  const [result, setResult] = useState<{ symbol: string; amount: number; currency: string } | null>(null);

  const code = format(normalize(raw));
  const [signedIn, setSignedIn] = useState(false);
  useEffect(() => setSignedIn(!!parseSession()), []);
  const complete = normalize(raw).length >= 14; // GG + 12 chars

  // Where the gift code comes from, in order:
  //  1. ?code= — a scanned QR (the QR encodes this URL, see Fulfillment's
  //     GenerateQrPng). It's moved into session storage and stripped from the
  //     address bar straight away, so the code doesn't linger in history,
  //     referrers or analytics for the rest of the journey.
  //  2. ?resume=1 — coming back from login / signup / identity check. The
  //     code is restored from session storage (30 min expiry), so nobody has
  //     to re-enter it or click "Redeem a gift" again.
  const [resumeNotice, setResumeNotice] = useState<string | null>(null);
  const isResume = searchParams.get("resume") === "1";
  useEffect(() => {
    const fromUrl = searchParams.get("code");
    if (fromUrl) {
      const formatted = format(normalize(fromUrl));
      setRaw(formatted);
      savePending(formatted);
      router.replace(REDEEM_RESUME_PATH);
      return;
    }
    if (isResume) {
      const pending = loadPending();
      if (pending) setRaw(pending.code);
      else {
        setResumeNotice("Your gift session expired, so please enter your code again — it's still valid.");
        router.replace("/redeem");
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams]);

  // Auto-previews once a full code is present, so returning from login/KYC
  // doesn't dump someone back on a blank form they have to resubmit by
  // hand — but claiming itself still always needs an explicit click (see
  // goToClaim), never auto-submitted, since this is a real financial action.
  useEffect(() => {
    if (complete && !preview && phase === "code") void previewCode();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [complete]);

  // Returning from login / signup / identity check: once the gift has been
  // re-previewed and the user is signed in, continue straight to the
  // family step (set up family / pick or add a child). This only makes the
  // harmless "become parent + load family" calls — the actual claim still
  // needs an explicit click. The ref stops React strict-mode double effects
  // and re-renders from running it twice.
  const autoResumed = useRef(false);
  useEffect(() => {
    if (!isResume || autoResumed.current) return;
    if (phase !== "preview" || preview?.status !== "Issued") return;
    if (!parseSession()) return;
    autoResumed.current = true;
    void goToClaim();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isResume, phase, preview]);

  async function previewCode() {
    setBusy(true);
    setError(null);
    try {
      const res = await api.previewGift({ code });
      setPreview(res);
      setPhase("preview");
      savePending(code); // survives login / signup / refresh
    } catch (e) {
      setError(
        e instanceof ApiError
          ? "That code didn't match. Check the ticket and try again."
          : "Something went wrong on our side. Try again shortly.",
      );
    } finally {
      setBusy(false);
    }
  }

  // Re-reads the family tree after someone is added inline from the picker,
  // so the new person shows up in the list right away without leaving the
  // revealed-code page.
  async function refreshFamily() {
    const family = await api.getFamily();
    setFamilyChildren(family.children.map((c) => ({ id: c.id, displayName: c.displayName })));
  }

  async function goToClaim() {
    const session = parseSession();
    if (!session) {
      savePending(code);
      router.push(buildLoginUrl(REDEEM_RESUME_PATH));
      return;
    }

    setBusy(true);
    setError(null);
    try {
      // Every recipient picks up family capabilities right here if they
      // don't have them yet — building a family tree needs no KYC (see
      // Family's Program.cs "Parent" policy, which deliberately doesn't
      // require kyc_status), so there's no reason to gate discovering it
      // behind an existing "parent" role someone may not have. A no-op if
      // they're already a parent. This also fixes the actual ordering bug:
      // KYC used to be checked before this, sending a brand-new recipient
      // straight to broker onboarding without ever offering to set up
      // their family tree or pick who the gift is for first.
      if (session.role !== "parent") {
        await api.becomeParent();
      }
      const family = await api.getFamily();
      setFamilyChildren(family.children.map((c) => ({ id: c.id, displayName: c.displayName })));
      setPhase("recipient");
    } catch (e) {
      // Previously silent — fell straight through to KYC/broker onboarding
      // with zero indication anything had actually gone wrong, which made
      // a real backend bug look identical to "no family set up yet."
      // Logged here so it's actually diagnosable; still falls back to
      // redeeming for yourself since a family-service hiccup shouldn't
      // block the claim entirely, but at least visibly now.
      console.error("Couldn't set up family/promotion before redeeming:", e);
      setError(
        e instanceof ApiError
          ? `Couldn't check your family (${e.message}) — continuing as if this is just for you.`
          : "Couldn't check your family — continuing as if this is just for you.",
      );
      void proceedToClaim();
    } finally {
      setBusy(false);
    }
  }

  // KYC is checked here — after a recipient has been picked, not before —
  // matching the intended order (build family tree → pick recipient → THEN
  // broker onboarding if needed). The account that actually gets onboarded
  // is always the logged-in caller (the custodian), regardless of who was
  // picked: see RedeemGiftCommand and GiftRedeemedConsumer, which always
  // buy into the caller's own ViewTrade account, never a separate one per
  // child. Returning here from /kyc lands back on the recipient step (code
  // and family both reload automatically) rather than auto-resubmitting —
  // claiming a real financial gift always needs one final explicit click.
  // Before sending anyone into broker onboarding, ask the backend whether they
  // already have a fully onboarded ViewTrade account (matched on their VERIFIED
  // e-mail, server-side). Only "start/continue onboarding" goes to /kyc; an
  // existing approved account goes straight to the claim; anything unclear
  // STOPS with a retry instead of risking a duplicate application. The gift
  // code stays saved throughout (savePending), so leaving and returning is fine.
  const [gate, setGate] = useState<{ action: Extract<GateAction, { kind: "stop" }>; beneficiaryChildId?: string } | null>(null);

  async function proceedToClaim(beneficiaryChildId?: string) {
    const session = parseSession();
    if (!session) {
      savePending(code);
      router.push(buildLoginUrl(REDEEM_RESUME_PATH));
      return;
    }
    if (!needsBrokerageCheck(session)) {
      void claim(beneficiaryChildId);
      return;
    }

    setBusy(true);
    setError(null);
    setGate(null);
    savePending(code); // keep the gift available whatever happens next
    try {
      const { decision } = await api.resolveExistingAccount();
      const action = actionFor(decision);
      if (action.kind === "onboard") {
        router.push(`/kyc?then=${encodeURIComponent(REDEEM_RESUME_PATH)}`);
        return;
      }
      if (action.kind === "stop") {
        setGate({ action, beneficiaryChildId });
        return;
      }
      // Existing, fully onboarded account: onboarding is skipped, but the claim itself still runs
      // exactly as before (it needs the refreshed token to carry the linked account).
      const ready = await waitForApproval(async () => {
        await api.refreshSession();
        const s = parseSession();
        return !!s && !needsBrokerageCheck(s); // approved AND no longer the fake dev account
      });
      if (!ready) {
        setGate({
          action: {
            kind: "stop",
            title: "Almost there",
            message: "We found your brokerage account and are finishing linking it. Your gift is saved — try again in a moment.",
            retryable: true,
          },
          beneficiaryChildId,
        });
        return;
      }
      await claim(beneficiaryChildId);
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) {
        router.push(buildLoginUrl(REDEEM_RESUME_PATH));
        return;
      }
      // Couldn't establish ViewTrade status: NOT "no account". Never start onboarding on a guess.
      setGate({ action: STATUS_UNAVAILABLE, beneficiaryChildId });
    } finally {
      setBusy(false);
    }
  }

  async function claim(beneficiaryChildId?: string) {
    setBusy(true);
    setError(null);
    try {
      const res = await api.redeemGift({ code, beneficiaryChildId });
      setResult(res);
      setPhase("done");
      clearPending(); // redeemed — nothing left to resume
    } catch (e) {
      if (e instanceof ApiError && (e.status === 401 || e.status === 403)) {
        // Session looked valid client-side but the backend's own KYC check
        // disagreed (e.g. a stale token) — route through it properly
        // rather than surfacing a raw permission error.
        router.push(`/kyc?then=${encodeURIComponent(REDEEM_RESUME_PATH)}`);
      } else {
        setError(
          e instanceof ApiError
            ? e.message
            : "Something went wrong on our side. Your code is still valid — try again shortly.",
        );
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mx-auto max-w-xl px-5 py-20">
      {phase === "code" && (
        <div className="space-y-6">
          <div className="space-y-2 text-center">
            <h1 className="font-display text-5xl font-semibold">Unwrap your shares</h1>
            <p className="text-ink/70">Enter the code from your ticket, or scan the QR on the box.</p>
          </div>

          <div className="ticket p-8 space-y-4">
            <label className="block text-center">
              <span className="text-[11px] font-bold uppercase tracking-[0.2em] text-ink/50">
                Redemption code
              </span>
              <input
                autoFocus
                value={code}
                onChange={(e) => {
                  setRaw(e.target.value);
                  setPreview(null);
                  setError(null);
                }}
                spellCheck={false}
                autoComplete="off"
                className="mt-2 w-full text-center font-mono text-2xl font-semibold tracking-[0.15em] bg-transparent border-b-2 border-ink/20 focus:border-leaf py-2 outline-none"
                placeholder="GG-____-____-____"
                aria-label="Redemption code"
              />
            </label>
            <p className="text-xs text-center text-ink/50">
              Mixed up O and 0, or I and 1? Don&apos;t worry — we treat them the same.
            </p>
            {resumeNotice && <p className="text-sm text-center text-ink/70">{resumeNotice}</p>}
            {error && <p className="text-sm text-center text-red-700">{error}</p>}
            <button
              onClick={previewCode}
              disabled={!complete || busy}
              className="w-full rounded-full bg-leaf text-white py-3 font-semibold hover:bg-leaf-deep disabled:opacity-40 transition-colors"
            >
              {busy ? "Checking…" : "See what's inside"}
            </button>
          </div>

          <p className="text-xs text-center text-ink/50">
            Codes are single-use and expire 12 months after issue. Too many wrong attempts will
            temporarily lock the code to protect your gift.
          </p>
        </div>
      )}

      {phase === "preview" && preview && (
        <div className="space-y-6 text-center">
          <span className="text-6xl">🎁</span>
          <h1 className="font-display text-5xl font-semibold">Here&apos;s what&apos;s inside</h1>

          {preview.status !== "Issued" ? (
            <div className="ticket p-8 space-y-3">
              <p className="text-lg font-semibold">This gift has already been claimed.</p>
              <p className="text-sm text-ink/60">
                If that wasn&apos;t you, contact support with the code from your ticket.
              </p>
            </div>
          ) : (
            <>
              <div className="ticket p-8 space-y-3">
                <p className="text-sm font-bold text-ink/50 uppercase tracking-wide">For {preview.recipientName}</p>
                <p className="text-3xl font-semibold">
                  ${preview.amount.toFixed(2)} {preview.currency} of {preview.symbol}
                </p>
                {preview.boxSku && <p className="text-sm text-ink/60">Arrives with a {preview.boxSku} gift box</p>}
                {preview.greetingMessage && (
                  <p className="mt-3 italic text-ink/70 border-t border-ink/10 pt-3">
                    &ldquo;{preview.greetingMessage}&rdquo;
                  </p>
                )}
                <p className="text-xs text-ink/40 pt-2">
                  Exact share count is set when you claim it, based on the live price at that moment.
                </p>
              </div>

              {error && <p className="text-sm text-red-700">{error}</p>}
              <button
                onClick={goToClaim}
                disabled={busy}
                className="w-full rounded-full bg-leaf text-white py-3 font-semibold hover:bg-leaf-deep disabled:opacity-40 transition-colors"
              >
                {busy ? "Claiming…" : "Claim this gift"}
              </button>
              {!signedIn && (
                <p className="text-sm text-ink/70">
                  New to GrowGift?{" "}
                  <Link
                    href={buildSignupUrl({ then: REDEEM_RESUME_PATH, persona: "recipient" })}
                    onClick={() => savePending(code)}
                    className="font-semibold text-leaf-deep underline underline-offset-2"
                  >
                    Create an account
                  </Link>
                </p>
              )}
              <p className="text-xs text-ink/50">
                Because you&apos;re about to own real securities, we&apos;ll ask you to sign in and verify your
                identity first if you haven&apos;t already — takes about two minutes.
              </p>
            </>
          )}
        </div>
      )}

      {phase === "recipient" && (
        <div className="space-y-6 text-center">
          <span className="text-6xl">👪</span>
          <h1 className="font-display text-5xl font-semibold">Who&apos;s this for?</h1>
          <p className="text-ink/70">
            Shares are held in your own account on their behalf until they&apos;re old enough to take over.
          </p>

          <div className="ticket p-4 space-y-2">
            <button
              onClick={() => proceedToClaim()}
              disabled={busy}
              className="w-full rounded-xl border-[1.5px] border-ink/15 px-5 py-4 text-left font-semibold hover:bg-mint transition disabled:opacity-40"
            >
              Myself
            </button>
            {familyChildren?.map((c) => (
              <button
                key={c.id}
                onClick={() => proceedToClaim(c.id)}
                disabled={busy}
                className="w-full rounded-xl border-[1.5px] border-ink/15 px-5 py-4 text-left font-semibold hover:bg-mint transition disabled:opacity-40"
              >
                {c.displayName}
              </button>
            ))}
          </div>

          {/* Always offered, not just when the tree is empty — and inline rather
              than a link to /family, so adding someone never takes you away from
              this page (the link used to also bounce through broker onboarding). */}
          <div className="space-y-2">
            {familyChildren?.length === 0 && (
              <p className="text-sm text-ink/50">
                No one on your family tree yet — add a child if this gift is for them.
              </p>
            )}
            <button
              onClick={() => setShowAddMember(true)}
              disabled={busy}
              className="rounded-full border-[1.5px] border-ink px-5 py-2 text-sm font-semibold hover:bg-mint transition disabled:opacity-40"
            >
              + Add a child to my family tree
            </button>
          </div>

          {showAddMember && (
            <AddChildModal onClose={() => setShowAddMember(false)} onDone={refreshFamily} />
          )}

          {gate && (
            <div role="alert" className="ticket p-5 space-y-3 text-left">
              <p className="font-semibold">{gate.action.title}</p>
              <p className="text-sm text-ink/70">{gate.action.message}</p>
              {gate.action.retryable && (
                <button
                  onClick={() => void proceedToClaim(gate.beneficiaryChildId)}
                  disabled={busy}
                  className="rounded-full bg-ink text-canvas px-6 py-2 text-sm font-semibold disabled:opacity-40"
                >
                  {busy ? "Checking…" : "Try again"}
                </button>
              )}
              {process.env.NODE_ENV !== "production" && (
                <p className="text-xs text-ink/50">
                  Dev only:{" "}
                  <Link href={`/kyc?then=${encodeURIComponent(REDEEM_RESUME_PATH)}`} className="underline">
                    go to verification anyway
                  </Link>
                </p>
              )}
            </div>
          )}
          {error && <p className="text-sm text-red-700">{error}</p>}
        </div>
      )}

      {phase === "done" && result && (
        <div className="space-y-6 text-center">
          <span className="text-6xl">🎉</span>
          <h1 className="font-display text-5xl font-semibold">It&apos;s yours!</h1>
          <p className="text-lg">
            <span className="font-mono font-semibold">
              ${result.amount.toFixed(2)} {result.currency}
            </span>{" "}
            of <span className="font-mono font-semibold">{result.symbol}</span> is being added to your account now
            — this usually takes just a moment.
          </p>
          <Link
            href="/profile"
            className="inline-block rounded-full bg-leaf text-white px-8 py-3 font-semibold hover:bg-leaf-deep transition-colors"
          >
            See my profile
          </Link>
        </div>
      )}
    </div>
  );
}
