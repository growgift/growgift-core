// Browser test of the Sell flow on My Profile with a MOCKED gateway (no backend, no ViewTrade, no real orders).
// Usage:   npm i --no-save playwright-core   (Chromium must be installed)
//          npm run dev -- -p 3100
//          BASE=http://localhost:3100 CHROMIUM=/path/to/chromium node scripts/e2e-sell.mjs
import { chromium } from "playwright-core";
import assert from "node:assert/strict";

const BASE = process.env.BASE ?? "http://localhost:3100";
const b64 = (o) => Buffer.from(JSON.stringify(o)).toString("base64url");
const jwt = (claims) => `${b64({ alg: "none" })}.${b64({ exp: Math.floor(Date.now() / 1000) + 3600, sub: "u1", email: "a@b.c", ...claims })}.x`;

function makeMock({ canTrade = true, reason = null, orderReply = { status: 200, body: { state: "accepted", orderRef: "OR-1", message: "ViewTrade accepted your order." } } } = {}) {
  const orders = [];
  const handler = async (route) => {
    const req = route.request();
    const path = new URL(req.url()).pathname.replace(/^\/api\/v1/, "");
    const json = (status, data) => route.fulfill({ status, contentType: "application/json", body: JSON.stringify(data) });
    if (path === "/orders/mine" || path === "/gifts/received") return json(200, []);
    if (path === "/portfolio")
      return json(200, { hasAccount: true, accountRef: "ORBS-01-004099", cashBalance: 0, recognised: true, holdings: [
        { symbol: "IBM", quantity: 0.02029, marketValue: 5.14, costBasis: 4.61, currency: "USD", lastPrice: 253.56, changePercent: 0.575, unrealisedPnl: 0.53, unrealisedPnlPercent: 11.63 },
      ] });
    if (path === "/trading/status") return json(200, { canTrade, reason, accountRef: canTrade ? "ORBS-01-004099" : null });
    if (path === "/trading/redemptions") return json(200, []);
    if (path === "/trading/open-orders") return json(200, { orders: [] });
    if (path === "/trading/orders" && req.method() === "POST") {
      orders.push({ body: JSON.parse(req.postData()), key: req.headers()["idempotency-key"] });
      await new Promise((r) => setTimeout(r, 150));
      return json(orderReply.status, orderReply.body);
    }
    return json(404, { error: { code: "mock.unhandled", message: path } });
  };
  return { orders, handler };
}

const browser = await chromium.launch({ executablePath: process.env.CHROMIUM ?? "/opt/pw-browsers/chromium", args: ["--no-sandbox"] });
let passed = 0;
async function scenario(name, opts, fn) {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await ctx.newPage();
  const mock = makeMock(opts);
  await page.route("**/api/v1/**", mock.handler);
  await page.addInitScript((t) => sessionStorage.setItem("gg_token", t), jwt({ role: "recipient", kyc_status: "approved", viewtrade_account: "ORBS-01-004099" }));
  try {
    await page.goto(`${BASE}/profile`);
    await fn({ page, mock });
    console.log("PASS", name); passed++;
  } catch (e) { console.log("FAIL", name, "\n  ", e.message.split("\n")[0]); process.exitCode = 1; }
  finally { await ctx.close(); }
}
const openSell = async (page) => { await page.getByRole("button", { name: "Sell IBM" }).click(); await page.getByRole("dialog").waitFor(); };

await scenario("sell all: review → confirm sends sellAll and no typed quantity", {}, async ({ page, mock }) => {
  await openSell(page);
  await page.getByText("Selling your whole position").waitFor();
  await page.getByRole("button", { name: "Review sale" }).click();
  await page.getByRole("button", { name: "Confirm sell" }).click();
  await page.getByRole("status").waitFor();
  assert.equal(mock.orders.length, 1);
  assert.deepEqual(mock.orders[0].body, { side: "SELL", symbol: "IBM", quantity: 0, sellAll: true });
  assert.match(mock.orders[0].key, /^[A-Za-z0-9_-]{8,64}$/);
  assert.match(await page.getByRole("status").innerText(), /accepted — not yet executed/i);
});

