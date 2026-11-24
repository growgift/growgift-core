import { clearPending } from "@/lib/redemption";
import type { OrderResult } from "@/lib/trading";

// Typed client for the GrowGift API gateway.
// All financial POSTs send an Idempotency-Key so retries are safe.

export type Collection = {
  id: string;
  slug: string;
  name: string;
  tagline: string;
  emoji: string;
  tickers: { symbol: string; name: string }[];
};

export type DeliveryMethod = "Physical" | "Email";

export type GiftOrder = {
  id: string;
  status:
    | "Draft"
    | "AwaitingPayment"
    | "Paid"
    | "SharesAcquired"
    | "CodeIssued"
    | "Shipped"
    | "Redeemed"
    | "Expired"
    | "Cancelled"
    | "Refunded";
  symbol: string;
  amount: { amount: number; currency: string };
  recipientName: string | null;
  deliveryMethod: DeliveryMethod;
  boxSku: string | null;
  boxPrice: number | null;
  postalFee: number | null;
  serviceFee: number | null;
  totalAmount: number;
};

export type BuyerOrderSummary = {
  id: string;
  symbol: string;
  amount: number;
  currency: string;
  recipientName: string | null;
  status: string;
  hasPhysicalBox: boolean;
  boxSku: string | null;
  deliveryMethod: DeliveryMethod;
  totalAmount: number;
  createdAtUtc: string;
  code: string | null;
};

export type ShippingAddress = {
  fullName: string;
  line1: string;
  line2?: string | null;
  city: string;
  region: string;
  postalCode: string;
  countryCode: string;
  phone?: string | null;
};

export type Me = {
  id: string;
  email: string;
  role: string;
  kycStatus: string;
  emailVerified: boolean;
  shippingAddress: ShippingAddress | null;
};

export type JarKind = "ShortTerm" | "MediumTerm" | "LongTerm";

export type Jar = {
  kind: JarKind;
  balance: number;
  lockYears: number;
};

const base = process.env.NEXT_PUBLIC_API_BASE ?? "/api/v1";

// Access token lives in memory (and sessionStorage for reload survival).
// Production hardening: swap for httpOnly cookies set by a BFF route.
let accessToken: string | null = null;

export function setToken(token: string | null) {
  accessToken = token;
  if (typeof window !== "undefined") {
    if (token) sessionStorage.setItem("gg_token", token);
    else sessionStorage.removeItem("gg_token");
    // The header lives in the root layout and is NOT re-mounted on client-side
    // navigation, so it can't notice a login/logout by itself. Broadcasting
    // here is what makes the menu switch between "Log in / Sign up" and
    // "My family / My profile" the moment the token changes.
    window.dispatchEvent(new Event("gg-auth-changed"));
  }
}

