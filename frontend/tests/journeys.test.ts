// Scenario-level checks that chain the same functions the pages call, in the
// order the pages call them. (Page rendering itself is exercised in the
// browser run — see scripts/e2e-redeem.mjs.)
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  buildLoginUrl, buildSignupUrl, resolvePostAuthDestination, isBrokerOnboardingPath, REDEEM_RESUME_PATH, safeInternalPath,
} from "../lib/redirects.ts";
import { savePending, hasPending, loadPending, clearPending, type KV } from "../lib/redemption.ts";

const mem = (): KV => { const m = new Map<string, string>(); return { getItem: (k) => m.get(k) ?? null, setItem: (k, v) => void m.set(k, v), removeItem: (k) => void m.delete(k) }; };
const CODE = "GG-84KQ-29MN-XP8D";

// what the signup page computes for continueHref
const afterSignup = (then: string, role: "buyer" | "recipient" | "parent", store: KV) =>
  resolvePostAuthDestination({
    then, hasPendingRedemption: hasPending(store),
    fallback: role === "buyer" ? "/collections" : `/kyc?then=${encodeURIComponent(role === "parent" ? "/family" : "/redeem")}`,
  });

test("Scenario 1 (new user): reveal → Create account → sign in → back to redemption, NOT broker onboarding", () => {
  const store = mem();
  savePending(CODE, store);                                         // gift revealed
  const signup = buildSignupUrl({ then: REDEEM_RESUME_PATH, persona: "recipient" });
  const then = new URL(signup, "http://x").searchParams.get("then")!;
  const dest = afterSignup(safeInternalPath(then, "/"), "recipient", store);
  assert.equal(dest, REDEEM_RESUME_PATH);
  assert.equal(isBrokerOnboardingPath(dest), false);
  const login = buildLoginUrl(dest);                                // "Sign in to continue"
  const loginThen = new URL(login, "http://x").searchParams.get("then")!;
  assert.equal(resolvePostAuthDestination({ then: loginThen, hasPendingRedemption: hasPending(store), fallback: "/" }), REDEEM_RESUME_PATH);
  assert.equal(loadPending(store)?.code, CODE);                     // no need to re-enter the code
});

test("Scenario 1b: the original bug — signing up from the login page without `then` still resumes the gift", () => {
  const store = mem();
  savePending(CODE, store);
  // user lands on /signup with no `then` (old login-page link) but a redemption is pending
  assert.equal(afterSignup("/", "recipient", store), REDEEM_RESUME_PATH);
});

test("Scenario 2/3 (existing user): login returns to the redemption", () => {
  const store = mem();
  savePending(CODE, store);
  assert.equal(resolvePostAuthDestination({ then: REDEEM_RESUME_PATH, hasPendingRedemption: hasPending(store), fallback: "/" }), REDEEM_RESUME_PATH);
});

test("Scenario 4: explicitly starting broker onboarding still goes to broker onboarding", () => {
  const store = mem();
  const dest = resolvePostAuthDestination({ then: "/kyc?then=%2Ffamily", hasPendingRedemption: hasPending(store), fallback: "/" });
  assert.equal(isBrokerOnboardingPath(dest), true);
  // direct recipient signup (no gift in progress) keeps its identity-check default
  assert.equal(afterSignup("/", "recipient", store), "/kyc?then=%2Fredeem");
  // direct plain login still goes home
  assert.equal(resolvePostAuthDestination({ then: null, hasPendingRedemption: false, fallback: "/" }), "/");
});

test("Scenario 6: refresh keeps context; expiry / sign-out / completion clear it", () => {
  const store = mem();
  savePending(CODE, store, 0);
  assert.ok(loadPending(store, 1000));
  assert.equal(loadPending(store, 31 * 60 * 1000), null);           // expired → clear recovery path (page shows a notice)
  savePending(CODE, store);
  clearPending(store);                                              // api.logout() / redeemed
  assert.equal(hasPending(store), false);
});

test("a stale pending redemption does not hijack an explicit destination (e.g. the cart)", () => {
  const store = mem();
  savePending(CODE, store);
  assert.equal(afterSignup("/cart", "buyer", store), "/cart");
});

test("hostile return URLs never leave the site", () => {
  const store = mem();
  for (const evil of ["https://evil.example/phish", "//evil.example", "/\\evil.example", "javascript:alert(1)"]) {
    const dest = afterSignup(safeInternalPath(evil, "/"), "buyer", store);
    assert.ok(dest.startsWith("/") && !dest.startsWith("//"), evil);
  }
});
