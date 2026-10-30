"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useCallback, useEffect, useRef, useState } from "react";
import { ErrorNote, errorText, useMe } from "@/components/shell";
import { api, CATEGORY_LABEL, streamTurn, type Session } from "@/lib/api";
import { isRealBargeIn, Listener, Speaker, speechRecognitionSupported, type AnswerCapture } from "@/lib/voice";

const SILENCE_SUBMIT_MS = 2500;
type Line = { speaker: "interviewer" | "candidate"; text: string };

function Room() {
  const router = useRouter();
  const id = useSearchParams().get("id");
  const me = useMe();

  const [session, setSession] = useState<Session | null>(null);
  const [lines, setLines] = useState<Line[]>([]);
  const [draft, setDraft] = useState("");
  const [thinking, setThinking] = useState(false);
  const [listening, setListening] = useState(false);
  const [micProblem, setMicProblem] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [ending, setEnding] = useState(false);

  const speaker = useRef<Speaker | null>(null);
  const listener = useRef<Listener | null>(null);
  const silenceTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const busyRef = useRef(false);
  const started = useRef(false);
  const t0 = useRef(0);
  const scroller = useRef<HTMLDivElement>(null);
  const submitRef = useRef<(c: AnswerCapture | { text: string }) => void>(() => {});

  const voice = session?.mode === "voice" && !micProblem;

  useEffect(() => { scroller.current?.scrollTo({ top: scroller.current.scrollHeight, behavior: "smooth" }); }, [lines, draft]);

  const finish = useCallback(() => router.replace(`/report/?id=${id}`), [router, id]);

  /** Streams one interviewer turn into the transcript and, in voice mode, out of the speakers. */
  const runTurn = useCallback(async (path: string, body: unknown, speak: boolean) => {
    busyRef.current = true;
    setThinking(true);
    setError(null);
    setLines((l) => [...l, { speaker: "interviewer", text: "" }]);
    const sp = speak ? speaker.current : null;
    if (sp) sp.spoken = "";
    try {
      const { ended } = await streamTurn(path, body, (delta) => {
        setThinking(false);
        setLines((l) => {
          const copy = [...l];
          copy[copy.length - 1] = { ...copy[copy.length - 1], text: copy[copy.length - 1].text + delta };
          return copy;
        });
        sp?.push(delta);
      });
      sp?.flush();
      if (ended) {
        listener.current?.stop();
        setListening(false);
        if (sp) await sp.whenIdle();
        finish();
      }
    } finally {
      busyRef.current = false;
      setThinking(false);
    }
  }, [finish]);

  const submit = useCallback(async (capture: AnswerCapture | { text: string }) => {
    if (busyRef.current || !capture.text.trim()) return;
    if (silenceTimer.current) clearTimeout(silenceTimer.current);
    setDraft("");
    setLines((l) => [...l, { speaker: "candidate", text: capture.text.trim() }]);
    try {
      await runTurn(`/sessions/${id}/messages`, capture, !!listener.current);
    } catch (e) {
      setLines((l) => l.filter((x, i) => !(i === l.length - 1 && x.speaker === "interviewer" && !x.text)));
      setError(`${errorText(e)} Your answer was: "${capture.text.slice(0, 80)}${capture.text.length > 80 ? "…" : ""}"`);
    }
  }, [id, runTurn]);
  submitRef.current = submit;

  // Load the session and start or resume it once.
  useEffect(() => {
    if (!me || !id || started.current) return;
    started.current = true;
    t0.current = performance.now();
    (async () => {
      try {
        const s = await api<Session>(`/sessions/${id}`);
        if (!["created", "active"].includes(s.status)) return finish();
        setSession(s);
        setLines(s.turns.map((t) => ({ speaker: t.speaker, text: t.text })));
        // Timings continue from any earlier part of a resumed session, so they stay increasing.
        const lastEnd = Math.max(0, ...s.turns.map((t) => t.endMs ?? 0));
        t0.current = performance.now() - lastEnd;

        const useVoice = s.mode === "voice" && speechRecognitionSupported();
        if (s.mode === "voice" && !useVoice) setMicProblem("This browser cannot recognise speech, so please type your answers.");
        if (useVoice) {
          speaker.current = new Speaker();
          listener.current = new Listener(() => performance.now() - t0.current, {
            transcript: setDraft,
            speechStart: (heard) => {
              const sp = speaker.current!;
              if (sp.speaking) {
                if (isRealBargeIn(heard, sp.spoken)) sp.cancel();
                else { listener.current!.reset(); return; }
              }
              if (silenceTimer.current) clearTimeout(silenceTimer.current);
              silenceTimer.current = setTimeout(() => {
                if (busyRef.current) return;
                const capture = listener.current!.take();
                if (capture) submitRef.current(capture);
              }, SILENCE_SUBMIT_MS);
            },
            error: (kind) => {
              if (kind === "not-allowed" || kind === "service-not-allowed") setMicProblem("Microphone access is blocked, so please type your answers. You can allow it in your browser's site settings.");
              else if (kind === "audio-capture") setMicProblem("We could not find a microphone, so please type your answers.");
              else if (kind === "network") setMicProblem("Speech recognition lost its connection, so please type your answers.");
              setListening(false);
            },
          });
          listener.current.start();
          setListening(true);
        }

        if (s.turns.length === 0) {
          await runTurn(`/sessions/${id}/start`, null, useVoice);
        } else if (useVoice && s.turns[s.turns.length - 1].speaker === "interviewer") {
          speaker.current!.push(s.turns[s.turns.length - 1].text);
          speaker.current!.flush();
        }
      } catch (e) {
        setError(errorText(e));
      }
    })();
    return () => {
      if (silenceTimer.current) clearTimeout(silenceTimer.current);
    };
  }, [me, id, runTurn, finish]);

  // Silence the mic and voice whenever the page goes away.
  useEffect(() => () => {
    listener.current?.stop();
    if (typeof speechSynthesis !== "undefined") speechSynthesis.cancel();
  }, []);

  function sendNow() {
    const captured = listener.current?.hasText ? listener.current.take() : null;
    const capture = captured ?? (draft.trim() ? { text: draft } : null);
    if (capture) submit(capture);
  }

  async function endInterview() {
    if (!window.confirm("End the interview and get your report now?")) return;
    setEnding(true);
    listener.current?.stop();
    speaker.current?.cancel();
    try {
      const { status } = await api<{ status: string }>(`/sessions/${id}/end`, { method: "POST" });
      if (status === "abandoned") router.replace("/dashboard/");
      else finish();
    } catch (e) {
      setError(errorText(e));
      setEnding(false);
    }
  }

  if (!id) return <p className="p-6">This interview link is not valid.</p>;

  return (
    <div className="flex h-dvh flex-col">
      <header className="flex items-center justify-between gap-3 border-b border-line bg-surface px-4 py-3">
        <div>
          <p className="font-semibold">{session ? `${CATEGORY_LABEL[session.category]} interview` : "Interview"}</p>
          {session && <p className="text-xs text-ink-3">{session.difficulty}, about {session.durationMin} minutes</p>}
        </div>
        <button className="btn btn-quiet" onClick={endInterview} disabled={ending || !session}>{ending ? "Ending…" : "End interview"}</button>
      </header>

      <div ref={scroller} className="flex-1 overflow-y-auto px-4 py-6" aria-live="polite">
        <div className="mx-auto max-w-2xl space-y-4">
          {lines.map((l, i) => (
            l.text && (
              <div key={i} className={l.speaker === "candidate" ? "ml-8 rounded-xl bg-accent-soft p-3 sm:ml-16" : "mr-8 sm:mr-16"}>
                <p className="mb-0.5 text-xs font-semibold text-ink-3">{l.speaker === "candidate" ? "You" : "Interviewer"}</p>
                <p className={l.speaker === "interviewer" ? "text-lg" : ""}>{l.text}</p>
              </div>
            )
          ))}
          {thinking && <p className="text-sm text-ink-3">The interviewer is thinking…</p>}
        </div>
      </div>

      <footer className="border-t border-line bg-surface px-4 py-3">
        <div className="mx-auto max-w-2xl space-y-2">
          <ErrorNote message={error ?? micProblem} />
          {voice && listening ? (
            <div className="flex items-center gap-4">
              <span className="listening relative block size-4 shrink-0 rounded-full bg-accent" aria-hidden />
              <p className="min-h-6 flex-1 text-sm text-ink-2" aria-live="off">{draft || "Listening. Just start talking, even over the interviewer."}</p>
              <button className="btn" onClick={sendNow} disabled={!draft.trim() || thinking}>Done</button>
            </div>
          ) : (
            <form className="flex items-end gap-2" onSubmit={(e) => { e.preventDefault(); sendNow(); }}>
              <label className="sr-only" htmlFor="answer">Your answer</label>
              <textarea id="answer" className="field flex-1" rows={3} maxLength={4000} placeholder="Type your answer" value={draft} onChange={(e) => setDraft(e.target.value)}
                onKeyDown={(e) => { if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); sendNow(); } }} />
              <button className="btn" disabled={!draft.trim() || thinking || ending}>Send</button>
            </form>
          )}
        </div>
      </footer>
    </div>
  );
}

export default function InterviewPage() {
  return <Suspense><Room /></Suspense>;
}
