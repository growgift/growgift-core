# GrowGift Mobile (Expo / React Native)

## What's actually built

- **Sign up** (`app/signup.tsx`) — three personas (buyer/recipient/parent), with
  inline shipping-address collection for buyers. Same backend contract as the
  web app's `/signup` page, ported to React Native components (no CSS/DOM —
  see `lib/theme.ts` for the Evergreen & Ribbon color tokens as RN constants).
- **Log in** (`app/login.tsx`)
- **API client** (`lib/api.ts`) — identical request/response shapes to
  `frontend/lib/api.ts`, with two RN-specific swaps: `expo-secure-store`
  instead of `sessionStorage` for the auth token, and a Hermes-safe UUID
  generator for idempotency keys (`crypto.randomUUID` isn't reliably
  available in React Native's JS engine).

## Configuring the API base URL

A phone can't resolve `localhost` the way a browser on the same machine can.
Set `EXPO_PUBLIC_API_BASE` before `npx expo start`:

```bash
# iOS simulator (can usually still reach the host's localhost)
EXPO_PUBLIC_API_BASE=http://localhost:8080/api/v1 npx expo start

# Android emulator (10.0.2.2 is the emulator's alias for the host machine)
EXPO_PUBLIC_API_BASE=http://10.0.2.2:8080/api/v1 npx expo start

# Physical device (must be on the same network as your machine)
EXPO_PUBLIC_API_BASE=http://<your-lan-ip>:8080/api/v1 npx expo start
```

## Not yet built (exists on web, not yet ported)

- Gift builder / collections browsing
- Redeem flow with **QR scan → deep link** (`growgift://redeem?code=...`,
  `expo-camera` already added as a dependency for this)
- KYC screen
- Family dashboard / kid view
- Push notifications (Expo Push, fed by the Notifications service)

Screens live in `app/` following expo-router file conventions, mirroring the
web app's route names where practical (`frontend/app/gift/new` →
`mobile/app/gift/new.tsx`, etc.) so the two codebases stay easy to
cross-reference even though the UI layer itself can't be shared.

