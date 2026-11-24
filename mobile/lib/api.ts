import * as SecureStore from "expo-secure-store";

// Web can hit `localhost` because the browser and the gateway are on the
// same machine. A phone (physical or emulator) can't: an iOS simulator can
// usually still reach `localhost`, but an Android emulator needs 10.0.2.2,
// and a real device needs your machine's LAN IP. Set EXPO_PUBLIC_API_BASE
// in your shell before `expo start` to override the default.
//   iOS simulator:      EXPO_PUBLIC_API_BASE=http://localhost:8080/api/v1
//   Android emulator:   EXPO_PUBLIC_API_BASE=http://10.0.2.2:8080/api/v1
//   Physical device:    EXPO_PUBLIC_API_BASE=http://<your-lan-ip>:8080/api/v1
const base = process.env.EXPO_PUBLIC_API_BASE ?? "http://localhost:8080/api/v1";

const TOKEN_KEY = "gg_token";

export async function setToken(token: string | null) {
  if (token) await SecureStore.setItemAsync(TOKEN_KEY, token);
  else await SecureStore.deleteItemAsync(TOKEN_KEY);
}

export async function getToken(): Promise<string | null> {
  return SecureStore.getItemAsync(TOKEN_KEY);
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

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = await getToken();
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

// Hermes (RN's JS engine) doesn't reliably expose crypto.randomUUID, so
// idempotency keys use a small local generator instead of depending on it.
function idempotencyKey(): string {
  return "gg-" + Array.from({ length: 4 }, () => Math.random().toString(36).slice(2)).join("-");
}

// ----- Shared types (mirrors frontend/lib/api.ts exactly) -----

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

export type GiftOrder = {
  id: string;
  status: string;
  symbol: string;
  amount: { amount: number; currency: string };
  recipientName: string | null;
};

export const api = {
  async register(input: {
    email: string;
    password: string;
    role: "buyer" | "parent";
    shippingAddress?: ShippingAddress;
  }) {
    return request<{ userId: string; message: string; devVerificationUrl: string | null }>("/auth/register", {
      method: "POST",
      body: JSON.stringify(input),
    });
  },

  async login(input: { email: string; password: string }) {
    const result = await request<{ accessToken: string; expiresInSeconds: number }>("/auth/login", {
      method: "POST",
      body: JSON.stringify(input),
    });
    await setToken(result.accessToken);
    return result;
  },

  async logout() {
    await setToken(null);
  },

  me() {
    return request<Me>("/auth/me");
  },

  updateShippingAddress(address: ShippingAddress) {
    return request<{ message: string }>("/auth/me/shipping-address", {
      method: "PUT",
      body: JSON.stringify(address),
    });
  },

  verifyEmail(token: string) {
    return request<{ message: string }>("/auth/verify-email", {
      method: "POST",
      body: JSON.stringify({ token }),
    });
  },

  async devApproveKyc() {
    const result = await request<{ accessToken: string }>("/auth/dev/approve-kyc", { method: "POST" });
    await setToken(result.accessToken);
    return result;
  },

  createGiftOrder(input: {
    symbol: string;
    amount: number;
    currency: string;
    recipientName: string;
    message: string;
    boxSku: string;
  }) {
    return request<GiftOrder>("/orders", {
      method: "POST",
      headers: { "Idempotency-Key": idempotencyKey() },
      body: JSON.stringify({
        symbol: input.symbol,
        amount: input.amount,
        currency: input.currency,
        recipientName: input.recipientName,
        greetingMessage: input.message || null,
        hasPhysicalBox: input.boxSku !== "BOX-NONE",
      }),
    });
  },

  async createCheckoutSession(orderId: string) {
    const result = await request<{ checkoutUrl: string }>(`/orders/${orderId}/checkout-session`, {
      method: "POST",
    });
    return result.checkoutUrl; // open with expo-web-browser / Linking on the checkout screen
  },

  redeemGift(input: { code: string }) {
    return request<{ giftId: string; symbol: string; quantity: number }>("/gifts/redeem", {
      method: "POST",
      headers: { "Idempotency-Key": idempotencyKey() },
      body: JSON.stringify(input),
    });
  },

  myOrders() {
    return request<GiftOrder[]>("/orders");
  },
};
