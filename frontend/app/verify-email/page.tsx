"use client";

import { Suspense, useEffect, useState } from "react";
import { useSearchParams } from "next/navigation";
import Link from "next/link";
import { api, ApiError } from "@/lib/api";

export default function VerifyEmailPage() {
  return (
    <Suspense>
      <VerifyEmail />
    </Suspense>
  );
}

function VerifyEmail() {
  const params = useSearchParams();
  const token = params.get("token");
  const [state, setState] = useState<"working" | "ok" | "failed">("working");
  const [message, setMessage] = useState("Verifying your email…");

  useEffect(() => {
    if (!token) {
      setState("failed");
      setMessage("This verification link is missing its token.");
      return;
    }
    api
      .verifyEmail(token)
      .then((r) => {
        setState("ok");
        setMessage(r.message);
      })
      .catch((e) => {
        setState("failed");
        setMessage(e instanceof ApiError ? e.message : "This link is invalid or already used.");
      });
  }, [token]);

  return (
    <div className="mx-auto max-w-md px-5 py-24 text-center space-y-5">
      <span className="text-6xl">{state === "ok" ? "🎉" : state === "failed" ? "🤔" : "⏳"}</span>
      <h1 className="font-display text-4xl font-semibold">
        {state === "ok" ? "Email verified" : state === "failed" ? "That didn't work" : "One moment"}
      </h1>
      <p className="text-ink/70">{message}</p>
      {state !== "working" && (
        <Link
          href={state === "ok" ? "/login" : "/signup"}
          className="inline-block rounded-full bg-leaf text-white px-8 py-3 font-semibold hover:bg-leaf-deep transition"
        >
          {state === "ok" ? "Sign in" : "Back to sign up"}
        </Link>
      )}
    </div>
  );
}
