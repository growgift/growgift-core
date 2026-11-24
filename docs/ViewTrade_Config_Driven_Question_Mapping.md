# ViewTrade Config-Driven Question Mapping

## 1. Problem

ViewTrade's account application is a long form (form id 175 by default) whose questions are identified by environment-specific numbers (`question_id`, `option_id`). Those numbers differ between sandbox and production and can change. GrowGift therefore never hardcodes them. Its own code uses stable **system identifiers** (for example `applicant.primary.name.first`) and a resolver translates them at run time.

## 2. The configuration file

`backend/src/Services/Brokerage/GrowGift.Brokerage/Infrastructure/ViewTrade/Onboarding/viewtrade_question_config.json` is an export of the form: 113 questions in 17 sections. Each entry:

| Field | Meaning |
|---|---|
| `question_id` | ViewTrade's numeric id for this environment |
| `system_identifier` | Stable name used by GrowGift, for example `application.account.type` |
| `type_id`, `type_name` | Input type: text, radio, checkbox, agreement, file, country, state, select, textarea, email, date |
| `section_id`, `section_name` | Form section |
| `question_label` | Text shown to the applicant |
| `is_optional`, `multiple_answers` | Validation hints |
| `options[]` | For choice questions: `option_id` and `label` |

Sections: Account Type, Identification of Beneficial Owner, US Indicia, Address, US Tax ID Number, Employment, Claim of Tax Treaty Benefits, Substitute Form W8-BEN Certification, Substitute Form W9 Certification, Investment Profile, Disclosures, Trusted Contact, Delivery Preferences, Acknowledgements, Documents, Signatures, Foreign Due Diligence.

## 3. The resolver

`ViewTradeQuestionConfigResolver` loads the file at start-up, groups entries by `system_identifier`, and logs a warning for any identifier that maps to more than one question id.

Resolution rules:
1. Look up the identifier.
2. If there is exactly one entry, use it.
3. If there are several, choose by the **option set**: when the answer is a label (for example Yes or No), pick the entry that offers that option.
4. If there is nothing to tell them apart, use the lowest `question_id` and log a warning.

Known duplicates in the current export:
- `application.trading-authorization`: two entries; one offers Yes/No, the other only No. Resolved by option, which is safe for both answers.
- `applicant.primary.document.id.social-security-card.number`: a text field with no options. The resolver uses the lower id (27169) and warns. **Unconfirmed:** whether 27169 or 27171 applies once the "Do you have a US Tax ID Number?" answer is given. Confirm with ViewTrade or a live section-by-section call before production.

## 4. Submission flow

1. The web wizard collects answers by system identifier and saves them: `PUT /api/v1/onboarding/answers` (stored in `brokerage.onboarding_applications`).
2. Documents (ID photos, signature image) are uploaded by system identifier: `POST /api/v1/onboarding/documents/{systemIdentifier}`.
3. `POST /api/v1/onboarding/submit` builds the ViewTrade payload by resolving each identifier, creates the application (with `form_id`), uploads each answer and document, and persists each external id immediately after it succeeds, so a retry never repeats a completed step.
4. Background check: after submission Brokerage calls `api/applications/background-check`; if documentation is required it calls `api/applications/verify-documents`. A failed check never undoes the submission. `POST /api/v1/onboarding/verify` re-runs it.

## 5. ViewTrade quirks handled

- Dates are sent as `MM/DD/YYYY`.
- Checkbox answers use a wrapped array.
- `phone.type` is multi-select, not radio.
- The token response is nested three levels deep.
- "Document already uploaded" (error 1081) is treated as success.
- Section identifiers can repeat; disambiguate by data shape, not by `section_id` alone.

## 6. Existing ViewTrade customers

Someone who already has an active ViewTrade account does not complete the form. `POST /api/v1/onboarding/resolve-existing` matches their verified GrowGift e-mail to exactly one active ViewTrade user, links the account and refreshes the token. Details are in `docs/REDEEM-EXISTING-VIEWTRADE.md`.

## 7. Maintaining the mapping

1. Re-export the form config from ViewTrade when the form changes.
2. Replace the JSON file and run the Brokerage tests.
3. Check the start-up log for new ambiguous identifiers.
4. Never hardcode `question_id` or `option_id` in calling code.

## 8. Open items

- Confirm the `form_id` request field name and the value for each environment (`VIEWTRADE_FORM_ID`, default 175).
- Confirm the Social Security Number duplicate above.
- Confirm how the `applicants.background_checks` expansion is requested; until then a missing check status is shown as "unknown".
