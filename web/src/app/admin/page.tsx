"use client";

import { useRouter } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { AppShell, ErrorNote, errorText, useMe } from "@/components/shell";
import { api, isStaff, PLAN_NAME, prettyDate, rupees } from "@/lib/api";

type Metrics = {
  users: number; activationRate: number; sessionsStarted: number; sessionsCompleted: number; completionRate: number;
  avgCostPaisePerCompleted: number | null; avgHelpfulRating: number | null; paid: { plan: string; users: number }[];
};
type UserRow = { id: string; email: string; name: string; role: string; isDisabled: boolean; createdAt: string; lastLoginAt: string | null; sessions: number; plan: string };
type UserPage = { total: number; page: number; pageSize: number; users: UserRow[] };
type Audit = { id: number; actorEmail: string; action: string; targetEmail: string | null; detail: string | null; createdAt: string };
type Payments = { revenuePaise: number; payments: { id: string; email: string | null; plan: string; amountPaise: number; status: string; createdAt: string }[] };

const ROLE_NAME: Record<string, string> = { candidate: "User", org_admin: "Org admin", admin: "Admin", super_admin: "Super admin" };
const percent = (v: number) => `${Math.round(v * 100)}%`;

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="card">
      <p className="text-sm text-ink-2">{label}</p>
      <p className="mt-1 text-2xl font-bold tabular-nums">{value}</p>
    </div>
  );
}

function Overview() {
  const [m, setM] = useState<Metrics | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api<Metrics>("/admin/metrics").then(setM).catch((e) => setError(errorText(e))); }, []);
  if (!m) return <ErrorNote message={error} />;
  const paid = (plan: string) => m.paid.find((p) => p.plan === plan)?.users ?? 0;
  return (
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
      <Stat label="Users" value={String(m.users)} />
      <Stat label="Finished an interview in 24 h" value={percent(m.activationRate)} />
      <Stat label="Interviews completed" value={String(m.sessionsCompleted)} />
      <Stat label="Completion rate" value={percent(m.completionRate)} />
      <Stat label="Plus users" value={String(paid("plus"))} />
      <Stat label="Pro users" value={String(paid("pro"))} />
      <Stat label="AI cost per interview" value={m.avgCostPaisePerCompleted === null ? "None yet" : rupees(Math.round(m.avgCostPaisePerCompleted))} />
      <Stat label="Feedback rating" value={m.avgHelpfulRating === null ? "None yet" : `${m.avgHelpfulRating.toFixed(1)} / 5`} />
    </div>
  );
}

