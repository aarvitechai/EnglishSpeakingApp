"use client";

import Link from "next/link";
import Script from "next/script";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useRef, useState } from "react";
import { api, homeFor, type AppConfig, type Me } from "@/lib/api";
import { ErrorNote, errorText, Logo } from "@/components/shell";

/* eslint-disable @typescript-eslint/no-explicit-any */

function LoginForm() {
  const router = useRouter();
  const params = useSearchParams();
  const [mode, setMode] = useState<"login" | "register">(params.get("mode") === "register" ? "register" : "login");
  const [form, setForm] = useState({ name: "", email: "", password: "", acceptTerms: false });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [clientId, setClientId] = useState<string | null>(null);
  const googleRef = useRef<HTMLDivElement>(null);
  const termsRef = useRef(false);
  termsRef.current = form.acceptTerms;

  useEffect(() => { api<AppConfig>("/config").then((c) => setClientId(c.googleClientId)).catch(() => {}); }, []);

  function initGoogle() {
    const g = (window as any).google;
    if (!g || !clientId || !googleRef.current) return;
    g.accounts.id.initialize({
      client_id: clientId,
      callback: async (r: { credential: string }) => {
        try {
          const me = await api<Me>("/auth/google", { method: "POST", json: { idToken: r.credential, acceptTerms: termsRef.current } });
          router.replace(homeFor(me));
        } catch (e) { setError(errorText(e)); }
      },
    });
    g.accounts.id.renderButton(googleRef.current, { width: 320, text: "continue_with" });
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const me = await api<Me>(mode === "login" ? "/auth/login" : "/auth/register", { method: "POST", json: form });
      router.replace(homeFor(me));
    } catch (err) {
      setError(errorText(err));
      setBusy(false);
    }
  }

  const register = mode === "register";
  return (
    <div className="mx-auto max-w-sm px-4 py-10">
      <Logo />
      <h1 className="mt-6 text-3xl font-semibold">{register ? "Create your account" : "Welcome back"}</h1>
      <form onSubmit={submit} className="mt-6 space-y-4">
        {register && (
          <div>
            <label className="label" htmlFor="name">Name</label>
            <input id="name" className="field" required autoComplete="name" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          </div>
        )}
        <div>
          <label className="label" htmlFor="email">Email</label>
          <input id="email" type="email" className="field" required autoComplete="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
        </div>
        <div>
          <label className="label" htmlFor="password">Password</label>
          <input id="password" type="password" className="field" required minLength={register ? 8 : undefined} autoComplete={register ? "new-password" : "current-password"} value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} />
          {register && <p className="mt-1 text-xs text-ink-3">At least 8 characters.</p>}
        </div>
        {register && (
          <label className="flex items-start gap-2 text-sm">
            <input type="checkbox" className="mt-1 size-4" checked={form.acceptTerms} onChange={(e) => setForm({ ...form, acceptTerms: e.target.checked })} />
            <span>I agree to the <Link href="/privacy/" className="underline" target="_blank">privacy notice and terms</Link>.</span>
          </label>
        )}
        <ErrorNote message={error} />
        <button className="btn w-full" disabled={busy || (register && !form.acceptTerms)}>{busy ? "Please wait…" : register ? "Create account" : "Sign in"}</button>
      </form>
      {clientId && (
        <>
          <Script src="https://accounts.google.com/gsi/client" onReady={initGoogle} />
          <div className="mt-4" ref={googleRef} />
        </>
      )}
      {!register && <p className="mt-4 text-sm text-ink-2">Forgot your password? Please contact support and an admin will reset it.</p>}
      <button className="mt-4 cursor-pointer text-sm underline" onClick={() => { setMode(register ? "login" : "register"); setError(null); }}>
        {register ? "Already have an account? Sign in" : "New here? Create an account"}
      </button>
    </div>
  );
}

export default function LoginPage() {
  return <Suspense><LoginForm /></Suspense>;
}
