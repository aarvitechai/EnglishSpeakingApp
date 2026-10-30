"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { api, ApiError, type Me } from "@/lib/api";

export function Logo() {
  return (
    <Link href="/" className="font-display text-xl font-semibold text-ink" style={{ fontFamily: "var(--font-heading)" }}>
      Pratibha
    </Link>
  );
}

/** Loads the signed-in user, sending anyone else to the login page. */
export function useMe() {
  const router = useRouter();
  const [me, setMe] = useState<Me | null>(null);
  useEffect(() => {
    api<Me>("/me/").then(setMe).catch((e) => {
      if (e instanceof ApiError && e.status === 401) router.replace("/login/");
    });
  }, [router]);
  return me;
}

export function AppShell({ children, wide }: { children: React.ReactNode; wide?: boolean }) {
  const me = useMe();
  const router = useRouter();
  async function logout() {
    await api("/auth/logout", { method: "POST" });
    router.replace("/");
  }
  return (
    <div>
      <header className="border-b border-line bg-surface">
        <div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-2 px-4 py-3">
          <Logo />
          <nav className="flex items-center gap-4 text-sm font-medium">
            <Link href="/dashboard/" className="py-2 hover:underline">Dashboard</Link>
            <Link href="/settings/" className="py-2 hover:underline">Settings</Link>
            <button onClick={logout} className="cursor-pointer py-2 text-ink-2 hover:underline">Sign out</button>
          </nav>
        </div>
      </header>
      <main className={`mx-auto px-4 py-6 sm:py-8 ${wide ? "max-w-5xl" : "max-w-3xl"}`}>
        {me ? children : <p className="text-ink-2">Loading…</p>}
      </main>
    </div>
  );
}

export function ErrorNote({ message }: { message: string | null }) {
  if (!message) return null;
  return <p role="alert" className="rounded-lg bg-warn-soft px-3 py-2 text-sm text-warn">{message}</p>;
}

export const errorText = (e: unknown) => (e instanceof Error ? e.message : "Something went wrong. Please try again.");
