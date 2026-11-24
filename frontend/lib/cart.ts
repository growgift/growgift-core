"use client";

import { useEffect, useState } from "react";
import type { DeliveryMethod } from "./api";

export type CartItem = {
  id: string; // client-generated, identifies this cart entry only (not a backend order id — none exists yet)
  deliveryMethod: DeliveryMethod;
  symbol: string;
  companyName: string;
  amount: number;
  currency: string;
  recipientName: string;
  recipientEmail: string;
  message: string;
  boxSku: string | null;
  boxName: string | null;
  boxPrice: number;
  postalOrServiceFee: number;
  addedAtUtc: string;
};

const CART_KEY = "growgift_cart_items";
const CART_EVENT = "growgift:cart-changed";

function read(): CartItem[] {
  if (typeof window === "undefined") return [];
  try {
    const raw = localStorage.getItem(CART_KEY);
    return raw ? JSON.parse(raw) : [];
  } catch {
    return []; // corrupt/unavailable storage — treat as empty rather than crash
  }
}

function write(items: CartItem[]) {
  try {
    localStorage.setItem(CART_KEY, JSON.stringify(items));
  } catch {
    // Storage full/disabled — the mutation still applies to this session's
    // in-memory state (components using useCart() re-render from the event
    // below regardless), it just won't survive a reload. Not worth
    // surfacing an error for a cart add/remove.
  }
  // Same-tab components (nav badge, cart page) don't see the browser's own
  // "storage" event, which only fires for OTHER tabs — this is what lets
  // everything in this tab stay in sync with each other.
  window.dispatchEvent(new Event(CART_EVENT));
}

export function getCart(): CartItem[] {
  return read();
}

export function addToCart(item: Omit<CartItem, "id" | "addedAtUtc">) {
  const items = read();
  items.push({ ...item, id: crypto.randomUUID(), addedAtUtc: new Date().toISOString() });
  write(items);
}

export function removeFromCart(id: string) {
  write(read().filter((i) => i.id !== id));
}

export function removeManyFromCart(ids: string[]) {
  const idSet = new Set(ids);
  write(read().filter((i) => !idSet.has(i.id)));
}

export function clearCart() {
  write([]);
}

/** Reactive cart contents — updates whenever any component in this tab mutates the cart. */
export function useCart(): CartItem[] {
  const [items, setItems] = useState<CartItem[]>([]);

  useEffect(() => {
    setItems(read());
    const onChange = () => setItems(read());
    window.addEventListener(CART_EVENT, onChange);
    window.addEventListener("storage", onChange); // keeps other tabs in sync too
    return () => {
      window.removeEventListener(CART_EVENT, onChange);
      window.removeEventListener("storage", onChange);
    };
  }, []);

  return items;
}

export function itemTotal(item: CartItem): number {
  return item.amount + item.boxPrice + item.postalOrServiceFee;
}
