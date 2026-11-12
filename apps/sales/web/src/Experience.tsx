import { useEffect, useMemo, useState } from "react";
import { LoaderCircle, RefreshCw } from "lucide-react";
import { api, ApiError } from "./api";

export interface ExperienceGroup {
  name: string;
  classification: string;
  userCount?: number | null;
  experienceProfile?: string | null;
  answers?: Record<string, string>;
}

export interface ExperienceSelection {
  industryKey?: string | null;
  industryOther?: string | null;
  answers?: Record<string, string>;
  groups?: ExperienceGroup[];
  frameworks?: string[];
}

interface Catalog {
  industries: { code: string; name: string }[];
  otherIndustryCode: string;
  frameworks: { code: string; name: string; description: string }[];
  classifications: { code: string; name: string; description: string; defaultProfile: string }[];
  profiles: { code: string; name: string; description: string }[];
  questions: {
    code: string;
    solution: string;
    topic: string;
    question: string;
    whyItMatters: string;
    defaultOption: string;
    options: { code: string; label: string; userExperience: string }[];
  }[];
}

interface Evaluation {
  groups: {
    key: string;
    name: string;
    classification: string;
    userCount?: number | null;
    profile: string;
    security: number;
    friction: number;
    securityWithFrameworks: number;
    frictionWithFrameworks: number;
    conflicts: string[];
    weaknesses: string[];
    findings: { question: string; option: string; framework: string; effect: string; reason: string; suggestion?: string }[];
  }[];
}

export function useCatalog() {
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [error, setError] = useState("");
  useEffect(() => {
    api<Catalog>("/api/experience/catalog").then(setCatalog).catch((e) =>
      setError(e instanceof ApiError ? e.message : "Experience catalog is unavailable."));
  }, []);
  return { catalog, error };
}

