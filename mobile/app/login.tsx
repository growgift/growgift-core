import { useState } from "react";
import { Link, useRouter } from "expo-router";
import { ActivityIndicator, Pressable, StyleSheet, Text, TextInput, View } from "react-native";
import { api, ApiError } from "../lib/api";
import { colors, radius, spacing } from "../lib/theme";

export default function Login() {
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit() {
    setBusy(true);
    setError(null);
    try {
      await api.login({ email, password });
      router.replace("/");
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 401
          ? "Email or password didn't match."
          : "Something went wrong. Please try again.",
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <View style={styles.container}>
      <Text style={styles.title}>Welcome back</Text>

      <View style={styles.card}>
        <View style={styles.field}>
          <Text style={styles.label}>Email</Text>
          <TextInput
            style={styles.input}
            value={email}
            onChangeText={setEmail}
            autoCapitalize="none"
            keyboardType="email-address"
          />
        </View>
        <View style={styles.field}>
          <Text style={styles.label}>Password</Text>
          <TextInput style={styles.input} value={password} onChangeText={setPassword} secureTextEntry />
        </View>

        {error && <Text style={styles.errorText}>{error}</Text>}

        <Pressable
          style={[styles.primaryBtn, (!email || !password || busy) && styles.disabledBtn]}
          disabled={!email || !password || busy}
          onPress={submit}
        >
          {busy ? <ActivityIndicator color="#fff" /> : <Text style={styles.primaryBtnText}>Sign in</Text>}
        </Pressable>
      </View>

      <Link href="/signup" style={styles.link}>
        <Text>
          New here? <Text style={styles.linkStrong}>Create an account</Text>
        </Text>
      </Link>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, padding: spacing(5), justifyContent: "center", gap: spacing(6) },
  title: { fontSize: 30, fontWeight: "700", color: colors.ink, textAlign: "center" },
  card: { backgroundColor: "#fff", borderWidth: 1.5, borderColor: colors.ink, borderRadius: radius.lg, padding: spacing(5), gap: spacing(3) },
  field: { gap: 4 },
  label: { fontSize: 13, fontWeight: "700", color: colors.ink },
  input: { borderWidth: 1.5, borderColor: colors.inkBorder, borderRadius: radius.sm, paddingHorizontal: spacing(4), paddingVertical: spacing(2.5), fontSize: 15, color: colors.ink },
  errorText: { fontSize: 12, color: colors.danger },
  primaryBtn: { backgroundColor: colors.leaf, borderRadius: radius.pill, paddingVertical: spacing(3.5), alignItems: "center" },
  primaryBtnText: { color: "#fff", fontWeight: "700", fontSize: 15 },
  disabledBtn: { opacity: 0.4 },
  link: { alignSelf: "center" },
  linkStrong: { color: colors.leafDeep, fontWeight: "700" },
});
