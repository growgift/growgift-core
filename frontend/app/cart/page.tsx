"use client";

import { Suspense, useEffect, useState } from "react";
import Link from "next/link";
import { api, ApiError, type ShippingAddress } from "@/lib/api";
import { parseSession } from "@/lib/auth";
import { useCart, removeFromCart, removeManyFromCart, itemTotal, type CartItem } from "@/lib/cart";
import { AddressFields, emptyAddress, isAddressComplete } from "@/components/AddressFields";

export default function CartPage() {
  return (
    <Suspense>
      <Cart />
    </Suspense>
  );
}

function Cart() {
  const items = useCart();
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [session, setSession] = useState(() => parseSession());
  const [address, setAddress] = useState<ShippingAddress | null>(null);
  const [addressDraft, setAddressDraft] = useState(emptyAddress());
  const [editingAddress, setEditingAddress] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // New items default to selected (e.g. just added from the wizard, or
  // from another tab); anything already explicitly deselected or removed
  // stays that way — a removed item is simply no longer in `items` at all,
  // so it can't be re-added here by definition.
  useEffect(() => {
    setSelected((prev) => {
      const next = new Set(prev);
      for (const item of items) if (!prev.has(item.id)) next.add(item.id);
      return next;
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [items.length]);

  const selectedItems = items.filter((i) => selected.has(i.id));
  const needsAddress = selectedItems.some((i) => i.deliveryMethod === "Physical");
  const total = selectedItems.reduce((sum, i) => sum + itemTotal(i), 0);

  useEffect(() => {
    if (!session || !needsAddress) return;
    api
      .me()
      .then((me) => {
        setAddress(me.shippingAddress);
        if (me.shippingAddress) setAddressDraft(me.shippingAddress);
        else setEditingAddress(true);
      })
      .catch(() => setEditingAddress(true));
  }, [session, needsAddress]);

  function toggle(id: string) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  function remove(id: string) {
    removeFromCart(id);
    setSelected((prev) => {
      const next = new Set(prev);
      next.delete(id);
      return next;
    });
  }

  async function saveAddress() {
    try {
      await api.updateShippingAddress(addressDraft);
      setAddress(addressDraft);
      setEditingAddress(false);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Couldn't save that address — try again.");
    }
  }

  async function checkout() {
    setBusy(true);
    setError(null);
    try {
      // Each cart item becomes a real backend order here, for the first
      // time — everything up to this point (the cart itself) has been
      // purely client-side. Sequential rather than parallel so a failure
      // partway through (e.g. one item's amount somehow falls outside the
      // server's allowed range) stops cleanly rather than leaving some
      // orders created and others not with no clear indication of which.
      const orderIds: string[] = [];
      for (const item of selectedItems) {
        const order = await api.createGiftOrder({
          symbol: item.symbol,
          amount: item.amount,
          currency: item.currency,
          recipientName: item.recipientName,
          message: item.message,
          deliveryMethod: item.deliveryMethod,
          boxSku: item.boxSku ?? undefined,
          recipientEmail: item.recipientEmail || undefined,
        });
        orderIds.push(order.id);
      }
      const checkoutUrl = await api.createMultiOrderCheckoutSession(orderIds);
      removeManyFromCart(selectedItems.map((i) => i.id));
      window.location.href = checkoutUrl;
      // No setBusy(false) on the happy path — navigating away.
    } catch (e) {
      if (e instanceof ApiError && e.message.toLowerCase().includes("shipping address")) {
        setEditingAddress(true);
        setError("Add a shipping address below so we know where to send the box.");
      } else {
        setError(e instanceof ApiError ? e.message : "Something went wrong. Nothing was charged.");
      }
      setBusy(false);
    }
  }

  if (items.length === 0) {
    return (
      <div className="mx-auto max-w-lg px-5 py-24 text-center space-y-4">
        <span className="text-6xl">🛒</span>
        <h1 className="font-display text-4xl font-semibold">Your cart is empty</h1>
        <Link
          href="/gift/new"
          className="inline-block rounded-full bg-leaf text-white px-8 py-3 font-semibold hover:bg-leaf-deep transition"
        >
          Build a gift
        </Link>
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-3xl px-5 py-14 space-y-8">
      <h1 className="font-display text-4xl font-semibold">Your cart</h1>

      <div className="space-y-4">
        {items.map((item) => (
          <CartRow
            key={item.id}
            item={item}
            checked={selected.has(item.id)}
            onToggle={() => toggle(item.id)}
            onRemove={() => remove(item.id)}
          />
        ))}
      </div>

      <div className="rounded-2xl border-[1.5px] border-ink bg-white p-5 space-y-4">
        <div className="flex justify-between text-sm">
          <span className="font-bold">{selectedItems.length} item(s) selected</span>
          <span className="font-mono font-semibold">${total.toFixed(2)} AUD</span>
        </div>

        {needsAddress && session && (
          <div className="space-y-3 border-t border-ink/10 pt-4">
            <div className="flex items-center justify-between">
              <p className="text-xs font-bold uppercase tracking-[0.15em] text-ink/50">Shipping to</p>
              {address && !editingAddress && (
                <button
                  onClick={() => setEditingAddress(true)}
                  className="text-xs font-bold text-leaf-deep hover:underline"
                >
                  Change
                </button>
              )}
            </div>
            {!editingAddress && address && (
              <p className="text-sm">
                {address.fullName}
                <br />
                {address.line1}
                {address.line2 ? `, ${address.line2}` : ""}
                <br />
                {address.city} {address.region} {address.postalCode}
                <br />
                {address.countryCode}
              </p>
            )}
            {editingAddress && (
              <div className="space-y-4">
                <AddressFields value={addressDraft} onChange={setAddressDraft} />
                <div className="flex gap-3">
                  <button
                    onClick={saveAddress}
                    disabled={!isAddressComplete(addressDraft)}
                    className="rounded-full bg-ink text-canvas px-5 py-2 text-sm font-semibold disabled:opacity-40 hover:bg-leaf-deep transition"
                  >
                    Save address
                  </button>
                  {address && (
                    <button
                      onClick={() => {
                        setAddressDraft(address);
                        setEditingAddress(false);
                      }}
                      className="rounded-full border-[1.5px] border-ink px-5 py-2 text-sm font-semibold hover:bg-mint transition"
                    >
                      Cancel
                    </button>
                  )}
                </div>
              </div>
            )}
          </div>
        )}

        {error && <p className="text-sm text-red-700">{error}</p>}

        {!session ? (
          <div className="space-y-3 border-t border-ink/10 pt-4">
            <p className="font-semibold">Sign in to check out</p>
            <p className="text-sm text-ink/70">Your cart stays exactly as it is — sign in and you&apos;ll land right back here.</p>
            <div className="flex gap-3">
              <Link
                href="/login?then=/cart"
                className="rounded-full bg-ink text-canvas px-6 py-2.5 font-semibold hover:bg-leaf-deep transition"
              >
                Sign in
              </Link>
              <Link
                href="/signup?persona=buyer&then=/cart"
                className="rounded-full border-[1.5px] border-ink px-6 py-2.5 font-semibold hover:bg-mint transition"
              >
                Create an account
              </Link>
            </div>
          </div>
        ) : (
          <button
            onClick={checkout}
            disabled={
              busy ||
              selectedItems.length === 0 ||
              (needsAddress && (!address || editingAddress))
            }
            className="w-full rounded-full bg-leaf text-white py-3 font-semibold hover:bg-leaf-deep disabled:opacity-40 transition-colors"
          >
            {busy ? "Taking you to secure payment…" : `Pay securely with Stripe — $${total.toFixed(2)}`}
          </button>
        )}
      </div>
    </div>
  );
}

function CartRow({
  item,
  checked,
  onToggle,
  onRemove,
}: {
  item: CartItem;
  checked: boolean;
  onToggle: () => void;
  onRemove: () => void;
}) {
  return (
    <div className="flex items-start gap-4 rounded-2xl border-[1.5px] border-ink bg-white p-5">
      <input type="checkbox" checked={checked} onChange={onToggle} className="mt-1.5 h-4 w-4" />
      <div className="flex-1">
        <p className="font-semibold">
          ${item.amount.toFixed(2)} {item.currency} of {item.symbol}
          <span className="font-normal text-ink/60"> — {item.companyName}</span>
        </p>
        <p className="text-sm text-ink/60">For {item.recipientName || "someone special"}</p>
        <p className="text-xs text-ink/50 mt-1">
          {item.deliveryMethod === "Physical"
            ? `${item.boxName} gift box · mailed · +$${item.boxPrice} box +$${item.postalOrServiceFee} postage`
            : `Emailed to ${item.recipientEmail} · +$${item.postalOrServiceFee} service fee`}
        </p>
      </div>
      <div className="text-right space-y-2">
        <p className="font-mono font-semibold">${itemTotal(item).toFixed(2)}</p>
        <button onClick={onRemove} className="text-xs font-bold text-red-700 hover:underline">
          Remove
        </button>
      </div>
    </div>
  );
}
