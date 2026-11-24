// Single source of truth for "where do we send the user after they sign in /
// sign up / finish an intermediate step". Deliberately dependency-free (no
// React, no Next, no path aliases) so it can be unit-tested with plain Node.
//
// REDIRECT PRECEDENCE after authentication (highest first):
//   1. An explicit, validated internal `then` destination the entry point
//      asked for (e.g. /cart, /redeem?resume=1, or /kyc when the user
//      explicitly started broker onboarding).
//   2. A still-valid pending gift redemption (the user came in through
//      "Redeem a gift", so they go back to it — never to broker onboarding).
//   3. The caller's own role-based default.
// Broker onboarding (/kyc, /onboarding) is therefore only ever entered when
// it is an explicit destination or the redeem flow itself decides KYC is
// needed at the claim step — never as a side effect of signing in.

export const REDEEM_RESUME_PATH = "/redeem?resume=1";

const PLACEHOLDER_ORIGIN = "http://internal.invalid";

/**
 * Returns `raw` only if it is a same-site relative path ("/something?x=1"),
 * otherwise `fallback`. Rejects absolute URLs, protocol-relative ("//host"),
 * backslash tricks ("/\host"), control characters, and the auth pages
 * themselves (which would loop).
 */
export function safeInternalPath(raw: string | null | undefined, fallback = "/"): string {
  if (typeof raw !== "string") return fallback;
  const candidate = raw.trim();
  if (!candidate.startsWith("/")) return fallback;
  if (candidate.startsWith("//") || candidate.includes("\\")) return fallback;
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f]/.test(candidate)) return fallback;

  let parsed: URL;
  try {
    parsed = new URL(candidate, PLACEHOLDER_ORIGIN);
  } catch {
    return fallback;
  }
  if (parsed.origin !== PLACEHOLDER_ORIGIN) return fallback;
  if (/^\/(login|signup)(\/|$)/.test(parsed.pathname)) return fallback;

  return parsed.pathname + parsed.search + parsed.hash;
}

export function isBrokerOnboardingPath(path: string): boolean {
  return /^\/(kyc|onboarding)(\/|\?|$)/.test(path);
}

export function isRedeemPath(path: string): boolean {
  return /^\/redeem(\/|\?|$)/.test(path);
}

export type Persona = "buyer" | "recipient" | "parent";

/** Which signup persona fits the place the user was heading. */
export function personaForDestination(path: string): Persona | undefined {
  if (isRedeemPath(path)) return "recipient";
  if (/^\/(cart|gift|collections)(\/|\?|$)/.test(path)) return "buyer";
  if (/^\/family(\/|\?|$)/.test(path)) return "parent";
  return undefined;
}

export function buildLoginUrl(then?: string | null): string {
  const safe = safeInternalPath(then, "");
  return safe && safe !== "/" ? `/login?then=${encodeURIComponent(safe)}` : "/login";
}

export function buildSignupUrl(opts: { then?: string | null; persona?: Persona } = {}): string {
  const safe = safeInternalPath(opts.then, "");
  const persona = opts.persona ?? (safe ? personaForDestination(safe) : undefined);
  const q: string[] = [];
  if (persona) q.push(`persona=${persona}`);
  if (safe && safe !== "/") q.push(`then=${encodeURIComponent(safe)}`);
  return q.length ? `/signup?${q.join("&")}` : "/signup";
}

/**
 * Decides where to go right after a successful login/signup. Returns
 * `fallback` (may be null so callers can apply their own role default) when
 * neither an explicit destination nor a pending redemption applies.
 */
export function resolvePostAuthDestination<F extends string | null>(opts: {
  then?: string | null;
  hasPendingRedemption: boolean;
  fallback: F;
}): string | F {
  const explicit = safeInternalPath(opts.then, "");
  if (explicit && explicit !== "/") return explicit;
  if (opts.hasPendingRedemption) return REDEEM_RESUME_PATH;
  return opts.fallback;
}
