import { test } from "node:test";
import assert from "node:assert/strict";
import { verificationMessage } from "../lib/verification.ts";

test("approved always reads as verified", () => {
  assert.equal(verificationMessage("needs_review", true).title, "You're verified!");
});
test("documents steps have their own wording", () => {
  assert.equal(verificationMessage("documents_under_review", false).title, "Checking your documents");
  assert.equal(verificationMessage("documents_required", false).title, "Identity documents needed");
  assert.equal(verificationMessage("needs_review", false).title, "Application under review");
});
test("unknown or missing values never read as approved", () => {
  for (const v of [undefined, null, "", "check_unavailable", "passed", "weird"]) {
    const m = verificationMessage(v as string | null | undefined, false);
    assert.notEqual(m.title, "You're verified!");
    assert.equal(m.title, "Application submitted");
  }
});
