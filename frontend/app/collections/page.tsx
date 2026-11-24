import Link from "next/link";
import { collections } from "@/lib/collections";

export default function Collections() {
  return (
    <div className="mx-auto max-w-6xl px-5 py-16">
      <div className="max-w-xl mb-12 space-y-3">
        <h1 className="font-display text-5xl font-semibold">Collections</h1>
        <p className="text-ink/70">
          Curated by our investment team so you don&apos;t have to scroll a stock screener.
          Every pick is a real, listed company.
        </p>
      </div>

      <div className="grid gap-6 md:grid-cols-2 lg:grid-cols-3">
        {collections.map((c) => (
          <Link
            key={c.slug}
            href={`/gift/new?collection=${c.slug}`}
            className="group rounded-2xl border-[1.5px] border-ink bg-white p-6 shadow-card hover:-translate-y-1 hover:shadow-lift transition-all"
          >
            <span className="text-4xl">{c.emoji}</span>
            <h2 className="font-display text-2xl mt-4 mb-1 group-hover:text-leaf-deep">
              {c.name}
            </h2>
            <p className="text-sm text-ink/70 mb-4">{c.tagline}</p>
            <div className="flex flex-wrap gap-2">
              {c.companies.map((co) => (
                <span
                  key={co.symbol}
                  className="rounded-full bg-mint px-3 py-1 text-xs font-mono font-semibold"
                >
                  {co.symbol}
                </span>
              ))}
            </div>
          </Link>
        ))}
      </div>
    </div>
  );
}
