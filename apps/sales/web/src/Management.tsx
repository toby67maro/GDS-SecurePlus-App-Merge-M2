import { useCallback, useEffect, useMemo, useState } from "react";
import { LoaderCircle, RefreshCw } from "lucide-react";
import { api, ApiError } from "./api";
import { SyncBadge } from "./Experience";

interface Row {
  id: string;
  companyName: string;
  salesperson: string;
  ownerEmail?: string | null;
  meetingDate?: string | null;
  createdUtc: string;
  users?: number | null;
  endpoints?: number | null;
  oneTimeHoursMin: number;
  oneTimeHoursMax: number;
  monthlyHoursMin: number;
  monthlyHoursMax: number;
  taskCount: number;
  plannerHandoffStatus?: string | null;
  deploymentSyncStatus: string;
  assessmentStatus?: string | null;
  assessmentScore?: number | null;
}

interface Summary {
  total: number;
  last30Days: number;
  synced: number;
  needsAttention: number;
  bySalesperson: { salesperson: string; count: number; hoursMax: number }[];
}

export function ManagementDashboard({ onOpen }: { onOpen: (id: string) => void }) {
  const [rows, setRows] = useState<Row[]>([]);
  const [summary, setSummary] = useState<Summary | null>(null);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [seller, setSeller] = useState("");
  const [status, setStatus] = useState("");
  const [search, setSearch] = useState("");
  const [busyId, setBusyId] = useState("");

  const load = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      const [list, totals] = await Promise.all([
        api<Row[]>("/api/management/engagements"),
        api<Summary>("/api/management/summary"),
      ]);
      setRows(list);
      setSummary(totals);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Management data could not be loaded.");
    } finally {
      setLoading(false);
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const resync = async (id: string) => {
    setBusyId(id);
    try {
      await api(`/api/management/engagements/${id}/resync`, { method: "POST" });
      await load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Resync failed.");
    } finally {
      setBusyId("");
    }
  };

  const sellers = useMemo(() => [...new Set(rows.map((r) => r.salesperson))].sort(), [rows]);
  const filtered = rows.filter((r) =>
    (!seller || r.salesperson === seller) &&
    (!status || r.deploymentSyncStatus === status) &&
    (!search || r.companyName.toLowerCase().includes(search.toLowerCase())));

  return (
    <div className="page management-page">
      <div className="page-heading">
        <div>
          <div className="eyebrow"><span className="eyebrow-line" /> MANAGEMENT</div>
          <h1>All engagements</h1>
          <p>Every submitted engagement across all salespeople, with deployment sync and assessment status.</p>
        </div>
        <button className="button button-secondary" onClick={() => void load()} disabled={loading}>
          <RefreshCw size={15} className={loading ? "spin" : ""} /> Refresh
        </button>
      </div>

      {error && <div className="page-alert" role="alert">{error}</div>}

      {summary && (
        <div className="mgmt-metrics">
          <div className="detail-mini-card"><strong>{summary.total}</strong><small>Total engagements</small></div>
          <div className="detail-mini-card"><strong>{summary.last30Days}</strong><small>Last 30 days</small></div>
          <div className="detail-mini-card"><strong>{summary.synced}</strong><small>Synced to deployment</small></div>
          <div className="detail-mini-card"><strong>{summary.needsAttention}</strong><small>Need attention</small></div>
        </div>
      )}

      <div className="mgmt-filters">
        <input placeholder="Search company…" value={search} onChange={(e) => setSearch(e.target.value)} />
        <select value={seller} onChange={(e) => setSeller(e.target.value)} aria-label="Salesperson">
          <option value="">All salespeople</option>
          {sellers.map((s) => <option key={s}>{s}</option>)}
        </select>
        <select value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Sync status">
          <option value="">All sync states</option>
          {["Synced", "Pending", "Failed", "NotConfigured"].map((s) => <option key={s}>{s}</option>)}
        </select>
      </div>

      <section className="content-card">
        {loading && !rows.length ? (
          <div className="loading-view"><LoaderCircle className="spin" /> Loading…</div>
        ) : (
          <table>
            <thead>
              <tr><th>Company</th><th>Salesperson</th><th>Created</th><th>Users</th><th>One-time hrs</th><th>Sync</th><th>Assessment</th><th /></tr>
            </thead>
            <tbody>
              {filtered.map((r) => (
                <tr key={r.id}>
                  <td><button className="link-button" onClick={() => onOpen(r.id)}>{r.companyName}</button></td>
                  <td>{r.salesperson}<br /><small>{r.ownerEmail}</small></td>
                  <td>{new Date(r.createdUtc).toLocaleDateString()}</td>
                  <td>{r.users ?? "—"}</td>
                  <td>{r.oneTimeHoursMin}–{r.oneTimeHoursMax}</td>
                  <td><SyncBadge status={r.deploymentSyncStatus} /></td>
                  <td>{r.assessmentStatus ? `${r.assessmentStatus}${r.assessmentScore != null ? ` · ${r.assessmentScore}` : ""}` : "—"}</td>
                  <td>
                    {r.deploymentSyncStatus !== "Synced" && (
                      <button className="button button-quiet" disabled={busyId === r.id} onClick={() => void resync(r.id)}>
                        {busyId === r.id ? "Syncing…" : "Resync"}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
              {!filtered.length && <tr><td colSpan={8} className="muted-note">No engagements match.</td></tr>}
            </tbody>
          </table>
        )}
      </section>

      {summary && summary.bySalesperson.length > 0 && (
        <section className="content-card mgmt-by-seller">
          <div className="card-heading"><div><h2>By salesperson</h2></div></div>
          <table>
            <thead><tr><th>Salesperson</th><th>Engagements</th><th>Max one-time hours</th></tr></thead>
            <tbody>{summary.bySalesperson.map((s) => <tr key={s.salesperson}><td>{s.salesperson}</td><td>{s.count}</td><td>{s.hoursMax}</td></tr>)}</tbody>
          </table>
        </section>
      )}
    </div>
  );
}
