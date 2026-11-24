"use client";

import {
  allocationSlices,
  formatMoney,
  formatPercent,
  formatPrice,
  formatShares,
  pnlTone,
  portfolioTotals,
  type Holding,
} from "@/lib/portfolio";

// Colour never carries meaning alone: gains/losses also have a +/- sign, and slices are labelled.
const SLICE_COLORS = ["#2F8F5B", "#F2A93B", "#12291F", "#7DBFA0", "#C9772A", "#5B8DB8", "#9A6FB0", "#B8B8B8"];
const TONE: Record<"up" | "down" | "flat", string> = { up: "text-leaf-deep", down: "text-red-700", flat: "text-ink/60" };

export function PortfolioView({
  holdings,
  onSell,
  sellUnavailable,
}: {
  holdings: Holding[];
  /** When given, each row gets a Sell button that calls this with the holding. */
  onSell?: (h: Holding) => void;
  /** Why selling is unavailable right now (shown instead of the buttons). */
  sellUnavailable?: string | null;
}) {
  const totals = portfolioTotals(holdings);
  const slices = allocationSlices(holdings);
  const cur = holdings[0]?.currency ?? "USD";

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-3 gap-3 text-sm">
        <Stat label="Market value" value={totals.marketValue === null ? "—" : formatMoney(totals.marketValue, cur)} />
        <Stat label="Total cost" value={formatMoney(totals.cost, cur)} />
        <Stat
          label="Unrealised P/L"
          value={totals.pnl === null ? "—" : `${totals.pnl > 0 ? "+" : ""}${formatMoney(totals.pnl, cur)} (${formatPercent(totals.pnlPercent)})`}
          tone={pnlTone(totals.pnl)}
        />
      </div>

      <div className="overflow-x-auto rounded-2xl border-[1.5px] border-ink bg-white">
        <table className="w-full min-w-[720px] text-sm">
          <thead>
            <tr className="border-b border-ink/15 text-left text-xs uppercase tracking-wide text-ink/50">
              <th className="px-4 py-3 font-semibold">Symbol</th>
              <th className="px-4 py-3 text-right font-semibold">Qty</th>
              <th className="px-4 py-3 text-right font-semibold">Last Price</th>
              <th className="px-4 py-3 text-right font-semibold">Change %</th>
              <th className="px-4 py-3 text-right font-semibold">Mkt. Value</th>
              <th className="px-4 py-3 text-right font-semibold">Total Cost</th>
              <th className="px-4 py-3 text-right font-semibold">Unr. P/L %</th>
              {onSell && <th className="px-4 py-3 text-right font-semibold"><span className="sr-only">Actions</span></th>}
            </tr>
          </thead>
          <tbody className="divide-y divide-ink/10">
            {holdings.map((h) => (
              <tr key={h.symbol}>
                <td className="px-4 py-3 font-bold">{h.symbol}</td>
                <td className="px-4 py-3 text-right font-mono">{formatShares(h.quantity)}</td>
                <td className="px-4 py-3 text-right font-mono">{formatPrice(h.lastPrice)}</td>
                <td className={`px-4 py-3 text-right font-mono ${TONE[pnlTone(h.changePercent)]}`}>{formatPercent(h.changePercent)}</td>
                <td className="px-4 py-3 text-right font-mono">{h.marketValue === null ? "—" : h.marketValue.toFixed(2)}</td>
                <td className="px-4 py-3 text-right font-mono">{h.costBasis.toFixed(2)}</td>
                <td className={`px-4 py-3 text-right font-mono ${TONE[pnlTone(h.unrealisedPnlPercent)]}`}>{formatPercent(h.unrealisedPnlPercent)}</td>
                {onSell && (
                  <td className="px-4 py-3 text-right">
                    <button
                      type="button"
                      onClick={() => onSell(h)}
                      disabled={!!sellUnavailable || !(h.quantity > 0)}
                      aria-label={`Sell ${h.symbol}`}
                      title={sellUnavailable ?? undefined}
                      className="rounded-full border-[1.5px] border-ink px-3 py-1 text-xs font-bold hover:bg-ink hover:text-white disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:bg-transparent disabled:hover:text-ink"
                    >
                      Sell
                    </button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {onSell && sellUnavailable && <p className="text-xs text-ink/60">{sellUnavailable}</p>}
      {holdings.some((h) => h.marketValue === null) && (
        <p className="text-xs text-ink/60">“—” means ViewTrade hasn&apos;t returned a price for that holding yet (for example, when the market is closed).</p>
      )}

      <div className="grid gap-6 md:grid-cols-2">
        <Donut slices={slices} />
        <CostVsValue holdings={holdings} />
      </div>
    </div>
  );
}

function Stat({ label, value, tone = "flat" }: { label: string; value: string; tone?: "up" | "down" | "flat" }) {
  return (
    <div className="rounded-2xl border-[1.5px] border-ink bg-white px-4 py-3">
      <p className="text-xs uppercase tracking-wide text-ink/50">{label}</p>
      <p className={`mt-1 font-mono text-base font-semibold ${tone === "flat" ? "" : TONE[tone]}`}>{value}</p>
    </div>
  );
}

function Donut({ slices }: { slices: ReturnType<typeof allocationSlices> }) {
  if (slices.length === 0) return <ChartEmpty title="Allocation" text="Allocation appears once ViewTrade returns prices." />;
  const R = 52, C = 2 * Math.PI * R;
  let offset = 0;
  return (
    <figure className="rounded-2xl border-[1.5px] border-ink bg-white p-4">
      <figcaption className="mb-2 text-sm font-semibold">Allocation by market value</figcaption>
      <div className="flex items-center gap-5">
        <svg viewBox="0 0 140 140" className="h-36 w-36 shrink-0 -rotate-90" role="img" aria-label="Portfolio allocation by symbol">
          {slices.map((s, i) => {
            const len = s.share * C;
            const el = (
              <circle key={s.symbol} cx="70" cy="70" r={R} fill="none" stroke={SLICE_COLORS[i % SLICE_COLORS.length]}
                strokeWidth="22" strokeDasharray={`${len} ${C - len}`} strokeDashoffset={-offset} />
            );
            offset += len;
            return el;
          })}
        </svg>
        <ul className="space-y-1 text-sm">
          {slices.map((s, i) => (
            <li key={s.symbol} className="flex items-center gap-2">
              <span className="inline-block h-3 w-3 rounded-sm" style={{ background: SLICE_COLORS[i % SLICE_COLORS.length] }} />
              <span className="font-bold">{s.symbol}</span>
              <span className="font-mono text-ink/70">{(s.share * 100).toFixed(1)}%</span>
            </li>
          ))}
        </ul>
      </div>
    </figure>
  );
}

function CostVsValue({ holdings }: { holdings: Holding[] }) {
  const priced = holdings.filter((h) => h.marketValue !== null);
  if (priced.length === 0) return <ChartEmpty title="Cost vs. value" text="Cost vs. value appears once ViewTrade returns prices." />;
  const max = Math.max(...priced.flatMap((h) => [h.costBasis, h.marketValue as number]), 0.01);
  return (
    <figure className="rounded-2xl border-[1.5px] border-ink bg-white p-4">
      <figcaption className="mb-3 flex items-center justify-between text-sm font-semibold">
        <span>Cost vs. market value</span>
        <span className="flex gap-3 text-xs font-normal text-ink/70">
          <Key color="#B8B8B8" label="Cost" />
          <Key color="#2F8F5B" label="Value" />
        </span>
      </figcaption>
      <div className="space-y-3">
        {priced.map((h) => (
          <div key={h.symbol} className="text-xs">
            <div className="mb-1 flex justify-between"><span className="font-bold">{h.symbol}</span>
              <span className="font-mono text-ink/70">{h.costBasis.toFixed(2)} → {(h.marketValue as number).toFixed(2)}</span></div>
            <div className="h-2.5 rounded bg-ink/5"><div className="h-2.5 rounded" style={{ width: `${(h.costBasis / max) * 100}%`, background: "#B8B8B8" }} /></div>
            <div className="mt-1 h-2.5 rounded bg-ink/5"><div className="h-2.5 rounded" style={{ width: `${((h.marketValue as number) / max) * 100}%`, background: "#2F8F5B" }} /></div>
          </div>
        ))}
      </div>
    </figure>
  );
}

function Key({ color, label }: { color: string; label: string }) {
  return <span className="flex items-center gap-1"><span className="inline-block h-2.5 w-2.5 rounded-sm" style={{ background: color }} />{label}</span>;
}

function ChartEmpty({ title, text }: { title: string; text: string }) {
  return (
    <figure className="rounded-2xl border-[1.5px] border-dashed border-ink/30 bg-white p-4">
      <figcaption className="mb-1 text-sm font-semibold">{title}</figcaption>
      <p className="text-sm text-ink/60">{text}</p>
    </figure>
  );
}
