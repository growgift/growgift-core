"use client";

import { useEffect, useRef, useState } from "react";
import Link from "next/link";
import { api, ApiError } from "@/lib/api";
import { useRequireAuth } from "@/lib/auth";
import {
  describeState,
  ineligibleMessage,
  newIdempotencyKey,
  validateOrderForm,
  type OrderResult,
  type Side,
} from "@/lib/trading";

type Holding = { symbol: string; quantity: number; marketValue: number | null };
type Step = "form" | "review" | "submitting" | "result";

export default function TradePage() {
  const session = useRequireAuth();

  const [status, setStatus] = useState<{ canTrade: boolean; reason: string | null } | null>(null);
  const [holdings, setHoldings] = useState<Holding[] | null>(null);
  const [side, setSide] = useState<Side>("BUY");
  const [symbol, setSymbol] = useState("");
  const [quantity, setQuantity] = useState("");
  const [step, setStep] = useState<Step>("form");
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<OrderResult | null>(null);
  // One key per deliberate attempt: created when the user reaches "review",
  // kept for any re-send of that same attempt, dropped once a result is shown.
  const keyRef = useRef<string | null>(null);
  const inFlight = useRef(false);

  useEffect(() => {
    if (!session) return;
    api.tradingStatus().then(setStatus).catch(() => setStatus({ canTrade: false, reason: "unavailable" }));
    api.portfolio().then((p) => setHoldings(p.holdings)).catch(() => setHoldings([]));
  }, [session]);

  if (!session) return null;

  const checked = validateOrderForm({ side, symbol, quantity }, holdings ?? undefined);

  function review() {
    setError(null);
    if (!checked.ok) return setError(checked.message);
    keyRef.current = newIdempotencyKey();
    setStep("review");
  }

  async function submit() {
    if (!checked.ok || inFlight.current || !keyRef.current) return;
    inFlight.current = true; // a double click can't start a second request
    setStep("submitting");
    setError(null);
    try {
      const r = await api.placeOrder({ side, symbol: checked.symbol, quantity: checked.quantity }, keyRef.current);
      setResult(r);
      setStep("result");
      keyRef.current = null;
    } catch (e) {
      // The backend said nothing was placed (validation, eligibility, session): safe to edit and try again.
      setError(e instanceof ApiError ? e.message : "Something went wrong. No order was placed.");
      keyRef.current = null;
      setStep("form");
    } finally {
      inFlight.current = false;
    }
  }

  function reset() {
    setResult(null);
    setError(null);
    setQuantity("");
    setStep("form");
    api.portfolio().then((p) => setHoldings(p.holdings)).catch(() => {});
  }

  const blocked = status && !status.canTrade ? ineligibleMessage(status.reason) : null;
  const view = result ? describeState(result.state) : null;

  return (
    <div className="mx-auto max-w-xl px-5 py-16 space-y-8">
      <div>
        <h1 className="font-display text-5xl font-semibold">Buy / Sell</h1>
        <p className="mt-2 text-sm text-ink/60">
          Market orders for US-listed shares through your GrowGift brokerage account.
        </p>
      </div>

      {status === null ? (
        <p className="text-sm text-ink/50">Loading…</p>
      ) : blocked ? (
        <div className="rounded-2xl border-[1.5px] border-ink bg-white p-5 text-sm space-y-3">
          <p>{blocked.text}</p>
          {blocked.href && (
            <Link href={blocked.href} className="font-semibold text-leaf-deep hover:underline">
              {blocked.cta}
            </Link>
          )}
        </div>
      ) : step === "result" && result && view ? (
        <div
          role="status"
          className={`rounded-2xl border-[1.5px] border-ink bg-white p-5 space-y-3 ${
            view.tone === "bad" ? "border-red-700" : ""
          }`}
        >
          <h2 className="font-display text-2xl font-semibold">{view.title}</h2>
          <p className="text-sm">{result.message}</p>
          {result.orderRef && (
            <p className="text-sm">
              Order reference: <span className="font-mono font-semibold">{result.orderRef}</span>
            </p>
          )}
          {result.status && (
            <p className="text-sm">
              Status from ViewTrade: <span className="font-mono">{result.status}</span>
            </p>
          )}
          {result.state === "unconfirmed" && (
            <p className="text-sm font-semibold text-red-700">
              Don&apos;t place this order again until you&apos;ve checked it — it may already have been placed.
            </p>
          )}
          <button onClick={reset} className="rounded-full border-[1.5px] border-ink px-5 py-2 text-sm font-semibold">
            {result.state === "unconfirmed" ? "I've checked — start a new order" : "New order"}
          </button>
        </div>
      ) : (
        <div className="space-y-6">
          <div className="grid grid-cols-2 gap-2" role="group" aria-label="Order side">
            {(["BUY", "SELL"] as Side[]).map((s) => (
              <button
                key={s}
                type="button"
                disabled={step !== "form"}
                aria-pressed={side === s}
                onClick={() => setSide(s)}
                className={`rounded-full border-[1.5px] border-ink py-2 text-sm font-bold ${
                  side === s ? "bg-ink text-white" : "bg-white"
                }`}
              >
                {s === "BUY" ? "Buy" : "Sell"}
              </button>
            ))}
          </div>

          {side === "SELL" && holdings && holdings.length > 0 && (
            <div className="space-y-2">
              <p className="text-xs font-semibold uppercase tracking-wide text-ink/50">Your holdings</p>
              <div className="flex flex-wrap gap-2">
                {holdings.map((h) => (
                  <button
                    key={h.symbol}
                    type="button"
                    disabled={step !== "form"}
                    onClick={() => {
                      setSymbol(h.symbol);
                      setQuantity(String(h.quantity));
                    }}
                    className="rounded-full border border-ink/30 bg-white px-3 py-1 text-xs"
                  >
                    {h.symbol} · {h.quantity}
                  </button>
                ))}
              </div>
            </div>
          )}

          <label className="block text-sm font-semibold">
            Ticker
            <input
              value={symbol}
              onChange={(e) => setSymbol(e.target.value)}
              disabled={step !== "form"}
              autoCapitalize="characters"
              autoComplete="off"
              placeholder="AAPL"
              className="mt-1 w-full rounded-xl border-[1.5px] border-ink bg-white px-4 py-2 font-mono uppercase"
            />
          </label>
          <label className="block text-sm font-semibold">
            {side === "SELL" ? "Quantity (fractions allowed)" : "Quantity (whole shares)"}
            <input
              value={quantity}
              onChange={(e) => setQuantity(e.target.value)}
              disabled={step !== "form"}
              inputMode={side === "SELL" ? "decimal" : "numeric"}
              placeholder="1"
              className="mt-1 w-full rounded-xl border-[1.5px] border-ink bg-white px-4 py-2 font-mono"
            />
          </label>

          {error && (
            <p role="alert" className="text-sm text-red-700">
              {error}
            </p>
          )}

          {step === "form" ? (
            <button
              type="button"
              onClick={review}
              className="w-full rounded-full bg-ink py-3 text-sm font-bold text-white"
            >
              Review order
            </button>
          ) : (
            checked.ok && (
              <div className="rounded-2xl border-[1.5px] border-ink bg-white p-5 space-y-4">
                <p className="text-sm">
                  <strong>{side === "BUY" ? "Buy" : "Sell"}</strong> {checked.quantity} × <strong>{checked.symbol}</strong>{" "}
                  at the market price.
                </p>
                <p className="text-xs text-ink/60">
                  Market orders fill at the prevailing price, which may differ from what you last saw. Once placed, an
                  order can&apos;t be edited here.
                </p>
                <div className="flex gap-3">
                  <button
                    type="button"
                    onClick={submit}
                    disabled={step === "submitting"}
                    className="flex-1 rounded-full bg-ink py-3 text-sm font-bold text-white disabled:opacity-50"
                  >
                    {step === "submitting" ? "Placing…" : `Confirm ${side === "BUY" ? "buy" : "sell"}`}
                  </button>
                  <button
                    type="button"
                    onClick={() => setStep("form")}
                    disabled={step === "submitting"}
                    className="rounded-full border-[1.5px] border-ink px-5 text-sm font-semibold disabled:opacity-50"
                  >
                    Edit
                  </button>
                </div>
              </div>
            )
          )}
        </div>
      )}
    </div>
  );
}
