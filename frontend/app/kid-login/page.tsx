"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { api, ApiError } from "@/lib/api";
import { parseSession } from "@/lib/auth";

// A child's account is a normal GrowGift login under the hood — the login
// code is stored as their "email" and the PIN as their "password" (see
// Identity's User.RegisterChildLogin) — so this posts to the exact same
// /auth/login endpoint as the adult login page, just with kid-friendly
// labels and a shorter, numeric-only PIN field instead of a real password.
export default function KidLoginPage() {
  const router = useRouter();
  const [loginCode, setLoginCode] = useState("");
  const [pin, setPin] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Already signed in as a child? Go straight to the portfolio instead of
  // asking for the code and PIN again.
  useEffect(() => {
    if (parseSession()?.role === "child") router.replace("/kid");
  }, [router]);

  async function submit() {
    setBusy(true);
    setError(null);
    try {
      await api.login({ email: loginCode, password: pin });
      if (parseSession()?.role !== "child") {
        // Someone typed a grown-up's credentials into the kids' form: don't
        // leave an adult session half-open on a page built for children.
        api.logout();
        setError("That isn't a child login. Grown-ups sign in from the main Log in page.");
        return;
      }
      router.push("/kid");
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 401
          ? "That code or PIN didn't match. Ask a parent to check it."
          : "Something went wrong. Please try again.",
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mx-auto max-w-md px-5 py-20 space-y-8">
      <div className="text-center space-y-2">
        <span className="text-6xl">🌱</span>
        <h1 className="font-display text-5xl font-semibold">Hey there!</h1>
        <p className="text-ink/70">Enter your login code and PIN to see your jars.</p>
      </div>

      <form
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
        className="ticket p-8 space-y-4"
      >
        <label className="block text-center">
          <span className="text-[11px] font-bold uppercase tracking-[0.2em] text-ink/50">Login code</span>
          <input
            autoFocus
            inputMode="numeric"
            value={loginCode}
            onChange={(e) => setLoginCode(e.target.value.replace(/\D/g, "").slice(0, 6))}
            spellCheck={false}
            autoComplete="off"
            className="mt-2 w-full text-center font-mono text-2xl font-semibold tracking-[0.2em] bg-transparent border-b-2 border-ink/20 focus:border-leaf py-2 outline-none"
            placeholder="000000"
          />
        </label>
        <label className="block text-center">
          <span className="text-[11px] font-bold uppercase tracking-[0.2em] text-ink/50">PIN</span>
          <input
            type="password"
            inputMode="numeric"
            value={pin}
            onChange={(e) => setPin(e.target.value.replace(/\D/g, "").slice(0, 6))}
            autoComplete="off"
            className="mt-2 w-full text-center font-mono text-2xl font-semibold tracking-[0.3em] bg-transparent border-b-2 border-ink/20 focus:border-leaf py-2 outline-none"
            placeholder="••••"
          />
        </label>

        {error && <p className="text-sm text-center text-red-700">{error}</p>}

        <button
          type="submit"
          disabled={!loginCode || !pin || busy}
          className="w-full rounded-full bg-leaf text-white py-3 font-semibold hover:bg-leaf-deep disabled:opacity-40 transition-colors"
        >
          {busy ? "Checking…" : "Let's go"}
        </button>
      </form>

      <p className="text-center text-sm text-ink/60">
        Grown-up?{" "}
        <Link href="/login" className="font-semibold text-leaf-deep hover:underline">
          Sign in here instead
        </Link>
      </p>
    </div>
  );
}
