"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import { AppShell, ErrorNote, errorText } from "@/components/shell";
import { api, homeFor, PLAN_NAME, prettyDate, type Me, type ProfileDto, type Progress } from "@/lib/api";

function SettingsForm() {
  const router = useRouter();
  const mustChange = useSearchParams().get("change") === "1";
  const [passwords, setPasswords] = useState({ currentPassword: "", newPassword: "" });
  const [passwordSaved, setPasswordSaved] = useState(false);
  const [profile, setProfile] = useState<ProfileDto | null>(null);
  const [progress, setProgress] = useState<Progress | null>(null);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api<ProfileDto>("/profile/").then(setProfile).catch((e) => setError(errorText(e)));
    api<Progress>("/progress").then(setProgress).catch(() => {});
  }, []);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setSaved(false);
    try {
      setProfile(await api<ProfileDto>("/profile/", { method: "PUT", json: profile }));
      setSaved(true);
    } catch (err) { setError(errorText(err)); }
  }

  async function removeResume() {
    try { setProfile(await api<ProfileDto>("/profile/resume", { method: "DELETE" })); }
    catch (err) { setError(errorText(err)); }
  }

  async function changePassword(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setPasswordSaved(false);
    try {
      await api("/me/password", { method: "POST", json: passwords });
      setPasswords({ currentPassword: "", newPassword: "" });
      setPasswordSaved(true);
      if (mustChange) router.replace(homeFor(await api<Me>("/me/")));
    } catch (err) { setError(errorText(err)); }
  }

  async function deleteAccount() {
    if (!window.confirm("Permanently delete your account, interviews and reports? This cannot be undone.")) return;
    try {
      await api("/me/", { method: "DELETE" });
      router.replace("/");
    } catch (err) { setError(errorText(err)); }
  }

  return (
    <AppShell>
      <h1 className="text-3xl font-semibold">Settings</h1>
      {mustChange && <p role="status" className="mt-3 rounded-lg bg-warn-soft px-3 py-2 text-sm text-warn">You signed in with a temporary password. Please choose a new one below to continue.</p>}
      <div className="mt-4"><ErrorNote message={error} /></div>

      {profile && (
        <form onSubmit={save} className="card mt-4 space-y-4">
          <h2 className="text-xl font-semibold">Profile</h2>
          <div>
            <label className="label" htmlFor="role">Target role</label>
            <input id="role" className="field" maxLength={200} value={profile.targetRole ?? ""} onChange={(e) => setProfile({ ...profile, targetRole: e.target.value })} />
          </div>
          <div>
            <label className="label" htmlFor="level">Experience</label>
            <select id="level" className="field" value={profile.experienceLevel ?? ""} onChange={(e) => setProfile({ ...profile, experienceLevel: e.target.value })}>
              <option value="">Choose one</option>
              <option value="student">Final-year student</option>
              <option value="fresher">Fresher</option>
              <option value="early">1–3 years</option>
            </select>
          </div>
          {profile.resumeFileName && (
            <p className="text-sm text-ink-2">Resume text from {profile.resumeFileName}. <button type="button" className="cursor-pointer underline" onClick={removeResume}>Remove it</button></p>
          )}
          <div className="flex items-center gap-3">
            <button className="btn">Save</button>
            {saved && <span role="status" className="text-sm text-ink-2">Saved</span>}
          </div>
        </form>
      )}

      {progress && (
        <section className="card mt-4">
          <h2 className="text-xl font-semibold">Plan</h2>
          <p className="mt-1 text-ink-2">
            You&rsquo;re on the {PLAN_NAME[progress.plan]} plan, with {progress.limit} interviews a month.
            {progress.renewsAt && <> Active until {prettyDate(progress.renewsAt)}.</>}
          </p>
          <Link href="/pricing/" className="btn btn-quiet mt-3">See plans</Link>
        </section>
      )}

      <form onSubmit={changePassword} className="card mt-4 space-y-4">
        <h2 className="text-xl font-semibold">Password</h2>
        <div>
          <label className="label" htmlFor="current">Current password</label>
          <input id="current" type="password" className="field" required autoComplete="current-password" value={passwords.currentPassword} onChange={(e) => setPasswords({ ...passwords, currentPassword: e.target.value })} />
        </div>
        <div>
          <label className="label" htmlFor="new">New password</label>
          <input id="new" type="password" className="field" required minLength={8} autoComplete="new-password" value={passwords.newPassword} onChange={(e) => setPasswords({ ...passwords, newPassword: e.target.value })} />
          <p className="mt-1 text-xs text-ink-3">At least 8 characters. This signs you out on other devices.</p>
        </div>
        <div className="flex items-center gap-3">
          <button className="btn">Change password</button>
          {passwordSaved && <span role="status" className="text-sm text-ink-2">Password changed</span>}
        </div>
      </form>

      <section className="card mt-4">
        <h2 className="text-xl font-semibold">Your data</h2>
        <p className="mt-1 text-ink-2">Download everything we hold about you, or delete it for good.</p>
        <div className="mt-3 flex flex-wrap gap-3">
          <a className="btn btn-quiet" href="/api/me/export">Download my data</a>
          <button className="btn btn-danger" onClick={deleteAccount}>Delete my account</button>
        </div>
      </section>
    </AppShell>
  );
}

export default function Settings() {
  return <Suspense><SettingsForm /></Suspense>;
}
