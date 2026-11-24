import { test } from "node:test";
import assert from "node:assert/strict";
import { navFor, isRouteAllowedForRole, CHILD_MENU_LABEL } from "../lib/navigation.ts";

test("child menu is exactly 'My Portfolio' → /kid, with no parent/buyer entries", () => {
  assert.equal(CHILD_MENU_LABEL, "My Portfolio");
  const nav = navFor("child");
  assert.deepEqual(nav.links, [{ href: "/kid", label: "My Portfolio" }]);
  assert.equal(nav.showCart, false);
  assert.equal(nav.showGiveCta, false);
  assert.equal(nav.signedIn, true);
  const labels = nav.links.map((l) => l.label.toLowerCase()).join("|");
  for (const forbidden of ["family", "redeem", "collections", "profile", "give"]) assert.ok(!labels.includes(forbidden));
});

test("signed-out visitors see a Child login link routed to /kid-login (plus Log in / Sign up)", () => {
  const nav = navFor(null);
  const child = nav.links.find((l) => l.label === "Child login");
  assert.deepEqual(child, { href: "/kid-login", label: "Child login" });
  assert.ok(nav.links.some((l) => l.href === "/login"));
  assert.ok(nav.links.some((l) => l.href === "/signup"));
  assert.equal(nav.signedIn, false);
});

test("signed-in adults keep My family / My profile and do not see Log in or Child login", () => {
  for (const role of ["parent", "buyer", "recipient", "user"]) {
    const nav = navFor(role);
    const hrefs = nav.links.map((l) => l.href);
    assert.ok(hrefs.includes("/family") && hrefs.includes("/profile"), role);
    assert.ok(!hrefs.includes("/login") && !hrefs.includes("/kid-login"), role);
    assert.equal(nav.showCart, true);
  }
});

test("a child session may only open /kid and /kid-login", () => {
  for (const ok of ["/kid", "/kid/", "/kid-login"]) assert.equal(isRouteAllowedForRole("child", ok), true, ok);
  for (const no of ["/family", "/redeem", "/gift/new", "/cart", "/profile", "/collections", "/kyc", "/onboarding", "/kidnap", "/"])
    assert.equal(isRouteAllowedForRole("child", no), false, no);
  for (const role of ["parent", "buyer", null]) assert.equal(isRouteAllowedForRole(role, "/family"), true);
});
