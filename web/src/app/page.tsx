import Link from "next/link";
import { Logo } from "@/components/shell";

const LEVELS = [
  { n: "Zero", d: "Never been interviewed? Simple questions, with a hint for each." },
  { n: "Beginner", d: "The common questions every fresher gets." },
  { n: "Intermediate", d: "A realistic round that asks for specifics." },
  { n: "Advanced", d: "Tough follow-ups, like a final round." },
  { n: "Expert", d: "Panel-level pressure on judgement and depth." },
];

const STEPS = [
  { t: "Pick a round", d: "HR, behavioural or technical, at the level you're ready for. Add your resume and the job description if you have them." },
  { t: "Answer out loud", d: "The interviewer asks one question at a time and follows up on what you actually said, just like a real panel." },
  { t: "Fix three things", d: "Your report quotes your own words, shows a better version, and gives you drills to do today." },
];

export default function Home() {
  return (
    <div>
      <header className="mx-auto flex max-w-5xl items-center justify-between px-4 py-4">
        <Logo />
        <nav className="flex items-center gap-4 text-sm font-semibold">
          <Link href="/pricing/" className="py-2 hover:underline">Plans</Link>
          <Link href="/login/" className="py-2 hover:underline">Sign in</Link>
        </nav>
      </header>
      <main className="mx-auto max-w-5xl px-4">
        <section className="grid items-center gap-10 py-10 md:grid-cols-[1.2fr_1fr] md:py-16">
          <div>
            <h1 className="text-4xl leading-tight font-semibold sm:text-5xl">Walk into your placement interview having already done it.</h1>
            <p className="mt-4 max-w-xl text-lg text-ink-2">
              A mock interviewer for Indian freshers and early-career candidates. Go from Level 1, with no experience at all, to Level 5, expert. Practise speaking, get honest feedback, and see your scores rise.
            </p>
            <div className="mt-6 flex flex-wrap gap-3">
              <Link href="/login/?mode=register" className="btn">Start a free interview</Link>
            </div>
            <p className="mt-3 text-sm text-ink-3">Practice only. We will never help you during a real interview.</p>
          </div>
          <figure className="card" aria-label="Example of the feedback in a report">
            <figcaption className="text-xs font-semibold tracking-wide text-ink-3 uppercase">From a sample report</figcaption>
            <p className="mt-3 text-sm text-ink-2">You said</p>
            <blockquote className="mt-1"><mark>&ldquo;We all worked very hard and the event went well.&rdquo;</mark></blockquote>
            <p className="mt-3 text-sm text-ink-2">The interviewer can&rsquo;t tell what you did. Try:</p>
            <p className="mt-1">&ldquo;I ran sponsorships for the fest. I pitched [number] local companies and raised ₹[amount], which paid for [what it funded].&rdquo;</p>
          </figure>
        </section>
        <section className="border-t border-line py-10">
          <h2 className="text-2xl font-semibold">Five levels, starting from zero</h2>
          <ol className="mt-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
            {LEVELS.map((l, i) => (
              <li key={l.n} className="card">
                <p className="text-sm font-semibold text-accent">Level {i + 1}</p>
                <h3 className="mt-1 text-lg font-semibold">{l.n}</h3>
                <p className="mt-1 text-sm text-ink-2">{l.d}</p>
              </li>
            ))}
          </ol>
        </section>
        <section className="grid gap-4 border-t border-line py-10 md:grid-cols-3">
          {STEPS.map((s, i) => (
            <div key={s.t}>
              <p className="text-sm font-semibold text-accent">Step {i + 1}</p>
              <h2 className="mt-1 text-xl font-semibold">{s.t}</h2>
              <p className="mt-1 text-ink-2">{s.d}</p>
            </div>
          ))}
        </section>
      </main>
      <footer className="mx-auto max-w-5xl border-t border-line px-4 py-6 text-sm text-ink-3">
        <Link href="/privacy/" className="underline">Privacy and terms</Link>
      </footer>
    </div>
  );
}
