"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { api } from "@/lib/api";
import { parseSession, type Session } from "@/lib/auth";
import { isRouteAllowedForRole, navFor } from "@/lib/navigation";
import { CartBadge } from "@/components/CartBadge";

export function SiteNav() {
  const router = useRouter();
  const pathname = usePathname();
  const [session, setSession] = useState<Session | null>(null);
  const [mounted, setMounted] = useState(false);
  const [open, setOpen] = useState(false);

  const refresh = useCallback(() => setSession(parseSession()), []);

  // Re-read on mount, on every route change, whenever a login/logout fires
  // "gg-auth-changed", and when another tab changes the token. (The header
  // lives in the root layout and is not re-mounted on client navigation.)
  useEffect(() => {
    refresh();
    setMounted(true);
    setOpen(false);
  }, [pathname, refresh]);

  useEffect(() => {
    window.addEventListener("gg-auth-changed", refresh);
    window.addEventListener("storage", refresh);
    return () => {
      window.removeEventListener("gg-auth-changed", refresh);
      window.removeEventListener("storage", refresh);
    };
  }, [refresh]);

  const role = session ? session.role || "user" : null;
  const isChild = role === "child";

  // Typing a URL doesn't get a child around the menu.
  useEffect(() => {
    if (!isRouteAllowedForRole(role, pathname)) router.replace("/kid");
  }, [role, pathname, router]);

  function signOut() {
    api.logout();
    setSession(null);
    setOpen(false);
    router.push(isChild ? "/kid-login" : "/");
  }

  if (!mounted) return <span className="w-24" aria-hidden="true" />; // avoid hydration mismatch

  const nav = navFor(role);

  const linkEls = nav.links.map((l) => (
    <Link key={l.href} href={l.href} className="hover:text-leaf-deep">
      {l.label}
    </Link>
  ));
  const signOutBtn = nav.signedIn ? (
    <button onClick={signOut} className="hover:text-leaf-deep font-semibold text-left">
      Sign out
    </button>
  ) : null;
  const giveCta = nav.showGiveCta ? (
    <Link
      href="/gift/new"
      className="rounded-full bg-ink text-canvas px-4 py-2 hover:bg-leaf-deep transition-colors text-center"
    >
      Give a share
    </Link>
  ) : null;

  return (
    <>
      {/* Desktop */}
      <nav className="hidden md:flex items-center gap-6 text-sm font-semibold" aria-label="Main">
        {linkEls}
        {signOutBtn}
        {nav.showCart && <CartBadge />}
        {giveCta}
      </nav>

      {/* Mobile: cart + hamburger, links in a dropdown */}
      <div className="md:hidden flex items-center gap-4 text-sm font-semibold">
        {nav.showCart && <CartBadge />}
        <button
          onClick={() => setOpen((o) => !o)}
          aria-expanded={open}
          aria-controls="mobile-menu"
          aria-label={open ? "Close menu" : "Open menu"}
          className="rounded-lg border-[1.5px] border-ink/30 px-3 py-1.5"
        >
          {open ? "✕" : "☰"}
        </button>
      </div>
      {open && (
        <nav
          id="mobile-menu"
          aria-label="Main (mobile)"
          className="md:hidden absolute left-0 right-0 top-16 border-b-[1.5px] border-ink/15 bg-canvas px-5 py-4 flex flex-col gap-4 text-sm font-semibold shadow-card"
        >
          {linkEls}
          {signOutBtn}
          {giveCta}
        </nav>
      )}
    </>
  );
}
