"use client";

import { useEffect, useRef, useState } from "react";
import Link from "next/link";
import { api, ApiError } from "@/lib/api";
import { formatMoney, type Holding } from "@/lib/portfolio";
import {
  checkSell,
  describeState,
  estimateProceeds,
  formatQty,
  newIdempotencyKey,
  quantityForFraction,
  type OrderResult,
  type SellMode,
} from "@/lib/trading";

type Step = "choose" | "review" | "submitting" | "result";

/**
 * Sell some or all of one holding. The browser sends only {SELL, symbol, quantity | sellAll} and an
 * Idempotency-Key; the server checks ViewTrade's own holdings, resolves "sell all" to the exact position and
 * never accepts an account or token from here. A market order is placed (it fills at the market price).
 */
export function SellDialog({ holding, onClose, onChanged }: { holding: Holding; onClose: () => void; onChanged: () => void }) {
  const [mode, setMode] = useState<SellMode>("all");
  const [input, setInput] = useState("");
  const [step, setStep] = useState<Step>("choose");
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<OrderResult | null>(null);
  const keyRef = useRef<string | null>(null);   // one key per deliberate attempt
  const inFlight = useRef(false);
  const dialogRef = useRef<HTMLDivElement>(null);

  const cur = holding.currency ?? "USD";
  const check = checkSell(holding.quantity, mode, input);
  const est = check.ok ? estimateProceeds(check.quantity, holding.lastPrice) : null;

  useEffect(() => {
    dialogRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && step !== "submitting") close();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [step]);

  function close() {
    if (step === "result") onChanged();
    onClose();
  }

  function review() {
    setError(null);
    if (!check.ok) return setError(check.message);
    keyRef.current = newIdempotencyKey();
    setStep("review");
  }

  async function submit() {
    if (!check.ok || inFlight.current || !keyRef.current) return;
    inFlight.current = true;            // a double click can't start a second request
    setStep("submitting");
    setError(null);
    try {
      const r = await api.placeOrder(
        { side: "SELL", symbol: holding.symbol, quantity: check.sellAll ? 0 : check.quantity, sellAll: check.sellAll },
        keyRef.current,
      );
      setResult(r);
      setStep("result");
      keyRef.current = null;
    } catch (e) {
      // The server said nothing was placed (validation, eligibility, holdings, session): safe to edit and retry.
      setError(e instanceof ApiError ? e.message : "Something went wrong. No order was placed.");
      keyRef.current = null;
      setStep("choose");
    } finally {
      inFlight.current = false;
    }
  }

  const view = result ? describeState(result.state) : null;
  const soldQty = result?.quantity ?? (check.ok ? check.quantity : null);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-ink/50 px-4" onMouseDown={(e) => e.target === e.currentTarget && step !== "submitting" && close()}>
      <div
        ref={dialogRef}
        tabIndex={-1}
        role="dialog"
        aria-modal="true"
        aria-labelledby="sell-title"
        className="w-full max-w-md space-y-5 rounded-2xl border-[1.5px] border-ink bg-white p-6 outline-none"
      >
        <div className="flex items-start justify-between gap-4">
          <div>
            <h2 id="sell-title" className="font-display text-2xl font-semibold">Sell {holding.symbol}</h2>
            <p className="text-sm text-ink/60">You hold {formatQty(holding.quantity)} share{holding.quantity === 1 ? "" : "s"}{holding.marketValue !== null ? ` · about ${formatMoney(holding.marketValue, cur)}` : ""}</p>
          </div>
          <button type="button" onClick={close} disabled={step === "submitting"} aria-label="Close" className="rounded-full px-2 text-xl leading-none text-ink/60 hover:text-ink disabled:opacity-40">×</button>
        </div>

        {step === "result" && result && view ? (
          <div role="status" className="space-y-3">
            <h3 className={`font-display text-xl font-semibold ${view.tone === "bad" ? "text-red-700" : ""}`}>{view.title}</h3>
            <p className="text-sm">{result.message}</p>
            {soldQty ? <p className="text-sm">Sell order: <span className="font-mono font-semibold">{formatQty(soldQty)} × {holding.symbol}</span> at the market price.</p> : null}
            {result.orderRef ? <p className="text-sm">Order reference: <span className="font-mono font-semibold">{result.orderRef}</span></p> : null}
            {result.state === "unconfirmed" && (
              <p className="text-sm font-semibold text-red-700">Don&apos;t sell again until you&apos;ve checked your open orders — this one may already have been placed.</p>
            )}
            {(result.state === "accepted" || result.state === "pending" || result.state === "partiallyfilled") && (
              <p className="text-xs text-ink/60">It will show under <strong>Open orders</strong> below until it executes. Orders placed when the US market is closed wait for the next session.</p>
            )}
            <button type="button" onClick={close} className="w-full rounded-full bg-ink py-3 text-sm font-bold text-white">Done</button>
          </div>
        ) : (
          <>
            <div className="grid grid-cols-2 gap-2" role="group" aria-label="How much to sell">
              {(["all", "part"] as SellMode[]).map((m) => (
                <button
                  key={m}
                  type="button"
                  disabled={step !== "choose"}
                  aria-pressed={mode === m}
                  onClick={() => setMode(m)}
                  className={`rounded-full border-[1.5px] border-ink py-2 text-sm font-bold ${mode === m ? "bg-ink text-white" : "bg-white"}`}
                >
                  {m === "all" ? "Sell all" : "Sell some"}
                </button>
              ))}
            </div>

            {mode === "part" && (
              <div className="space-y-2">
                <label className="block text-sm font-semibold">
                  Shares to sell
                  <input
                    value={input}
                    onChange={(e) => setInput(e.target.value)}
                    disabled={step !== "choose"}
                    inputMode="decimal"
                    autoComplete="off"
                    placeholder={`up to ${formatQty(holding.quantity)}`}
                    className="mt-1 w-full rounded-xl border-[1.5px] border-ink bg-white px-4 py-2 font-mono"
                  />
                </label>
                <div className="flex gap-2" aria-label="Quick amounts">
                  {[0.25, 0.5, 0.75].map((f) => (
                    <button key={f} type="button" disabled={step !== "choose"} onClick={() => setInput(String(quantityForFraction(holding.quantity, f)))}
                      className="rounded-full border border-ink/30 px-3 py-1 text-xs font-semibold hover:bg-ink/5 disabled:opacity-50">
                      {f * 100}%
                    </button>
                  ))}
                </div>
              </div>
            )}

            {check.ok && (
              <div className="rounded-xl bg-ink/5 p-3 text-sm">
                <p>
                  {check.sellAll ? "Selling your whole position: " : "Selling "}
                  <strong className="font-mono">{formatQty(check.quantity)}</strong> × <strong>{holding.symbol}</strong>
                  {est !== null ? <> — about <strong>{formatMoney(est, cur)}</strong></> : null}
                </p>
                {!check.sellAll && <p className="mt-1 text-xs text-ink/60">You&apos;ll keep about {formatQty(Math.max(0, holding.quantity - check.quantity))} share(s).</p>}
                <p className="mt-1 text-xs text-ink/60">{est !== null ? "Estimate at the last price. " : "No live price yet. "}A market order fills at the price when it executes, which can differ.</p>
              </div>
            )}

            {error && <p role="alert" className="text-sm text-red-700">{error}</p>}

            {step === "choose" ? (
              <button type="button" onClick={review} className="w-full rounded-full bg-ink py-3 text-sm font-bold text-white">Review sale</button>
            ) : (
              check.ok && (
                <div className="space-y-3">
                  <p className="text-sm font-semibold">Confirm: sell {check.sellAll ? "all" : formatQty(check.quantity)} {holding.symbol} at the market price.</p>
                  <p className="text-xs text-ink/60">Once placed, this order can&apos;t be changed here. Selling may have tax consequences — GrowGift doesn&apos;t give tax advice.</p>
                  <div className="flex gap-3">
                    <button type="button" onClick={submit} disabled={step === "submitting"} className="flex-1 rounded-full bg-ink py-3 text-sm font-bold text-white disabled:opacity-50">
                      {step === "submitting" ? "Placing…" : "Confirm sell"}
                    </button>
                    <button type="button" onClick={() => setStep("choose")} disabled={step === "submitting"} className="rounded-full border-[1.5px] border-ink px-5 text-sm font-semibold disabled:opacity-50">Edit</button>
                  </div>
                </div>
              )
            )}
            <p className="text-center text-xs text-ink/50">Or use the <Link href="/trade" className="underline">Buy / Sell page</Link>.</p>
          </>
        )}
      </div>
    </div>
  );
}
