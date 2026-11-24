import type { Metadata } from "next";
import { Fraunces, Nunito_Sans, IBM_Plex_Mono } from "next/font/google";
import Link from "next/link";
import { SiteNav } from "@/components/SiteNav";
import "./globals.css";

const fraunces = Fraunces({
  subsets: ["latin"],
  variable: "--font-fraunces",
  axes: ["opsz", "SOFT", "WONK"],
});
const nunito = Nunito_Sans({ subsets: ["latin"], variable: "--font-nunito" });
const mono = IBM_Plex_Mono({
  subsets: ["latin"],
  weight: ["500", "600"],
  variable: "--font-mono",
});

export const metadata: Metadata = {
  title: "GrowGift — give a share, grow a future",
  description:
    "Gift real fractional shares in a beautiful box. They unwrap it, scan the code, and own a piece of a company they love.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en-AU">
      <body
        className={`${fraunces.variable} ${nunito.variable} ${mono.variable} font-body antialiased min-h-screen flex flex-col`}
      >
        <header className="border-b-[1.5px] border-ink/15 bg-canvas/90 backdrop-blur sticky top-0 z-40">
          <div className="mx-auto max-w-6xl px-5 h-16 flex items-center justify-between">
            <Link href="/" className="font-display text-2xl font-semibold tracking-tight">
              Grow<span className="text-leaf">Gift</span>
              <span className="text-ribbon">.</span>
            </Link>
            <SiteNav />
          </div>
        </header>

        <main className="flex-1">{children}</main>

        <footer className="border-t-[1.5px] border-ink/15 mt-24">
          <div className="mx-auto max-w-6xl px-5 py-10 text-sm text-ink/70 space-y-3">
            <p className="font-display text-lg text-ink">GrowGift</p>
            <p>
              GrowGift Pty Ltd. Securities are held with our partner broker until redemption.
              Investing involves risk, including loss of principal. Nothing here is financial
              advice.
            </p>
            <p>ABN 00 000 000 000 · AFSL pending · Sydney, Australia</p>
          </div>
        </footer>
      </body>
    </html>
  );
}
