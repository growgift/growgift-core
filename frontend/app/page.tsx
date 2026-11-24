import Link from "next/link";
import { Ticket } from "@/components/Ticket";

const steps = [
  {
    n: "01",
    title: "Pick a company they love",
    body: "Choose from curated collections — gaming, space, the brands on their lunchbox. You buy a dollar amount; we handle the fractions.",
  },
  {
    n: "02",
    title: "We ship a real gift box",
    body: "A beautiful box arrives with a share certificate-style ticket, goodies, and a one-time redemption code.",
  },
  {
    n: "03",
    title: "They scan, redeem, own",
    body: "The recipient scans the QR code, verifies their identity once, and the shares transfer into their own account. Kids get a read-only view until 18.",
  },
];

export default function Landing() {
  return (
    <>
      {/* Hero */}
      <section className="grain border-b-[1.5px] border-ink/15">
        <div className="mx-auto max-w-6xl px-5 py-20 grid gap-12 lg:grid-cols-2 lg:items-center">
          <div className="space-y-6">
            <p className="inline-flex items-center gap-2 rounded-full border-[1.5px] border-ink px-4 py-1.5 text-xs font-bold uppercase tracking-[0.15em]">
              <span className="h-2 w-2 rounded-full bg-ribbon" /> The gift that compounds
            </p>
            <h1 className="font-display text-5xl lg:text-6xl leading-[1.05] font-semibold">
              Give a share.
              <br />
              Grow a <span className="text-leaf">future</span>.
            </h1>
            <p className="text-lg text-ink/70 max-w-md">
              GrowGift turns real fractional shares into physical gifts. No brokerage account
              needed to give one — just pick a company, and we ship the box.
            </p>
            <div className="flex flex-wrap gap-4">
              <Link
                href="/gift/new"
                className="rounded-full bg-leaf text-white px-6 py-3 font-semibold hover:bg-leaf-deep transition-colors shadow-lift"
              >
                Build a gift box
              </Link>
              <Link
                href="/redeem"
                className="rounded-full border-[1.5px] border-ink px-6 py-3 font-semibold hover:bg-mint transition-colors"
              >
                I received a code
              </Link>
            </div>
            <p className="text-sm text-ink/70">
              Got a child account?{" "}
              <Link href="/kid-login" className="font-semibold text-leaf-deep underline underline-offset-2 hover:text-leaf">
                Child login →
              </Link>
            </p>
          </div>
          <div className="flex justify-center lg:justify-end">
            <Ticket
              symbol="NTDO"
              companyName="Nintendo Co., Ltd."
              recipientName="Maya, 9"
              message="Happy birthday — you own a piece of Mario now."
              code="GG-84KQ-29MN-XP8D"
            />
          </div>
        </div>
      </section>

      {/* How it works */}
      <section className="mx-auto max-w-6xl px-5 py-20">
        <h2 className="font-display text-4xl font-semibold mb-12">How it works</h2>
        <div className="grid gap-8 md:grid-cols-3">
          {steps.map((s) => (
            <div key={s.n} className="rounded-2xl border-[1.5px] border-ink bg-white p-6 shadow-card">
              <p className="font-mono text-ribbon font-semibold">{s.n}</p>
              <h3 className="font-display text-2xl mt-3 mb-2">{s.title}</h3>
              <p className="text-sm text-ink/70 leading-relaxed">{s.body}</p>
            </div>
          ))}
        </div>
      </section>

      {/* Family */}
      <section className="bg-ink text-canvas">
        <div className="mx-auto max-w-6xl px-5 py-20 grid gap-10 lg:grid-cols-2 lg:items-center">
          <div className="space-y-5">
            <h2 className="font-display text-4xl font-semibold">
              Three jars. One patient little investor.
            </h2>
            <p className="text-canvas/70 max-w-md">
              Kids get a wallet with Short-Term, Medium-Term and Long-Term jars. Chores earn
              rewards that split across all three — and everything transfers to them, for real,
              at 18.
            </p>
            <Link
              href="/family"
              className="inline-block rounded-full bg-ribbon text-ink px-6 py-3 font-semibold hover:brightness-110 transition"
            >
              Explore family accounts
            </Link>
          </div>
          <div className="grid grid-cols-3 gap-4">
            {[
              { name: "Short-Term", years: "1 yr", pct: "33%" },
              { name: "Medium-Term", years: "3 yrs", pct: "33%" },
              { name: "Long-Term", years: "10 yrs", pct: "34%" },
            ].map((j) => (
              <div key={j.name} className="rounded-2xl bg-canvas text-ink p-5 text-center space-y-1">
                <p className="font-display text-lg">{j.name}</p>
                <p className="font-mono text-2xl font-semibold text-leaf-deep">{j.pct}</p>
                <p className="text-xs text-ink/60">locked {j.years}</p>
              </div>
            ))}
          </div>
        </div>
      </section>
    </>
  );
}
