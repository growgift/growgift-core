// Browser-level journey test with a MOCKED gateway (no backend needed).
// Usage:   npm i --no-save playwright-core   (Chromium must be installed)
//          npm run dev -- -p 3100
//          BASE=http://localhost:3100 CHROMIUM=/path/to/chromium node scripts/e2e-redeem.mjs
import { chromium } from "playwright-core";
import assert from "node:assert/strict";

const BASE = process.env.BASE ?? "http://localhost:3100";
const CODE = "GG-84KQ-29MN-XP8D";
const b64 = (o) => Buffer.from(JSON.stringify(o)).toString("base64url");
const jwt = (claims) => `${b64({ alg: "none" })}.${b64({ exp: Math.floor(Date.now() / 1000) + 3600, sub: "u1", ...claims })}.x`;

function makeMock({ kyc = "approved", resolve = [{ status: 200, decision: "start_onboarding" }] } = {}) {
  const calls = [];
  const state = { children: [], accounts: new Map(), resolveQueue: [...resolve], linked: false };
  const handler = async (route) => {
    const req = route.request();
    const url = new URL(req.url());
    const path = url.pathname.replace(/^\/api\/v1/, "");
    const method = req.method();
    const body = req.postData() ? JSON.parse(req.postData()) : null;
    calls.push(`${method} ${path}`);
    const json = (status, data) => route.fulfill({ status, contentType: "application/json", body: JSON.stringify(data) });
    if (path === "/gifts/preview") return json(200, { symbol: "NTDO", amount: 50, currency: "AUD", boxSku: null, greetingMessage: "Hi", recipientName: "Maya", status: "Issued" });
    if (path === "/auth/register") { state.accounts.set(body.email, body); return json(201, { userId: "u1", message: "ok", emailSent: true, devVerificationUrl: null }); }
    if (path === "/auth/login") {
      if (/^\d{6}$/.test(body.email)) return json(200, { accessToken: jwt({ role: "child" }), expiresInSeconds: 900 });
      if (!state.accounts.has(body.email) && body.email !== "existing@example.com") return route.fulfill({ status: 401, body: "" });
      return json(200, { accessToken: jwt({ role: "recipient", kyc_status: kyc }), expiresInSeconds: 900 });
    }
    if (path === "/auth/me/become-parent") return json(200, { accessToken: jwt({ role: "parent", kyc_status: kyc }) });
    if (path === "/family") return json(200, { familyId: "f1", partners: [], children: state.children });
    if (path === "/children" && method === "POST") {
      const dupe = state.children.find((c) => c.displayName === body.displayName && c.dateOfBirth === body.dateOfBirth);
      if (dupe) return json(200, { childId: dupe.id, loginCode: dupe.loginCode, alreadyExisted: true });
      const c = { id: "c" + (state.children.length + 1), displayName: body.displayName, dateOfBirth: body.dateOfBirth, loginCode: "123456", ownershipTransferred: false };
      state.children.push(c);
      return json(201, { childId: c.id, loginCode: c.loginCode });
    }
    if (path === "/gifts/redeem") return json(200, { giftId: "g1", symbol: "NTDO", amount: 50, currency: "AUD" });
    if (path === "/me/child-view") return json(200, { name: "Maya", jars: { shortTerm: 1, mediumTerm: 1, longTerm: 1, currency: "AUD" }, pendingTasks: [], giftedShares: [] });
    if (path === "/onboarding/resolve-existing") {
      const r = state.resolveQueue.length > 1 ? state.resolveQueue.shift() : state.resolveQueue[0];
      if (r.status !== 200) return json(r.status, { error: { code: "onboarding.status_unavailable", message: "x" } });
      if (r.decision === "skip_onboarding") state.linked = true;
      return json(200, { decision: r.decision, accountRef: r.decision === "skip_onboarding" ? "ORBS-01-004099" : null });
    }
    // Identity re-issues the token from server state: approved only once an existing account was linked.
    if (path === "/auth/me/refresh") return json(200, { accessToken: jwt({ role: "parent", kyc_status: state.linked ? "approved" : "none" }) });
    if (path.startsWith("/onboarding")) return json(200, { status: "InProgress", answers: {}, multiAnswers: {}, documentsProvided: [], rejectionReason: null, accountRef: null });
    return json(404, { error: { code: "mock.unhandled", message: path } });
  };
  return { calls, state, handler };
}

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM ?? "/opt/pw-browsers/chromium", args: ["--no-sandbox"] });
let passed = 0;
async function scenario(name, fn, { mobile = false, kyc = "approved", resolve } = {}) {
  const ctx = await browser.newContext({ viewport: mobile ? { width: 390, height: 800 } : { width: 1280, height: 900 } });
  const page = await ctx.newPage();
  const mock = makeMock({ kyc, resolve });
  await page.route("**/api/v1/**", mock.handler);
  const visited = [];
  page.on("framenavigated", (f) => f === page.mainFrame() && visited.push(new URL(f.url()).pathname + new URL(f.url()).search));
  try {
    await fn({ page, mock, visited, ctx });
    console.log("PASS", name);
    passed++;
  } catch (e) {
    console.log("FAIL", name, "\n  ", e.message.split("\n")[0], "\n   visited:", visited.join(" → "));
    process.exitCode = 1;
  } finally {
    await ctx.close();
  }
}
const revealGift = async (page) => {
  await page.goto(`${BASE}/redeem`);
  await page.getByLabel("Redemption code").fill(CODE);
  await page.getByText("Here's what's inside").waitFor();
};

