"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { AppShell, ErrorNote, errorText } from "@/components/shell";
import { ScoreChart } from "@/components/score-chart";
import { api, CATEGORY_LABEL, levelLabel, prettyDate, type PracticePlan, type Progress, type SessionRow } from "@/lib/api";

const STATUS: Record<string, string> = {
  created: "Not started", active: "In progress", evaluating: "Scoring…", failed: "Scoring failed", abandoned: "Ended early",
};

function hrefFor(s: SessionRow) {
  return s.status === "created" || s.status === "active" ? `/interview/?id=${s.id}` : `/report/?id=${s.id}`;
}

export default function Dashboard() {
  const [progress, setProgress] = useState<Progress | null>(null);
  const [sessions, setSessions] = useState<SessionRow[] | null>(null);
  const [plans, setPlans] = useState<PracticePlan[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([api<Progress>("/progress"), api<SessionRow[]>("/sessions/"), api<PracticePlan[]>("/practice-plans")])
      .then(([p, s, d]) => { setProgress(p); setSessions(s); setPlans(d); })
      .catch((e) => setError(errorText(e)));
  }, []);

  async function markDone(id: string) {
    setPlans((list) => list.filter((p) => p.id !== id));
    await api(`/practice-plans/${id}`, { method: "PATCH", json: { status: "done" } }).catch((e) => setError(errorText(e)));
  }

  const atLimit = progress ? progress.used >= progress.limit : false;

  return (
    <AppShell wide>
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-3xl font-semibold">Your practice</h1>
          {progress && <p className="mt-1 text-sm text-ink-2">{progress.used} of {progress.limit} interviews used this month</p>}
        </div>
        {atLimit ? <Link href="/pricing/" className="btn">Get more interviews</Link> : <Link href="/new/" className="btn">Start an interview</Link>}
      </div>
      <div className="mt-4"><ErrorNote message={error} /></div>

      {sessions?.length === 0 && (
        <div className="card mt-6">
          <h2 className="text-xl font-semibold">Your first interview takes about 10 minutes</h2>
          <p className="mt-1 text-ink-2">Start with a Level 1 HR round. It assumes no experience at all. Your scores and practice plan will appear here afterwards.</p>
          <Link href="/new/" className="btn mt-4">Start now</Link>
        </div>
      )}

      {progress && progress.sessions.length > 0 && (
        <div className="mt-6 grid items-start gap-4 lg:grid-cols-[1.5fr_1fr]">
          <section className="card"><ScoreChart sessions={progress.sessions} /></section>
          <section className="card">
            <h2 className="text-lg font-semibold">Skills</h2>
            <p className="text-sm text-ink-2">Out of 5, weakest first</p>
            <ul className="mt-3 space-y-3">
              {progress.skills.map((s) => (
                <li key={s.name}>
                  <div className="flex justify-between gap-2 text-sm">
                    <span>{s.name}</span>
                    <span className="text-ink-2 tabular-nums">
                      {s.latest.toFixed(1)}
                      {s.count > 1 && <> ({s.latest >= s.first ? "+" : "−"}{Math.abs(s.latest - s.first).toFixed(1)} since first)</>}
                    </span>
                  </div>
                  <div className="mt-1 h-1.5 rounded-full bg-surface-2"><div className="h-full rounded-full bg-accent" style={{ width: `${(s.latest / 5) * 100}%` }} /></div>
                </li>
              ))}
            </ul>
          </section>
        </div>
      )}

      {plans.length > 0 && (
        <section className="mt-6">
          <h2 className="text-xl font-semibold">Practice plan</h2>
          <div className="mt-3 grid gap-4 md:grid-cols-3">
            {plans.slice(0, 3).map((p) => (
              <div key={p.id} className="card flex flex-col">
                <h3 className="font-semibold">{p.skill}</h3>
                <p className="text-xs text-ink-3">By {prettyDate(p.dueDate)}</p>
                <ul className="mt-2 flex-1 list-disc space-y-1 pl-4 text-sm text-ink-2">{p.drills.map((d) => <li key={d}>{d}</li>)}</ul>
                <button className="btn btn-quiet mt-3" onClick={() => markDone(p.id)}>Mark as done</button>
              </div>
            ))}
          </div>
        </section>
      )}

      {sessions && sessions.length > 0 && (
        <section className="mt-6">
          <h2 className="text-xl font-semibold">History</h2>
          <ul className="card mt-3 divide-y divide-line p-0!">
            {sessions.map((s) => (
              <li key={s.id}>
                <Link href={hrefFor(s)} className="flex items-center justify-between gap-3 px-4 py-3 hover:bg-surface-2">
                  <span>
                    <span className="font-medium">{CATEGORY_LABEL[s.category]}, {levelLabel(s.level)}</span>
                    <span className="block text-sm text-ink-3">{prettyDate(s.createdAt)} · {s.mode}</span>
                  </span>
                  <span className="text-right text-sm text-ink-2 tabular-nums">{s.score !== null ? <strong className="text-lg text-ink">{s.score}</strong> : STATUS[s.status] ?? s.status}</span>
                </Link>
              </li>
            ))}
          </ul>
        </section>
      )}
    </AppShell>
  );
}
