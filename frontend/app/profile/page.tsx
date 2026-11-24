"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { api, type BuyerOrderSummary } from "@/lib/api";
import type { SubscriptionView } from "@/lib/billing";
import { SubscriptionCard } from "@/components/SubscriptionCard";
import { useRequireAuth } from "@/lib/auth";
import { PortfolioView } from "@/components/PortfolioView";
import { SellDialog } from "@/components/SellDialog";
import { sellUnavailableReason } from "@/lib/trading";
import { parseSession } from "@/lib/auth";
import { needsBrokerageCheck, waitForApproval } from "@/lib/brokerageGate";
import { boxName, describeGiftOrder, orderRef, safeTrackingUrl, shipmentsByOrder, type ShipmentLite } from "@/lib/giftStatus";
import { OPEN_ORDERS_EXPLAINER, describeOrder, openOrderTiming, portfolioErrorMessage, redemptionStateLabel, unlinkedMessage, type Holding, type OpenOrder, type Redemption } from "@/lib/portfolio";

type ReceivedGift = {
  giftId: string;
  orderId: string;
  symbol: string;
  amount: number;
  currency: string;
  redeemedAtUtc: string;
};

type PortfolioData = {
  hasAccount: boolean;
  cashBalance: number;
  holdings: Holding[];
  recognised?: boolean;
  raw?: string | null;
};

