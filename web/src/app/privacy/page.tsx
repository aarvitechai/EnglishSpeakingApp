import { Logo } from "@/components/shell";

export const metadata = { title: "Privacy and terms: NeuroTara" };

export default function Privacy() {
  return (
    <main className="mx-auto max-w-2xl px-4 py-8">
      <Logo />
      <h1 className="mt-6 text-3xl font-semibold">Privacy and terms</h1>
      <p className="mt-2 rounded-lg bg-warn-soft px-3 py-2 text-sm text-warn">Draft: to be reviewed by a lawyer before public launch.</p>
      <div className="mt-6 space-y-6">
        <section>
          <h2 className="text-xl font-semibold">What we store</h2>
          <p className="mt-1 text-ink-2">Your name and email, the text extracted from any resume you upload, job descriptions you paste, interview transcripts, and your scores. We discard the resume file itself after reading it.</p>
        </section>
        <section>
          <h2 className="text-xl font-semibold">Your voice</h2>
          <p className="mt-1 text-ink-2">We only use your microphone after you agree. Your browser converts speech to text and may send audio to its own speech service (Google, in Chrome). We receive the text only. We do not record audio or video.</p>
          <p className="mt-2 text-ink-2">The camera is optional and off by default. If you turn it on, the picture is shown only to you on your own device. It is never uploaded, stored or scored.</p>
        </section>
        <section>
          <h2 className="text-xl font-semibold">Who processes your data</h2>
          <p className="mt-1 text-ink-2">When AI features are on, your transcript, resume text and job description are sent to Anthropic to generate questions and feedback. Payments are handled by Razorpay, and we never see your card or UPI details.</p>
        </section>
        <section>
          <h2 className="text-xl font-semibold">Staff access</h2>
          <p className="mt-1 text-ink-2">Our admins can see your name, email, plan, and how many interviews you have taken, so they can help you. They cannot read your transcripts or resume through the admin console.</p>
        </section>
        <section>
          <h2 className="text-xl font-semibold">Your choices</h2>
          <p className="mt-1 text-ink-2">In Settings you can download all your data or delete your account at any time. Deletion removes your interviews and reports. Payment records are kept without your account details where tax law requires them.</p>
        </section>
        <section>
          <h2 className="text-xl font-semibold">Fair use</h2>
          <p className="mt-1 text-ink-2">This is a practice tool. Do not use it during a real interview. Scores are coaching guidance, not a prediction of any hiring result.</p>
        </section>
      </div>
    </main>
  );
}
