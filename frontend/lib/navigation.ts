// Which menu a given session sees, and which routes a child may open.
// Pure data/logic (no React) so it is unit-tested directly and shared by the
// desktop and mobile menus — one place for labels, no duplicated strings.

export type NavLink = { href: string; label: string };

export const CHILD_MENU_LABEL = "My Portfolio";
export const CHILD_LOGIN_LABEL = "Child login";

// A child's login may only ever see their own portfolio area. Anything else
// is redirected to /kid client-side; the backend independently rejects the
// child role on every parent/buyer endpoint (this is only the UX layer).
export const CHILD_ALLOWED_PREFIXES = ["/kid", "/kid-login"];

export function isRouteAllowedForRole(role: string | null, pathname: string): boolean {
  if (role !== "child") return true;
  return CHILD_ALLOWED_PREFIXES.some((p) => pathname === p || pathname.startsWith(p + "/"));
}

export type NavModel = {
  links: NavLink[];
  signedIn: boolean;
  showCart: boolean;
  showGiveCta: boolean;
};

export function navFor(role: string | null): NavModel {
  if (role === "child") {
    return {
      links: [{ href: "/kid", label: CHILD_MENU_LABEL }],
      signedIn: true,
      showCart: false,
      showGiveCta: false,
    };
  }
  const common: NavLink[] = [
    { href: "/collections", label: "Collections" },
    { href: "/redeem", label: "Redeem a gift" },
  ];
  if (role) {
    return {
      links: [
        ...common,
        { href: "/trade", label: "Buy / Sell" },
        { href: "/family", label: "My family" },
        { href: "/profile", label: "My profile" },
      ],
      signedIn: true,
      showCart: true,
      showGiveCta: true,
    };
  }
  return {
    links: [
      ...common,
      { href: "/kid-login", label: CHILD_LOGIN_LABEL },
      { href: "/login", label: "Log in" },
      { href: "/signup", label: "Sign up" },
    ],
    signedIn: false,
    showCart: true,
    showGiveCta: true,
  };
}
