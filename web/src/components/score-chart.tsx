"use client";

import { useState } from "react";
import { CATEGORY_LABEL, prettyDate, type Progress } from "@/lib/api";

const W = 640, H = 220, L = 34, R = 14, T = 14, B = 28;

/** Overall score over time. One series, so the card title names it and no legend is needed. */
export function ScoreChart({ sessions }: { sessions: Progress["sessions"] }) {
  const [active, setActive] = useState<number | null>(null);
  const [asTable, setAsTable] = useState(false);

  const x = (i: number) => (sessions.length === 1 ? (L + W - R) / 2 : L + (i * (W - L - R)) / (sessions.length - 1));
  const y = (v: number) => T + ((100 - v) * (H - T - B)) / 100;
  const path = sessions.map((s, i) => `${i ? "L" : "M"}${x(i)} ${y(s.score)}`).join(" ");
  const shown = active === null ? null : sessions[active];

  return (
    <div>
      <div className="mb-2 flex items-center justify-between gap-3">
        <h2 className="text-lg font-semibold">Overall score over time</h2>
        <button className="cursor-pointer text-sm text-ink-2 underline" onClick={() => setAsTable(!asTable)}>
          {asTable ? "Show chart" : "Show table"}
        </button>
      </div>
      {asTable ? (
        <table className="w-full text-left text-sm">
          <thead><tr className="border-b border-line text-ink-2"><th className="py-1 font-medium">Date</th><th className="font-medium">Type</th><th className="text-right font-medium">Score</th></tr></thead>
          <tbody>
            {sessions.map((s) => (
              <tr key={s.id} className="border-b border-line">
                <td className="py-1">{prettyDate(s.createdAt)}</td><td>{CATEGORY_LABEL[s.category]}</td><td className="text-right tabular-nums">{s.score}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <div className="relative">
          <svg viewBox={`0 0 ${W} ${H}`} className="w-full" role="img" aria-label={`Line chart of ${sessions.length} interview scores. Latest score ${sessions[sessions.length - 1].score} out of 100.`} onMouseLeave={() => setActive(null)}>
            {[0, 50, 100].map((v) => (
              <g key={v}>
                <line x1={L} x2={W - R} y1={y(v)} y2={y(v)} stroke="var(--line)" strokeWidth="1" />
                <text x={L - 8} y={y(v) + 4} textAnchor="end" fontSize="12" fill="var(--ink-3)">{v}</text>
              </g>
            ))}
            {sessions.length > 1 && <path d={path} fill="none" stroke="var(--series)" strokeWidth="2" strokeLinejoin="round" />}
            {sessions.map((s, i) => (
              <g key={s.id}>
                <circle cx={x(i)} cy={y(s.score)} r={active === i ? 6 : 4.5} fill="var(--series)" stroke="var(--surface)" strokeWidth="2" />
                <circle cx={x(i)} cy={y(s.score)} r="18" fill="transparent" tabIndex={0} aria-label={`${prettyDate(s.createdAt)}: ${s.score}`}
                  onMouseEnter={() => setActive(i)} onFocus={() => setActive(i)} onBlur={() => setActive(null)} />
              </g>
            ))}
            <text x={L} y={H - 8} fontSize="12" fill="var(--ink-3)">{prettyDate(sessions[0].createdAt)}</text>
            {sessions.length > 1 && <text x={W - R} y={H - 8} textAnchor="end" fontSize="12" fill="var(--ink-3)">{prettyDate(sessions[sessions.length - 1].createdAt)}</text>}
          </svg>
          {shown && (
            <div className="pointer-events-none absolute rounded-md border border-line bg-surface px-2 py-1 text-xs shadow-sm"
              style={{ left: `${(x(active!) / W) * 100}%`, top: `${(y(shown.score) / H) * 100}%`, transform: active! > sessions.length / 2 ? "translate(-105%, -120%)" : "translate(5%, -120%)" }}>
              <div className="font-semibold tabular-nums">{shown.score} / 100</div>
              <div className="text-ink-2">{CATEGORY_LABEL[shown.category]}, {prettyDate(shown.createdAt)}</div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
