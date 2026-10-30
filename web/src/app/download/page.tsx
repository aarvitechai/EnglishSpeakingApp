import { Logo } from "@/components/shell";

export const metadata = { title: "Android app: NeuroTara" };

export default function Download() {
  return (
    <main className="mx-auto max-w-xl px-4 py-8">
      <Logo />
      <h1 className="mt-6 text-3xl font-semibold">NeuroTara for Android</h1>
      <p className="mt-2 text-ink-2">Practise interviews from your phone. Android 6 or newer, with Chrome installed for voice interviews.</p>
      <a className="btn mt-5" href="/download/neurotara.apk" download>Download the app</a>
      <h2 className="mt-8 text-xl font-semibold">How to install</h2>
      <ol className="mt-2 list-decimal space-y-2 pl-5 text-ink-2">
        <li>Tap the button above and open the file when it has downloaded.</li>
        <li>If your phone asks, allow your browser to install unknown apps. The app is not on the Play Store yet, so Android shows this warning.</li>
        <li>Tap <strong>Install</strong>, then open NeuroTara.</li>
        <li>Allow the microphone when you start a live interview.</li>
      </ol>
    </main>
  );
}
