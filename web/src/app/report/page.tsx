"use client";

import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense, useCallback, useEffect, useState } from "react";
import { AppShell, ErrorNote, errorText } from "@/components/shell";
import { api, CATEGORY_LABEL, levelLabel, prettyDate, type Report } from "@/lib/api";

function Stat({ label, value, note }: { label: string; value: string; note?: string }) {
  return (
    <div>
      <p className="text-sm text-ink-2">{label}</p>
      <p className="text-2xl font-semibold tabular-nums">{value}</p>
      {note && <p className="text-xs text-ink-3">{note}</p>}
    </div>
  );
}

function ReportView() {
  const id = useSearchParams().get("id");
  const [report, setReport] = useState<Report | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [rating, setRating] = useState<number | null>(null);

  const load = useCallback(async () => {
    try {
      const r = await api<Report>(`/sessions/${id}/report`);
      setReport(r);
      setRating(r.evaluation?.helpfulRating ?? null);
    } catch (e) {
      setError(errorText(e));
    }
  }, [id]);

  useEffect(() => { if (id) load(); }, [id, load]);

  const scoring = report?.session.status === "evaluating";

  // SignalR tells us the moment the report is ready; polling covers a dropped connection.
  useEffect(() => {
    if (!scoring) return;
    const hub = new HubConnectionBuilder().withUrl("/hubs/reports").configureLogging(LogLevel.None).withAutomaticReconnect().build();
    hub.on("reportReady", (e: { sessionId: string }) => { if (e.sessionId === id) load(); });
    hub.start().catch(() => {});
    const poll = setInterval(load, 4000);
    return () => { clearInterval(poll); hub.stop(); };
  }, [scoring, id, load]);

  async function retry() {
    await api(`/sessions/${id}/retry-evaluation`, { method: "POST" }).catch((e) => setError(errorText(e)));
    load();
  }

  async function rate(value: number) {
    setRating(value);
    await api(`/sessions/${id}/rating`, { method: "POST", json: { rating: value } }).catch((e) => setError(errorText(e)));
  }

  const e = report?.evaluation;
  const d = report?.delivery;
  const turnBySeq = new Map(report?.session.turns.map((t) => [t.seq, t]));

  return (
    <AppShell>
      <ErrorNote message={error} />
      {!report && !error && <p className="text-ink-2">Loading your report…</p>}

      {scoring && (
        <div className="card text-center" role="status">
          <h1 className="text-2xl font-semibold">Scoring your interview</h1>
          <p className="mt-1 text-ink-2">This usually takes under a minute. You can stay here, and the report will appear on its own.</p>
        </div>
      )}
      {report?.session.status === "failed" && (
        <div className="card">
          <h1 className="text-2xl font-semibold">We could not finish your report</h1>
          <p className="mt-1 text-ink-2">{report.session.failureReason} Your answers are safe.</p>
          <button className="btn mt-4" onClick={retry}>Try again</button>
        </div>
      )}
      {report?.session.status === "abandoned" && (
        <div className="card">
          <h1 className="text-2xl font-semibold">No answers to score</h1>
          <p className="mt-1 text-ink-2">This interview ended before you answered a question.</p>
          <Link href="/new/" className="btn mt-4">Start a new interview</Link>
        </div>
      )}

      {e && report && (
        <>
          <p className="text-sm text-ink-2">{CATEGORY_LABEL[report.session.category]}, {levelLabel(report.session.level)} · {prettyDate(report.session.createdAt)}</p>
          <div className="mt-1 flex items-baseline gap-3">
            <h1 className="text-6xl font-semibold tabular-nums">{e.overallScore}</h1>
            <span className="text-ink-2">out of 100</span>
          </div>
          <p className="mt-2 text-lg">{e.feedback.summary}</p>

          {e.modelVersion.startsWith("rule-based") && (
            <p className="mt-3 rounded-lg bg-warn-soft px-3 py-2 text-sm text-warn">
              Scored by automatic rules, not AI. They check structure and specifics, such as examples, your own actions and results. They can&rsquo;t judge whether your content is correct.
            </p>
          )}

          <section className="mt-8">
            <h2 className="text-2xl font-semibold">Your top {e.feedback.improvements.length === 1 ? "improvement" : `${e.feedback.improvements.length} improvements`}</h2>
            <ol className="mt-3 space-y-4">
              {e.feedback.improvements.map((imp, i) => (
                <li key={i} className="card">
                  <h3 className="text-lg font-semibold">{i + 1}. {imp.title}</h3>
                  <p className="mt-3 text-sm text-ink-2">You said</p>
                  <blockquote className="mt-1"><mark>&ldquo;{imp.quote}&rdquo;</mark></blockquote>
                  <p className="mt-3 text-ink-2">{imp.why}</p>
                  <p className="mt-3 text-sm font-semibold">A stronger version</p>
                  <p className="mt-1">{imp.rewrite}</p>
                  <p className="mt-3 text-sm font-semibold">Practise today</p>
                  <ul className="mt-1 list-disc space-y-1 pl-5 text-sm text-ink-2">{imp.drills.map((x) => <li key={x}>{x}</li>)}</ul>
                </li>
              ))}
            </ol>
          </section>

          {e.feedback.strengths.length > 0 && (
            <section className="mt-8">
              <h2 className="text-2xl font-semibold">What worked</h2>
              {e.feedback.strengths.map((s, i) => (
                <div key={i} className="card mt-3">
                  <blockquote><mark>&ldquo;{s.quote}&rdquo;</mark></blockquote>
                  <p className="mt-2 text-ink-2">{s.why}</p>
                </div>
              ))}
            </section>
          )}

          {d && (
            <section className="mt-8">
              <h2 className="text-2xl font-semibold">Delivery</h2>
              <p className="text-sm text-ink-2">Counted by code from your transcript. These don&rsquo;t affect your score, and accent is never scored.</p>
              <div className="card mt-3 grid grid-cols-2 gap-4 sm:grid-cols-3">
                {d.wpm !== null && <Stat label="Pace" value={`${d.wpm} wpm`} note="Around 120–160 is comfortable" />}
                <Stat label="Fillers" value={String(d.fillerCount)} note="um, uh, you know, basically…" />
                {report.session.mode === "voice" && <Stat label="Long pauses" value={String(d.longPauses)} note="Over 3 seconds" />}
                <Stat label="You spoke" value={`${Math.round(d.talkRatio * 100)}%`} note="Share of all words" />
                <Stat label="Average answer" value={d.avgAnswerSec !== null ? `${Math.round(d.avgAnswerSec)} s` : `${Math.round(d.avgAnswerWords)} words`} />
              </div>
            </section>
          )}

          <details className="card mt-8">
            <summary className="cursor-pointer font-semibold">Score details</summary>
            <ul className="mt-3 space-y-3">
              {e.scores.dimensions.map((dim) => (
                <li key={dim.key}>
                  <div className="flex justify-between text-sm"><span>{dim.name}</span><span className="tabular-nums">{dim.score.toFixed(1)} / 5</span></div>
                  <div className="mt-1 h-1.5 rounded-full bg-surface-2"><div className="h-full rounded-full bg-accent" style={{ width: `${(dim.score / 5) * 100}%` }} /></div>
                </li>
              ))}
            </ul>
          </details>

          <details className="card mt-3">
            <summary className="cursor-pointer font-semibold">Answer by answer</summary>
            <div className="mt-3 space-y-5">
              {e.scores.answers.map((a) => {
                const dims = new Map(e.scores.dimensions.map((x) => [x.key, x.name]));
                return (
                  <div key={a.seq}>
                    <p>{turnBySeq.get(a.seq)?.text}</p>
                    <ul className="mt-2 space-y-1 text-sm text-ink-2">
                      {a.scores.map((s) => <li key={s.dimension}><strong className="text-ink">{dims.get(s.dimension)}: {s.score}/5.</strong> &ldquo;{s.evidence}&rdquo;</li>)}
                    </ul>
                  </div>
                );
              })}
            </div>
          </details>

          <details className="card mt-3">
            <summary className="cursor-pointer font-semibold">Full transcript</summary>
            <div className="mt-3 space-y-2 text-sm">
              {report.session.turns.map((t) => <p key={t.seq}><strong>{t.speaker === "candidate" ? "You" : "Interviewer"}:</strong> {t.text}</p>)}
            </div>
          </details>

          <section className="mt-8 flex flex-wrap items-center gap-2">
            <span className="text-sm font-medium">Was this feedback useful?</span>
            {[1, 2, 3, 4, 5].map((v) => (
              <button key={v} aria-pressed={rating === v} aria-label={`${v} out of 5`} onClick={() => rate(v)}
                className={`size-11 cursor-pointer rounded-lg border ${rating === v ? "border-accent bg-accent-soft font-semibold" : "border-line"}`}>{v}</button>
            ))}
          </section>

          <div className="mt-8 flex flex-wrap gap-3">
            <Link href="/new/" className="btn">Practise again</Link>
            <Link href="/dashboard/" className="btn btn-quiet">Back to dashboard</Link>
          </div>
        </>
      )}
    </AppShell>
  );
}

export default function ReportPage() {
  return <Suspense><ReportView /></Suspense>;
}
