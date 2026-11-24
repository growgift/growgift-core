import { useState } from "react";
import { Modal, Pressable, StyleSheet, Text, TextInput, View } from "react-native";
import { colors, radius, spacing } from "../lib/theme";
import type { ShippingAddress } from "../lib/api";

const countries: [string, string][] = [
  ["AU", "Australia"], ["US", "United States"], ["GB", "United Kingdom"],
  ["NZ", "New Zealand"], ["CA", "Canada"], ["IE", "Ireland"], ["SG", "Singapore"],
];

export function emptyAddress(): ShippingAddress {
  return { fullName: "", line1: "", line2: "", city: "", region: "", postalCode: "", countryCode: "AU", phone: "" };
}

export function isAddressComplete(a: ShippingAddress) {
  return !!(a.fullName && a.line1 && a.city && a.region && a.postalCode && a.countryCode);
}

function Field({
  label,
  value,
  onChangeText,
  placeholder,
  keyboardType,
}: {
  label: string;
  value: string;
  onChangeText: (v: string) => void;
  placeholder?: string;
  keyboardType?: "default" | "phone-pad";
}) {
  return (
    <View style={styles.field}>
      <Text style={styles.label}>{label}</Text>
      <TextInput
        style={styles.input}
        value={value}
        onChangeText={onChangeText}
        placeholder={placeholder}
        placeholderTextColor={colors.inkFaint}
        keyboardType={keyboardType}
        autoCapitalize="words"
      />
    </View>
  );
}

// A minimal picker built from a text button + modal list — avoids pulling in
// @react-native-picker/picker just for a 7-country list.
function CountryPicker({ value, onChange }: { value: string; onChange: (code: string) => void }) {
  const [open, setOpen] = useState(false);
  const label = countries.find(([code]) => code === value)?.[1] ?? value;

  return (
    <View style={styles.field}>
      <Text style={styles.label}>Country</Text>
      <Pressable style={styles.input} onPress={() => setOpen(true)}>
        <Text>{label}</Text>
      </Pressable>
      <Modal visible={open} transparent animationType="fade" onRequestClose={() => setOpen(false)}>
        <Pressable style={styles.modalBackdrop} onPress={() => setOpen(false)}>
          <View style={styles.modalCard}>
            {countries.map(([code, name]) => (
              <Pressable
                key={code}
                style={styles.modalRow}
                onPress={() => {
                  onChange(code);
                  setOpen(false);
                }}
              >
                <Text style={code === value ? styles.modalRowTextActive : styles.modalRowText}>{name}</Text>
              </Pressable>
            ))}
          </View>
        </Pressable>
      </Modal>
    </View>
  );
}

export function AddressFields({
  value,
  onChange,
}: {
  value: ShippingAddress;
  onChange: (next: ShippingAddress) => void;
}) {
  const set = (patch: Partial<ShippingAddress>) => onChange({ ...value, ...patch });

  return (
    <View style={{ gap: spacing(3) }}>
      <Field label="Full name" value={value.fullName} onChangeText={(v) => set({ fullName: v })} placeholder="Who should the box be addressed to?" />
      <Field label="Address line 1" value={value.line1} onChangeText={(v) => set({ line1: v })} placeholder="Street address" />
      <Field label="Address line 2 (optional)" value={value.line2 ?? ""} onChangeText={(v) => set({ line2: v })} placeholder="Apartment, suite, etc." />
      <View style={styles.row}>
        <View style={{ flex: 1 }}>
          <Field label="City" value={value.city} onChangeText={(v) => set({ city: v })} />
        </View>
        <View style={{ flex: 1 }}>
          <Field label="State / region" value={value.region} onChangeText={(v) => set({ region: v })} />
        </View>
      </View>
      <View style={styles.row}>
        <View style={{ flex: 1 }}>
          <Field label="Postal code" value={value.postalCode} onChangeText={(v) => set({ postalCode: v })} />
        </View>
        <View style={{ flex: 1 }}>
          <CountryPicker value={value.countryCode} onChange={(v) => set({ countryCode: v })} />
        </View>
      </View>
      <Field
        label="Phone (optional, helps the courier)"
        value={value.phone ?? ""}
        onChangeText={(v) => set({ phone: v })}
        placeholder="+61 4xx xxx xxx"
        keyboardType="phone-pad"
      />
    </View>
  );
}

const styles = StyleSheet.create({
  field: { gap: 4 },
  label: { fontSize: 13, fontWeight: "700", color: colors.ink },
  input: {
    borderWidth: 1.5,
    borderColor: colors.inkBorder,
    borderRadius: radius.sm,
    paddingHorizontal: spacing(4),
    paddingVertical: spacing(2.5),
    fontSize: 15,
    backgroundColor: "#fff",
    color: colors.ink,
  },
  row: { flexDirection: "row", gap: spacing(3) },
  modalBackdrop: { flex: 1, backgroundColor: "rgba(18,41,31,0.4)", justifyContent: "center", padding: spacing(6) },
  modalCard: { backgroundColor: "#fff", borderRadius: radius.md, padding: spacing(2), maxHeight: "60%" },
  modalRow: { paddingVertical: spacing(3), paddingHorizontal: spacing(3) },
  modalRowText: { fontSize: 15, color: colors.ink },
  modalRowTextActive: { fontSize: 15, color: colors.leafDeep, fontWeight: "700" },
});
