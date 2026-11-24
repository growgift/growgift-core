## What and why

## Checklist
- [ ] `dotnet test backend/GrowGift.sln` and (frontend) `npm test`, `npm run lint`, `npm run build` pass
- [ ] Added or updated tests (a regression test for every bug fix)
- [ ] No secrets, tokens, customer addresses, identity data or redemption codes in code, logs or screenshots
- [ ] Third-party calls are server-side, in test/sandbox/staging only
- [ ] Money or goods calls: idempotency key kept, no blind retry, unconfirmed outcomes reconciled
- [ ] Docs updated (`docs/`, `docs/CHANGES-latest.md`); ADR added if the decision is hard to reverse
- [ ] Shared logic in `frontend/lib` changed? Mobile repo needs `npm run sync:pull`
