"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { Activity, ArrowLeft, Clock3, Layers3, Pause, Play, ShieldCheck, Users, Zap } from "lucide-react";
import { ShoppingControls, useShopping } from "./shopping";
import "./admin-page.css";

type Run = { id: string; userId: string; displayName: string; status: string; createdAt: string; durationMs: number | null;
  toolCalls: number; toolErrors: number; modelCalls: number; inputTokens: number | null; outputTokens: number | null };
type Snapshot = { generatedAt: string; windowStart: string; limit: number; truncated: boolean; usersTotal: number; pendingReviews: number;
  users: { id: string; displayName: string; email: string; locked: boolean; conversations: number }[];
  runs: Run[]; tools: { name: string; calls: number; errors: number }[] };
type Event = { sequence: number; at: string; kind: string; name: string | null; isError: boolean | null;
  durationMs: number | null; inputTokens: number | null; outputTokens: number | null; errorType: string | null };
const duration = (value: number | null) => value === null ? "—" : value < 1000 ? `${Math.round(value)} ms` : `${(value / 1000).toFixed(1)} s`;
const time = (value: string) => new Date(value).toLocaleTimeString("en-GB");

export function AdminPage() {
  const shop = useShopping();
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [events, setEvents] = useState<Event[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [user, setUser] = useState("all");
  const [paused, setPaused] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [detailError, setDetailError] = useState<string | null>(null);

  useEffect(() => {
    setSnapshot(null); setSelected(null); setEvents([]); setUser("all"); setError(null);
  }, [shop.user?.id]);

  useEffect(() => {
    if (!shop.user?.isAdmin) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      if (document.visibilityState === "visible") {
        try {
          const response = await fetch("/api/shop/admin/overview", { cache: "no-store", signal: controller.signal });
          if (!response.ok) {
            if (response.status === 401 || response.status === 403) setSnapshot(null);
            throw new Error(response.status === 401 || response.status === 403 ? "Administrator access is required. Sign in again or check your permissions." : "Connection lost. Displaying the last snapshot.");
          }
          const data: Snapshot = await response.json();
          if (!controller.signal.aborted) { setSnapshot(data); setError(null); }
        } catch (cause) {
          if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : "Unable to refresh activity.");
        }
      }
      if (!controller.signal.aborted && !paused) timer = setTimeout(poll, 3000);
    }
    void poll();
    return () => { controller.abort(); clearTimeout(timer); };
  }, [shop.user?.id, shop.user?.isAdmin, paused]);

  useEffect(() => {
    setEvents([]); setDetailError(null);
    if (!selected || !shop.user?.isAdmin) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      if (document.visibilityState === "visible") {
        try {
          const response = await fetch(`/api/shop/admin/turns/${selected}`, { cache: "no-store", signal: controller.signal });
          if (!response.ok) throw new Error("Timeline unavailable. The conversation may have been deleted or access revoked.");
          const data: { events: Event[]; truncated: boolean } = await response.json();
          if (!controller.signal.aborted) { setEvents(data.events); setDetailError(data.truncated ? "Showing the first 200 events." : null); }
        } catch (cause) {
          if (!controller.signal.aborted) { setEvents([]); setDetailError(cause instanceof Error ? cause.message : "Timeline unavailable."); }
        }
      }
      if (!controller.signal.aborted && !paused) timer = setTimeout(poll, 3000);
    }
    void poll();
    return () => { controller.abort(); clearTimeout(timer); };
  }, [selected, shop.user?.id, shop.user?.isAdmin, paused]);

  const runs = snapshot?.runs.filter(run => user === "all" || run.userId === user) ?? [];
  const active = runs.filter(run => run.status === "Running").length;
  const failed = runs.filter(run => run.status === "Failed" || run.status === "Interrupted").length;
  const measured = runs.filter(run => run.inputTokens !== null);
  const tokens = measured.reduce((sum, run) => sum + (run.inputTokens ?? 0) + (run.outputTokens ?? 0), 0);
  const timings = runs.filter(run => run.status === "Completed" && run.durationMs !== null).map(run => run.durationMs!).sort((a, b) => a - b);
  const chosen = snapshot?.runs.find(run => run.id === selected);

  return <div className="admin-shell">
    <header className="admin-header"><Link href="/" className="admin-brand"><Layers3 size={24} /> CommercePilot <span>OPERATIONS</span></Link><ShoppingControls /></header>
    <main className="admin-main">
      <Link href="/" className="admin-back"><ArrowLeft size={14} /> Back to the store</Link>
      <div className="admin-heading"><div><div className="admin-eyebrow"><ShieldCheck size={14} /> ADMINISTRATOR WORKSPACE</div>
        <h1>Behind the conversation.</h1><p>People, requests and the tools that bring your assistant to life.</p></div>
        {shop.user?.isAdmin && <button className="admin-toggle" onClick={() => setPaused(!paused)}>{paused ? <Play size={15} /> : <Pause size={15} />}{paused ? "Resume updates" : "Pause updates"}</button>}</div>
      {!shop.ready ? <p className="admin-empty">Checking your session…</p> : !shop.user?.isAdmin ?
        <section className="admin-panel admin-gate"><ShieldCheck size={34} /><h2>Administrator access</h2><p>Sign in with your administrator account to view activity. Shopper accounts cannot access this workspace.</p>
          <button className="admin-toggle" onClick={() => shop.setAccountOpen(true)}>Open account</button></section> : <>
        <div className="admin-live"><span className={error || paused ? "admin-dot idle" : "admin-dot"} />{error ? "Disconnected" : paused ? "Updates paused" : "Auto-refresh · every 3 seconds"}
          <span>{snapshot ? `Last updated ${time(snapshot.generatedAt)}` : "Loading activity…"}</span></div>
        {error && <p className="admin-alert" role="alert">{error}</p>}
        {snapshot && <>
          <div className="admin-metrics">
            <Metric title="Chat requests" value={String(runs.length)} detail="In the current window" icon={<Activity size={19} />} />
            <Metric title="Running now" value={String(active)} detail="Active conversation leases" icon={<Zap size={19} />} />
            <Metric title="Failed / interrupted" value={String(failed)} detail={`${runs.length ? Math.round(failed / runs.length * 100) : 0}% of displayed requests`} icon={<ShieldCheck size={19} />} />
            <Metric title="Recorded tokens" value={tokens.toLocaleString("en-US")} detail={`${measured.length} requests with model telemetry`} icon={<Layers3 size={19} />} />
          </div>
          <div className="admin-context"><span><Users size={14} /> {snapshot.usersTotal} users</span><span><Clock3 size={14} /> P95 completed: {duration(timings.length ? timings[Math.ceil(timings.length * .95) - 1] : null)}</span><span>{snapshot.pendingReviews} pending reviews</span></div>
          <p className="admin-note">Latest {snapshot.limit} retained chat requests in the last 24 hours. {snapshot.truncated && "Window limit reached. "}Older requests may not have token or timing telemetry. Counts include review imports. Tokens include model thinking; its contents are never shown.</p>
          <div className="admin-grid"><div>
            <section className="admin-panel"><div className="admin-panel-title"><h2>Request activity</h2><label>User <select value={user} onChange={event => { setUser(event.target.value); setSelected(null); }}><option value="all">All users</option>{snapshot.users.map(u => <option key={u.id} value={u.id}>{u.displayName}</option>)}</select></label></div>
              {!runs.length ? <p className="admin-empty">No requests in this window. Open the assistant and send a message to see activity here.</p> : <div className="admin-table-wrap"><table className="admin-table"><thead><tr><th>User / request</th><th>Status</th><th>Time</th><th>Tools</th><th>Tokens</th></tr></thead><tbody>{runs.map(run =>
                <tr key={run.id} className={selected === run.id ? "selected" : ""}><td><button className="admin-run" onClick={() => setSelected(run.id)}><strong>{run.displayName}</strong><small>{time(run.createdAt)} · {run.id.slice(0, 8)}</small></button></td><td><span className={`admin-status ${run.status.toLowerCase()}`}>{run.status}</span></td><td>{duration(run.durationMs)}{run.status === "Running" && <small>elapsed</small>}</td><td>{run.toolCalls}{run.toolErrors > 0 && <small>{run.toolErrors} errors</small>}</td><td>{run.inputTokens === null ? "—" : `${run.inputTokens} / ${run.outputTokens}`}</td></tr>
              )}</tbody></table></div>}
              <p className="admin-footnote">Select a request to inspect its timeline. Tokens: input / output.</p></section>
            <section className="admin-panel"><div className="admin-panel-title"><h2>People</h2><span>{snapshot.users.length} of {snapshot.usersTotal}</span></div><div className="admin-people">{snapshot.users.map(person =>
              <button key={person.id} onClick={() => { setUser(person.id); setSelected(null); }}><span className="admin-avatar">{person.displayName.slice(0, 1)}</span><span><strong>{person.displayName}</strong><small>{person.email}</small></span><span className="admin-person-meta">{person.locked ? "Locked" : `${person.conversations} conversations`}</span></button>
            )}</div></section>
          </div><aside>
            <section className="admin-panel"><div className="admin-panel-title"><h2>Request timeline</h2><Activity size={16} /></div>
              {!selected ? <p className="admin-empty">Select a request to follow model and tool steps.</p> : <><p className="admin-footnote">{chosen?.displayName ?? "Request"} · {selected.slice(0, 8)}</p>{detailError && <p className="admin-alert" role="status">{detailError}</p>}<ol className="admin-timeline">{events.map(event => <li key={event.sequence} className={event.isError || event.kind === "turn.failed" ? "failed" : ""}><div><strong>{event.name ?? event.kind.replaceAll(".", " ")}</strong><time>{time(event.at)}</time></div><small>{event.name ? event.kind.replaceAll(".", " ") : event.errorType ?? ""}{event.isError ? " · error" : ""}{event.durationMs !== null ? ` · ${duration(event.durationMs)}` : ""}{event.inputTokens !== null ? ` · ${event.inputTokens} in / ${event.outputTokens} out` : ""}</small></li>)}</ol>{!events.length && !detailError && <p className="admin-empty">Waiting for recorded events…</p>}</>}
            </section>
            <section className="admin-panel"><div className="admin-panel-title"><h2>MCP tool activity</h2><Zap size={16} /></div><p className="admin-footnote">All users · current window</p>{!snapshot.tools.length ? <p className="admin-empty">No tool calls recorded yet.</p> : snapshot.tools.map(tool => <div className="admin-tool" key={tool.name}><div><code>{tool.name}</code><span>{tool.calls}</span></div><div className="admin-bar"><i style={{ width: `${tool.calls / Math.max(...snapshot.tools.map(t => t.calls)) * 100}%` }} /></div>{tool.errors > 0 && <small>{tool.errors} errors</small>}</div>)}</section>
          </aside></div>
          <p className="admin-note">Source: persisted storefront chat events. Terminal LangGraph runs are separate and are not attributed to shoppers. This workspace shows operational metadata, not conversation text or tool payloads.</p>
        </>}
      </>}
    </main>
  </div>;
}

function Metric({ title, value, detail, icon }: { title: string; value: string; detail: string; icon: React.ReactNode }) {
  return <section className="admin-metric"><div>{title}{icon}</div><strong>{value}</strong><small>{detail}</small></section>;
}
