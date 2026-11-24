"use client";

import { verificationMessage } from "@/lib/verification";
import { Suspense, useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { api, ApiError, type Country } from "@/lib/api";
import { useRequireAuth } from "@/lib/auth";
import { buildSignupUrl, safeInternalPath } from "@/lib/redirects";

// Fields not shown to the user get sensible defaults, submitted alongside
// the answers actually collected below. These are all genuinely defaultable
// for GrowGift's use case (a digital gifting platform, not a trading app),
// but every one of them is a real compliance answer — review before launch.
const AUTO_ANSWERS: Record<string, string> = {
  "application.customer.type": "Individual",
  "application.account.type": "Cash",
  "application.acknowledgment.tax-form-information": "As a withholding agent, ViewTrade Securities is required by federal tax law to collect information through IRS Form W-9 or W-8 series. While we do not provide tax, legal or accounting advice, we include some helpful information along the way. We encourage you to contact a tax professional with questions, and to review the IRS instructions to Form W-9 and Form W8-BEN on irs.gov. You can also copy and paste https://bit.ly/vt-w9 for Form W-9 instructions and https://bit.ly/vt-w8ben for Form W-8BEN instructions.",
  "application.edocs.tax.form": "Yes",
  "application.edocs.confirmations.opt-in": "Yes",
  "application.edocs.statements.opt-in": "Yes",
  "application.edocs.proxies.opt-in": "Yes",
  "application.edocs.prospectus.opt-in": "Yes",
  "application.fdd.deposit.initial.type": "Wire",
  // GrowGift's buy-at-redemption model has no deposit at account opening —
  // shares are purchased later, directly into the recipient's account, only
  // once a gift is redeemed. "0" reflects that honestly rather than
  // guessing a placeholder dollar figure.
  "application.fdd.deposit.initial.value": "0",
  "application.fdd.withdrawal.expected": "OCCASIONAL",
  "application.fdd.business-nature": "Gifting platform - fractional share purchase on redemption",
  "application.fdd.activity-type": "LONG_TERM_INVESTING",
  "application.fdd.referred.to-broker": "no",
  "application.fdd.contact.initial": "GrowGift",
  "application.legalese.agreement.utp": "I agree",
  "application.legalese.cta.exhibit-b": "I agree",
  "application.legalese.client.disclosure.annual": "I agree",
  "application.legalese.form-a2": "agree",
  "application.legalese.client.fintech-fees": "I agree",
  "application.legalese.disclosure.other": "agree",
  "application.acknowledgment.crs-regbi": "I agree",
};

// Sane bounds for the date-of-birth field: old enough to plausibly be
// alive, young enough to be at least 18 (this is the adult buyer/recipient
// KYC flow — minors go through the separate family/custodial module).
// Both bounds are computed via real Date arithmetic (not naive year
// subtraction), so MAX_DOB correctly accounts for whether this year's
// birthday has passed yet — someone born earlier in the year than today's
// month/day is a year older than a naive "current year minus 18" check
// would assume.
const today = new Date();
const MIN_DOB = `${today.getFullYear() - 120}-01-01`;
const MAX_DOB = new Date(today.getFullYear() - 18, today.getMonth(), today.getDate()).toISOString().slice(0, 10);

// Plain string comparison (e.g. answers[f.id] < MIN_DOB) silently passes a
// mistyped 6-digit year like "200000-05-15": lexicographic comparison
// assumes matching lengths, so "200000-..." compares as LESS than
// "2007-...-..." purely because '0' < '7' at the 4th character, regardless
// of having two extra digits before it. The regex below anchors the year
// to exactly 4 digits first, which makes the later MIN_DOB/MAX_DOB string
// comparisons safe (guaranteed-matching "YYYY-MM-DD" lengths on both sides).
type DobCheck = { ok: true } | { ok: false; reason: "format" | "too-young" | "unrealistic" };

function checkDob(value: string): DobCheck {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return { ok: false, reason: "format" };

  const parsed = new Date(value);
  if (isNaN(parsed.getTime()) || parsed.toISOString().slice(0, 10) !== value) {
    return { ok: false, reason: "format" }; // not a real calendar date, e.g. Feb 30
  }
  if (value < MIN_DOB) return { ok: false, reason: "unrealistic" };
  if (value > MAX_DOB) return { ok: false, reason: "too-young" };
  return { ok: true };
}

type ShowIf = (a: Record<string, string>, multi: Record<string, string[]>) => boolean;
type Radio = { id: string; label: string; options: string[]; showIf?: ShowIf };
type Text = { id: string; label: string; placeholder?: string; type?: string; showIf?: ShowIf };
// A genuine multi-select checkbox group (0+ selections) — distinct from
// Radio, which is single-choice. ViewTrade's config marks these as
// type_name "checkbox"; confirmed via a real captured example that
// applicant.primary.phone.type is exactly this shape (both "Home phone"
// and "Mobile phone" can be selected together), not the single-choice
// Radio it was originally (incorrectly) modeled as.
type MultiSelect = { id: string; label: string; multiOptions: string[]; showIf?: ShowIf };
// Backed by ViewTrade's CountriesList — see api.getCountries(). The value
// stored/submitted for these fields is still the country's "code" (matches
// what ViewTrade's "country" type questions expect), the dropdown just
// replaces free-text entry of that code.
type CountrySelect = { id: string; label: string; kind: "country"; showIf?: ShowIf };

const steps: { title: string; fields: (Radio | Text | CountrySelect | MultiSelect)[] }[] = [
  {
    title: "About you",
    fields: [
      { id: "applicant.primary.name.first", label: "First name" },
      { id: "applicant.primary.name.middle", label: "Middle name (optional)" },
      { id: "applicant.primary.name.last", label: "Last name" },
      { id: "applicant.primary.dob", label: "Date of birth", type: "date" },
      { id: "applicant.primary.email.personal", label: "Email for ViewTrade communications" },
      {
        id: "applicant.primary.phone.type",
        label: "Which phone number(s) are you providing?",
        multiOptions: ["Home phone", "Mobile phone"],
      } as MultiSelect,
      {
        id: "applicant.primary.phone.home",
        label: "Home phone",
        placeholder: "+61 2 xxxx xxxx",
        showIf: (_a, multi) => (multi["applicant.primary.phone.type"] ?? []).includes("Home phone"),
      },
      {
        id: "applicant.primary.phone.mobile",
        label: "Mobile phone",
        placeholder: "+61 4xx xxx xxx",
        showIf: (_a, multi) => (multi["applicant.primary.phone.type"] ?? []).includes("Mobile phone"),
      },
      { id: "applicant.primary.citizenship.country.us", label: "Are you a US citizen?", options: ["Yes", "No"] } as Radio,
      { id: "applicant.primary.citizenship.country", label: "Country of citizenship", kind: "country" } as CountrySelect,
      { id: "applicant.primary.birth-country.country.us", label: "Were you born in the US?", options: ["Yes", "No"] } as Radio,
      { id: "applicant.primary.birth-country.country", label: "Country of birth", kind: "country" } as CountrySelect,
    ],
  },
  {
    title: "Residential address",
    fields: [
      { id: "applicant.primary.address.home.country.us", label: "Is your home address in the US?", options: ["Yes", "No"] } as Radio,
      { id: "applicant.primary.address.home.line.1", label: "Address line 1" },
      { id: "applicant.primary.address.home.line.2", label: "Address line 2 (optional)" },
      { id: "applicant.primary.address.home.city", label: "City" },
      { id: "applicant.primary.address.home.state", label: "State" },
      { id: "applicant.primary.address.home.zip", label: "Postal code" },
      { id: "applicant.primary.address.home.country", label: "Country", kind: "country" } as CountrySelect,
      { id: "applicant.primary.address.mailing.same-as-home", label: "Mailing address same as home?", options: ["Yes", "No"] } as Radio,
      {
        id: "applicant.primary.address.mailing.country.us",
        label: "Is your mailing address in the US?",
        options: ["Yes", "No"],
        showIf: (a) => a["applicant.primary.address.mailing.same-as-home"] === "No",
      } as Radio,
      {
        id: "applicant.primary.address.mailing.line.1",
        label: "Mailing address line 1",
        showIf: (a) => a["applicant.primary.address.mailing.same-as-home"] === "No",
      },
      {
        id: "applicant.primary.address.mailing.line.2",
        label: "Mailing address line 2 (optional)",
        showIf: (a) => a["applicant.primary.address.mailing.same-as-home"] === "No",
      },
      {
        id: "applicant.primary.address.mailing.city",
        label: "Mailing city",
        showIf: (a) => a["applicant.primary.address.mailing.same-as-home"] === "No",
      },
      {
        id: "applicant.primary.address.mailing.state",
        label: "Mailing state",
        showIf: (a) => a["applicant.primary.address.mailing.same-as-home"] === "No",
      },
      {
        id: "applicant.primary.address.mailing.zip",
        label: "Mailing postal code",
        showIf: (a) => a["applicant.primary.address.mailing.same-as-home"] === "No",
      },
      {
        id: "applicant.primary.address.mailing.country",
        label: "Mailing country",
        kind: "country",
        showIf: (a) => a["applicant.primary.address.mailing.same-as-home"] === "No",
      } as CountrySelect,
    ],
  },
  {
    title: "Tax status & employment",
    fields: [
      { id: "applicant.primary.has-ssn", label: "Do you have a US Tax ID Number (SSN)?", options: ["Yes", "No"] } as Radio,
      { id: "applicant.primary.employment.status", label: "Employment status", options: ["Employed", "Unemployed", "Retired", "Student"] } as Radio,
      { id: "applicant.primary.employment.employer", label: "Employer (if employed)" },
      { id: "applicant.primary.employment.source-of-funds", label: "Primary source of funds", options: ["Savings", "Investments"] } as Radio,
      { id: "applicant.primary.employment.income.source.not-employed", label: "If not employed, what is your source of income? (leave blank if employed)" },
      { id: "applicant.primary.w8.ftin.not-required", label: "Is a Foreign Tax ID Number (FTIN) not required in your country of tax residence?", options: ["Yes", "No"] } as Radio,
      { id: "applicant.primary.w8.special-rate.applicable", label: "Does a special withholding tax rate apply to you under a tax treaty?", options: ["Yes", "No"] } as Radio,
      // applicant.primary.w8.treaty-country is NOT asked here — its
      // question_label IS the beneficial-owner residency certification
      // ("I certify that the beneficial owner is a resident of the
      // country selected below..."), and that residency is exactly what
      // the home address country already captures. Asking the applicant
      // to pick a country a second time risks a mismatch between the two,
      // which would misstate the actual certification. The backend derives
      // this answer directly from address.home.country instead — see
      // OnboardingEndpoints.Submit.
      { id: "applicant.primary.document.id.national.number", label: "National ID number" },
    ],
  },
  {
    title: "Investment profile",
    fields: [
      { id: "application.suitability.investment.objective", label: "Investment objective", options: ["Capital Preservation", "Income", "Growth", "Speculation", "Growth Income", "Maximum Growth", "Balanced", "Other"] } as Radio,
      { id: "application.suitability.annual-income", label: "Annual income", options: ["Under $25000", "$25001 to 50000", "50001 to 100000", "100001 to 200000", "200001 to 300000", "300001 to 500000", "500001 to 1200001", "Over 1200001"] } as Radio,
      { id: "application.suitability.net-worth.liquid", label: "Liquid net worth", options: ["Under $50000", "$50001 to $100000", "$100001 to $200000", "$200001 to $500000", "$500001 to $1000000", "$1000001 to $5000000", "Over $5000001"] } as Radio,
      { id: "application.suitability.net-worth.total", label: "Total net worth", options: ["Under $50000", "$50001 to $100000", "$100001 to $200000", "$200001 to $500000", "$500001 to $1000000", "$1000001 to $5000000", "Over $5000001"] } as Radio,
      { id: "application.suitability.risk-tolerance", label: "Risk tolerance", options: ["Low", "Medium", "High"] } as Radio,
      { id: "application.suitability.liquidity", label: "How important is liquidity?", options: ["Very Important", "Somewhat Important", "Not Important"] } as Radio,
      { id: "application.suitability.time-horizon", label: "Investment time horizon", options: ["Short (Less than 3 Years)", "Average (4-7 Years)", "Long (8 Years+)"] } as Radio,
      { id: "application.suitability.investment.experience", label: "Investment experience", options: ["None", "Limited", "Good", "Extensive"] } as Radio,
    ],
  },
  {
    title: "Disclosures",
    fields: [
      { id: "application.info.domestic-or-foreign", label: "Applicant status", options: ["Domestic", "Foreign"] } as Radio,
      { id: "applicant.primary.politically-exposed", label: "Are you a politically exposed person?", options: ["Yes", "No"] } as Radio,
      { id: "applicant.primary.control-person", label: "Are you a control person of a publicly traded company?", options: ["Yes", "No"] } as Radio,
      { id: "applicant.primary.affiliated-with-member-firm", label: "Affiliated with a FINRA/stock-exchange member firm?", options: ["Yes", "No"] } as Radio,
      // application.trading-authorization and application.trusted-contact.opt-in
      // are deliberately not asked here: ViewTrade has no registered "Yes"
      // option for the former, and there's no UI for the name/email
      // follow-up the latter would require. Both are always answered "No"
      // directly in the backend's Submit handler instead — see
      // BackendOwnedAnswers in OnboardingEndpoints.cs — rather than showing
      // a single-option control that can't actually do anything.
    ],
  },
  {
    title: "Identity document",
    fields: [
      { id: "applicant.primary.document.id.type", label: "Document type", options: ["Driver's License", "Passport", "Identification Card"] } as Radio,
      { id: "applicant.primary.document.country", label: "Document issuing country", kind: "country" } as CountrySelect,
      { id: "applicant.primary.document.id.number", label: "Document number" },
      { id: "applicant.primary.document.id.photo-id.contains-address", label: "Does this document show your address?", options: ["Yes", "No"] } as Radio,
    ],
  },
];

const fileSteps = [
  { id: "applicant.primary.document.id.photo-id.front.image", label: "Photo ID — front" },
  { id: "applicant.primary.document.id.photo-id.back.image", label: "Photo ID — back" },
  { id: "applicant.primary.document.signature.image", label: "Signature (a photo of your handwritten signature is fine)" },
];

const agreements = [
  {
    id: "application.legalese.signature.agreement",
    text: "I agree to the above and am electronically signing this application.",
    // ViewTrade's exact required option label for this question — deliberately
    // different wording from the UI text above; submitting the UI text (or a
    // generic "I agree") fails resolution against viewtrade_question_config.json.
    viewTradeLabel: "I agree to the above and electronically sign the agreement",
  },
  {
    id: "application.legalese.signature.agreement.tax",
    text: "I consent to sign my tax certification electronically.",
    viewTradeLabel: "I provide my consent to sign electronically",
  },
  {
    id: "applicant.primary.w8.certification",
    text: "I certify, under penalties of perjury, that the information on this form is correct and that I am not a US person for tax purposes (or have completed the appropriate US tax forms with support separately).",
    viewTradeLabel: "I certify that I have the capacity to sign for the beneficial owner in this application.",
  },
];

export default function OnboardingPage() {
  return (
    <Suspense>
      <Onboarding />
    </Suspense>
  );
}

function Onboarding() {
  const session = useRequireAuth();
  const router = useRouter();
  const params = useSearchParams();
  const then = safeInternalPath(params.get("then"), "/redeem?resume=1");

  const [step, setStep] = useState(0);

  // Without this, advancing to a long step (or going back) leaves the
  // viewport wherever the previous step's scroll position happened to be —
  // easy to land mid-form and think fields above are missing.
  useEffect(() => {
    window.scrollTo({ top: 0, behavior: "smooth" });
  }, [step]);
  const [answers, setAnswers] = useState<Record<string, string>>({});
  const [multiAnswers, setMultiAnswers] = useState<Record<string, string[]>>({});
  const [uploaded, setUploaded] = useState<Set<string>>(new Set());
  const [agreed, setAgreed] = useState<Set<string>>(new Set());
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Set only when the last submit failed because ViewTrade already has a user
  // under this account's email. Stored as the message text itself, so the
  // "use a different email" action below shows only while exactly that
  // message is what's on screen — any later, unrelated error replaces
  // `error` and the button disappears with it, no separate flag to forget
  // to clear at every other setError call.
  const [emailTakenMessage, setEmailTakenMessage] = useState<string | null>(null);
  const [result, setResult] = useState<{ status: string; accountRef: string; backgroundCheck?: string } | null>(null);
  const [countries, setCountries] = useState<Country[]>([]);
  const [countriesError, setCountriesError] = useState(false);

  useEffect(() => {
    if (!session) return;
    api
      .getOnboarding()
      .then((app) => {
        setAnswers(app.answers);
        setMultiAnswers(app.multiAnswers);
        setUploaded(new Set(app.documentsProvided));
        if (app.status === "Approved" || app.status === "Pending") {
          setResult({ status: app.status, accountRef: app.accountRef ?? "" });
        }
      })
      .catch(() => {})
      .finally(() => setLoading(false));
  }, [session]);

  // Fetched once and reused across all four country-type fields below.
  useEffect(() => {
    if (!session) return;
    api
      .getCountries()
      .then(setCountries)
      .catch(() => setCountriesError(true));
  }, [session]);

  function set(id: string, value: string) {
    setAnswers((a) => ({ ...a, [id]: value }));
  }

  function toggleMulti(id: string, option: string) {
    setMultiAnswers((m) => {
      const current = m[id] ?? [];
      const next = current.includes(option) ? current.filter((o) => o !== option) : [...current, option];
      return { ...m, [id]: next };
    });
  }

  async function saveStep() {
    setBusy(true);
    setError(null);
    try {
      // Catch an out-of-range date (e.g. a typo'd year like "20000", or
      // someone under 18) before it's saved — previously a malformed value
      // silently vanished via the "only send truthy fields" filter below,
      // surfacing much later as a confusing "required" rejection from
      // ViewTrade instead of a clear message right here.
      for (const f of steps[step].fields) {
        if ("type" in f && f.type === "date" && answers[f.id]) {
          const result = checkDob(answers[f.id]);
          if (!result.ok) {
            const message =
              result.reason === "too-young"
                ? `${f.label}: you must be at least 18 years old to register.`
                : result.reason === "unrealistic"
                  ? `${f.label}: please double-check this date.`
                  : `${f.label}: please enter a valid date.`;
            setError(message);
            setBusy(false);
            return;
          }
        }
      }

      const current = steps[step].fields
        .filter((f): f is Radio | Text | CountrySelect => !("multiOptions" in f))
        .filter((f) => (!f.showIf || f.showIf(answers, multiAnswers)) && answers[f.id])
        .map((f) => ({ systemIdentifier: f.id, value: answers[f.id] }));

      const currentMulti = steps[step].fields
        .filter((f): f is MultiSelect => "multiOptions" in f)
        .filter((f) => (!f.showIf || f.showIf(answers, multiAnswers)) && (multiAnswers[f.id]?.length ?? 0) > 0)
        .map((f) => ({ systemIdentifier: f.id, values: multiAnswers[f.id] }));

      if (current.length) await api.saveOnboardingAnswers(current);
      if (currentMulti.length) await api.saveOnboardingAnswers(currentMulti);
      setStep((s) => Math.min(steps.length, s + 1));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Couldn't save — try again.");
    } finally {
      setBusy(false);
    }
  }

  async function uploadFile(id: string, file: File) {
    setBusy(true);
    setError(null);
    try {
      await api.uploadOnboardingDocument(id, file);
      setUploaded((u) => new Set(u).add(id));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Upload failed — try again.");
    } finally {
      setBusy(false);
    }
  }

  async function submit() {
    setBusy(true);
    setError(null);
    try {
      // Auto-defaulted, not-user-facing answers (see AUTO_ANSWERS comment).
      await api.saveOnboardingAnswers(
        Object.entries(AUTO_ANSWERS).map(([systemIdentifier, value]) => ({ systemIdentifier, value })),
      );
      for (const a of agreements) {
        await api.saveOnboardingAnswers([{ systemIdentifier: a.id, value: agreed.has(a.id) ? a.viewTradeLabel : "" }]);
      }
      const submitted = await api.submitOnboarding();
      setResult(submitted);
    } catch (e) {
      const message =
        e instanceof ApiError ? e.message : "Submission failed — your answers are saved, try again shortly.";
      setError(message);
      setEmailTakenMessage(e instanceof ApiError && e.code === "onboarding.viewtrade_user_exists" ? message : null);
    } finally {
      setBusy(false);
    }
  }

  // Retrying can never fix this particular failure (ViewTrade will keep
  // rejecting the same email), so the way forward is a different account.
  // Lands on signup with the same return path, so a brand-new account ends
  // up back at the revealed gift code rather than somewhere generic.
  function signOutForDifferentEmail() {
    api.logout();
    router.push(buildSignupUrl({ then, persona: "buyer" }));
  }

  if (!session || loading) return null;

  if (result) {
    return (
      <div className="mx-auto max-w-xl px-5 py-20 text-center space-y-6">
        <span className="text-6xl">{result.status === "Approved" ? "🎉" : "⏳"}</span>
        <h1 className="font-display text-4xl font-semibold">{verificationMessage(result.backgroundCheck, result.status === "Approved").title}</h1>
        <p className="text-ink/70">{verificationMessage(result.backgroundCheck, result.status === "Approved").body}</p>
        <button
          onClick={() => router.push(then)}
          className="rounded-full bg-leaf text-white px-8 py-3 font-semibold hover:bg-leaf-deep transition"
        >
          Continue
        </button>
      </div>
    );
  }

  const isReview = step === steps.length;
  const inputCls =
    "mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 text-sm focus:border-leaf outline-none";

  return (
    <div className="mx-auto max-w-2xl px-5 py-14 space-y-8">
      <div>
        <p className="text-xs font-bold uppercase tracking-[0.15em] text-leaf-deep mb-2">
          Brokerage application · step {Math.min(step + 1, steps.length + 1)} of {steps.length + 1}
        </p>
        <h1 className="font-display text-4xl font-semibold">
          {isReview ? "Documents & signature" : steps[step].title}
        </h1>
      </div>

      {error && (
        <div className="space-y-3">
          <p className="text-sm text-red-700">{error}</p>
          {error === emailTakenMessage && (
            <button
              onClick={signOutForDifferentEmail}
              className="rounded-full border-[1.5px] border-ink px-5 py-2 text-sm font-semibold hover:bg-mint transition"
            >
              Sign out and use a different email
            </button>
          )}
        </div>
      )}

      {!isReview && (
        <div className="ticket p-6 space-y-4">
          {steps[step].fields
            .filter((f) => !f.showIf || f.showIf(answers, multiAnswers))
            .map((f) =>
            "multiOptions" in f ? (
              <div key={f.id}>
                <p className="text-sm font-bold mb-2">{f.label}</p>
                <div className="flex flex-wrap gap-2">
                  {f.multiOptions.map((opt) => {
                    const checked = (multiAnswers[f.id] ?? []).includes(opt);
                    return (
                      <button
                        key={opt}
                        onClick={() => toggleMulti(f.id, opt)}
                        className={`rounded-full border-[1.5px] px-4 py-2 text-sm font-semibold transition ${
                          checked ? "border-leaf bg-leaf text-white" : "border-ink/20 bg-white hover:border-ink"
                        }`}
                      >
                        {checked ? "✓ " : ""}
                        {opt}
                      </button>
                    );
                  })}
                </div>
              </div>
            ) : "options" in f ? (
              <div key={f.id}>
                <p className="text-sm font-bold mb-2">{f.label}</p>
                <div className="flex flex-wrap gap-2">
                  {f.options.map((opt) => (
                    <button
                      key={opt}
                      onClick={() => set(f.id, opt)}
                      className={`rounded-full border-[1.5px] px-4 py-2 text-sm font-semibold transition ${
                        answers[f.id] === opt
                          ? "border-leaf bg-leaf text-white"
                          : "border-ink/20 bg-white hover:border-ink"
                      }`}
                    >
                      {opt}
                    </button>
                  ))}
                </div>
              </div>
            ) : "kind" in f && f.kind === "country" ? (
              <label key={f.id} className="block">
                <span className="text-sm font-bold">{f.label}</span>
                <select
                  value={answers[f.id] ?? ""}
                  onChange={(e) => set(f.id, e.target.value)}
                  disabled={countries.length === 0}
                  className={inputCls}
                >
                  <option value="" disabled>
                    {countriesError ? "Couldn't load countries" : countries.length === 0 ? "Loading…" : "Select a country"}
                  </option>
                  {countries.map((c) => (
                    <option key={c.id} value={c.code}>
                      {c.code} — {c.name}
                    </option>
                  ))}
                </select>
              </label>
            ) : (
              <label key={f.id} className="block">
                <span className="text-sm font-bold">{f.label}</span>
                <input
                  type={"type" in f ? (f.type ?? "text") : "text"}
                  value={answers[f.id] ?? ""}
                  onChange={(e) => set(f.id, e.target.value)}
                  placeholder={"placeholder" in f ? f.placeholder : undefined}
                  // Date inputs (currently just DOB) get sane bounds so a
                  // typo'd year (e.g. "20000") can't silently produce an
                  // unparseable date that then just vanishes from what gets
                  // saved — it previously surfaced many steps later as a
                  // confusing "required" rejection from ViewTrade instead
                  // of a clear validation message here.
                  min={"type" in f && f.type === "date" ? MIN_DOB : undefined}
                  max={"type" in f && f.type === "date" ? MAX_DOB : undefined}
                  className={inputCls}
                />
              </label>
            ),
          )}
        </div>
      )}

      {isReview && (
        <div className="space-y-5">
          <div className="ticket p-6 space-y-4">
            <p className="text-xs font-bold uppercase tracking-[0.15em] text-ink/50">Upload documents</p>
            {fileSteps.map((f) => (
              <div key={f.id} className="flex items-center justify-between gap-3">
                <span className="text-sm">{f.label}</span>
                {uploaded.has(f.id) ? (
                  <span className="text-xs font-bold text-leaf-deep">✓ Uploaded</span>
                ) : (
                  <input
                    type="file"
                    accept="image/*,.pdf"
                    onChange={(e) => e.target.files?.[0] && uploadFile(f.id, e.target.files[0])}
                    className="text-xs"
                  />
                )}
              </div>
            ))}
          </div>

          <div className="ticket p-6 space-y-3">
            <p className="text-xs font-bold uppercase tracking-[0.15em] text-ink/50">Agreements</p>
            {agreements.map((a) => (
              <label key={a.id} className="flex items-start gap-3 text-sm">
                <input
                  type="checkbox"
                  checked={agreed.has(a.id)}
                  onChange={(e) =>
                    setAgreed((s) => {
                      const next = new Set(s);
                      e.target.checked ? next.add(a.id) : next.delete(a.id);
                      return next;
                    })
                  }
                  className="mt-1"
                />
                <span>{a.text}</span>
              </label>
            ))}
          </div>

          <p className="text-xs text-ink/50">
            A handful of standard account-opening disclosures (delivery preferences, account type,
            source-of-funds detail) are submitted with sensible defaults for a gifting platform —
            reach out if you&apos;d like to review or change any of them.
          </p>
        </div>
      )}

      <div className="flex justify-between">
        <button
          onClick={() => setStep((s) => Math.max(0, s - 1))}
          disabled={step === 0 || busy}
          className="rounded-full border-[1.5px] border-ink px-6 py-2.5 font-semibold disabled:opacity-30 hover:bg-mint transition"
        >
          Back
        </button>
        {isReview ? (
          <button
            onClick={submit}
            disabled={busy || fileSteps.some((f) => !uploaded.has(f.id)) || agreements.some((a) => !agreed.has(a.id))}
            className="rounded-full bg-leaf text-white px-8 py-2.5 font-semibold disabled:opacity-40 hover:bg-leaf-deep transition"
          >
            {busy ? "Submitting…" : "Submit application"}
          </button>
        ) : (
          <button
            onClick={saveStep}
            disabled={busy}
            className="rounded-full bg-ink text-canvas px-6 py-2.5 font-semibold disabled:opacity-40 hover:bg-leaf-deep transition"
          >
            {busy ? "Saving…" : "Continue"}
          </button>
        )}
      </div>
    </div>
  );
}
