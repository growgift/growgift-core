"use client";

import { Suspense, useState } from "react";
import { useSearchParams, useRouter } from "next/navigation";
import Link from "next/link";
import { api, ApiError } from "@/lib/api";
import { hasPending } from "@/lib/redemption";
import { buildLoginUrl, isRedeemPath, resolvePostAuthDestination, safeInternalPath } from "@/lib/redirects";
import { AddressFields, emptyAddress, isAddressComplete } from "@/components/AddressFields";

// Buyer and recipient/family are deliberately separate paths, not just two
// cards on the same form: they collect different information (buyers give
// us a shipping address, never touch KYC; recipients and family members
// verify their identity, never touch an address here) and the backend now
// tracks them as genuinely distinct roles.
type Stage = "entry" | "buyer-form" | "verify-choose" | "verify-form";
type VerifyPersona = "recipient" | "parent";

export default function SignupPage() {
  return (
    <Suspense>
      <Signup />
    </Suspense>
  );
}

function Signup() {
  const router = useRouter();
  const params = useSearchParams();
  const then = safeInternalPath(params.get("then"), "/");

  const urlPersona = params.get("persona");
  const [stage, setStage] = useState<Stage>(
    urlPersona === "buyer" ? "buyer-form" : urlPersona === "recipient" || urlPersona === "parent" ? "verify-form" : "entry",
  );
  const [verifyPersona, setVerifyPersona] = useState<VerifyPersona>(urlPersona === "parent" ? "parent" : "recipient");

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [address, setAddress] = useState(emptyAddress());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<{ devLink: string | null; role: "buyer" | VerifyPersona; emailSent: boolean } | null>(null);

  const passwordOk = password.length >= 12;
  const match = password === confirm;
  const isBuyer = stage === "buyer-form";
  const addressOk = !isBuyer || isAddressComplete(address);
  const canSubmit = email.includes("@") && passwordOk && match && addressOk && !busy;

  async function submit(role: "buyer" | VerifyPersona) {
    setBusy(true);
    setError(null);
    try {
      const created = await api.register({
        email,
        password,
        role,
        shippingAddress: role === "buyer" ? address : undefined,
      });
      // Deliberately NOT signing in automatically: the person confirms the
      // account by signing in themselves on the next screen, then continues
      // to wherever they were headed (e.g. their cart). Clear any token left
      // over from an earlier session so they really do start signed out.
      api.clearSession();
      setDone({ devLink: created.devVerificationUrl, role, emailSent: created.emailSent ?? true });
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 409
          ? "An account with this email already exists — try signing in instead."
          : e instanceof ApiError
            ? e.message
            : "Something went wrong. Please try again.",
      );
    } finally {
      setBusy(false);
    }
  }

  // ---------- Success screen ----------
  if (done) {
    // Precedence (see lib/redirects.ts): an explicit destination (e.g. the
    // gift being redeemed, the cart) wins, then a still-valid pending
    // redemption, and only then the role default — which for a recipient or
    // parent signing up directly is the identity check. Broker onboarding is
    // never inserted ahead of a gift the user came here to redeem.
    const roleDefault =
      done.role === "buyer"
        ? "/collections"
        : `/kyc?then=${encodeURIComponent(done.role === "parent" ? "/family" : "/redeem")}`;
    const continueHref = resolvePostAuthDestination({
      then,
      hasPendingRedemption: hasPending(),
      fallback: roleDefault,
    });

    return (
      <div className="mx-auto max-w-xl px-5 py-20 text-center space-y-6">
        <span className="text-6xl">🎉</span>
        <h1 className="font-display text-5xl font-semibold">Account created!</h1>
        <p className="text-ink/70 max-w-md mx-auto">
          {done.emailSent ? (
            <>
              We&apos;ve sent a verification link to <span className="font-semibold text-ink">{email}</span>.
              Click it to confirm it&apos;s really you.
            </>
          ) : (
            <>We couldn&apos;t send a verification email to {email} just now — you can still sign in.</>
          )}{" "}
          Now sign in with your new account to continue
          {then.startsWith("/cart") ? " to your cart" : isRedeemPath(then) ? " and get back to your gift" : ""}.
          {done.role !== "buyer" && " You'll then add a card to finish signing up — nothing is charged for the first 3 months."}
        </p>
        {done.devLink && (
          <p className="rounded-xl bg-ribbon/15 border-[1.5px] border-dashed border-ribbon px-4 py-3 text-xs">
            🛠 Dev mode — the verification email couldn&apos;t be sent (is Notifications running with a valid SENDGRID_API_KEY?). Use this link instead:{" "}
            <Link href={done.devLink} className="font-mono font-semibold underline break-all">
              verify now
            </Link>
          </p>
        )}
        <button
          onClick={() => router.push(buildLoginUrl(continueHref))}
          className="rounded-full bg-leaf text-white px-8 py-3 font-semibold hover:bg-leaf-deep transition"
        >
          Sign in to continue
        </button>
      </div>
    );
  }

  // ---------- Entry: buyer vs. recipient/family ----------
  if (stage === "entry") {
    return (
      <div className="mx-auto max-w-2xl px-5 py-20 space-y-8">
        <div className="text-center space-y-2">
          <h1 className="font-display text-5xl font-semibold">Create your account</h1>
          <p className="text-ink/70">First, what brings you here?</p>
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <button
            onClick={() => setStage("buyer-form")}
            className="rounded-2xl border-[1.5px] border-ink/20 bg-white p-6 text-left hover:border-leaf hover:bg-mint/30 transition"
          >
            <span className="text-4xl">🎀</span>
            <p className="font-display text-2xl mt-3">I&apos;m sending a gift</p>
            <p className="text-sm text-ink/60 mt-2">
              Just your name and address so we know where to ship the box. No identity
              verification needed — you&apos;re not opening a brokerage account.
            </p>
          </button>
          <button
            onClick={() => setStage("verify-choose")}
            className="rounded-2xl border-[1.5px] border-ink/20 bg-white p-6 text-left hover:border-leaf hover:bg-mint/30 transition"
          >
            <span className="text-4xl">🎁</span>
            <p className="font-display text-2xl mt-3">I&apos;m receiving a gift, or investing for family</p>
            <p className="text-sm text-ink/60 mt-2">
              You&apos;ll own real shares, so we&apos;ll ask you to verify your identity — quick,
              and only once.
            </p>
          </button>
        </div>
        <p className="text-center text-sm text-ink/60">
          Already have an account?{" "}
          <Link href={buildLoginUrl(then)} className="font-semibold text-leaf-deep hover:underline">
            Sign in
          </Link>
        </p>
      </div>
    );
  }

  // ---------- Sub-choice: recipient vs. parent (both verify, differ in destination) ----------
  if (stage === "verify-choose") {
    return (
      <div className="mx-auto max-w-2xl px-5 py-20 space-y-8">
        <button
          onClick={() => setStage("entry")}
          className="text-sm font-semibold text-ink/50 hover:text-ink"
        >
          ← Back
        </button>
        <div className="text-center space-y-2">
          <h1 className="font-display text-5xl font-semibold">Which one&apos;s you?</h1>
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <button
            onClick={() => {
              setVerifyPersona("recipient");
              setStage("verify-form");
            }}
            className="rounded-2xl border-[1.5px] border-ink/20 bg-white p-6 text-left hover:border-leaf hover:bg-mint/30 transition"
          >
            <span className="text-4xl">🎁</span>
            <p className="font-display text-2xl mt-3">I received a gift</p>
            <p className="text-sm text-ink/60 mt-2">
              Redeem a code, verify your ID once, and own your shares for real.
            </p>
          </button>
          <button
            onClick={() => {
              setVerifyPersona("parent");
              setStage("verify-form");
            }}
            className="rounded-2xl border-[1.5px] border-ink/20 bg-white p-6 text-left hover:border-leaf hover:bg-mint/30 transition"
          >
            <span className="text-4xl">🌳</span>
            <p className="font-display text-2xl mt-3">Family investing</p>
            <p className="text-sm text-ink/60 mt-2">
              Create child accounts, three savings jars, chores &amp; rewards — transfers at 18.
            </p>
          </button>
        </div>
      </div>
    );
  }

  // ---------- Forms (buyer-form or verify-form) ----------
  const heading = isBuyer
    ? "Where should we ship the box?"
    : verifyPersona === "parent"
      ? "Set up your family account"
      : "Verify it's you";

  return (
    <div className="mx-auto max-w-xl px-5 py-16 space-y-8">
      <button
        onClick={() => setStage(isBuyer ? "entry" : "verify-choose")}
        className="text-sm font-semibold text-ink/50 hover:text-ink"
      >
        ← Back
      </button>

      <div className="text-center space-y-2">
        <h1 className="font-display text-5xl font-semibold">{heading}</h1>
        {!isBuyer && (
          <p className="text-ink/70">
            {isRedeemPath(then)
              ? "Two minutes now — then you'll go straight back to your gift."
              : "Two minutes now — we'll ask you to verify your identity right after this, since you'll be holding real shares."}
          </p>
        )}
      </div>

      {isBuyer && (
        <div className="ticket p-6 space-y-4">
          <AddressFields value={address} onChange={setAddress} />
        </div>
      )}

      <div className="ticket p-8 space-y-4">
        <label className="block">
          <span className="text-sm font-bold">Email</span>
          <input
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoComplete="email"
            className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf outline-none"
            placeholder="you@example.com"
          />
        </label>
        <label className="block">
          <span className="text-sm font-bold">Password</span>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="new-password"
            className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf outline-none"
            placeholder="At least 12 characters"
          />
          {password && !passwordOk && (
            <p className="text-xs text-red-700 mt-1">Needs at least 12 characters.</p>
          )}
        </label>
        <label className="block">
          <span className="text-sm font-bold">Confirm password</span>
          <input
            type="password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            autoComplete="new-password"
            className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf outline-none"
          />
          {confirm && !match && <p className="text-xs text-red-700 mt-1">Passwords don&apos;t match.</p>}
        </label>

        {error && <p className="text-sm text-red-700">{error}</p>}
        {isBuyer && !addressOk && (email || password) && (
          <p className="text-xs text-ink/50">Fill in your shipping address above to continue.</p>
        )}

        <button
          onClick={() => submit(isBuyer ? "buyer" : verifyPersona)}
          disabled={!canSubmit}
          className="w-full rounded-full bg-leaf text-white py-3 font-semibold hover:bg-leaf-deep disabled:opacity-40 transition-colors"
        >
          {busy
            ? "Creating account…"
            : isBuyer
              ? "Create account & choose a gift"
              : verifyPersona === "parent"
                ? "Create account & verify"
                : "Create account & continue"}
        </button>

        {!isBuyer && (
          <p className="text-xs text-ink/50 text-center">
            {isRedeemPath(then)
              ? "You'll set up your family and choose who the gift is for first. A one-time identity check (about 2 minutes with photo ID) only comes at the very end, when you claim — it's required by law before you can own securities."
              : "Next step after this: a one-time identity check (about 2 minutes with photo ID) — required by law before you can own securities."}
          </p>
        )}
      </div>

      <p className="text-center text-sm text-ink/60">
        Already have an account?{" "}
        <Link href={buildLoginUrl(then)} className="font-semibold text-leaf-deep hover:underline">
          Sign in
        </Link>
      </p>
    </div>
  );
}