export function getToken(): string | null {
  if (accessToken) return accessToken;
  if (typeof window !== "undefined") accessToken = sessionStorage.getItem("gg_token");
  return accessToken;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = getToken();
  const res = await fetch(`${base}${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...(init?.headers ?? {}),
    },
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new ApiError(res.status, body?.error?.code ?? "unknown", body?.error?.message ?? res.statusText);
  }
  return res.json() as Promise<T>;
}

export class ApiError extends Error {
  constructor(
    public status: number,
    public code: string,
    message: string,
  ) {
    super(message);
  }
}

export type Country = { id: number; code: string; name: string };

export const api = {
  // ----- Brokerage onboarding (ViewTrade) -----
  getCountries() {
    return request<Country[]>("/onboarding/countries");
  },

  getOnboarding() {
    return request<{
      status: "InProgress" | "Submitting" | "Pending" | "Approved" | "Rejected" | "Failed";
      answers: Record<string, string>;
      multiAnswers: Record<string, string[]>;
      documentsProvided: string[];
      rejectionReason: string | null;
      accountRef: string | null;
    }>("/onboarding/");
  },

  saveOnboardingAnswers(answers: { systemIdentifier: string; value?: string; values?: string[] }[]) {
    return request<{ message: string; saved: number }>("/onboarding/answers", {
      method: "PUT",
      body: JSON.stringify(answers),
    });
  },

  async uploadOnboardingDocument(systemIdentifier: string, file: File) {
    const token = getToken();
    const form = new FormData();
    form.append("file", file);
    const base = process.env.NEXT_PUBLIC_API_BASE ?? "/api/v1";
    const res = await fetch(`${base}/onboarding/documents/${encodeURIComponent(systemIdentifier)}`, {
      method: "POST",
      headers: token ? { Authorization: `Bearer ${token}` } : {},
      body: form,
    });
    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      throw new ApiError(res.status, body?.error?.code ?? "unknown", body?.error?.message ?? res.statusText);
    }
    return res.json() as Promise<{ message: string; systemIdentifier: string }>;
  },

  submitOnboarding() {
    return request<{ status: string; accountRef: string; backgroundCheck?: string }>("/onboarding/submit", { method: "POST" });
  },


  createGiftOrder(input: {
    symbol: string;
    amount: number;
    currency: string;
    recipientName: string;
    message: string;
    deliveryMethod: DeliveryMethod;
    boxSku?: string;
    recipientEmail?: string;
  }) {
    return request<GiftOrder>("/orders", {
      method: "POST",
      headers: { "Idempotency-Key": crypto.randomUUID() },
      body: JSON.stringify({
        symbol: input.symbol,
        amount: input.amount,
        currency: input.currency,
        recipientName: input.recipientName,
        greetingMessage: input.message || null,
        deliveryMethod: input.deliveryMethod,
        boxSku: input.deliveryMethod === "Physical" ? input.boxSku : null,
        recipientEmail: input.deliveryMethod === "Email" ? input.recipientEmail : null,
      }),
    });
  },

  // Returns a Stripe-hosted Checkout URL. Card, Apple Pay, Google Pay and
  // Link all appear automatically on supported browsers/devices — Stripe
  // decides eligibility, nothing extra to configure here.
  async createCheckoutSession(orderId: string) {
    const result = await request<{ checkoutUrl: string }>(
      `/orders/${orderId}/checkout-session`,
      { method: "POST" },
    );
    return result.checkoutUrl;
  },

  // Combines several already-created orders into one Stripe Checkout
  // Session — one card charge for the whole cart selection, not one per
  // item. Used by the cart page, never the single-item buy flow.
  async createMultiOrderCheckoutSession(orderIds: string[]) {
    const result = await request<{ checkoutUrl: string }>("/orders/checkout-session", {
      method: "POST",
      body: JSON.stringify({ orderIds }),
    });
    return result.checkoutUrl;
  },

  // Webhook fallback: asks our backend to check the Stripe Checkout Session
  // directly. Works even when the Stripe webhook never reaches us (e.g.
  // `stripe listen` isn't running locally).
  syncPayment(sessionId: string, orderIds: string[]) {
    return request<{ paid: boolean; paymentStatus?: string; sessionStatus?: string; confirmed?: string[] }>(
      "/orders/sync-payment",
      { method: "POST", body: JSON.stringify({ sessionId, orderIds }) },
    );
  },

  getRevealedCode(orderId: string) {
    return request<{ status: string; code: string | null }>(`/orders/${orderId}/code`);
  },

  previewGift(input: { code: string }) {
    return request<{
      symbol: string;
      amount: number;
      currency: string;
      boxSku: string | null;
      greetingMessage: string | null;
      recipientName: string;
      status: string;
    }>("/gifts/preview", {
      method: "POST",
      body: JSON.stringify(input),
    });
  },

  redeemGift(input: { code: string; beneficiaryChildId?: string }) {
    return request<{ giftId: string; symbol: string; amount: number; currency: string }>("/gifts/redeem", {
      method: "POST",
      headers: { "Idempotency-Key": crypto.randomUUID() },
      body: JSON.stringify(input),
    });
  },

  myOrders() {
    return request<BuyerOrderSummary[]>("/orders");
  },

  /** The buyer's own shipments, in customer-safe form (status, carrier, tracking; suburb/state/postcode only). */
  myShipments() {
    return request<{
      orderId: string;
      status: string;
      carrier?: string | null;
      trackingNumber?: string | null;
      trackingUrl?: string | null;
    }[]>("/shipments");
  },

  receivedGifts() {
    return request<{
      giftId: string;
      orderId: string;
      symbol: string;
      amount: number;
      currency: string;
      redeemedAtUtc: string;
    }[]>("/gifts/received");
  },

  portfolio() {
    return request<{
      hasAccount: boolean;
      accountRef?: string;
      simulated?: boolean;
      cashBalance: number;
      holdings: {
        symbol: string;
        quantity: number;
        marketValue: number | null;
        costBasis: number;
        avgPrice?: number | null;
        currency?: string;
        lastPrice?: number | null;
        changePercent?: number | null;
        unrealisedPnl?: number | null;
        unrealisedPnlPercent?: number | null;
      }[];
      recognised?: boolean;
      /** Development only: ViewTrade's raw reply when its shape wasn't recognised. */
      raw?: string | null;
    }>("/portfolio");
  },

  openOrders() {
    return request<{ hasAccount: boolean; recognised?: boolean; orders: import("./portfolio").OpenOrder[] }>("/trading/open-orders");
  },

  redemptions() {
    return request<import("./portfolio").Redemption[]>("/trading/redemptions");
  },

  retryRedemption(giftId: string, confirmNoOrder = false) {
    return request<{ giftId: string; state: string; orderRef: string }>(
      `/trading/redemptions/${giftId}/retry${confirmNoOrder ? "?confirmNoOrder=true" : ""}`,
      { method: "POST" },
    );
  },

  /** Development only (404 elsewhere): per-stage result of the ViewTrade sign-in chain. Contains no tokens. */
  diagnoseTrading() {
    return request<Record<string, unknown>>("/trading/diagnose");
  },

  tradingStatus() {
    return request<{ canTrade: boolean; reason: string | null; accountRef: string | null }>("/trading/status");
  },

  /**
   * Places a market order via the backend. Doesn't use request(): the order
   * endpoint answers 202/422/502 with a meaningful body ({state, message, ...})
   * that the page must show, and a bare "unknown error" there could make a
   * user resubmit an order that may in fact have been placed.
   */
  async placeOrder(
    input: { side: "BUY" | "SELL"; symbol: string; quantity: number; sellAll?: boolean },
    idempotencyKey: string,
  ): Promise<OrderResult> {
    const token = getToken();
    let res: Response;
    try {
      res = await fetch(`${base}/trading/orders`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Idempotency-Key": idempotencyKey,
          ...(token ? { Authorization: `Bearer ${token}` } : {}),
        },
        body: JSON.stringify(input),
      });
    } catch {
      // Network drop AFTER we may have sent it: say so, never "failed".
      return {
        state: "unconfirmed",
        message: "We couldn't confirm whether this order went through. Check your orders before trying again.",
      };
    }
    const body = await res.json().catch(() => ({}));
    if (body && typeof body.state === "string") return body as OrderResult;
    if (res.ok) return { state: "unconfirmed", message: "We couldn't read the brokerage's reply. Check your orders before trying again." };
    const message = body?.message ?? body?.error?.message ?? "We couldn't place this order.";
    // A 5xx that did NOT come from our own trading endpoint (no error code, e.g. a gateway timeout)
    // may have happened after the order was sent, so it must not read as "not placed".
    if (res.status >= 500 && !body?.code)
      return { state: "unconfirmed", message: "We couldn't confirm whether this order went through. Check your orders before trying again." };
    throw new ApiError(res.status, body?.code ?? body?.error?.code ?? "unknown", message);
  },

  async register(input: {
    email: string;
    password: string;
    role: "buyer" | "parent" | "recipient";
    shippingAddress?: ShippingAddress;
  }) {
    return request<{ userId: string; message: string; devVerificationUrl: string | null; emailSent?: boolean }>("/auth/register", {
      method: "POST",
      body: JSON.stringify(input),
    });
  },

  me() {
    return request<Me>("/auth/me");
  },

  async updateShippingAddress(address: ShippingAddress) {
    const result = await request<{ message: string; accessToken: string }>("/auth/me/shipping-address", {
      method: "PUT",
      body: JSON.stringify(address),
    });
    // Without this, the existing token keeps its stale has_shipping_address
    // claim until it naturally expires (up to 15 min) — long enough that
    // saving an address mid-checkout and immediately clicking pay would
    // still fail the order-creation check with the old, now-incorrect value.
    setToken(result.accessToken);
    return result;
  },

  async becomeParent() {
    const result = await request<{ accessToken: string }>("/auth/me/become-parent", { method: "POST" });
    // Same reasoning as updateShippingAddress above — the session needs a
    // fresh role=parent claim immediately, not whenever the old token
    // happens to expire, since the very next thing that happens is loading
    // the family dashboard with that new capability.
    setToken(result.accessToken);
    return result;
  },

  async login(input: { email: string; password: string }) {
    const result = await request<{ accessToken: string; expiresInSeconds: number }>("/auth/login", {
      method: "POST",
      body: JSON.stringify(input),
    });
    setToken(result.accessToken);
    return result;
  },

  // Drops the token only. Used when a signup starts the person on a clean
  // slate but must NOT lose the gift they're in the middle of redeeming.
  clearSession() {
    setToken(null);
  },

  logout() {
    setToken(null);
    clearPending(); // a pending gift redemption belongs to the session that started it
  },

  verifyEmail(token: string) {
    return request<{ message: string }>("/auth/verify-email", {
      method: "POST",
      body: JSON.stringify({ token }),
    });
  },

  /**
   * Asks the backend whether the signed-in user already has a fully onboarded
   * ViewTrade account (looked up by their VERIFIED e-mail server-side; nothing
   * identifying is sent from here). 503 = couldn't check — never "no account".
   */
  resolveExistingAccount() {
    return request<{ decision: string; accountRef?: string | null }>("/onboarding/resolve-existing", { method: "POST" });
  },

  // ---- Subscription (server-side Stripe; the browser only ever gets a Stripe-hosted URL) ----
  subscription() {
    return request<import("./billing").SubscriptionView>("/billing/subscription");
  },

  async startSubscriptionCheckout() {
    const result = await request<{ url: string }>("/billing/checkout", { method: "POST" });
    return result.url;
  },

  async openBillingPortal() {
    const result = await request<{ url: string }>("/billing/portal", { method: "POST" });
    return result.url;
  },

  /** Asks the server to re-read the subscription from Stripe (covers a slow webhook) and re-issues the token. */
  async syncSubscription() {
    const result = await request<{ accessToken: string }>("/billing/sync", { method: "POST" });
    setToken(result.accessToken);
    return result;
  },

  /** Re-issues the token from current server state (picks up new kyc / account claims). */
  async refreshSession() {
    const result = await request<{ accessToken: string }>("/auth/me/refresh", { method: "POST" });
    setToken(result.accessToken);
    return result;
  },

  async devApproveKyc() {
    const result = await request<{ accessToken: string }>("/auth/dev/approve-kyc", {
      method: "POST",
    });
    setToken(result.accessToken); // refresh claims immediately
    return result;
  },

  // ----- Family -----
  listChildren() {
    return request<
      {
        id: string;
        name: string;
        dateOfBirth: string;
        loginCode: string | null;
        jars: { shortTerm: number; mediumTerm: number; longTerm: number };
        pendingTasks: { id: string; title: string; reward: number; state: string }[];
      }[]
    >("/children");
  },

  createChild(input: { displayName: string; dateOfBirth: string; pin: string }) {
    return request<{ childId: string; loginCode: string }>("/children", {
      method: "POST",
      body: JSON.stringify(input),
    });
  },

  addPartner(input: { email: string }) {
    return request<{ partnerId: string }>("/partners", {
      method: "POST",
      body: JSON.stringify(input),
    });
  },

  getFamily() {
    return request<{
      familyId: string;
      partners: { id: string; email: string; addedAtUtc: string }[];
      children: { id: string; displayName: string; dateOfBirth: string; loginCode: string | null; ownershipTransferred: boolean }[];
    }>("/family");
  },

  getChildView() {
    return request<{
      name: string;
      jars: { shortTerm: number; mediumTerm: number; longTerm: number; currency: string };
      pendingTasks: { id: string; title: string; reward: number; state: string }[];
      giftedShares: { symbol: string; amount: number; currency: string; redeemedAtUtc: string }[];
    }>("/me/child-view");
  },

  createTask(input: {
    childId: string;
    title: string;
    description?: string;
    rewardAmount: number;
    dueDate: string;
    recurrence: "OneTime" | "Daily" | "Weekly" | "Monthly";
    requiresPhotoProof: boolean;
  }) {
    return request<{ taskId: string }>("/tasks", {
      method: "POST",
      body: JSON.stringify(input),
    });
  },

  approveTask(taskId: string) {
    return request<void>(`/tasks/${taskId}/approve`, { method: "POST" });
  },
};