function Users({ meId, isSuper }: { meId: string; isSuper: boolean }) {
  const [data, setData] = useState<UserPage | null>(null);
  const [query, setQuery] = useState("");
  const [page, setPage] = useState(1);
  const [error, setError] = useState<string | null>(null);
  const [temp, setTemp] = useState<{ email: string; password: string } | null>(null);

  const load = useCallback(() => {
    api<UserPage>(`/admin/users?q=${encodeURIComponent(query)}&page=${page}`).then(setData).catch((e) => setError(errorText(e)));
  }, [query, page]);
  useEffect(() => { load(); }, [load]);

  async function act(fn: () => Promise<unknown>) {
    setError(null);
    try { await fn(); load(); } catch (e) { setError(errorText(e)); }
  }

  const toggle = (u: UserRow) => {
    if (window.confirm(`${u.isDisabled ? "Enable" : "Disable"} ${u.email}?`))
      act(() => api(`/admin/users/${u.id}/status`, { method: "POST", json: { disabled: !u.isDisabled } }));
  };
  const reset = (u: UserRow) => {
    if (window.confirm(`Reset the password for ${u.email}? Their current password will stop working.`))
      act(async () => {
        const r = await api<{ temporaryPassword: string }>(`/admin/users/${u.id}/reset-password`, { method: "POST" });
        setTemp({ email: u.email, password: r.temporaryPassword });
      });
  };
  const setRole = (u: UserRow, role: string) => {
    if (window.confirm(`Make ${u.email} a ${ROLE_NAME[role]}? They will be signed out.`))
      act(() => api(`/admin/users/${u.id}/role`, { method: "POST", json: { role } }));
    else load();
  };
  const setPlan = (u: UserRow, plan: string) => {
    if (window.confirm(`Set ${u.email} to ${PLAN_NAME[plan]}${plan === "free" ? "" : " for 30 days, free of charge"}?`))
      act(() => api(`/admin/users/${u.id}/plan`, { method: "POST", json: { plan, days: 30 } }));
    else load();
  };

  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  return (
    <div className="space-y-3">
      <label className="label" htmlFor="search">Search by name or email</label>
      <input id="search" className="field max-w-md" value={query} onChange={(e) => { setQuery(e.target.value); setPage(1); }} />
      <ErrorNote message={error} />
      {temp && <TempPassword {...temp} onClose={() => setTemp(null)} />}
      <div className="card overflow-x-auto p-0!">
        <table className="w-full min-w-[820px] text-left text-sm">
          <thead>
            <tr className="border-b border-line text-ink-2">
              {["User", "Role", "Plan", "Interviews", "Last sign-in", "Actions"].map((h) => <th key={h} className="px-3 py-2 font-medium">{h}</th>)}
            </tr>
          </thead>
          <tbody>
            {data?.users.map((u) => {
              const self = u.id === meId;
              const canManage = !self && (isSuper || u.role === "candidate");
              return (
                <tr key={u.id} className="border-b border-line align-top last:border-0">
                  <td className="px-3 py-2">
                    <span className="font-medium">{u.name}</span>{self && <span className="text-ink-3"> (you)</span>}
                    <span className="block text-ink-2">{u.email}</span>
                    {u.isDisabled && <span className="text-xs font-semibold text-danger">Disabled</span>}
                  </td>
                  <td className="px-3 py-2">
                    {isSuper && !self ? (
                      <select aria-label={`Role for ${u.email}`} className="field min-h-9! py-1!" value={u.role} onChange={(e) => setRole(u, e.target.value)}>
                        <option value="candidate">User</option>
                        <option value="admin">Admin</option>
                        <option value="super_admin">Super admin</option>
                      </select>
                    ) : ROLE_NAME[u.role]}
                  </td>
                  <td className="px-3 py-2">
                    {isSuper ? (
                      <select aria-label={`Plan for ${u.email}`} className="field min-h-9! py-1!" value={u.plan} onChange={(e) => setPlan(u, e.target.value)}>
                        {Object.entries(PLAN_NAME).map(([id, name]) => <option key={id} value={id}>{name}</option>)}
                      </select>
                    ) : PLAN_NAME[u.plan]}
                  </td>
                  <td className="px-3 py-2 tabular-nums">{u.sessions}</td>
                  <td className="px-3 py-2">{u.lastLoginAt ? prettyDate(u.lastLoginAt) : "Never"}</td>
                  <td className="px-3 py-2">
                    {canManage && (
                      <div className="flex flex-wrap gap-3">
                        <button className="cursor-pointer underline" onClick={() => reset(u)}>Reset password</button>
                        <button className="cursor-pointer text-danger underline" onClick={() => toggle(u)}>{u.isDisabled ? "Enable" : "Disable"}</button>
                      </div>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <div className="flex items-center gap-3 text-sm">
        <button className="btn btn-quiet" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
        <span>Page {page} of {pages}</span>
        <button className="btn btn-quiet" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button>
      </div>
    </div>
  );
}

function TempPassword({ email, password, onClose }: { email: string; password: string; onClose: () => void }) {
  return (
    <div role="status" className="rounded-lg bg-warn-soft p-3 text-sm">
      <p><strong>Temporary password for {email}:</strong> <code data-testid="temp-password" className="font-mono select-all">{password}</code></p>
      <p className="mt-1">Share it privately. It is shown only once, and they must change it when they sign in.</p>
      <button className="mt-2 cursor-pointer underline" onClick={onClose}>Dismiss</button>
    </div>
  );
}

function Team() {
  const [form, setForm] = useState({ email: "", name: "", role: "admin" });
  const [error, setError] = useState<string | null>(null);
  const [temp, setTemp] = useState<{ email: string; password: string } | null>(null);

  async function create(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const r = await api<{ temporaryPassword: string }>("/admin/staff", { method: "POST", json: form });
      setTemp({ email: form.email, password: r.temporaryPassword });
      setForm({ email: "", name: "", role: "admin" });
    } catch (err) { setError(errorText(err)); }
  }

  return (
    <form onSubmit={create} className="card max-w-lg space-y-4">
      <h2 className="text-xl font-semibold">Add a staff member</h2>
      <div>
        <label className="label" htmlFor="staff-name">Name</label>
        <input id="staff-name" className="field" required value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
      </div>
      <div>
        <label className="label" htmlFor="staff-email">Email</label>
        <input id="staff-email" type="email" className="field" required value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
      </div>
      <div>
        <label className="label" htmlFor="staff-role">Role</label>
        <select id="staff-role" className="field" value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value })}>
          <option value="admin">Admin: supports users</option>
          <option value="super_admin">Super admin: full control</option>
        </select>
      </div>
      <ErrorNote message={error} />
      {temp && <TempPassword {...temp} onClose={() => setTemp(null)} />}
      <button className="btn">Create account</button>
    </form>
  );
}

function PaymentsTab() {
  const [data, setData] = useState<Payments | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api<Payments>("/admin/payments").then(setData).catch((e) => setError(errorText(e))); }, []);
  if (!data) return <ErrorNote message={error} />;
  return (
    <div className="space-y-3">
      <div className="max-w-xs"><Stat label="Total revenue" value={rupees(data.revenuePaise)} /></div>
      {data.payments.length === 0 ? <p className="text-ink-2">No payments yet.</p> : (
        <div className="card overflow-x-auto p-0!">
          <table className="w-full min-w-[560px] text-left text-sm">
            <thead><tr className="border-b border-line text-ink-2">{["Date", "User", "Plan", "Amount", "Status"].map((h) => <th key={h} className="px-3 py-2 font-medium">{h}</th>)}</tr></thead>
            <tbody>
              {data.payments.map((p) => (
                <tr key={p.id} className="border-b border-line last:border-0">
                  <td className="px-3 py-2">{prettyDate(p.createdAt)}</td><td className="px-3 py-2">{p.email ?? "Deleted account"}</td>
                  <td className="px-3 py-2">{PLAN_NAME[p.plan]}</td><td className="px-3 py-2 tabular-nums">{rupees(p.amountPaise)}</td><td className="px-3 py-2">{p.status}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function AuditTab() {
  const [rows, setRows] = useState<Audit[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api<Audit[]>("/admin/audit").then(setRows).catch((e) => setError(errorText(e))); }, []);
  if (!rows) return <ErrorNote message={error} />;
  if (rows.length === 0) return <p className="text-ink-2">No staff actions yet.</p>;
  return (
    <div className="card overflow-x-auto p-0!">
      <table className="w-full min-w-[640px] text-left text-sm">
        <thead><tr className="border-b border-line text-ink-2">{["When", "Staff member", "Action", "On", "Detail"].map((h) => <th key={h} className="px-3 py-2 font-medium">{h}</th>)}</tr></thead>
        <tbody>
          {rows.map((a) => (
            <tr key={a.id} className="border-b border-line last:border-0">
              <td className="px-3 py-2">{new Date(a.createdAt).toLocaleString("en-IN")}</td><td className="px-3 py-2">{a.actorEmail}</td>
              <td className="px-3 py-2 font-mono text-xs">{a.action}</td><td className="px-3 py-2">{a.targetEmail}</td><td className="px-3 py-2">{a.detail}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export default function AdminPage() {
  const router = useRouter();
  const me = useMe();
  const [tab, setTab] = useState("Overview");
  useEffect(() => { if (me && !isStaff(me.role)) router.replace("/dashboard/"); }, [me, router]);

  const isSuper = me?.role === "super_admin";
  const tabs = isSuper ? ["Overview", "Users", "Team", "Payments", "Audit log"] : ["Overview", "Users"];

  return (
    <AppShell wide>
      {me && isStaff(me.role) && (
        <>
          <div className="flex flex-wrap items-baseline justify-between gap-2">
            <h1 className="text-3xl font-semibold">{isSuper ? "Super admin" : "Admin"}</h1>
            {isSuper && <a className="text-sm underline" href="/ops/jobs">Background jobs</a>}
          </div>
          <nav className="mt-4 flex flex-wrap gap-1 border-b border-line" aria-label="Admin sections">
            {tabs.map((t) => (
              <button key={t} aria-current={tab === t ? "page" : undefined} onClick={() => setTab(t)}
                className={`cursor-pointer border-b-2 px-3 py-2 text-sm font-semibold ${tab === t ? "border-accent text-ink" : "border-transparent text-ink-2"}`}>{t}</button>
            ))}
          </nav>
          <div className="mt-4">
            {tab === "Overview" && <Overview />}
            {tab === "Users" && <Users meId={me.id} isSuper={isSuper} />}
            {tab === "Team" && <Team />}
            {tab === "Payments" && <PaymentsTab />}
            {tab === "Audit log" && <AuditTab />}
          </div>
        </>
      )}
    </AppShell>
  );
}
