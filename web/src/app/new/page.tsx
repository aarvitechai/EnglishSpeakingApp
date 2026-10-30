"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { AppShell, ErrorNote, errorText } from "@/components/shell";
import { api, PLAN_NAME, type Catalog, type ProfileDto, type Progress } from "@/lib/api";
import { speechRecognitionSupported } from "@/lib/voice";

const CATEGORIES = [
  { id: "hr", name: "HR", desc: "Introductions, motivation, strengths, salary and fit." },
  { id: "behavioural", name: "Behavioural", desc: "\"Tell me about a time…\" stories, scored on STAR." },
  { id: "technical", name: "Technical", desc: "DSA, DBMS, OS, networks, OOP and your projects." },
];
function Choice({ selected, onClick, title, desc }: { selected: boolean; onClick: () => void; title: string; desc: string }) {
  return (
    <button type="button" aria-pressed={selected} onClick={onClick}
      className={`cursor-pointer rounded-lg border p-3 text-left ${selected ? "border-accent bg-accent-soft" : "border-line bg-surface"}`}>
      <span className="block font-semibold">{title}</span>
      <span className="block text-sm text-ink-2">{desc}</span>
    </button>
  );
}

export default function NewInterview() {
  const router = useRouter();
  const [category, setCategory] = useState("hr");
  const [level, setLevel] = useState("1");
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [plan, setPlan] = useState("free");
  const [mode, setMode] = useState<"voice" | "text">("text");
  const [voiceOk, setVoiceOk] = useState(false);
  const [consent, setConsent] = useState(false);
  const [camera, setCamera] = useState(false);
  const [jobDescription, setJd] = useState("");
  const [profile, setProfile] = useState<ProfileDto | null>(null);
  const [targetRole, setTargetRole] = useState("");
  const [uploading, setUploading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const ok = speechRecognitionSupported();
    setVoiceOk(ok);
    if (ok) setMode("voice");
    api<Catalog>("/plans").then(setCatalog).catch(() => {});
    api<Progress>("/progress").then((p) => setPlan(p.plan)).catch(() => {});
    api<ProfileDto>("/profile/").then((p) => { setProfile(p); setTargetRole(p.targetRole ?? ""); }).catch(() => {});
  }, []);

  async function upload(file: File) {
    setUploading(true);
    setError(null);
    const body = new FormData();
    body.append("file", file);
    try {
      setProfile(await api<ProfileDto>("/profile/resume", { method: "POST", body }));
    } catch (e) {
      setError(errorText(e));
    } finally {
      setUploading(false);
    }
  }

  async function start() {
    setBusy(true);
    setError(null);
    try {
      if (targetRole !== (profile?.targetRole ?? ""))
        await api("/profile/", { method: "PUT", json: { targetRole, experienceLevel: profile?.experienceLevel } });
      const { id } = await api<{ id: string }>("/sessions/", { method: "POST", json: { category, level, jobDescription, mode, voiceConsent: mode === "voice" && consent } });
      router.push(`/interview/?id=${id}${mode === "voice" && camera ? "&camera=1" : ""}`);
    } catch (e) {
      setError(errorText(e));
      setBusy(false);
    }
  }

  return (
    <AppShell>
      <h1 className="text-3xl font-semibold">New interview</h1>

      <section className="mt-6">
        <h2 className="label">Round</h2>
        <div className="grid gap-3 sm:grid-cols-3">{CATEGORIES.map((c) => <Choice key={c.id} selected={category === c.id} onClick={() => setCategory(c.id)} title={c.name} desc={c.desc} />)}</div>
      </section>

      <section className="mt-6">
        <h2 className="label">Level</h2>
        <div className="grid gap-3 sm:grid-cols-2">
          {catalog?.levels.map((l) => {
            const included = catalog.plans.find((x) => x.id === plan)?.levels.includes(l.id);
            return included
              ? <Choice key={l.id} selected={level === l.id} onClick={() => setLevel(l.id)} title={`Level ${l.id}: ${l.name}`} desc={`${l.summary} About ${l.minutes} minutes.`} />
              : (
                <Link key={l.id} href="/pricing/" className="rounded-lg border border-dashed border-line p-3">
                  <span className="block font-semibold">Level {l.id}: {l.name}</span>
                  <span className="block text-sm text-ink-2">{l.summary}</span>
                  <span className="mt-1 block text-sm font-semibold text-accent">Unlock with {PLAN_NAME[l.minPlan]}</span>
                </Link>
              );
          })}
        </div>
      </section>

      <section className="mt-6">
        <h2 className="label">How do you want to answer?</h2>
        <div className="grid gap-3 sm:grid-cols-2">
          <Choice selected={mode === "voice"} onClick={() => voiceOk && setMode("voice")} title="Live call" desc={voiceOk ? "Speak face to face with the interviewer, with your camera if you like. Measures your pace and fillers." : "Not available in this browser. Please use Chrome or Edge."} />
          <Choice selected={mode === "text"} onClick={() => setMode("text")} title="Type" desc="Good for quiet places and for first attempts." />
        </div>
        {mode === "voice" && (
          <div className="mt-3 space-y-3 rounded-lg bg-surface-2 p-3 text-sm">
            <label className="flex items-start gap-2">
              <input type="checkbox" className="mt-1 size-4" checked={consent} onChange={(e) => setConsent(e.target.checked)} />
              <span>I agree to use my microphone. Your browser turns speech into text, and may send audio to its speech service (Google for Chrome) to do so. We store only the text. We never receive or record your audio.</span>
            </label>
            <label className="flex items-start gap-2">
              <input type="checkbox" className="mt-1 size-4" checked={camera} onChange={(e) => setCamera(e.target.checked)} />
              <span>Turn on my camera (optional). It shows you to yourself, like a video call, so you can practise eye contact. Video stays on your device. It is not recorded, uploaded or scored.</span>
            </label>
          </div>
        )}
      </section>

      <details className="card mt-6" open={!!profile?.resumeFileName}>
        <summary className="cursor-pointer font-semibold">Tailor it to you (optional)</summary>
        <div className="mt-4 space-y-4">
          <div>
            <label className="label" htmlFor="role">Target role</label>
            <input id="role" className="field" maxLength={200} placeholder="e.g. Software Engineer Trainee" value={targetRole} onChange={(e) => setTargetRole(e.target.value)} />
          </div>
          <div>
            <label className="label" htmlFor="resume">Resume (PDF, DOCX or TXT, up to 5 MB)</label>
            <input id="resume" type="file" accept=".pdf,.docx,.txt" className="field" onChange={(e) => e.target.files?.[0] && upload(e.target.files[0])} />
            {uploading && <p className="mt-1 text-sm text-ink-2">Reading your resume…</p>}
            {profile?.resumeFileName && !uploading && (
              <p className="mt-1 text-sm text-ink-2">Using {profile.resumeFileName}{profile.resumeSkills.length > 0 && <>. Found: {profile.resumeSkills.join(", ")}</>}. We keep the text only, not the file.</p>
            )}
          </div>
          <div>
            <label className="label" htmlFor="jd">Job description</label>
            <textarea id="jd" className="field" rows={5} maxLength={8000} placeholder="Paste it here" value={jobDescription} onChange={(e) => setJd(e.target.value)} />
          </div>
        </div>
      </details>

      <div className="mt-6 space-y-3">
        <ErrorNote message={error} />
        <button className="btn w-full sm:w-auto" onClick={start} disabled={busy || uploading || (mode === "voice" && !consent)}>{busy ? "Preparing…" : "Begin interview"}</button>
      </div>
    </AppShell>
  );
}
