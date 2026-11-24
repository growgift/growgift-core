"use client";

import { useEffect, useState } from "react";
import { useRouter, usePathname } from "next/navigation";
import { getToken } from "@/lib/api";

export type Session = {
  userId: string;
  role: string;
  kycApproved: boolean;
  viewTradeAccount: string | null;
  /** False only when the server explicitly says a subscription needs attention; absent claim = fine. */
  billingOk: boolean;
  /** True when the server would like this person to add a card (they may skip while their free period runs). */
  billingPrompt: boolean;
};

export function parseSession(): Session | null {
  const token = getToken();
  if (!token) return null;
  try {
    const payload = JSON.parse(atob(token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/")));
    if (payload.exp && payload.exp * 1000 < Date.now()) return null;
    return {
      userId: payload.sub ?? payload.nameid ?? "",
      role: (payload.role ?? payload["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] ?? "").toString().toLowerCase(),
      kycApproved: payload.kyc_status === "approved",
      viewTradeAccount: payload.viewtrade_account ?? null,
      billingOk: String(payload.billing_ok ?? "true").toLowerCase() !== "false",
      billingPrompt: String(payload.billing_prompt ?? "false").toLowerCase() === "true",
    };
  } catch {
    return null;
  }
}

/**
 * Redirects to /login (preserving the return path) when not signed in;
 * optionally also requires KYC to already be approved (redirecting to /kyc
 * rather than surfacing a raw 403 from the backend policy). Returns the
 * session once available.
 *
 * Deliberately doesn't gate on a specific role. Roles here are additive
 * capabilities a signed-in account can pick up over time (see Identity's
 * /me/become-parent and PromoteToParent's own doc), not a single exclusive
 * label fixed at signup — an account that registered as a buyer or
 * recipient can still go on to build a family tree. A hook that redirected
 * an already-authenticated session to /signup on a role mismatch (the
 * previous behavior here) is confusing regardless of context: it reads as
 * "you don't have an account" to someone who very much does. Where a page
 * needs a specific capability and the session doesn't have it yet, the
 * right move is to offer to grant it in place (see FamilyDashboard's own
 * "become a parent" prompt) rather than bounce them to a generic signup page.
 */
export function useRequireAuth(
  _requiredRole?: string,
  opts?: { requireKyc?: boolean },
): Session | null {
  const router = useRouter();
  const pathname = usePathname();
  const [session, setSession] = useState<Session | null>(null);
  const [checked, setChecked] = useState(false);

  useEffect(() => {
    const s = parseSession();
    if (!s) {
      router.replace(`/login?then=${encodeURIComponent(pathname)}`);
      return;
    }
    if (opts?.requireKyc && !s.kycApproved) {
      router.replace(`/kyc?then=${encodeURIComponent(pathname)}`);
      return;
    }
    setSession(s);
    setChecked(true);
  }, [router, pathname, opts?.requireKyc]);

  return checked ? session : null;
}