await scenario("sell some: partial quantity, 50% button, keeps the rest", {}, async ({ page, mock }) => {
  await openSell(page);
  await page.getByRole("button", { name: "Sell some" }).click();
  await page.getByRole("button", { name: "50%" }).click();
  assert.equal(await page.getByLabel("Shares to sell").inputValue(), "0.010145");
  await page.getByText(/You'll keep about/).waitFor();
  await page.getByRole("button", { name: "Review sale" }).click();
  await page.getByRole("button", { name: "Confirm sell" }).click();
  await page.getByRole("status").waitFor();
  assert.deepEqual(mock.orders[0].body, { side: "SELL", symbol: "IBM", quantity: 0.010145, sellAll: false });
});

await scenario("typing the whole position is promoted to sell-all", {}, async ({ page, mock }) => {
  await openSell(page);
  await page.getByRole("button", { name: "Sell some" }).click();
  await page.getByLabel("Shares to sell").fill("0.02029");
  await page.getByText("Selling your whole position").waitFor();
  await page.getByRole("button", { name: "Review sale" }).click();
  await page.getByRole("button", { name: "Confirm sell" }).click();
  await page.getByRole("status").waitFor();
  assert.equal(mock.orders[0].body.sellAll, true);
});

await scenario("too many shares / bad input is stopped before anything is sent", {}, async ({ page, mock }) => {
  await openSell(page);
  await page.getByRole("button", { name: "Sell some" }).click();
  for (const bad of ["0.03", "abc", "0", ""]) {
    await page.getByLabel("Shares to sell").fill(bad);
    await page.getByRole("button", { name: "Review sale" }).click();
    await page.getByRole("dialog").getByRole("alert").waitFor();
  }
  assert.equal(mock.orders.length, 0);
});

await scenario("double click on confirm sends exactly one order", {}, async ({ page, mock }) => {
  await openSell(page);
  await page.getByRole("button", { name: "Review sale" }).click();
  const confirm = page.getByRole("button", { name: "Confirm sell" });
  await confirm.dblclick();
  await page.getByRole("status").waitFor();
  assert.equal(mock.orders.length, 1);
});

await scenario("unconfirmed order warns not to sell again", { orderReply: { status: 202, body: { state: "unconfirmed", message: "We couldn't confirm this order." } } }, async ({ page }) => {
  await openSell(page);
  await page.getByRole("button", { name: "Review sale" }).click();
  await page.getByRole("button", { name: "Confirm sell" }).click();
  await page.getByText(/Don't sell again until you've checked/).waitFor();
});

await scenario("server refusal (nothing placed) returns to the form with the reason", { orderReply: { status: 422, body: { code: "trading.insufficient_shares", message: "You only hold 0.01 share of IBM." } } }, async ({ page }) => {
  await openSell(page);
  await page.getByRole("button", { name: "Review sale" }).click();
  await page.getByRole("button", { name: "Confirm sell" }).click();
  await page.getByRole("dialog").getByRole("alert").filter({ hasText: "You only hold 0.01" }).waitFor();
  await page.getByRole("button", { name: "Review sale" }).waitFor();
});

await scenario("ineligible user (child/KYC) cannot open the sell dialog", { canTrade: false, reason: "kyc_required" }, async ({ page }) => {
  const btn = page.getByRole("button", { name: "Sell IBM" });
  await btn.waitFor();
  assert.equal(await btn.isDisabled(), true);
  await page.getByText(/identity verification/i).waitFor();
});

await scenario("Escape closes the dialog without ordering", {}, async ({ page, mock }) => {
  await openSell(page);
  await page.keyboard.press("Escape");
  await page.getByRole("dialog").waitFor({ state: "detached" });
  assert.equal(mock.orders.length, 0);
});

await browser.close();
console.log(`${passed} scenarios passed`);
