# ADR-0009: Mobile app in Expo with shared logic copied under CI guard

- Status: Accepted
- Date: 2026-11-25 (recorded)

## Context
One small team needs iOS and Android and wants fixes to reach the website and the app quickly and consistently.

## Decision
Build the app with Expo and React Native in its own repository (`growgift-mobile`). Business rules shared with the website (`frontend/lib`) are copied into the app and listed in `shared.manifest.json`; CI fails if a copy drifts from the website. Payments and the long identity form use hosted pages. The app calls only the gateway.

## Consequences
- One codebase for both platforms; the backend fix is made once.
- Changes to shared logic start in the website, then `npm run sync:pull`.
- A package or monorepo is a later option if the shared set grows.
- Installed apps can be older than the backend, so API changes must be backward compatible.
