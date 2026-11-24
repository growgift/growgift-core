// Friendly wording for the outcome of ViewTrade's two-step background check, as reported by our backend at
// submission time. Unknown values fail safe to the neutral "under review" message — never to an approval.

export function verificationMessage(backgroundCheck: string | null | undefined, approved: boolean): { title: string; body: string } {
  if (approved) {
    return { title: "You're verified!", body: "Your brokerage account is open. You're all set to redeem or manage your family's investments." };
  }
  switch (backgroundCheck) {
    case "documents_under_review":
      return {
        title: "Checking your documents",
        body: "Your details passed the first check and we've sent your identity documents for verification. This can take a little while — we'll let you know as soon as it's done.",
      };
    case "documents_required":
      return {
        title: "Identity documents needed",
        body: "ViewTrade needs to verify your identity documents before opening your account. We couldn't start that just now — please try again shortly.",
      };
    case "needs_review":
      return {
        title: "Application under review",
        body: "ViewTrade is manually reviewing your application. We'll let you know as soon as there's an update.",
      };
    default:
      return {
        title: "Application submitted",
        body: "ViewTrade is reviewing your application. This can take a little while — we'll let you know as soon as it's approved.",
      };
  }
}
