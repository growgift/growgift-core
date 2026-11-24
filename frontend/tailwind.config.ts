import type { Config } from "tailwindcss";

export default {
  content: ["./app/**/*.{ts,tsx}", "./components/**/*.{ts,tsx}"],
  theme: {
    extend: {
      colors: {
        ink: "#12291F",
        canvas: "#F6FBF4",
        leaf: { DEFAULT: "#2F8F5B", deep: "#1F6B42" },
        ribbon: "#F2A93B",
        mint: "#BFE3D2",
      },
      fontFamily: {
        display: ["var(--font-fraunces)", "Georgia", "serif"],
        body: ["var(--font-nunito)", "system-ui", "sans-serif"],
        mono: ["var(--font-mono)", "ui-monospace", "monospace"],
      },
      boxShadow: {
        lift: "0 2px 0 0 #12291F",
        card: "0 12px 32px -16px rgba(18, 41, 31, 0.35)",
      },
    },
  },
  plugins: [],
} satisfies Config;
