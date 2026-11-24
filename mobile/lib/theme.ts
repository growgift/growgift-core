// Evergreen & Ribbon design tokens — the RN equivalent of frontend/tailwind.config.ts.
// React Native has no CSS, so every screen imports these constants directly.
export const colors = {
  ink: "#12291F",
  canvas: "#F6FBF4",
  leaf: "#2F8F5B",
  leafDeep: "#1F6B42",
  ribbon: "#F2A93B",
  mint: "#BFE3D2",
  inkFaint: "rgba(18,41,31,0.55)",
  inkBorder: "rgba(18,41,31,0.2)",
  danger: "#B3261E",
};

export const radius = { sm: 10, md: 16, lg: 22, pill: 999 };
export const spacing = (n: number) => n * 4;
