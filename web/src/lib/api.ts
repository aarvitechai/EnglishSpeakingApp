export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

const FRIENDLY: Record<number, string> = {
  401: "Please sign in again.",
  404: "We could not find that.",
  409: "That action is no longer available. Please refresh the page.",
  429: "You're going a little fast. Please wait a minute and try again.",
};

async function toError(res: Response) {
  let detail: string | undefined;
  try {
    detail = (await res.json()).detail;
  } catch {
    /* non-JSON error body */
  }
  return new ApiError(res.status, detail ?? FRIENDLY[res.status] ?? "Something went wrong. Please try again.");
}

export async function api<T = unknown>(path: string, init?: RequestInit & { json?: unknown }): Promise<T> {
  const { json, ...rest } = init ?? {};
  const res = await fetch(`/api${path}`, {
    ...rest,
    credentials: "same-origin",
    headers: json !== undefined ? { "Content-Type": "application/json" } : undefined,
    body: json !== undefined ? JSON.stringify(json) : rest.body,
  });
  if (!res.ok) throw await toError(res);
  if (res.status === 204) return undefined as T;
  return res.json();
}

/** POSTs and reads the interviewer's reply as server-sent events, calling onDelta for each chunk. */
export async function streamTurn(path: string, body: unknown, onDelta: (text: string) => void): Promise<{ ended: boolean }> {
  const res = await fetch(`/api${path}`, {
    method: "POST",
    credentials: "same-origin",
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!res.ok || !res.body) throw await toError(res);

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let ended = false;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let split: number;
    while ((split = buffer.indexOf("\n\n")) >= 0) {
      const line = buffer.slice(0, split);
      buffer = buffer.slice(split + 2);
      if (!line.startsWith("data: ")) continue;
      const event = JSON.parse(line.slice(6));
      if (event.delta) onDelta(event.delta);
      if (event.done) ended = event.ended;
    }
  }
  return { ended };
}

export type Me = { id: string; email: string; name: string; role: string; plan: string; mustChangePassword: boolean };
export type AppConfig = { googleClientId: string | null; aiEnabled: boolean; billingEnabled: boolean };
export type PlanInfo = { id: string; pricePaise: number; days: number; interviewsPerMonth: number; levels: string[] };
export type LevelInfo = { id: string; name: string; summary: string; minutes: number; minPlan: string };
export type Catalog = { plans: PlanInfo[]; levels: LevelInfo[] };
export type Turn = { seq: number; speaker: "interviewer" | "candidate"; text: string; startMs: number | null; endMs: number | null };
export type Session = {
  id: string; status: string; mode: "text" | "voice"; createdAt: string; failureReason: string | null;
  category: string; level: string; durationMin: number; turns: Turn[];
};
export type SessionRow = { id: string; status: string; mode: string; createdAt: string; category: string; level: string; score: number | null };
export type Improvement = { skill: string; title: string; seq: number; quote: string; why: string; rewrite: string; drills: string[] };
export type Report = {
  session: Session;
  evaluation: null | {
    overallScore: number; modelVersion: string; helpfulRating: number | null;
    scores: {
      dimensions: { key: string; name: string; score: number; weight: number }[];
      answers: { seq: number; scores: { dimension: string; score: number; evidence: string }[] }[];
    };
    feedback: { summary: string; improvements: Improvement[]; strengths: { seq: number; quote: string; why: string }[] };
  };
  delivery: null | { wpm: number | null; fillerCount: number; longPauses: number; talkRatio: number; avgAnswerSec: number | null; avgAnswerWords: number };
};
export type Progress = {
  plan: string; renewsAt: string | null; used: number; limit: number;
  sessions: { id: string; createdAt: string; category: string; score: number }[];
  skills: { name: string; count: number; first: number; latest: number }[];
};
export type PracticePlan = { id: string; skill: string; status: string; dueDate: string; sourceSessionId: string | null; drills: string[] };
export type ProfileDto = { targetRole: string | null; experienceLevel: string | null; resumeFileName: string | null; resumeSkills: string[] };

export const LEVEL_NAME: Record<string, string> = { "1": "Zero", "2": "Beginner", "3": "Intermediate", "4": "Advanced", "5": "Expert" };
export const levelLabel = (id: string) => `Level ${id}: ${LEVEL_NAME[id] ?? id}`;
export const PLAN_NAME: Record<string, string> = { free: "Free", plus: "Plus", pro: "Pro" };
export const isStaff = (role?: string) => role === "admin" || role === "super_admin";
export const homeFor = (me: Pick<Me, "role" | "mustChangePassword">) =>
  me.mustChangePassword ? "/settings/?change=1" : isStaff(me.role) ? "/admin/" : "/dashboard/";
export const rupees = (paise: number) => `₹${(paise / 100).toLocaleString("en-IN")}`;
export const CATEGORY_LABEL: Record<string, string> = { hr: "HR", behavioural: "Behavioural", technical: "Technical" };
export const prettyDate = (s: string) => new Date(s).toLocaleDateString("en-IN", { day: "numeric", month: "short", year: "numeric" });
