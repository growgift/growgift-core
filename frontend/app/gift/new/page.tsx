"use client";

import { Suspense, useMemo, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Ticket } from "@/components/Ticket";
import type { DeliveryMethod } from "@/lib/api";
import { collections, findCollection } from "@/lib/collections";
import { addToCart } from "@/lib/cart";

const boxes = [
  { sku: "BOX-CLASSIC", name: "Classic", desc: "Ticket + certificate frame", price: 12 },
  { sku: "BOX-DELUXE", name: "Deluxe", desc: "Classic + themed goodies", price: 29 },
  { sku: "BOX-GRAND", name: "Grand", desc: "Deluxe + keepsake wooden box", price: 49 },
];

// Display-only — the actual Stripe charge always comes from the server's
// own configured value (Fees:PostalCharge / Fees:ServiceCharge), never these.
const postalFee = 6;
const serviceFee = 7;

const amounts = [25, 50, 100, 250];
const MIN_AMOUNT = 20;
const MAX_AMOUNT = 4500;

type Step = 0 | 1 | 2 | 3 | 4;

export default function GiftBuilderPage() {
  return (
    <Suspense>
      <GiftBuilder />
    </Suspense>
  );
}

function GiftBuilder() {
  const router = useRouter();
  const searchParams = useSearchParams();

  // No login check anywhere in this wizard — browsing, picking a company/
  // amount, writing a message, and adding the finished gift to the cart
  // all work anonymously. Login is only asked for later, on the cart page,
  // at the point of actually paying — see that page for why that's a
  // cleaner place for it than here.
  const [step, setStep] = useState<Step>(0);
  const [deliveryMethod, setDeliveryMethod] = useState<DeliveryMethod | null>(null);
  const [collectionSlug, setCollectionSlug] = useState(() => findCollection(searchParams.get("collection")).slug);
  const activeCollection = useMemo(() => findCollection(collectionSlug), [collectionSlug]);
  const companies = useMemo(() => activeCollection.companies, [activeCollection]);
  // Deliberately starts with nothing picked — a default pre-selected
  // company meant the Continue button on this step was never actually
  // gated on anything, letting someone click straight through without
  // ever choosing which company they're gifting at all.
  const [symbol, setSymbol] = useState<string | null>(null);
  const selected = companies.find((c) => c.symbol === symbol) ?? null;
  const [amount, setAmount] = useState(50);
  const [customAmount, setCustomAmount] = useState("");
  const [recipientName, setRecipientName] = useState("");
  const [recipientEmail, setRecipientEmail] = useState("");
  const [message, setMessage] = useState("");
  const [box, setBox] = useState(boxes[1]);
  const [added, setAdded] = useState(false);

  const effectiveAmount = customAmount ? Number(customAmount) : amount;
  const amountValid = effectiveAmount >= MIN_AMOUNT && effectiveAmount <= MAX_AMOUNT;
  const recipientEmailValid = /\S+@\S+\.\S+/.test(recipientEmail);
  const feeAmount = deliveryMethod === "Physical" ? postalFee : serviceFee;
  const boxPriceIfAny = deliveryMethod === "Physical" ? box.price : 0;
  const displayTotal = effectiveAmount + boxPriceIfAny + feeAmount;

  function handleAddToCart() {
    if (!selected) return; // guarded by canContinueFromStep[1] already, but no non-null assertion needed either way
    addToCart({
      deliveryMethod: deliveryMethod!,
      symbol: selected.symbol,
      companyName: selected.name,
      amount: effectiveAmount,
      currency: "AUD",
      recipientName,
      recipientEmail: deliveryMethod === "Email" ? recipientEmail : "",
      message,
      boxSku: deliveryMethod === "Physical" ? box.sku : null,
      boxName: deliveryMethod === "Physical" ? box.name : null,
      boxPrice: boxPriceIfAny,
      postalOrServiceFee: feeAmount,
    });
    setAdded(true);
  }

  const stepLabels = ["Delivery", "Company", "Amount", "Make it theirs", "Review"];
  const canContinueFromStep: Record<Step, boolean> = {
    0: deliveryMethod !== null,
    1: selected !== null,
    2: amountValid,
    3: deliveryMethod === "Email" ? !!recipientName && recipientEmailValid : !!recipientName,
    4: true,
  };

  if (added) {
    return (
      <div className="mx-auto max-w-lg px-5 py-24 text-center space-y-6">
        <span className="text-6xl">🛒</span>
        <h1 className="font-display text-4xl font-semibold">Added to your cart</h1>
        <p className="text-ink/70">Add another gift, or head to your cart to check out.</p>
        <div className="flex justify-center gap-3">
          <button
            onClick={() => {
              setAdded(false);
              setStep(0);
              setDeliveryMethod(null);
              setRecipientName("");
              setRecipientEmail("");
              setMessage("");
              setCustomAmount("");
            }}
            className="rounded-full border-[1.5px] border-ink px-6 py-2.5 font-semibold hover:bg-mint transition"
          >
            Add another gift
          </button>
          <button
            onClick={() => router.push("/cart")}
            className="rounded-full bg-leaf text-white px-6 py-2.5 font-semibold hover:bg-leaf-deep transition"
          >
            Go to cart
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-6xl px-5 py-14 grid gap-12 lg:grid-cols-[1fr_420px]">
      <div>
        <ol className="flex gap-2 mb-10 text-xs font-bold uppercase tracking-wider">
          {stepLabels.map((label, i) => (
            <li
              key={label}
              className={`flex-1 border-t-4 pt-2 ${
                i <= step ? "border-leaf text-leaf-deep" : "border-ink/15 text-ink/40"
              }`}
            >
              {label}
            </li>
          ))}
        </ol>

        {step === 0 && (
          <section className="space-y-5">
            <h1 className="font-display text-4xl font-semibold">How should this gift arrive?</h1>
            <div className="grid gap-4 sm:grid-cols-2">
              <button
                onClick={() => setDeliveryMethod("Physical")}
                className={`rounded-2xl border-[1.5px] p-6 text-left transition ${
                  deliveryMethod === "Physical" ? "border-leaf bg-mint/50" : "border-ink/20 bg-white hover:border-ink"
                }`}
              >
                <span className="text-3xl">🎁</span>
                <p className="font-display text-xl mt-2">Build your own Gift Box</p>
                <p className="text-sm text-ink/60 mt-1">
                  A physical box with a share certificate, gift card, and a few fun extras — mailed to your
                  recipient. A postage fee applies.
                </p>
              </button>
              <button
                onClick={() => setDeliveryMethod("Email")}
                className={`rounded-2xl border-[1.5px] p-6 text-left transition ${
                  deliveryMethod === "Email" ? "border-leaf bg-mint/50" : "border-ink/20 bg-white hover:border-ink"
                }`}
              >
                <span className="text-3xl">📧</span>
                <p className="font-display text-xl mt-2">Gift your shares via email</p>
                <p className="text-sm text-ink/60 mt-1">
                  Nothing to mail — your recipient gets their redemption code straight to their inbox. A small
                  service fee applies instead of postage.
                </p>
              </button>
            </div>
          </section>
        )}

        {step === 1 && (
          <section className="space-y-5">
            <h1 className="font-display text-4xl font-semibold">Whose story are you gifting?</h1>
            <div className="flex flex-wrap gap-2">
              {collections.map((col) => (
                <button
                  key={col.slug}
                  onClick={() => setCollectionSlug(col.slug)}
                  className={`rounded-full border-[1.5px] px-4 py-1.5 text-sm font-semibold transition ${
                    col.slug === activeCollection.slug
                      ? "border-leaf bg-leaf text-white"
                      : "border-ink/20 bg-white hover:border-ink"
                  }`}
                >
                  {col.emoji} {col.name}
                </button>
              ))}
            </div>
            <div className="grid gap-3 sm:grid-cols-2">
              {companies.map((c) => (
                <button
                  key={c.symbol}
                  onClick={() => setSymbol(c.symbol)}
                  className={`rounded-2xl border-[1.5px] p-4 text-left transition ${
                    selected?.symbol === c.symbol
                      ? "border-leaf bg-mint/50"
                      : "border-ink/20 bg-white hover:border-ink"
                  }`}
                >
                  <p className="font-mono text-sm font-semibold text-leaf-deep">{c.symbol}</p>
                  <p className="font-semibold">{c.name}</p>
                  <p className="text-xs text-ink/60 mt-1">{c.blurb}</p>
                </button>
              ))}
            </div>
          </section>
        )}

        {step === 2 && (
          <section className="space-y-4">
            <h1 className="font-display text-4xl font-semibold">How much to invest?</h1>
            <p className="text-ink/70">
              We buy fractional shares, so any amount works (${MIN_AMOUNT}–${MAX_AMOUNT}).
            </p>
            <div className="flex flex-wrap gap-3">
              {amounts.map((a) => (
                <button
                  key={a}
                  onClick={() => {
                    setAmount(a);
                    setCustomAmount("");
                  }}
                  className={`rounded-full border-[1.5px] px-6 py-3 font-mono font-semibold transition ${
                    !customAmount && amount === a
                      ? "border-leaf bg-leaf text-white"
                      : "border-ink/20 bg-white hover:border-ink"
                  }`}
                >
                  ${a}
                </button>
              ))}
              <input
                inputMode="decimal"
                placeholder="Custom"
                value={customAmount}
                onChange={(e) => setCustomAmount(e.target.value.replace(/[^\d.]/g, ""))}
                className="w-32 rounded-full border-[1.5px] border-ink/20 px-5 py-3 font-mono bg-white focus:border-leaf"
              />
            </div>
            {!amountValid && customAmount && (
              <p className="text-sm text-red-700">
                Amount must be between ${MIN_AMOUNT} and ${MAX_AMOUNT}.
              </p>
            )}
          </section>
        )}

        {step === 3 && (
          <section className="space-y-6">
            <h1 className="font-display text-4xl font-semibold">Make it theirs</h1>
            <div className="space-y-3">
              <label className="block">
                <span className="text-sm font-bold">Recipient&apos;s name</span>
                <input
                  value={recipientName}
                  onChange={(e) => setRecipientName(e.target.value)}
                  maxLength={60}
                  className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf"
                  placeholder="Maya"
                />
              </label>
              {deliveryMethod === "Email" && (
                <label className="block">
                  <span className="text-sm font-bold">Recipient&apos;s email</span>
                  <input
                    type="email"
                    value={recipientEmail}
                    onChange={(e) => setRecipientEmail(e.target.value)}
                    className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf"
                    placeholder="maya@example.com"
                  />
                  <span className="text-xs text-ink/50">
                    Their redemption code goes straight here — double-check it&apos;s correct.
                  </span>
                </label>
              )}
              <label className="block">
                <span className="text-sm font-bold">
                  Gift message {deliveryMethod === "Physical" ? "(printed on the ticket)" : "(included in the email)"}
                </span>
                <textarea
                  value={message}
                  onChange={(e) => setMessage(e.target.value)}
                  maxLength={140}
                  rows={2}
                  className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf"
                  placeholder="Happy birthday — you own a piece of Mario now."
                />
              </label>
            </div>

            {deliveryMethod === "Physical" && (
              <div className="grid gap-3 sm:grid-cols-3">
                {boxes.map((b) => (
                  <button
                    key={b.sku}
                    onClick={() => setBox(b)}
                    className={`rounded-2xl border-[1.5px] p-4 text-left transition ${
                      box.sku === b.sku ? "border-leaf bg-mint/50" : "border-ink/20 bg-white hover:border-ink"
                    }`}
                  >
                    <p className="font-display text-xl">{b.name}</p>
                    <p className="text-xs text-ink/60 mb-2">{b.desc}</p>
                    <p className="font-mono font-semibold">+${b.price}</p>
                  </button>
                ))}
              </div>
            )}
          </section>
        )}

        {step === 4 && (
          <section className="space-y-5">
            <h1 className="font-display text-4xl font-semibold">Review</h1>
            <dl className="rounded-2xl border-[1.5px] border-ink bg-white divide-y divide-ink/10">
              {[
                ["Shares", `$${effectiveAmount.toFixed(2)} of ${selected?.symbol ?? "—"}`],
                ...(deliveryMethod === "Physical" ? [["Gift box", `${box.name} (+$${box.price})`]] : []),
                [deliveryMethod === "Physical" ? "Postage" : "Service fee", `$${feeAmount.toFixed(2)}`],
                ["Total", `$${displayTotal.toFixed(2)} AUD`],
              ].map(([k, v]) => (
                <div key={k} className="flex justify-between px-5 py-3 text-sm">
                  <dt className="font-bold">{k}</dt>
                  <dd className="font-mono">{v}</dd>
                </div>
              ))}
            </dl>

            <button
              onClick={handleAddToCart}
              disabled={
                !selected || !recipientName || !amountValid || (deliveryMethod === "Email" && !recipientEmailValid)
              }
              className="rounded-full bg-leaf text-white px-8 py-3 font-semibold hover:bg-leaf-deep disabled:opacity-40 transition-colors"
            >
              Add to cart
            </button>
            <p className="text-xs text-ink/50 max-w-md">
              You&apos;re buying a gift, not opening a brokerage account. Your gift locks in a dollar amount of
              the chosen company — the shares are purchased at the moment your recipient redeems, straight
              into an account in their own name.
            </p>
          </section>
        )}

        <div className="flex justify-between mt-10">
          <button
            onClick={() => setStep((s) => Math.max(0, s - 1) as Step)}
            disabled={step === 0}
            className="rounded-full border-[1.5px] border-ink px-6 py-2.5 font-semibold disabled:opacity-30 hover:bg-mint transition"
          >
            Back
          </button>
          {step < 4 && (
            <button
              onClick={() => setStep((s) => Math.min(4, s + 1) as Step)}
              disabled={!canContinueFromStep[step]}
              className="rounded-full bg-ink text-canvas px-6 py-2.5 font-semibold disabled:opacity-30 hover:bg-leaf-deep transition"
            >
              Continue
            </button>
          )}
        </div>
      </div>

      <aside className="lg:sticky lg:top-24 h-fit">
        <p className="text-xs font-bold uppercase tracking-[0.2em] text-ink/50 mb-3">Live preview</p>
        <Ticket
          symbol={selected?.symbol ?? "—"}
          companyName={selected?.name ?? "Pick a company"}
          recipientName={recipientName || "Your recipient"}
          message={message || undefined}
          code="GG-••••-••••-••••"
        />
      </aside>
    </div>
  );
}
