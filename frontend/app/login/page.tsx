"use client";

import { Suspense, useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import Link from "next/link";
import { api, ApiError } from "@/lib/api";
import { parseSession } from "@/lib/auth";
import { hasPending } from "@/lib/redemption";
import { postLoginDestination } from "@/lib/billing";
import { buildSignupUrl, resolvePostAuthDestination, safeInternalPath } from "@/lib/redirects";

export default function LoginPage() {
  return (
    <Suspense>
      <Login />
    </Suspense>
  );
}

function Login() {
  const router = useRouter();
  const params = useSearchParams();
  // Validated once, here: an attacker-supplied ?then=https://evil.example
  // (or //evil.example) collapses to "" and is ignored.
  const then = safeInternalPath(params.get("then"), "");

  // Where to go once signed in — see lib/redirects.ts for the precedence
  // policy. A child account always lands in its own area regardless of any
  // ?then=, since nothing else is open to it.
  function destinationAfterLogin() {
    const session = parseSession();
    if (session?.role === "child") return "/kid";
    const destination = resolvePostAuthDestination({ then, hasPendingRedemption: hasPending(), fallback: "/" });
    // A parent or recipient whose subscription isn't in place is taken to subscribe first (card required), then on
    // to where they were headed. Everyone else is unaffected.
    let alreadyPrompted = false;
    try {
      alreadyPrompted = sessionStorage.getItem("gg_sub_prompted") === "1";
    } catch {
      /* storage unavailable: prompting again is harmless */
    }
    const target = postLoginDestination(session?.billingOk, destination, { billingPrompt: session?.billingPrompt, alreadyPrompted });
    if (target !== destination) {
      try {
        sessionStorage.setItem("gg_sub_prompted", "1");
      } catch {
        /* ignore */
      }
    }
    return target;
  }

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Already signed in (e.g. returned here via Back): don't ask again.
  useEffect(() => {
    if (parseSession()) router.replace(destinationAfterLogin());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function submit() {
    setBusy(true);
    setError(null);
    try {
      await api.login({ email, password });
      router.push(destinationAfterLogin());
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 401
          ? "Email or password didn't match."
          : "Something went wrong. Please try again.",
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mx-auto max-w-md px-5 py-20 space-y-8">
      <div className="text-center space-y-2">
        <h1 className="font-display text-5xl font-semibold">Welcome back</h1>
      </div>

      <form
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
        className="ticket p-8 space-y-4"
      >
        <label className="block">
          <span className="text-sm font-bold">Email</span>
          <input
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoComplete="email"
            className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf outline-none"
          />
        </label>
        <label className="block">
          <span className="text-sm font-bold">Password</span>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf outline-none"
          />
        </label>

        {error && <p className="text-sm text-red-700">{error}</p>}

        <button
          type="submit"
          disabled={!email || !password || busy}
          className="w-full rounded-full bg-leaf text-white py-3 font-semibold hover:bg-leaf-deep disabled:opacity-40 transition-colors"
        >
          {busy ? "Signing in…" : "Sign in"}
        </button>
      </form>

      <p className="text-center text-sm text-ink/60">
        New here?{" "}
        <Link href={buildSignupUrl({ then })} className="font-semibold text-leaf-deep hover:underline">
          Create an account
        </Link>
      </p>
    </div>
  );
}
