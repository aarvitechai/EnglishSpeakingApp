"use client";

import Link from "next/link";
import Script from "next/script";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { ErrorNote, errorText, Logo } from "@/components/shell";
import { api, ApiError, LEVEL_NAME, PLAN_NAME, prettyDate, rupees, type AppConfig, type Catalog, type Progress } from "@/lib/api";

/* eslint-disable @typescript-eslint/no-explicit-any */

export default function Pricing() {
  const router = useRouter();
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [config, setConfig] = useState<AppConfig | null>(null);
  const [progress, setProgress] = useState<Progress | null>(null);
  const [signedIn, setSignedIn] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  async function loadProgress() {
    try {
      setProgress(await api<Progress>("/progress"));
      setSignedIn(true);
    } catch (e) {
      if (!(e instanceof ApiError && e.status === 401)) setError(errorText(e));
    }
  }

  useEffect(() => {
    api<Catalog>("/plans").then(setCatalog).catch((e) => setError(errorText(e)));
    api<AppConfig>("/config").then(setConfig).catch(() => {});
    loadProgress();
  }, []);

  async function buy(plan: string) {
    setError(null);
    setNotice(null);
    if (!signedIn) return router.push("/login/?mode=register");
    try {
      const order = await api<{ orderId: string; keyId: string; amountPaise: number }>("/billing/checkout", { method: "POST", json: { plan } });
      new (window as any).Razorpay({
        key: order.keyId,
        order_id: order.orderId,
        amount: order.amountPaise,
        currency: "INR",
        name: "NeuroTara",
        description: `${PLAN_NAME[plan]} plan, 30 days`,
        theme: { color: "#be1a2e" },
        handler: async (r: { razorpay_order_id: string; razorpay_payment_id: string; razorpay_signature: string }) => {
          try {
            await api("/billing/verify", { method: "POST", json: { orderId: r.razorpay_order_id, paymentId: r.razorpay_payment_id, signature: r.razorpay_signature } });
            await loadProgress();
            setNotice(`Payment received. Your ${PLAN_NAME[plan]} plan is active.`);
          } catch (err) { setError(errorText(err)); }
        },
      }).open();
    } catch (e) { setError(errorText(e)); }
  }

  return (
    <div className="mx-auto max-w-5xl px-4 py-5">
      <header className="flex items-center justify-between">
        <Logo />
        <Link href={signedIn ? "/dashboard/" : "/login/"} className="py-2 text-sm font-semibold hover:underline">{signedIn ? "Dashboard" : "Sign in"}</Link>
      </header>
      <h1 className="mt-8 text-4xl font-semibold">Plans</h1>
      <p className="mt-2 text-ink-2">Start free from Level 1. Upgrade when you are ready for harder rounds.</p>
      <div className="mt-4 space-y-2">
        <ErrorNote message={error} />
        {notice && <p role="status" className="rounded-lg bg-accent-soft px-3 py-2 text-sm">{notice}</p>}
      </div>
      {config?.billingEnabled && <Script src="https://checkout.razorpay.com/v1/checkout.js" />}

      <div className="mt-6 grid gap-4 md:grid-cols-3">
        {catalog?.plans.map((p) => {
          const current = progress?.plan === p.id;
          return (
            <section key={p.id} className={`card flex flex-col ${p.id === "plus" ? "border-accent" : ""}`}>
              <h2 className="text-xl font-semibold">{PLAN_NAME[p.id]}</h2>
              <p className="mt-2 text-3xl font-bold tabular-nums">{p.pricePaise ? rupees(p.pricePaise) : "₹0"}</p>
              <p className="text-sm text-ink-2">{p.pricePaise ? `for ${p.days} days` : "always"}</p>
              <ul className="mt-4 flex-1 space-y-2 text-sm">
                <li>{p.interviewsPerMonth} interviews a month</li>
                <li>Levels {p.levels[0]} to {p.levels[p.levels.length - 1]}: {LEVEL_NAME[p.levels[0]]} to {LEVEL_NAME[p.levels[p.levels.length - 1]]}</li>
                <li>Voice, video and typed interviews</li>
                <li>Reports and practice plans</li>
              </ul>
              {current && progress?.renewsAt && <p className="mt-3 text-sm text-ink-2">Active until {prettyDate(progress.renewsAt)}</p>}
              {p.pricePaise === 0
                ? <Link href={signedIn ? "/new/" : "/login/?mode=register"} className="btn btn-quiet mt-4">{current ? "Your plan" : "Start free"}</Link>
                : config?.billingEnabled
                  ? <button className="btn mt-4" onClick={() => buy(p.id)}>{current ? "Add 30 more days" : `Get ${PLAN_NAME[p.id]}`}</button>
                  : <p className="mt-4 rounded-lg bg-surface-2 px-3 py-2 text-center text-sm text-ink-2">{current ? "Your plan" : "Online payment coming soon"}</p>}
            </section>
          );
        })}
      </div>
      <p className="mt-4 text-sm text-ink-3">Paid plans are 30-day passes in INR, paid by UPI or card through Razorpay. They do not renew by themselves.</p>
    </div>
  );
}
