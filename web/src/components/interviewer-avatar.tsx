export type AvatarState = "speaking" | "thinking" | "listening";

const LABEL: Record<AvatarState, string> = { speaking: "Speaking", thinking: "Thinking", listening: "Listening" };

/** A drawn stand-in for the interviewer. It is plainly an illustration, so nobody mistakes it for a real person. */
export function InterviewerAvatar({ state }: { state: AvatarState }) {
  return (
    <div className="relative flex h-full min-h-0 w-full items-end justify-center bg-surface-2">
      <svg viewBox="0 0 200 200" className={`h-5/6 max-h-80 ${state === "thinking" ? "avatar-thinking" : ""}`} role="img" aria-label={`Interviewer: ${LABEL[state]}`}>
        <path d="M30 200c6-44 36-62 70-62s64 18 70 62z" fill="var(--accent)" />
        <path d="M84 132h32v18c-8 8-24 8-32 0z" fill="#c98f66" />
        <circle cx="100" cy="88" r="50" fill="#dba77f" />
        <path d="M50 86c-4-44 26-58 50-58s56 12 50 58c-6-16-14-26-24-30-20 8-50 8-62 4-8 6-12 14-14 26z" fill="#2b2118" />
        <g className="avatar-eyes" fill="#2b2118">
          <circle cx="82" cy="90" r="4.5" />
          <circle cx="118" cy="90" r="4.5" />
        </g>
        <ellipse className={state === "speaking" ? "avatar-mouth-talk" : ""} cx="100" cy="114" rx="11" ry="3" fill="#7a3b2e" />
      </svg>
      <span className="absolute bottom-2 left-2 rounded-md bg-black/60 px-2 py-1 text-xs font-medium text-white">
        Interviewer · {LABEL[state]}
      </span>
    </div>
  );
}
