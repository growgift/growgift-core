import { useState } from "react";
import { useLocalSearchParams, useRouter } from "expo-router";
import { ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, TextInput, View } from "react-native";
import { AddressFields, emptyAddress, isAddressComplete } from "../components/AddressFields";
import { api, ApiError, type ShippingAddress } from "../lib/api";
import { colors, radius, spacing } from "../lib/theme";

type Persona = "buyer" | "recipient" | "parent";

const personas: { key: Persona; emoji: string; title: string; blurb: string }[] = [
  { key: "buyer", emoji: "🎀", title: "I'm sending a gift", blurb: "Buy a share for someone. We'll need an address to ship the box to." },
  { key: "recipient", emoji: "🎁", title: "I received a gift", blurb: "Redeem a code, verify your ID once, and own your shares for real." },
  { key: "parent", emoji: "🌳", title: "Family investing", blurb: "Create child accounts, three savings jars, chores & rewards." },
];

export default function Signup() {
  const router = useRouter();
  const params = useLocalSearchParams<{ persona?: string; then?: string }>();

  const [persona, setPersona] = useState<Persona>(
    params.persona === "parent" ? "parent" : params.persona === "buyer" ? "buyer" : "recipient",
  );
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [address, setAddress] = useState<ShippingAddress>(emptyAddress());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<{ devLink: string | null } | null>(null);

  const passwordOk = password.length >= 12;
  const match = password === confirm;
  const addressOk = persona !== "buyer" || isAddressComplete(address);
  const canSubmit = email.includes("@") && passwordOk && match && addressOk && !busy;

  async function submit() {
    setBusy(true);
    setError(null);
    try {
      const created = await api.register({
        email,
        password,
        role: persona === "parent" ? "parent" : "buyer",
        shippingAddress: persona === "buyer" ? address : undefined,
      });
      await api.login({ email, password });
      setDone({ devLink: created.devVerificationUrl });
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 409
          ? "An account with this email already exists — try logging in instead."
          : e instanceof ApiError
            ? e.message
            : "Something went wrong. Please try again.",
      );
    } finally {
      setBusy(false);
    }
  }

  if (done) {
    return (
      <View style={styles.center}>
        <Text style={{ fontSize: 56 }}>🎉</Text>
        <Text style={styles.title}>Account created!</Text>
        <Text style={styles.body}>
          We've sent a verification link to {email}. Check your inbox to confirm it's really you.
        </Text>
        {done.devLink && (
          <View style={styles.devBox}>
            <Text style={styles.devText}>
              🛠 Dev mode — no email provider connected yet. Open this link in a browser to verify: {"\n"}
              {done.devLink}
            </Text>
          </View>
        )}
        <Pressable
          style={styles.primaryBtn}
          onPress={() =>
            // TODO: route to /kyc, /gift/new, /family once those screens are
            // ported (see the web app for the equivalent flows already live).
            router.replace("/")
          }
        >
          <Text style={styles.primaryBtnText}>Continue</Text>
        </Pressable>
      </View>
    );
  }

  return (
    <ScrollView contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled">
      <Text style={styles.title}>Create your account</Text>
      <Text style={styles.subtitle}>Two minutes now, a lifetime of compounding later.</Text>

      <View style={{ gap: spacing(3), marginTop: spacing(6) }}>
        {personas.map((p) => (
          <Pressable
            key={p.key}
            onPress={() => setPersona(p.key)}
            style={[styles.personaCard, persona === p.key && styles.personaCardActive]}
          >
            <Text style={{ fontSize: 28 }}>{p.emoji}</Text>
            <Text style={styles.personaTitle}>{p.title}</Text>
            <Text style={styles.personaBlurb}>{p.blurb}</Text>
          </Pressable>
        ))}
      </View>

      {persona === "buyer" && (
        <View style={styles.card}>
          <Text style={styles.cardLabel}>WHERE SHOULD WE SHIP THE BOX?</Text>
          <AddressFields value={address} onChange={setAddress} />
        </View>
      )}

      <View style={styles.card}>
        <View style={styles.field}>
          <Text style={styles.label}>Email</Text>
          <TextInput
            style={styles.input}
            value={email}
            onChangeText={setEmail}
            autoCapitalize="none"
            autoComplete="email"
            keyboardType="email-address"
            placeholder="you@example.com"
            placeholderTextColor={colors.inkFaint}
          />
        </View>
        <View style={styles.field}>
          <Text style={styles.label}>Password</Text>
          <TextInput
            style={styles.input}
            value={password}
            onChangeText={setPassword}
            secureTextEntry
            placeholder="At least 12 characters"
            placeholderTextColor={colors.inkFaint}
          />
          {!!password && !passwordOk && <Text style={styles.errorText}>Needs at least 12 characters.</Text>}
        </View>
        <View style={styles.field}>
          <Text style={styles.label}>Confirm password</Text>
          <TextInput style={styles.input} value={confirm} onChangeText={setConfirm} secureTextEntry />
          {!!confirm && !match && <Text style={styles.errorText}>Passwords don't match.</Text>}
        </View>

        {error && <Text style={styles.errorText}>{error}</Text>}
        {persona === "buyer" && !addressOk && (email || password) && (
          <Text style={styles.hintText}>Fill in your shipping address above to continue.</Text>
        )}

        <Pressable
          style={[styles.primaryBtn, !canSubmit && styles.disabledBtn]}
          disabled={!canSubmit}
          onPress={submit}
        >
          {busy ? (
            <ActivityIndicator color="#fff" />
          ) : (
            <Text style={styles.primaryBtnText}>
              {persona === "parent" ? "Start our family tree" : persona === "buyer" ? "Create account & choose a gift" : "Create account & continue"}
            </Text>
          )}
        </Pressable>
      </View>
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  scroll: { padding: spacing(5), paddingBottom: spacing(16) },
  center: { flex: 1, alignItems: "center", justifyContent: "center", padding: spacing(8), gap: spacing(4) },
  title: { fontSize: 30, fontWeight: "700", color: colors.ink, textAlign: "center" },
  subtitle: { fontSize: 14, color: colors.inkFaint, textAlign: "center", marginTop: spacing(2) },
  body: { fontSize: 14, color: colors.inkFaint, textAlign: "center", lineHeight: 20 },
  devBox: { backgroundColor: "rgba(242,169,59,0.15)", borderWidth: 1.5, borderColor: colors.ribbon, borderRadius: radius.md, padding: spacing(3) },
  devText: { fontSize: 11, color: colors.ink },
  personaCard: { borderWidth: 1.5, borderColor: colors.inkBorder, borderRadius: radius.md, padding: spacing(4), backgroundColor: "#fff" },
  personaCardActive: { borderColor: colors.leaf, backgroundColor: "rgba(191,227,210,0.4)" },
  personaTitle: { fontSize: 17, fontWeight: "700", color: colors.ink, marginTop: spacing(2) },
  personaBlurb: { fontSize: 12, color: colors.inkFaint, marginTop: spacing(1) },
  card: { backgroundColor: "#fff", borderWidth: 1.5, borderColor: colors.ink, borderRadius: radius.lg, padding: spacing(5), marginTop: spacing(5), gap: spacing(3) },
  cardLabel: { fontSize: 11, fontWeight: "800", letterSpacing: 1, color: colors.inkFaint },
  field: { gap: 4 },
  label: { fontSize: 13, fontWeight: "700", color: colors.ink },
  input: { borderWidth: 1.5, borderColor: colors.inkBorder, borderRadius: radius.sm, paddingHorizontal: spacing(4), paddingVertical: spacing(2.5), fontSize: 15, color: colors.ink },
  errorText: { fontSize: 12, color: colors.danger },
  hintText: { fontSize: 12, color: colors.inkFaint },
  primaryBtn: { backgroundColor: colors.leaf, borderRadius: radius.pill, paddingVertical: spacing(3.5), alignItems: "center" },
  primaryBtnText: { color: "#fff", fontWeight: "700", fontSize: 15 },
  disabledBtn: { opacity: 0.4 },
});
