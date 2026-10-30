/* eslint-disable @typescript-eslint/no-explicit-any */

export function speechRecognitionSupported() {
  return typeof window !== "undefined" && ("SpeechRecognition" in window || "webkitSpeechRecognition" in window);
}

const LONG_PAUSE_MS = 3000;

export type AnswerCapture = { text: string; startMs: number; endMs: number; longPauses: number };

/**
 * Wraps the browser's speech recognition for one interview. Browsers end recognition on their own after
 * silence, so it restarts for as long as we are meant to be listening.
 */
export class Listener {
  private rec: any;
  private wanted = false;
  private finals = "";
  private interim = "";
  private firstAt: number | null = null;
  private lastAt = 0;
  private pauses = 0;

  constructor(
    private clock: () => number,
    private on: { transcript: (text: string) => void; speechStart: (interim: string) => void; error: (kind: string) => void },
  ) {
    const Ctor = (window as any).SpeechRecognition ?? (window as any).webkitSpeechRecognition;
    this.rec = new Ctor();
    this.rec.lang = "en-IN";
    this.rec.continuous = true;
    this.rec.interimResults = true;
    this.rec.onresult = (e: any) => this.handle(e);
    this.rec.onend = () => { if (this.wanted) this.safeStart(); };
    this.rec.onerror = (e: any) => {
      if (e.error === "no-speech" || e.error === "aborted") return;
      if (e.error === "not-allowed" || e.error === "service-not-allowed" || e.error === "audio-capture") this.wanted = false;
      this.on.error(e.error);
    };
  }

  private handle(e: any) {
    const now = this.clock();
    if (this.firstAt === null) this.firstAt = now;
    else if (now - this.lastAt > LONG_PAUSE_MS) this.pauses++;
    this.lastAt = now;

    let interim = "";
    for (let i = e.resultIndex; i < e.results.length; i++) {
      const r = e.results[i];
      if (r.isFinal) this.finals += r[0].transcript.trim() + " ";
      else interim += r[0].transcript;
    }
    this.interim = interim;
    const text = (this.finals + interim).trim();
    this.on.speechStart(text);
    this.on.transcript(text);
  }

  private safeStart() {
    try { this.rec.start(); } catch { /* already running */ }
  }

  start() {
    this.wanted = true;
    this.safeStart();
  }

  stop() {
    this.wanted = false;
    try { this.rec.stop(); } catch { /* not running */ }
  }

  /** Clears the transcript, for instance when speech so far was the interviewer's voice leaking into the mic. */
  reset() {
    this.finals = "";
    this.interim = "";
    this.firstAt = null;
    this.pauses = 0;
    this.on.transcript("");
  }

  take(): AnswerCapture | null {
    const text = (this.finals + this.interim).trim();
    if (!text) return null;
    const capture = { text, startMs: Math.round(this.firstAt ?? this.clock()), endMs: Math.round(this.lastAt), longPauses: this.pauses };
    this.reset();
    return capture;
  }

  get lastHeardAt() { return this.lastAt; }
  get hasText() { return (this.finals + this.interim).trim().length > 0; }
}

/** Speaks a stream of text sentence by sentence, so the first words play long before the reply finishes. */
export class Speaker {
  private buffer = "";
  private pending = 0;
  private idleWaiters: (() => void)[] = [];
  spoken = "";

  private voice(): SpeechSynthesisVoice | undefined {
    const voices = speechSynthesis.getVoices();
    return voices.find((v) => v.lang === "en-IN") ?? voices.find((v) => v.lang.startsWith("en"));
  }

  get speaking() { return this.pending > 0; }

  push(delta: string) {
    this.buffer += delta;
    let m: RegExpMatchArray | null;
    while ((m = this.buffer.match(/^(.+?[.!?])(\s+|$)/s)) && m[2]) {
      this.say(m[1]);
      this.buffer = this.buffer.slice(m[0].length);
    }
  }

  flush() {
    if (this.buffer.trim()) this.say(this.buffer.trim());
    this.buffer = "";
    if (this.pending === 0) this.notifyIdle();
  }

  private say(text: string) {
    this.spoken += " " + text;
    const u = new SpeechSynthesisUtterance(text);
    const voice = this.voice();
    if (voice) u.voice = voice;
    u.lang = voice?.lang ?? "en-IN";
    u.rate = 1.02;
    this.pending++;
    const done = () => {
      this.pending = Math.max(0, this.pending - 1);
      if (this.pending === 0) this.notifyIdle();
    };
    u.onend = done;
    u.onerror = done;
    speechSynthesis.speak(u);
  }

  /** Barge-in: stop talking the moment the candidate starts. */
  cancel() {
    this.buffer = "";
    this.pending = 0;
    speechSynthesis.cancel();
    this.notifyIdle();
  }

  whenIdle() {
    return this.pending === 0 ? Promise.resolve() : new Promise<void>((r) => this.idleWaiters.push(r));
  }

  private notifyIdle() {
    this.idleWaiters.splice(0).forEach((r) => r());
  }
}

/**
 * True when text heard on the mic is really the candidate rather than our own voice leaking from the speakers:
 * at least three words, most of them not in what the interviewer just said.
 */
export function isRealBargeIn(heard: string, spoken: string) {
  const words = heard.toLowerCase().match(/[\p{L}\p{N}']+/gu) ?? [];
  if (words.length < 3) return false;
  const said = new Set(spoken.toLowerCase().match(/[\p{L}\p{N}']+/gu) ?? []);
  return words.filter((w) => !said.has(w)).length / words.length > 0.5;
}
