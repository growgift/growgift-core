import { test } from "node:test";
import assert from "node:assert/strict";
import {
  safeInternalPath, buildLoginUrl, buildSignupUrl, resolvePostAuthDestination,
  isBrokerOnboardingPath, personaForDestination, REDEEM_RESUME_PATH,
} from "../lib/redirects.ts";

test("safeInternalPath accepts same-site relative paths (with query/hash)", () => {
  assert.equal(safeInternalPath("/cart"), "/cart");
  assert.equal(safeInternalPath("/redeem?resume=1"), "/redeem?resume=1");
  assert.equal(safeInternalPath("/kyc?then=%2Fredeem#x"), "/kyc?then=%2Fredeem#x");
});

test("safeInternalPath rejects external / protocol-relative / tricky destinations", () => {
  for (const bad of [
    "https://evil.example", "http://evil.example/x", "//evil.example", "///evil.example",
    "/\\evil.example", "\\\\evil.example", "javascript:alert(1)", "data:text/html,x",
    "evil.example", "", "   ", "/ok\nSet-Cookie: x=1", "/ok\u0000",
  ]) {
    assert.equal(safeInternalPath(bad, "FALLBACK"), "FALLBACK", JSON.stringify(bad));
  }
  assert.equal(safeInternalPath(null, "F"), "F");
  assert.equal(safeInternalPath(undefined, "F"), "F");
});

test("safeInternalPath refuses to bounce back into the auth pages (redirect loops)", () => {
  assert.equal(safeInternalPath("/login?then=/cart", "F"), "F");
  assert.equal(safeInternalPath("/signup", "F"), "F");
});

test("resolvePostAuthDestination: explicit destination beats pending redemption", () => {
  assert.equal(resolvePostAuthDestination({ then: "/cart", hasPendingRedemption: true, fallback: "/" }), "/cart");
  // explicit broker onboarding is honoured when the user asked for it
  assert.equal(resolvePostAuthDestination({ then: "/kyc?then=%2Ffamily", hasPendingRedemption: true, fallback: "/" }), "/kyc?then=%2Ffamily");
});

test("resolvePostAuthDestination: pending redemption beats the default (never broker onboarding)", () => {
  const dest = resolvePostAuthDestination({ then: undefined, hasPendingRedemption: true, fallback: "/kyc?then=%2Fredeem" });
  assert.equal(dest, REDEEM_RESUME_PATH);
  assert.equal(isBrokerOnboardingPath(dest), false);
  assert.equal(resolvePostAuthDestination({ then: "/", hasPendingRedemption: true, fallback: "/" }), REDEEM_RESUME_PATH);
});

test("resolvePostAuthDestination: falls back to the caller's default, ignores hostile `then`", () => {
  assert.equal(resolvePostAuthDestination({ then: null, hasPendingRedemption: false, fallback: "/collections" }), "/collections");
  assert.equal(resolvePostAuthDestination({ then: "https://evil.example", hasPendingRedemption: false, fallback: "/" }), "/");
  assert.equal(resolvePostAuthDestination({ then: "//evil.example", hasPendingRedemption: true, fallback: "/" }), REDEEM_RESUME_PATH);
  assert.equal(resolvePostAuthDestination({ then: undefined, hasPendingRedemption: false, fallback: null }), null);
});

test("login → signup link keeps the redemption context (the original bug)", () => {
  assert.equal(buildSignupUrl({ then: REDEEM_RESUME_PATH }), "/signup?persona=recipient&then=%2Fredeem%3Fresume%3D1");
  assert.equal(buildSignupUrl({ then: "/cart" }), "/signup?persona=buyer&then=%2Fcart");
  assert.equal(buildSignupUrl({}), "/signup");
  assert.equal(buildSignupUrl({ then: "https://evil.example" }), "/signup");
  assert.equal(buildLoginUrl(REDEEM_RESUME_PATH), "/login?then=%2Fredeem%3Fresume%3D1");
  assert.equal(buildLoginUrl("//evil.example"), "/login");
  assert.equal(buildLoginUrl(), "/login");
});

test("personaForDestination", () => {
  assert.equal(personaForDestination("/redeem?resume=1"), "recipient");
  assert.equal(personaForDestination("/cart"), "buyer");
  assert.equal(personaForDestination("/family"), "parent");
  assert.equal(personaForDestination("/profile"), undefined);
});

test("broker onboarding paths are recognised (and the redeem path is not one)", () => {
  assert.equal(isBrokerOnboardingPath("/kyc"), true);
  assert.equal(isBrokerOnboardingPath("/onboarding?then=/redeem"), true);
  assert.equal(isBrokerOnboardingPath("/redeem?resume=1"), false);
});
