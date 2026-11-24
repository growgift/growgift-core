"use client";

// Shared between the Family page and the redeem flow's "Who's this for?"
// picker, so adding a child looks and behaves identically in both places —
// including the one-time login-code screen shown right after creation.

import { useState } from "react";
import { api, ApiError } from "@/lib/api";

export function Modal({ title, onClose, children }: { title: string; onClose: () => void; children: React.ReactNode }) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-ink/40 p-4" onClick={onClose}>
      <div
        className="ticket w-full max-w-md p-7 space-y-4"
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
      >
        <div className="flex items-center justify-between">
          <h3 className="font-display text-2xl">{title}</h3>
          <button onClick={onClose} aria-label="Close" className="text-2xl leading-none hover:text-leaf-deep">
            ×
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}

export function AddChildModal({ onClose, onDone }: { onClose: () => void; onDone: () => Promise<void> }) {
  const [name, setName] = useState("");
  const [dob, setDob] = useState("");
  const [pin, setPin] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [createdCode, setCreatedCode] = useState<string | null>(null);

  async function submit() {
    setBusy(true);
    setError(null);
    try {
      const result = await api.createChild({ displayName: name, dateOfBirth: dob, pin });
      setCreatedCode(result.loginCode);
      await onDone();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Couldn't add them — check the details.");
    } finally {
      setBusy(false);
    }
  }

  // Shown once, right after creation — the login code isn't stored or
  // shown again by this modal afterward (though it does stay visible on
  // the child's card on the dashboard, for whenever it needs to be
  // re-shared), so this is the one moment to write it down or share it.
  if (createdCode) {
    return (
      <Modal title={`${name}'s login is ready`} onClose={onClose}>
        <div className="rounded-xl bg-mint p-5 text-center space-y-2">
          <p className="text-xs font-bold uppercase tracking-wide text-ink/60">Login code</p>
          <p className="font-mono text-3xl tracking-widest">{createdCode}</p>
        </div>
        <p className="text-sm text-ink/70">
          {name} signs in at the kids&apos; login page with this code and the PIN you just set. Nothing else —
          no email needed.
        </p>
        <button
          onClick={onClose}
          className="w-full rounded-full bg-leaf text-white py-3 font-semibold hover:bg-leaf-deep transition"
        >
          Got it
        </button>
      </Modal>
    );
  }

  return (
    <Modal title="Add a child" onClose={onClose}>
      <label className="block">
        <span className="text-sm font-bold">Name</span>
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          maxLength={60}
          className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf outline-none"
          placeholder="Maya"
        />
      </label>
      <label className="block">
        <span className="text-sm font-bold">Date of birth</span>
        <input
          type="date"
          value={dob}
          onChange={(e) => setDob(e.target.value)}
          max={new Date().toISOString().slice(0, 10)}
          className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 focus:border-leaf outline-none"
        />
      </label>
      <label className="block">
        <span className="text-sm font-bold">Set their PIN (4–6 digits)</span>
        <input
          inputMode="numeric"
          value={pin}
          onChange={(e) => setPin(e.target.value.replace(/\D/g, "").slice(0, 6))}
          maxLength={6}
          className="mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-3 font-mono tracking-widest focus:border-leaf outline-none"
          placeholder="1234"
        />
        <span className="text-xs text-ink/50">They&apos;ll use this with a login code (shown next) to sign in — read-only, no email needed.</span>
      </label>
      {error && <p className="text-sm text-red-700">{error}</p>}
      <button
        onClick={submit}
        disabled={!name || !dob || pin.length < 4 || busy}
        className="w-full rounded-full bg-leaf text-white py-3 font-semibold hover:bg-leaf-deep disabled:opacity-40 transition"
      >
        {busy ? "Adding…" : "Open their three jars"}
      </button>
    </Modal>
  );
}
