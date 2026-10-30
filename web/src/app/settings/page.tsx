"use client";

import Script from "next/script";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { AppShell, ErrorNote, errorText } from "@/components/shell";
import { api, type AppConfig, type ProfileDto, type Progress } from "@/lib/api";

/* eslint-disable @typescript-eslint/no-explicit-any */

export default function Settings() {
  const router = useRouter();
  const [profile, setProfile] = useState<ProfileDto | null>(null);
  const [config, setConfig] = useState<AppConfig | null>(null);
  const [progress, setProgress] = useState<Progress | null>(null);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api<ProfileDto>("/profile/").then(setProfile).catch((e) => setError(errorText(e)));
    api<AppConfig>("/config").then(setConfig).catch(() => {});
    api<Progress>("/progress").then(setProgress).catch(() => {});
  }, []);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setSaved(false);
    try {
      setProfile(await api<ProfileDto>("/profile/", { method: "PUT", json: profile }));
      setSaved(true);
    } catch (err) { setError(errorText(err)); }
  }

  async function removeResume() {
    try { setProfile(await api<ProfileDto>("/profile/resume", { method: "DELETE" })); }
    catch (err) { setError(errorText(err)); }
  }

  async function upgrade() {
    setError(null);
    try {
      const order = await api<{ orderId: string; keyId: string; amountPaise: number }>("/billing/checkout", { method: "POST" });
      const checkout = new (window as any).Razorpay({
        key: order.keyId,
        order_id: order.orderId,
        amount: order.amountPaise,
        currency: "INR",
        name: "Pratibha",
        description: "Pro plan, 30 days",
        handler: async (r: { razorpay_order_id: string; razorpay_payment_id: string; razorpay_signature: string }) => {
          try {
            await api("/billing/verify", { method: "POST", json: { orderId: r.razorpay_order_id, paymentId: r.razorpay_payment_id, signature: r.razorpay_signature } });
            setProgress(await api<Progress>("/progress"));
          } catch (err) { setError(errorText(err)); }
        },
      });
      checkout.open();
    } catch (err) { setError(errorText(err)); }
  }

  async function deleteAccount() {
    if (!window.confirm("Permanently delete your account, interviews and reports? This cannot be undone.")) return;
    try {
      await api("/me/", { method: "DELETE" });
      router.replace("/");
    } catch (err) { setError(errorText(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-3xl font-semibold">Settings</h1>
      <div className="mt-4"><ErrorNote message={error} /></div>

      {profile && (
        <form onSubmit={save} className="card mt-4 space-y-4">
          <h2 className="text-xl font-semibold">Profile</h2>
          <div>
            <label className="label" htmlFor="role">Target role</label>
            <input id="role" className="field" maxLength={200} value={profile.targetRole ?? ""} onChange={(e) => setProfile({ ...profile, targetRole: e.target.value })} />
          </div>
          <div>
            <label className="label" htmlFor="level">Experience</label>
            <select id="level" className="field" value={profile.experienceLevel ?? ""} onChange={(e) => setProfile({ ...profile, experienceLevel: e.target.value })}>
              <option value="">Choose one</option>
              <option value="student">Final-year student</option>
              <option value="fresher">Fresher</option>
              <option value="early">1–3 years</option>
            </select>
          </div>
          {profile.resumeFileName && (
            <p className="text-sm text-ink-2">Resume text from {profile.resumeFileName}. <button type="button" className="cursor-pointer underline" onClick={removeResume}>Remove it</button></p>
          )}
          <div className="flex items-center gap-3">
            <button className="btn">Save</button>
            {saved && <span role="status" className="text-sm text-ink-2">Saved</span>}
          </div>
        </form>
      )}

      {progress && (
        <section className="card mt-4">
          <h2 className="text-xl font-semibold">Plan</h2>
          <p className="mt-1 text-ink-2">
            You&rsquo;re on the {progress.plan === "pro" ? "Pro" : "Free"} plan, with {progress.limit} interviews a month.
          </p>
          {progress.plan !== "pro" && config?.billingEnabled && (
            <>
              <Script src="https://checkout.razorpay.com/v1/checkout.js" />
              <button className="btn mt-3" onClick={upgrade}>Upgrade to Pro, ₹{(config.proPricePaise / 100).toLocaleString("en-IN")} for 30 days</button>
            </>
          )}
          {progress.plan !== "pro" && config && !config.billingEnabled && <p className="mt-2 text-sm text-ink-3">Upgrades are not available yet.</p>}
        </section>
      )}

      <section className="card mt-4">
        <h2 className="text-xl font-semibold">Your data</h2>
        <p className="mt-1 text-ink-2">Download everything we hold about you, or delete it for good.</p>
        <div className="mt-3 flex flex-wrap gap-3">
          <a className="btn btn-quiet" href="/api/me/export">Download my data</a>
          <button className="btn btn-danger" onClick={deleteAccount}>Delete my account</button>
        </div>
      </section>
    </AppShell>
  );
}