export default function ProfilePage() {
  const session = useRequireAuth();

  const [subscription, setSubscription] = useState<SubscriptionView | null>(null);
  const [given, setGiven] = useState<BuyerOrderSummary[] | null>(null);
  const [shipments, setShipments] = useState<Record<string, ShipmentLite>>({});
  const [shownCodes, setShownCodes] = useState<Set<string>>(new Set());
  const [received, setReceived] = useState<ReceivedGift[] | null>(null);
  const [portfolio, setPortfolio] = useState<PortfolioData | null>(null);
  const [givenError, setGivenError] = useState<string | null>(null);
  const [receivedError, setReceivedError] = useState<string | null>(null);
  const [portfolioError, setPortfolioError] = useState<string | null>(null);
  const [openOrders, setOpenOrders] = useState<OpenOrder[] | null>(null);
  const [openOrdersError, setOpenOrdersError] = useState<string | null>(null);
  const [purchases, setPurchases] = useState<Redemption[] | null>(null);
  const [retrying, setRetrying] = useState<string | null>(null);
  const [retryMsg, setRetryMsg] = useState<string | null>(null);
  const [diag, setDiag] = useState<string | null>(null);
  // Selling: eligibility comes from the backend (KYC, child login, linked account) — the Sell button only
  // reflects it; the server enforces it again on every order.
  const [trade, setTrade] = useState<{ canTrade: boolean; reason: string | null } | null>(null);
  const [sellTarget, setSellTarget] = useState<Holding | null>(null);
  const [syncNote, setSyncNote] = useState<string | null>(null);
  const [linkNote, setLinkNote] = useState<{ text: string; cta?: { href: string; label: string } } | null>(null);

  // Three independent, unrelated backends (Gifting x2, Brokerage) — loaded
  // in parallel and rendered as each resolves rather than gating the whole
  // page on the slowest one. Each has its own error state: a single shared
  // error previously meant (a) a failure never cleared that section's
  // "Loading…" — it just stayed null forever alongside an unrelated banner,
  // and (b) "first error wins" logic silently swallowed every failure after
  // the first, so a second endpoint failing produced no visible message at
  // all instead of its own.
  useEffect(() => {
    if (!session) return;
    // Subscription status is a bonus: if it can't load, the rest of the profile still shows.
    api.subscription().then(setSubscription).catch(() => {});
    api.myOrders().then(setGiven).catch(() => setGivenError("Couldn't load gifts you've given."));
    // Delivery progress for physical gifts is a bonus: if it can't load, the gift list still shows.
    api.myShipments().then((l) => setShipments(shipmentsByOrder(l))).catch(() => {});
    api.receivedGifts().then(setReceived).catch(() => setReceivedError("Couldn't load gifts you've received."));
    void loadPortfolio();
    api.tradingStatus().then(setTrade).catch(() => setTrade({ canTrade: false, reason: "unavailable" }));
    api.redemptions().then(setPurchases).catch(() => setPurchases([]));
    api.openOrders().then((r) => setOpenOrders(r.orders)).catch((e) => setOpenOrdersError(portfolioErrorMessage(e)));
  }, [session]);

  // After a sell: show the new open order and the updated holdings (a market order may not have filled yet).
  function refreshAfterSell() {
    void loadPortfolio();
    setOpenOrdersError(null);
    api.openOrders().then((r) => setOpenOrders(r.orders)).catch((e) => setOpenOrdersError(portfolioErrorMessage(e)));
  }

  async function retry(giftId: string, confirmNoOrder = false) {
    setRetrying(giftId);
    setRetryMsg(null);
    try {
      await api.retryRedemption(giftId, confirmNoOrder);
      setPurchases(await api.redemptions());
      void loadPortfolio();
    } catch (e) {
      setRetryMsg((e as { message?: string })?.message ?? "Couldn't retry just now.");
    } finally {
      setRetrying(null);
    }
  }

  // A user can already have a ViewTrade account before ever redeeming a gift here. If nothing is linked yet
  // (or only the dev placeholder), ask the backend to look for and link the existing account — it decides, using
  // the verified email — then refresh the token and load the real portfolio. A failed lookup is shown as a
  // retrievable problem, never as "you have no account".
  async function loadPortfolio() {
    try {
      let p = await api.portfolio();
      const s = parseSession();
      if (s && needsBrokerageCheck(s)) {
        setLinkNote(null);
        try {
          const { decision } = await api.resolveExistingAccount();
          if (decision === "skip_onboarding") {
            // Brokerage has recorded the link already, so the portfolio is available now; the token catches up in the background.
            p = await api.portfolio();
            // Identity learns about the link asynchronously, so wait (briefly) for the refreshed token to carry
            // KYC approval, then re-ask whether trading is allowed. Without this the Sell button kept the answer
            // computed from the OLD token ("finish identity verification") even though the account was linked.
            const synced = await waitForApproval(async () => {
              await api.refreshSession();
              return parseSession()?.kycApproved === true;
            });
            api.tradingStatus().then(setTrade).catch(() => setTrade({ canTrade: false, reason: "unavailable" }));
            if (!p.hasAccount) setLinkNote({ text: "We found your ViewTrade account but couldn't load it just now. Please refresh to try again." });
            else if (!synced) setSyncNote("We found your ViewTrade account. Your verification is still syncing, so selling may stay unavailable for a minute — please refresh shortly.");
          } else if (!p.hasAccount || p.accountRef?.startsWith("VT-DEV-")) {
            setLinkNote(unlinkedMessage(decision));
          }
        } catch {
          setLinkNote({ text: "We couldn't check ViewTrade for an existing account just now. Nothing has changed — please refresh to try again." });
        }
      }
      setPortfolio(p);
    } catch (e) {
      setPortfolioError(portfolioErrorMessage(e));
    }
  }

  if (!session) return null;

  return (
    <div className="mx-auto max-w-3xl px-5 py-16 space-y-12">
      {sellTarget && <SellDialog holding={sellTarget} onClose={() => setSellTarget(null)} onChanged={refreshAfterSell} />}
      <div>
        <h1 className="font-display text-5xl font-semibold">My profile</h1>
      </div>

      {subscription?.required && (
        <section className="space-y-3">
          <h2 className="font-display text-2xl font-semibold">Subscription</h2>
          <SubscriptionCard view={subscription} />
        </section>
      )}

      <section className="space-y-4">
        <h2 className="font-display text-2xl font-semibold">Portfolio</h2>
        {process.env.NODE_ENV !== "production" && (
          <div className="text-xs">
            <button
              type="button"
              className="rounded-full border border-ink/30 px-3 py-1 font-semibold hover:bg-ink/5"
              onClick={() => {
                setDiag("Checking…");
                api.diagnoseTrading().then((r) => setDiag(JSON.stringify(r, null, 2))).catch((e) => setDiag(`Check failed: ${e?.message ?? e}`));
              }}
            >
              Check ViewTrade connection (dev)
            </button>
            {diag ? <pre className="mt-2 max-h-64 overflow-auto rounded-xl bg-ink/5 p-3">{diag}</pre> : null}
          </div>
        )}
        {syncNote && <p className="text-sm text-ink/70">{syncNote}</p>}
        {portfolioError ? (
          <p className="text-sm text-red-700">{portfolioError}</p>
        ) : portfolio === null ? (
          <p className="text-sm text-ink/50">Loading…</p>
        ) : linkNote ? (
          <p className="text-sm text-ink/70">
            {linkNote.text}
            {linkNote.cta ? (
              <>
                {" "}
                <Link href={linkNote.cta.href} className="font-semibold text-leaf-deep hover:underline">
                  {linkNote.cta.label}
                </Link>
                .
              </>
            ) : null}
          </p>
        ) : portfolio.recognised === false ? (
          <div className="space-y-2 text-sm text-ink/70">
            <p>ViewTrade answered, but in a format we don&apos;t recognise yet.</p>
            {portfolio.raw ? (
              <pre className="max-h-64 overflow-auto rounded-xl bg-ink/5 p-3 text-xs">{portfolio.raw}</pre>
            ) : null}
          </div>
        ) : portfolio.holdings.length === 0 ? (
          <p className="text-sm text-ink/70">No holdings yet.</p>
        ) : (
          <PortfolioView
            holdings={portfolio.holdings}
            onSell={setSellTarget}
            sellUnavailable={sellUnavailableReason(trade?.canTrade ?? null, trade?.reason)}
          />
        )}
      </section>

      {(openOrdersError || (openOrders && openOrders.length > 0)) && (
        <section className="space-y-3">
          <h2 className="font-display text-2xl font-semibold">Open orders</h2>
          {openOrdersError ? (
            <p className="text-sm text-red-700">{openOrdersError}</p>
          ) : (
            <>
              <p className="text-sm text-ink/70">{OPEN_ORDERS_EXPLAINER}</p>
              <div className="divide-y divide-ink/10 rounded-2xl border-[1.5px] border-ink bg-white">
                {openOrders!.map((o) => (
                  <div key={o.orderRef} className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 px-5 py-3 text-sm">
                    <span className="font-bold">{describeOrder(o)}</span>
                    <span className="rounded-full bg-ribbon/30 px-2 py-0.5 text-xs font-semibold">Open — waiting to fill</span>
                    {o.quantity != null && o.orderType.toLowerCase() === "notional" && (
                      <span className="text-xs text-ink/60">≈ {o.quantity.toFixed(5)} shares{o.expectedPrice ? ` at ~${o.expectedPrice.toFixed(2)}` : ""}</span>
                    )}
                    <span className="w-full text-xs text-ink/50">
                      ViewTrade order {o.orderRef}{o.validity ? ` · ${o.validity}` : ""}{openOrderTiming(o) ? ` · ${openOrderTiming(o)}` : ""}
                    </span>
                  </div>
                ))}
              </div>
            </>
          )}
        </section>
      )}

      {purchases && purchases.length > 0 && (
        <section className="space-y-3">
          <h2 className="font-display text-2xl font-semibold">Share purchases from redeemed gifts</h2>
          <div className="divide-y divide-ink/10 rounded-2xl border-[1.5px] border-ink bg-white">
            {purchases.map((p) => {
              const st = redemptionStateLabel(p.state);
              return (
                <div key={p.giftId} className="flex flex-wrap items-center justify-between gap-2 px-5 py-3 text-sm">
                  <span className="font-bold">{p.symbol}</span>
                  <span className="font-mono">{p.currency} {p.amount.toFixed(2)}{p.usdAmount ? ` (US$${p.usdAmount.toFixed(2)})` : ""}</span>
                  <span className={st.tone === "ok" ? "text-leaf-deep" : st.tone === "bad" ? "text-red-700" : "text-ink/70"}>{st.text}</span>
                  {(p.orderRef || p.account) && (
                    <span className="w-full text-xs text-ink/50">
                      {p.orderRef ? `ViewTrade order ${p.orderRef}` : "No order reference"}
                      {p.account ? ` · account ${p.account}` : ""}
                    </span>
                  )}
                  {p.canRetryIfNoOrderExists && (
                    <button type="button" disabled={retrying === p.giftId}
                      className="rounded-full border-[1.5px] border-ink px-3 py-1 text-xs font-semibold hover:bg-ink/5 disabled:opacity-50"
                      title="Development only. Use this only after checking ViewTrade and finding no order for this gift."
                      onClick={() => void retry(p.giftId, true)}>
                      {retrying === p.giftId ? "Retrying…" : "I checked — no order exists, buy again (dev)"}
                    </button>
                  )}
                  {p.canRetry && (
                    <button type="button" disabled={retrying === p.giftId}
                      className="rounded-full border-[1.5px] border-ink px-3 py-1 text-xs font-semibold hover:bg-ink/5 disabled:opacity-50"
                      onClick={() => void retry(p.giftId)}>
                      {retrying === p.giftId ? "Retrying…" : "Retry purchase"}
                    </button>
                  )}
                </div>
              );
            })}
          </div>
          {retryMsg && <p className="text-sm text-red-700">{retryMsg}</p>}
        </section>
      )}

      <section className="space-y-4">
        <h2 className="font-display text-2xl font-semibold">Gifts you&apos;ve given</h2>
        {givenError ? (
          <p className="text-sm text-red-700">{givenError}</p>
        ) : given === null ? (
          <p className="text-sm text-ink/50">Loading…</p>
        ) : given.length === 0 ? (
          <p className="text-sm text-ink/70">
            No gifts yet —{" "}
            <Link href="/gift/new" className="font-semibold text-leaf-deep hover:underline">
              send your first one
            </Link>
            .
          </p>
        ) : (
          <div className="rounded-2xl border-[1.5px] border-ink bg-white divide-y divide-ink/10">
            {given.map((o) => {
              const st = describeGiftOrder(o);
              const ship = o.deliveryMethod === "Physical" && st.paid ? shipments[o.id] : undefined;
              const track = safeTrackingUrl(ship?.trackingUrl);
              const tone = st.tone === "good" ? "text-leaf-deep" : st.tone === "bad" || st.tone === "warn" ? "text-red-700" : "text-ink/70";
              const box = boxName(o.boxSku);
              return (
                <div key={o.id} className="flex flex-col gap-2 px-5 py-4 text-sm sm:flex-row sm:items-start sm:justify-between sm:gap-4">
                  <div className="min-w-0 space-y-1">
                    <p className="font-bold">
                      {o.symbol} for {o.recipientName ?? "someone special"}
                    </p>
                    <p className={`font-semibold ${tone}`}>{st.label}</p>
                    <p className="text-xs text-ink/50">
                      {o.deliveryMethod === "Physical" ? `📦 ${box ?? "Physical gift box"}` : "📧 Delivered by email"} ·{" "}
                      {new Date(o.createdAtUtc).toLocaleDateString()} · {orderRef(o.id)}
                    </p>
                    {st.next && <p className="text-xs text-ink/70">{st.next}</p>}
                    {ship && (
                      <p className="text-xs text-ink/70">
                        Delivery: {ship.status}
                        {ship.carrier ? ` · ${ship.carrier}` : ""}
                        {ship.trackingNumber ? ` · ${ship.trackingNumber}` : ""}
                        {track && (
                          <>
                            {" "}
                            <a href={track} target="_blank" rel="noopener noreferrer" className="font-semibold text-leaf-deep hover:underline">
                              Track
                            </a>
                          </>
                        )}
                      </p>
                    )}
                    {st.canShowCode && o.code && (
                      <p className="text-xs">
                        {shownCodes.has(o.id) ? (
                          <span className="font-mono tracking-wide text-leaf-deep">{o.code}</span>
                        ) : (
                          <button
                            type="button"
                            className="font-semibold text-leaf-deep hover:underline"
                            onClick={() => setShownCodes((prev) => new Set(prev).add(o.id))}
                          >
                            Show redemption code
                          </button>
                        )}
                      </p>
                    )}
                  </div>
                  <div className="shrink-0 sm:text-right">
                    <p className="font-mono">
                      ${o.amount.toFixed(2)} {o.currency} of {o.symbol}
                    </p>
                    {st.paid && o.totalAmount !== o.amount && (
                      <p className="font-mono text-xs text-ink/50">${o.totalAmount.toFixed(2)} total paid</p>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </section>

      <section className="space-y-4">
        <h2 className="font-display text-2xl font-semibold">Gifts you&apos;ve received</h2>
        {receivedError ? (
          <p className="text-sm text-red-700">{receivedError}</p>
        ) : received === null ? (
          <p className="text-sm text-ink/50">Loading…</p>
        ) : received.length === 0 ? (
          <p className="text-sm text-ink/70">
            Nothing redeemed yet —{" "}
            <Link href="/redeem" className="font-semibold text-leaf-deep hover:underline">
              have a code?
            </Link>
          </p>
        ) : (
          <div className="rounded-2xl border-[1.5px] border-ink bg-white divide-y divide-ink/10">
            {received.map((g) => (
              <div key={g.giftId} className="flex justify-between px-5 py-3 text-sm">
                <span className="font-bold">{g.symbol}</span>
                <span className="text-ink/50">{new Date(g.redeemedAtUtc).toLocaleDateString()}</span>
                <span className="font-mono">
                  ${g.amount.toFixed(2)} {g.currency}
                </span>
              </div>
            ))}
          </div>
        )}
      </section>
    </div>
  );
}