await scenario("1. New user: reveal → Claim → login page → Create account → sign in → family step (no broker app, no re-entering code)", async ({ page, mock, visited }) => {
  await revealGift(page);
  await page.getByRole("button", { name: "Claim this gift" }).click();
  await page.waitForURL(/\/login\?then=%2Fredeem%3Fresume%3D1/);
  await page.getByRole("link", { name: "Create an account" }).click();      // the link that used to drop the return path
  await page.waitForURL(/\/signup\?persona=recipient&then=%2Fredeem%3Fresume%3D1/);
  await page.getByPlaceholder("you@example.com").fill("new@example.com");
  await page.getByPlaceholder("At least 12 characters").fill("a-long-password-1");
  await page.locator('input[autocomplete="new-password"]').nth(1).fill("a-long-password-1");
  await page.getByRole("button", { name: /Create account/ }).click();
  await page.getByRole("button", { name: "Sign in to continue" }).click();
  await page.waitForURL(/\/login\?then=%2Fredeem%3Fresume%3D1/);
  await page.locator('input[type="email"]').fill("new@example.com");
  await page.locator('input[type="password"]').fill("a-long-password-1");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.getByText("Who's this for?").waitFor();                          // family step reached automatically
  assert.ok(!visited.some((v) => /\/(kyc|onboarding)/.test(v)), "must not visit broker onboarding: " + visited.join(","));
  assert.equal(new URL(page.url()).searchParams.get("code"), null, "gift code must not be in the URL");
  assert.ok(mock.calls.includes("POST /auth/me/become-parent"));
  // create the child inline, then pick them
  await page.getByRole("button", { name: "+ Add a child to my family tree" }).click();
  await page.getByPlaceholder("Maya").fill("Maya");
  await page.locator('input[type="date"]').fill("2018-05-01");
  await page.getByPlaceholder("1234").fill("1234");
  await page.getByRole("button", { name: "Open their three jars" }).click();
  await page.getByRole("button", { name: "Got it" }).click();
  await page.getByRole("button", { name: "Maya" }).click();
  await page.getByText("It's yours!").waitFor();
  assert.equal(mock.calls.filter((c) => c === "POST /children").length, 1);
  assert.equal(mock.calls.filter((c) => c === "POST /gifts/redeem").length, 1);
});