export function useEvaluation(selection: ExperienceSelection, enabled: boolean) {
  const [result, setResult] = useState<Evaluation | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const key = JSON.stringify(selection);
  const run = async () => {
    setBusy(true);
    setError("");
    try {
      setResult(await api<Evaluation>("/api/experience/evaluate", {
        method: "POST",
        body: JSON.stringify({ experience: selection, frameworks: selection.frameworks ?? [] }),
      }));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Evaluation failed.");
    } finally {
      setBusy(false);
    }
  };
  useEffect(() => {
    if (!enabled || !(selection.groups ?? []).some((g) => g.name.trim())) { setResult(null); return; }
    const timer = setTimeout(() => void run(), 500);
    return () => clearTimeout(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, enabled]);
  return { result, busy, error, run };
}

function Scores({ evaluation, busy, error }: { evaluation: Evaluation | null; busy: boolean; error: string }) {
  if (error) return <p className="muted-note" role="alert">{error}</p>;
  if (!evaluation) return <p className="muted-note">{busy ? "Evaluating…" : "Add a user group to preview its experience and security scores."}</p>;
  return (
    <div className="exp-results">
      {evaluation.groups.map((g) => (
        <div className="exp-group" key={g.key}>
          <div className="exp-group-head">
            <strong>{g.name}</strong>
            <span className="exp-chip">{g.profile}</span>
            {g.userCount ? <span className="exp-chip">{g.userCount} users</span> : null}
          </div>
          <div className="exp-bars">
            <Bar label="Security" value={g.securityWithFrameworks || g.security} tone="security" />
            <Bar label="Friction" value={g.frictionWithFrameworks || g.friction} tone="friction" />
          </div>
          {[...g.conflicts, ...g.weaknesses].map((item, i) => <p className="exp-warn" key={i}>{item}</p>)}
          {g.findings.map((f, i) => (
            <p className="exp-finding" key={i}>
              <strong>{f.framework}</strong> · {f.question}: {f.option} — {f.reason}
              {f.suggestion ? <em> Suggest: {f.suggestion}</em> : null}
            </p>
          ))}
        </div>
      ))}
    </div>
  );
}

function Bar({ label, value, tone }: { label: string; value: number; tone: string }) {
  return (
    <div className="exp-bar">
      <span>{label}</span>
      <div><i className={tone} style={{ width: `${Math.max(0, Math.min(100, value))}%` }} /></div>
      <b>{Math.round(value)}</b>
    </div>
  );
}

export function ExperienceStep({
  value,
  onChange,
}: {
  value: ExperienceSelection;
  onChange: (next: ExperienceSelection) => void;
}) {
  const { catalog, error: catalogError } = useCatalog();
  const { result, busy, error, run } = useEvaluation(value, Boolean(catalog));
  const [showQuestions, setShowQuestions] = useState(false);
  const groups = value.groups ?? [];
  const bySolution = useMemo(() => {
    const map = new Map<string, Catalog["questions"]>();
    catalog?.questions.forEach((q) => map.set(q.solution, [...(map.get(q.solution) ?? []), q]));
    return [...map.entries()];
  }, [catalog]);

  if (catalogError) return <p className="muted-note" role="alert">{catalogError}</p>;
  if (!catalog) return <div className="loading-view"><LoaderCircle className="spin" /> Loading experience catalog…</div>;

  const set = (patch: Partial<ExperienceSelection>) => onChange({ ...value, ...patch });
  const setGroup = (index: number, patch: Partial<ExperienceGroup>) =>
    set({ groups: groups.map((g, i) => (i === index ? { ...g, ...patch } : g)) });
  const toggleFramework = (code: string) => {
    const current = value.frameworks ?? [];
    set({ frameworks: current.includes(code) ? current.filter((c) => c !== code) : [...current, code] });
  };

  return (
    <div className="exp-step">
      <div className="form-grid">
        <label className="field">
          <span>Industry</span>
          <select value={value.industryKey ?? ""} onChange={(e) => set({ industryKey: e.target.value || null })}>
            <option value="">Not established</option>
            {catalog.industries.map((i) => <option key={i.code} value={i.code}>{i.name}</option>)}
            <option value={catalog.otherIndustryCode}>Other</option>
          </select>
        </label>
        {value.industryKey === catalog.otherIndustryCode && (
          <label className="field">
            <span>Other industry</span>
            <input value={value.industryOther ?? ""} onChange={(e) => set({ industryOther: e.target.value })} maxLength={100} />
          </label>
        )}
      </div>

      <h3 className="exp-sub">User groups</h3>
      {groups.map((g, index) => (
        <div className="exp-group-row" key={index}>
          <input aria-label="Group name" placeholder="Group name (e.g. Executives)" value={g.name} maxLength={80}
            onChange={(e) => setGroup(index, { name: e.target.value })} />
          <select aria-label="Classification" value={g.classification}
            onChange={(e) => setGroup(index, { classification: e.target.value, experienceProfile: null })}>
            {catalog.classifications.map((c) => <option key={c.code} value={c.code}>{c.name}</option>)}
          </select>
          <input aria-label="User count" type="number" min="0" placeholder="Users" value={g.userCount ?? ""}
            onChange={(e) => setGroup(index, { userCount: e.target.value ? Number(e.target.value) : null })} />
          <select aria-label="Experience profile" value={g.experienceProfile ?? ""}
            onChange={(e) => setGroup(index, { experienceProfile: e.target.value || null })}>
            <option value="">Default for classification</option>
            {catalog.profiles.map((p) => <option key={p.code} value={p.code}>{p.name}</option>)}
          </select>
          <button type="button" className="button button-quiet" onClick={() => set({ groups: groups.filter((_, i) => i !== index) })}>Remove</button>
        </div>
      ))}
      {groups.length < 25 && (
        <button type="button" className="button button-secondary"
          onClick={() => set({ groups: [...groups, { name: "", classification: catalog.classifications[0]?.code ?? "" }] })}>
          Add user group
        </button>
      )}

      <h3 className="exp-sub">Compliance frameworks</h3>
      <div className="exp-frameworks">
        {catalog.frameworks.map((f) => (
          <label key={f.code} title={f.description}>
            <input type="checkbox" checked={(value.frameworks ?? []).includes(f.code)} onChange={() => toggleFramework(f.code)} /> {f.name}
          </label>
        ))}
      </div>

      <button type="button" className="button button-quiet" onClick={() => setShowQuestions((v) => !v)}>
        {showQuestions ? "Hide" : "Fine-tune"} experience answers
      </button>
      {showQuestions && bySolution.map(([solution, questions]) => (
        <details key={solution} className="exp-solution">
          <summary>{solution}</summary>
          {questions.map((q) => (
            <label className="field" key={q.code} title={q.whyItMatters}>
              <span>{q.question}</span>
              <select value={value.answers?.[q.code] ?? ""}
                onChange={(e) => {
                  const next = { ...(value.answers ?? {}) };
                  if (e.target.value) next[q.code] = e.target.value; else delete next[q.code];
                  set({ answers: next });
                }}>
                <option value="">Default</option>
                {q.options.map((o) => <option key={o.code} value={o.code}>{o.label}</option>)}
              </select>
            </label>
          ))}
        </details>
      ))}

      <div className="exp-preview-head">
        <h3 className="exp-sub">Live preview</h3>
        <button type="button" className="button button-quiet" onClick={() => void run()} disabled={busy}>
          <RefreshCw size={14} className={busy ? "spin" : ""} /> Refresh
        </button>
      </div>
      <Scores evaluation={result} busy={busy} error={error} />
    </div>
  );
}

export function ExperienceReport({ experience }: { experience: ExperienceSelection }) {
  const { result, busy, error } = useEvaluation(experience, true);
  const industry = experience.industryOther || experience.industryKey;
  return (
    <section className="content-card exp-report">
      <div className="card-heading">
        <div><h2>Experience &amp; security report</h2><p>{industry ? `Industry: ${industry}` : "Industry not captured"}</p></div>
        <span className="record-count">SECURE PLUS</span>
      </div>
      <Scores evaluation={result} busy={busy} error={error} />
    </section>
  );
}

export function SyncBadge({ status, message }: { status?: string | null; message?: string | null }) {
  const value = status ?? "Pending";
  const tone = value === "Synced" ? "ok" : value === "Failed" ? "bad" : "wait";
  const label = value === "NotConfigured" ? "Not linked" : value === "Synced" ? "Synced to deployment" : value;
  return <span className={`sync-badge ${tone}`} title={message ?? undefined}>{label}</span>;
}
