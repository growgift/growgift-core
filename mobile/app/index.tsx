import { Link } from "expo-router";
import { Pressable, StyleSheet, Text, View } from "react-native";
import { colors, radius, spacing } from "../lib/theme";

export default function Home() {
  return (
    <View style={styles.container}>
      <Text style={styles.eyebrow}>THE GIFT THAT COMPOUNDS</Text>
      <Text style={styles.title}>
        Give a share.{"\n"}Grow a <Text style={{ color: colors.leaf }}>future</Text>.
      </Text>
      <Text style={styles.body}>
        GrowGift turns real fractional shares into physical gifts you can hold, unwrap, and pass on.
      </Text>

      <View style={{ gap: spacing(3), marginTop: spacing(8), width: "100%" }}>
        <Link href="/signup" asChild>
          <Pressable style={styles.primaryBtn}>
            <Text style={styles.primaryBtnText}>Get started</Text>
          </Pressable>
        </Link>
        <Link href="/login" asChild>
          <Pressable style={styles.secondaryBtn}>
            <Text style={styles.secondaryBtnText}>I already have an account</Text>
          </Pressable>
        </Link>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.canvas, padding: spacing(6), justifyContent: "center" },
  eyebrow: { fontSize: 11, fontWeight: "800", letterSpacing: 1.5, color: colors.leafDeep, marginBottom: spacing(3) },
  title: { fontSize: 36, fontWeight: "700", color: colors.ink, lineHeight: 42 },
  body: { fontSize: 15, color: colors.inkFaint, marginTop: spacing(4), lineHeight: 22 },
  primaryBtn: { backgroundColor: colors.leaf, borderRadius: radius.pill, paddingVertical: spacing(4), alignItems: "center" },
  primaryBtnText: { color: "#fff", fontWeight: "700", fontSize: 16 },
  secondaryBtn: { borderWidth: 1.5, borderColor: colors.ink, borderRadius: radius.pill, paddingVertical: spacing(4), alignItems: "center" },
  secondaryBtnText: { color: colors.ink, fontWeight: "700", fontSize: 16 },
});