await scenario("2. Existing user logs in → straight back to the gift's family step", async ({ page, visited }) => {
  await revealGift(page);
  await page.getByRole("button", { name: "Claim this gift" }).click();
  await page.waitForURL(/\/login/);
  await page.locator('input[type="email"]').fill("existing@example.com");
  await page.locator('input[type="password"]').fill("whatever-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.getByText("Who's this for?").waitFor();
  assert.ok(!visited.some((v) => /\/(kyc|onboarding)/.test(v)));
}, {});

await scenario("3. Existing family: children listed, none duplicated by refresh / repeat", async ({ page, mock }) => {
  mock.state.children.push({ id: "c1", displayName: "Maya", dateOfBirth: "2018-05-01", loginCode: "123456", ownershipTransferred: false });
  await revealGift(page);
  await page.getByRole("button", { name: "Claim this gift" }).click();
  await page.locator('input[type="email"]').fill("existing@example.com");
  await page.locator('input[type="password"]').fill("whatever-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.getByText("Who's this for?").waitFor();
  await page.reload();                                                         // refresh mid-journey
  await page.getByText("Who's this for?").waitFor();
  await page.getByRole("button", { name: "Maya" }).waitFor();
  assert.equal(mock.state.children.length, 1);
  assert.equal(mock.calls.filter((c) => c === "POST /children").length, 0);
});

await scenario("4. Needs identity check: only at claim time, returns to the gift afterwards", async ({ page, visited }) => {
  await revealGift(page);
  await page.getByRole("button", { name: "Claim this gift" }).click();
  await page.locator('input[type="email"]').fill("existing@example.com");
  await page.locator('input[type="password"]').fill("whatever-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.getByText("Who's this for?").waitFor();
  assert.ok(!visited.some((v) => /\/kyc/.test(v)), "no KYC before the recipient step");
  await page.getByRole("button", { name: "Myself" }).click();                  // kyc not approved → identity check now
  await page.waitForURL(/\/kyc\?then=%2Fredeem%3Fresume%3D1/);
}, { kyc: "none" });

await scenario("5. Refresh on the preview keeps the gift; expired context gives a clear recovery message", async ({ page }) => {
  await revealGift(page);
  await page.getByRole("button", { name: "Claim this gift" }).click();
  await page.waitForURL(/\/login/);
  await page.goto(`${BASE}/redeem?resume=1`);                                  // back button / manual return
  await page.getByText("Here's what's inside").waitFor();
  await page.evaluate(() => sessionStorage.clear());                           // simulate expiry
  await page.goto(`${BASE}/redeem?resume=1`);
  await page.getByText(/session expired/i).waitFor();
  assert.equal(new URL(page.url()).pathname + new URL(page.url()).search, "/redeem");
});

await scenario("6. Scanned QR (?code=) is moved out of the URL immediately", async ({ page }) => {
  await page.goto(`${BASE}/redeem?code=${CODE}`);
  await page.getByText("Here's what's inside").waitFor();
  assert.ok(!page.url().includes("GG-"), page.url());
});

await scenario("7. Open-redirect attempts are ignored after login", async ({ page }) => {
  await page.goto(`${BASE}/login?then=${encodeURIComponent("https://evil.example/x")}`);
  await page.locator('input[type="email"]').fill("existing@example.com");
  await page.locator('input[type="password"]').fill("whatever-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.waitForURL(`${BASE}/`);
  assert.equal(new URL(page.url()).origin, new URL(BASE).origin);
});

await scenario("8. Child login: link on home + nav, signs in, 'My Portfolio' only, parent areas blocked", async ({ page }) => {
  await page.goto(`${BASE}/`);
  assert.ok(await page.getByRole("link", { name: /Child login/ }).first().isVisible());
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Child login" }).click();
  await page.waitForURL(/\/kid-login/);
  await page.locator('input[inputmode="numeric"]').first().fill("123456");
  await page.locator('input[type="password"]').fill("1234");
  await page.getByRole("button").filter({ hasText: /./ }).last().click();
  await page.waitForURL(/\/kid$/);
  const nav = page.getByRole("navigation", { name: "Main" });
  await nav.getByRole("link", { name: "My Portfolio" }).waitFor();
  const text = (await nav.innerText()).toLowerCase();
  for (const bad of ["family", "redeem", "collections", "profile", "give a share", "log in", "sign up"]) assert.ok(!text.includes(bad), `child nav must not show "${bad}": ${text}`);
  assert.ok(!text.includes("my profile"));
  for (const blocked of ["/family", "/redeem", "/gift/new", "/cart", "/profile"]) {
    await page.goto(`${BASE}${blocked}`);
    await page.waitForURL(/\/kid$/, { timeout: 5000 });
  }
  await page.goto(`${BASE}/kid-login`);                                        // already authenticated → no second login
  await page.waitForURL(/\/kid$/);
});

await scenario("9. Mobile: Child login reachable from the menu", async ({ page }) => {
  await page.goto(`${BASE}/`);
  await page.getByRole("button", { name: "Open menu" }).click();
  await page.getByRole("navigation", { name: "Main (mobile)" }).getByRole("link", { name: "Child login" }).click();
  await page.waitForURL(/\/kid-login/);
}, { mobile: true });

const signInAndPickMyself = async (page) => {
  await page.getByRole("button", { name: "Claim this gift" }).click();
  await page.locator('input[type="email"]').fill("existing@example.com");
  await page.locator('input[type="password"]').fill("whatever-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.getByText("Who's this for?").waitFor();
  await page.getByRole("button", { name: "Myself" }).click();
};
const redeemCalls = (mock) => mock.calls.filter((c) => c === "POST /gifts/redeem").length;

await scenario("10. Existing fully-onboarded ViewTrade user: onboarding SKIPPED, gift still claimed exactly once", async ({ page, mock, visited }) => {
  await revealGift(page);
  await signInAndPickMyself(page);
  await page.getByText("It's yours!").waitFor();
  assert.ok(!visited.some((v) => /\/(kyc|onboarding)/.test(v)), "must not visit onboarding: " + visited.join(","));
  assert.ok(mock.calls.includes("POST /onboarding/resolve-existing"));
  assert.ok(mock.calls.includes("POST /auth/me/refresh"));
  assert.equal(redeemCalls(mock), 1);
}, { kyc: "none", resolve: [{ status: 200, decision: "skip_onboarding" }] });

await scenario("11. Incomplete onboarding (existing ViewTrade user, application in progress): existing onboarding path", async ({ page, mock }) => {
  await revealGift(page);
  await signInAndPickMyself(page);
  await page.waitForURL(/\/kyc\?then=%2Fredeem%3Fresume%3D1/);
  assert.equal(redeemCalls(mock), 0);
}, { kyc: "none", resolve: [{ status: 200, decision: "continue_onboarding" }] });

await scenario("12. Pending review: stays on the gift with a clear message, no onboarding, no claim, gift kept", async ({ page, mock, visited }) => {
  await revealGift(page);
  await signInAndPickMyself(page);
  await page.getByText("Your brokerage account is being reviewed").waitFor();
  assert.ok(!visited.some((v) => /\/(kyc|onboarding)/.test(v)));
  assert.equal(redeemCalls(mock), 0);
  const pending = await page.evaluate(() => JSON.stringify(sessionStorage));
  assert.ok(pending.includes("GG"), "gift code is still saved for later");
}, { kyc: "none", resolve: [{ status: 200, decision: "pending_review" }] });

await scenario("13. ViewTrade check fails: retry offered, NO onboarding started, then retry succeeds and claims once", async ({ page, mock, visited }) => {
  await revealGift(page);
  await signInAndPickMyself(page);
  await page.getByText("We couldn't check your brokerage status").waitFor();
  assert.ok(!visited.some((v) => /\/(kyc|onboarding)/.test(v)), "a failed check must not start onboarding");
  assert.equal(redeemCalls(mock), 0);
  await page.getByRole("button", { name: "Try again" }).click();
  await page.getByText("It's yours!").waitFor();
  assert.equal(redeemCalls(mock), 1);
}, { kyc: "none", resolve: [{ status: 503 }, { status: 200, decision: "skip_onboarding" }] });

await scenario("14. Ambiguous or ineligible match: stops with support message and no retry loop", async ({ page, mock }) => {
  await revealGift(page);
  await signInAndPickMyself(page);
  await page.getByText("We couldn't safely match your account").waitFor();
  assert.equal(await page.getByRole("button", { name: "Try again" }).count(), 0);
  assert.equal(redeemCalls(mock), 0);
}, { kyc: "none", resolve: [{ status: 200, decision: "ambiguous" }] });

await scenario("15. Direct visit to /kyc by an existing user does not show 'Start your brokerage application'", async ({ page }) => {
  await page.goto(`${BASE}/login`);
  await page.locator('input[type="email"]').fill("existing@example.com");
  await page.locator('input[type="password"]').fill("whatever-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await page.waitForURL(`${BASE}/`);
  await page.goto(`${BASE}/kyc`);
  await page.waitForURL(/\/redeem/);                                           // skipped straight on to the gift flow
  assert.equal(await page.getByRole("button", { name: "Start your brokerage application" }).count(), 0);
}, { kyc: "none", resolve: [{ status: 200, decision: "skip_onboarding" }] });

await browser.close();
console.log(`\n${passed} scenarios passed`);
