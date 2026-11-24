"use client";

import type { ShippingAddress } from "@/lib/api";

const countries = [["AU", "Australia"]] as const;

// GrowGift only ships within Australia right now — the country field
// reflects that as a single fixed choice rather than a full country list
// that would suggest international shipping is supported.
const auStates = [
  ["NSW", "New South Wales"],
  ["VIC", "Victoria"],
  ["QLD", "Queensland"],
  ["WA", "Western Australia"],
  ["SA", "South Australia"],
  ["TAS", "Tasmania"],
  ["ACT", "Australian Capital Territory"],
  ["NT", "Northern Territory"],
] as const;

export function emptyAddress(): ShippingAddress {
  return { fullName: "", line1: "", line2: "", city: "", region: "", postalCode: "", countryCode: "AU", phone: "" };
}

export function isAddressComplete(a: ShippingAddress) {
  return !!(a.fullName && a.line1 && a.city && a.region && a.postalCode && a.countryCode);
}

export function AddressFields({
  value,
  onChange,
}: {
  value: ShippingAddress;
  onChange: (next: ShippingAddress) => void;
}) {
  const set = (patch: Partial<ShippingAddress>) => onChange({ ...value, ...patch });
  const inputCls =
    "mt-1 w-full rounded-xl border-[1.5px] border-ink/20 bg-white px-4 py-2.5 text-sm focus:border-leaf outline-none";

  return (
    <div className="space-y-3">
      <label className="block">
        <span className="text-sm font-bold">Full name</span>
        <input
          value={value.fullName}
          onChange={(e) => set({ fullName: e.target.value })}
          maxLength={120}
          className={inputCls}
          placeholder="Who should the box be addressed to?"
        />
      </label>
      <label className="block">
        <span className="text-sm font-bold">Address line 1</span>
        <input
          value={value.line1}
          onChange={(e) => set({ line1: e.target.value })}
          maxLength={200}
          className={inputCls}
          placeholder="Street address"
        />
      </label>
      <label className="block">
        <span className="text-sm font-bold">Address line 2 (optional)</span>
        <input
          value={value.line2 ?? ""}
          onChange={(e) => set({ line2: e.target.value })}
          maxLength={200}
          className={inputCls}
          placeholder="Apartment, suite, etc."
        />
      </label>
      <div className="grid grid-cols-2 gap-3">
        <label className="block">
          <span className="text-sm font-bold">City</span>
          <input value={value.city} onChange={(e) => set({ city: e.target.value })} maxLength={100} className={inputCls} />
        </label>
        <label className="block">
          <span className="text-sm font-bold">State / territory</span>
          <select value={value.region} onChange={(e) => set({ region: e.target.value })} className={inputCls}>
            <option value="" disabled>
              Select a state
            </option>
            {auStates.map(([code, name]) => (
              <option key={code} value={code}>
                {name}
              </option>
            ))}
          </select>
        </label>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <label className="block">
          <span className="text-sm font-bold">Postal code</span>
          <input
            value={value.postalCode}
            onChange={(e) => set({ postalCode: e.target.value })}
            maxLength={20}
            className={inputCls}
          />
        </label>
        <label className="block">
          <span className="text-sm font-bold">Country</span>
          <select
            value={value.countryCode}
            onChange={(e) => set({ countryCode: e.target.value })}
            className={inputCls}
          >
            {countries.map(([code, name]) => (
              <option key={code} value={code}>
                {name}
              </option>
            ))}
          </select>
        </label>
      </div>
      <label className="block">
        <span className="text-sm font-bold">Phone (optional, helps the courier)</span>
        <input
          value={value.phone ?? ""}
          onChange={(e) => set({ phone: e.target.value })}
          maxLength={30}
          className={inputCls}
          placeholder="+61 4xx xxx xxx"
        />
      </label>
    </div>
  );
}
