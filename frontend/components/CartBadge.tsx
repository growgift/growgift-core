"use client";

import Link from "next/link";
import { useCart } from "@/lib/cart";

export function CartBadge() {
  const items = useCart();

  return (
    <Link href="/cart" className="relative hover:text-leaf-deep" aria-label={`Cart, ${items.length} item${items.length === 1 ? "" : "s"}`}>
      <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="9" cy="21" r="1.5" />
        <circle cx="19" cy="21" r="1.5" />
        <path d="M2 3h2l2.4 12.4a2 2 0 0 0 2 1.6h8.6a2 2 0 0 0 2-1.6L21 7H6" />
      </svg>
      {items.length > 0 && (
        <span className="absolute -top-2 -right-2 flex h-5 min-w-5 items-center justify-center rounded-full bg-ribbon px-1 text-[11px] font-bold text-white">
          {items.length}
        </span>
      )}
    </Link>
  );
}
