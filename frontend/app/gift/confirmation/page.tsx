"use client";

import { Suspense, useEffect, useRef, useState } from "react";
import { useSearchParams } from "next/navigation";
import Link from "next/link";
import QRCode from "qrcode";
import { api } from "@/lib/api";
import { confirmationFor } from "@/lib/giftStatus";

export default function ConfirmationPage() {
  return (
    <Suspense>
      <Confirmation />
    </Suspense>
  );
}

type OrderState = { status: string | null; code: string | null; qrDataUrl: string | null };

function Confirmation() {
  const params = useSearchParams();
  const sessionId = params.get("session_id");

  // Supports both a single-item buy (?order_id=...) and a cart checkout
  // covering several orders in one combined payment (?order_ids=a,b,c —
  // see CreateMultiOrderCheckoutSessionCommand's SuccessUrl). Previously
  // this only ever read order_id (singular), so landing here from a cart
  // checkout meant the polling effect below never even started — status
  // stayed null forever, which read as "Confirming your payment…"
  // indefinitely even though the order had actually gone through and the
  // code was already visible on the profile page.
  const orderIds = (() => {
    const single = params.get("order_id");
    if (single) return [single];
    const multi = params.get("order_ids");
    return multi ? multi.split(",").filter(Boolean) : [];
  })();

  const [orders, setOrders] = useState<Record<string, OrderState>>(() =>
    Object.fromEntries(orderIds.map((id) => [id, { status: null, code: null, qrDataUrl: null }])),
  );
  const [timedOut, setTimedOut] = useState(false);
  // Result of asking Stripe (via our backend) whether the session was paid.
  // "unpaid" = Stripe says no payment was completed; "unreachable" = we
  // couldn't ask. Either one means we must NOT claim the payment went through.
  const [paymentCheck, setPaymentCheck] = useState<"checking" | "paid" | "unpaid" | "unreachable">("checking");

  // The code isn't generated synchronously at checkout — it's issued once
  // Stripe's webhook confirms payment (see GiftOrderPaidConsumer), which
  // can trail the buyer's redirect back here by a few seconds, or — if the
  // webhook never arrives at all (e.g. a local dev listener that wasn't
  // running) — never happen. Tracking each order's actual status, not just
  // whether a code has shown up yet, is what lets the messaging below stay
  // honest about which of those is actually happening.
  const paymentCheckRef = useRef<"checking" | "paid" | "unpaid" | "unreachable">("checking");
  useEffect(() => {
    if (orderIds.length === 0) return;
    let cancelled = false;
    let attempts = 0;
    const maxAttempts = 30; // ~60s at 2s intervals — generous for a webhook round-trip

    async function poll() {
      if (cancelled) return;
      let anyStillPending = false;

      // Ask Stripe directly (via our backend) until it says "paid" — so we
      // don't depend on the webhook alone. If the webhook listener isn't
      // running this is what actually marks the orders paid.
      if (sessionId && paymentCheckRef.current !== "paid" && paymentCheckRef.current !== "unpaid") {
        try {
          const r = await api.syncPayment(sessionId, orderIds);
          if (cancelled) return;
          const next = r.paid ? "paid" : "unpaid";
          paymentCheckRef.current = next;
          setPaymentCheck(next);
        } catch {
          if (cancelled) return;
          paymentCheckRef.current = "unreachable";
          setPaymentCheck("unreachable");
        }
      }

      await Promise.all(
        orderIds.map(async (id) => {
          // Stop polling this one specific order once it has a code — but
          // keep polling any others in the same cart checkout that aren't
          // ready yet, rather than treating the whole batch as one unit.
          const alreadyDone = orders[id]?.code;
          if (alreadyDone) return;
          try {
            const result = await api.getRevealedCode(id);
            if (cancelled) return;
            setOrders((prev) => ({ ...prev, [id]: { ...prev[id], status: result.status, code: result.code } }));
            if (!result.code) anyStillPending = true;
          } catch {
            // Transient (order not committed yet, brief network hiccup) —
            // keep trying rather than surfacing an error for something
            // that resolves itself within a few more seconds.
            anyStillPending = true;
          }
        }),
      );

      if (cancelled) return;
      attempts += 1;
      if (paymentCheckRef.current === "unpaid") {
        setTimedOut(true);
        return;
      }
      if (anyStillPending && attempts < maxAttempts) {
        setTimeout(poll, 2000);
      } else if (anyStillPending) {
        setTimedOut(true);
      }
    }

    poll();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [orderIds.join(",")]);

  // One QR per order, generated as each order's code arrives.
  useEffect(() => {
    for (const [id, state] of Object.entries(orders)) {
      if (state.code && !state.qrDataUrl) {
        QRCode.toDataURL(`${window.location.origin}/redeem?code=${encodeURIComponent(state.code)}`, {
          width: 240,
          margin: 1,
        })
          .then((url) => setOrders((prev) => ({ ...prev, [id]: { ...prev[id], qrDataUrl: url } })))
          .catch(() => {}); // non-fatal — the plaintext code alone is still fully usable without the QR
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [orders]);

  const states = Object.entries(orders);
  const readyCodes = states.filter(([, s]) => s.code);
  const stillPending = states.filter(([, s]) => !s.code);
  // Genuinely still unconfirmed on our end for at least one order —
  // AwaitingPayment means the webhook that would move it to Paid hasn't
  // been processed (or never arrived at all). "Payment received" would be
  // a claim this system can't actually back up in that state, whether or
  // not Stripe itself processed the charge.
  const anyNotYetConfirmed = stillPending.some(([, s]) => s.status === null || s.status === "AwaitingPayment");

  // Wording follows what the SERVER says about each order, never the mere fact that we landed on this URL.
  const view = confirmationFor(states.map(([, s]) => s.status), paymentCheck);

  return (
    <div className="mx-auto max-w-xl px-5 py-24 text-center space-y-6">
      <span className="text-6xl">🎁</span>
      <h1 className="font-display text-5xl font-semibold">
        {view.headline}
      </h1>

      {view.celebrate && (
        <p className="text-ink/70 max-w-md mx-auto">
          Thank you for giving a gift that can grow over time. Your purchase was successful. You can follow its status
          in My Profile, and share the code below with your recipient so they can redeem it.
        </p>
      )}

      {readyCodes.map(([id, state]) => (
        <div key={id} className="ticket p-8 space-y-4">
          <p className="text-sm font-bold text-ink/60">Your redemption code</p>
          <p className="font-mono text-3xl tracking-widest">{state.code}</p>
          {state.qrDataUrl && (
            // eslint-disable-next-line @next/next/no-img-element -- a short-lived data: URI, not worth Next's remote-image pipeline
            <img
              src={state.qrDataUrl}
              alt="Redemption QR code"
              className="mx-auto rounded-xl border-[1.5px] border-ink/10"
            />
          )}
          <p className="text-sm text-ink/70">
            Share this with your recipient — scanning the QR (or typing the code at growgift.com.au/redeem) is all they
            need to claim their shares. If you chose a gift box, it will arrive separately; you can follow it in My Profile.
          </p>
        </div>
      ))}

      {stillPending.length > 0 && paymentCheck === "unpaid" && (
        <div className="space-y-3">
          <p className="text-red-700 max-w-md mx-auto font-semibold">
            Stripe has no completed payment for this checkout, so no gift has been created or sent and you
            haven&apos;t been charged.
          </p>
          <Link
            href="/cart"
            className="inline-block rounded-full bg-ink text-canvas px-6 py-2.5 font-semibold hover:bg-leaf-deep transition"
          >
            Return to cart and try again
          </Link>
        </div>
      )}

      {stillPending.length > 0 &&
        paymentCheck !== "unpaid" &&
        (timedOut && anyNotYetConfirmed ? (
          <div className="space-y-2">
            <p className="text-ink/70 max-w-md mx-auto">
              We haven&apos;t been able to confirm {stillPending.length > 1 ? "some of your payments" : "your payment"}{" "}
              yet{paymentCheck === "unreachable" ? " — we couldn't reach our payment provider to check" : ""}.
              Nothing has been sent. If your card was charged, this usually resolves on its own within a few minutes.
            </p>
            <p className="text-ink/70 max-w-md mx-auto">
              Check your{" "}
              <Link href="/profile" className="font-semibold text-leaf-deep hover:underline">
                profile page
              </Link>{" "}
              in a few minutes. If it still shows as awaiting payment after that, or you&apos;re unsure whether you
              were charged, check your bank/card statement or contact support with the order ID(s) below.
            </p>
          </div>
        ) : timedOut ? (
          <div className="space-y-2">
            <p className="text-ink/70 max-w-md mx-auto">
              Payment is confirmed, but generating {stillPending.length > 1 ? "some codes" : "the code"} is taking
              longer than usual. {stillPending.length > 1 ? "They" : "It"}&apos;ll appear on your{" "}
              <Link href="/profile" className="font-semibold text-leaf-deep hover:underline">
                profile page
              </Link>{" "}
              as soon as ready — no need to keep waiting here.
            </p>
          </div>
        ) : (
          <p className="text-ink/70 max-w-md mx-auto">
            {anyNotYetConfirmed
              ? "Confirming your payment now — this usually takes just a few seconds…"
              : "Generating your redemption code now, this usually takes a few seconds…"}
          </p>
        ))}

      {orderIds.length > 0 && (
        <p className="text-xs text-ink/40 font-mono break-all">
          order{orderIds.length > 1 ? "s" : ""} {orderIds.join(", ")}
          {sessionId ? ` · stripe session ${sessionId.slice(0, 20)}…` : ""}
        </p>
      )}

      <div className="flex flex-wrap justify-center gap-4">
        <Link
          href="/profile"
          className="rounded-full bg-leaf text-white px-6 py-3 font-semibold hover:bg-leaf-deep transition"
        >
          View in My Profile
        </Link>
        <Link
          href="/gift/new"
          className="rounded-full border-[1.5px] border-ink px-6 py-3 font-semibold hover:bg-mint transition"
        >
          Send another gift
        </Link>
        <Link
          href="/"
          className="rounded-full border-[1.5px] border-ink px-6 py-3 font-semibold hover:bg-mint transition"
        >
          Back home
        </Link>
      </div>
    </div>
  );
}
