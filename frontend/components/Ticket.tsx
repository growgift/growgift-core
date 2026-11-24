// The redemption ticket — GrowGift's signature component.
// Top half: the gift (company, shares, message). Bottom stub: the code.

export function Ticket({
  symbol,
  companyName,
  recipientName,
  message,
  code,
}: {
  symbol: string;
  companyName: string;
  recipientName: string;
  message?: string;
  code: string; // GG-XXXX-XXXX-XPXD; masked until issued
}) {
  return (
    <div className="ticket max-w-md w-full overflow-hidden">
      <div className="p-6 space-y-4">
        <div className="flex items-center justify-between">
          <span className="text-xs font-bold uppercase tracking-[0.2em] text-leaf-deep">
            One share of the future
          </span>
          <span className="rounded-full bg-mint px-3 py-1 text-xs font-bold">{symbol}</span>
        </div>
        <h3 className="font-display text-3xl leading-tight">{companyName}</h3>
        <p className="text-sm text-ink/70">
          For <span className="font-semibold text-ink">{recipientName}</span>
          {message ? <> — “{message}”</> : null}
        </p>
      </div>

      <div className="ticket-perf" aria-hidden="true" />

      <div className="p-6 bg-canvas/60">
        <p className="text-[11px] font-bold uppercase tracking-[0.2em] text-ink/50 mb-2">
          Redemption code
        </p>
        <p className="font-mono text-xl font-semibold tracking-widest" data-testid="redemption-code">
          {code}
        </p>
        <p className="mt-2 text-xs text-ink/50">
          Single use · Expires 12 months from issue · growgift.com.au/redeem
        </p>
      </div>
    </div>
  );
}
