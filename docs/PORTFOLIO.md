# ViewTrade portfolio on My Profile

`GET /api/v1/portfolio` (Brokerage) → `ViewTradePortfolioService` → same chain as trading
(user lookup → impersonate → Watchman → SSO) → `GET api/user/portfolio` on the Trading host with
`Authorization: SessionId …` (Postman: Trading/AccountPortfolio). No account id is sent; ViewTrade answers for the session's user.

Reply shape (from the saved Postman example): top-level array; per holding `symbol`, `quantity`, `price` (avg cost/share),
`costValue`, `value` (0 = unpriced), `currency`, `quote.lastPrice`. Unpriced holdings show "Price not available".

Failures are returned as `portfolio.session_failed.<stage>` (userlookup / impersonation / watchman / sso),
`portfolio.no_viewtrade_user`, `portfolio.ambiguous_viewtrade_user` or `portfolio.viewtrade_<http status>`;
the Brokerage log carries the same stage (no tokens/bodies). Needs `VIEWTRADE_SSO_BASE_URL` and `VIEWTRADE_TRADING_BASE_URL`.

## Existing-account status mapping (ExistingAccountEvaluator.Classify)
ViewTrade returns two fields: application `status` (e.g. `background_check_success`) and `account_status` (e.g. `in_progress`);
`account_number` stays null until an account is issued (ApplicationGet sample).
- rejected/declined/denied or closed/cancelled/suspended/restricted in either field → not eligible (never linked)
- an issued `account_number` → account exists → skip onboarding (a stale `status` can't override it)
- no number: `approved` or any unknown/submitted value → pending review; draft `in_progress` → continue onboarding
Only values seen in samples are named; unknown values never count as approved. My Profile runs the same lookup for unlinked users.
