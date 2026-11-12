import { useCallback, useEffect, useMemo, useState, type FormEvent, type ReactNode } from "react";
import {
  ArrowLeft,
  ArrowRight,
  ArrowUpRight,
  Check,
  ChevronDown,
  CircleHelp,
  ClipboardList,
  Clock3,
  Cloud,
  Download,
  FileSpreadsheet,
  FileText,
  Layers3,
  LoaderCircle,
  Plus,
  RefreshCw,
  Shield,
  Eye,
  Sparkles,
  Sun,
  UsersRound,
  Moon,
} from "lucide-react";
import { api, ApiError } from "./api";
import { ExperienceReport, ExperienceStep, SyncBadge, type ExperienceSelection } from "./Experience";
import { ManagementDashboard } from "./Management";
import type {
  Engagement,
  EngagementListItem,
  Estimate,
  AuthSession,
  Health,
  IntakeAnswers,
  WbsTask,
} from "./types";

type Screen =
  | { kind: "home" }
  | { kind: "new" }
  | { kind: "detail"; id: string }
  | { kind: "management" };

type CustomerFields = {
  companyName: string;
  salesperson: string;
  contactName: string;
  contactEmail: string;
  meetingDate: string;
  users: string;
  endpoints: string;
  notes: string;
};

const emptyCustomer: CustomerFields = {
  companyName: "",
  salesperson: "",
  contactName: "",
  contactEmail: "",
  meetingDate: "",
  users: "",
  endpoints: "",
  notes: "",
};

const initialHealth: Health = {
  status: "loading",
  databaseProvider: "Sqlite",
  plannerHandoff: "not-configured",
};

function formatHours(min: number, max: number) {
  return `${formatNumber(min)}–${formatNumber(max)} hrs`;
}

function formatNumber(value: number) {
  return new Intl.NumberFormat("en-US", { maximumFractionDigits: 1 }).format(value);
}

function formatDate(value: string | null | undefined) {
  if (!value) return "Date not set";
  return new Intl.DateTimeFormat("en-US", {
    month: "short",
    day: "numeric",
    year: "numeric",
    timeZone: "UTC",
  }).format(new Date(`${value.slice(0, 10)}T12:00:00Z`));
}

function taskCountLabel(count: number) {
  return `${count} ${count === 1 ? "task" : "tasks"}`;
}

export default function App() {
  const [theme, setTheme] = useState<"light" | "dark">(() => {
    try {
      return localStorage.getItem("gds-theme") === "dark" ? "dark" : "light";
    } catch {
      return "light";
    }
  });
  const [screen, setScreen] = useState<Screen>({ kind: "home" });
  const [authSession, setAuthSession] = useState<AuthSession | null>(null);
  const [authLoaded, setAuthLoaded] = useState(false);
  const [authError, setAuthError] = useState("");
  const [health, setHealth] = useState<Health>(initialHealth);
  const [engagements, setEngagements] = useState<EngagementListItem[]>([]);
  const [activeEngagement, setActiveEngagement] = useState<Engagement | null>(null);
  const [loading, setLoading] = useState(true);
  const [pageError, setPageError] = useState("");

  const refreshDashboard = useCallback(async () => {
    setPageError("");
    try {
      const [serviceHealth, items] = await Promise.all([
        api<Health>("/api/health"),
        api<EngagementListItem[]>("/api/engagements"),
      ]);
      setHealth(serviceHealth);
      setEngagements(items);
    } catch (error) {
      setPageError(error instanceof Error ? error.message : "The local app could not be reached.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    let active = true;
    api<AuthSession>("/api/auth/me")
      .then((session) => {
        if (active) setAuthSession(session);
      })
      .catch((error: unknown) => {
        if (active) setAuthError(error instanceof Error ? error.message : "Sign-in status could not be checked.");
      })
      .finally(() => {
        if (active) setAuthLoaded(true);
      });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    if (!authLoaded) return;
    if (authSession?.authenticated) {
      void refreshDashboard();
    } else {
      setLoading(false);
    }
  }, [authLoaded, authSession, refreshDashboard]);

  useEffect(() => {
    if (screen.kind !== "detail") {
      setActiveEngagement(null);
      return;
    }
    let active = true;
    setPageError("");
    api<Engagement>(`/api/engagements/${screen.id}`)
      .then((item) => {
        if (active) setActiveEngagement(item);
      })
      .catch((error: unknown) => {
        if (active) {
          setPageError(error instanceof Error ? error.message : "Engagement could not be loaded.");
        }
      });
    return () => {
      active = false;
    };
  }, [screen]);

  const openEngagement = (id: string) => setScreen({ kind: "detail", id });

  const handleCreated = async (item: Engagement) => {
    await refreshDashboard();
    setScreen({ kind: "detail", id: item.id });
  };

  const canViewAll = authSession?.roles.some((role) => ["Management", "DeploymentAdmin"].includes(role)) ?? false;

  const signOut = async () => {
    try {
      await api<void>("/api/auth/logout", { method: "POST" });
      window.location.assign("/");
    } catch (error) {
      setPageError(error instanceof Error ? error.message : "Sign out failed.");
    }
  };

  const toggleTheme = () => {
    setTheme((current) => {
      const next = current === "light" ? "dark" : "light";
      try {
        localStorage.setItem("gds-theme", next);
      } catch {
        // Keep the toggle usable when browser storage is unavailable.
      }
      return next;
    });
  };

  return (
    !authLoaded ? (
      <div className="auth-loading"><LoaderCircle className="spin" /> Checking sign-in…</div>
    ) : !authSession?.authenticated ? (
      <div className="auth-page">
        <div className="auth-card">
          <img className="brand-logo" src="/gds-logo-white.png" alt="Global Data Systems" />
          <div className="eyebrow"><span className="eyebrow-line" /> GLOBAL DATA SYSTEMS · SECURE PLUS</div>
          <h1>Sign in to your workspace</h1>
          <p>Use your organization account to access sales or deployment tools assigned to your role.</p>
          {authError && <div className="page-alert" role="alert"><CircleHelp size={17} />{authError}</div>}
          <button className="button button-primary auth-sign-in" onClick={() => window.location.assign("/api/auth/login")}>
            Sign in with Microsoft Entra ID
          </button>
        </div>
      </div>
    ) : (
    <div className="app-frame" data-theme={theme}>
      <aside className="sidebar">
        <button className="brand-lockup" onClick={() => setScreen({ kind: "home" })}>
          <img className="brand-logo" src="/gds-logo-white.png" alt="Global Data Systems" />
          <span className="brand-copy">
            <strong>Secure Plus</strong>
            <span>SALES INTAKE</span>
          </span>
        </button>
        <div className="sidebar-section-label">WORKSPACE</div>
        <button
          className={`nav-item ${screen.kind === "home" ? "active" : ""}`}
          onClick={() => setScreen({ kind: "home" })}
        >
          <Layers3 size={17} />
          <span>Engagements</span>
          <span className="nav-count">{engagements.length}</span>
        </button>
        {canViewAll && <button
          className={`nav-item ${screen.kind === "management" ? "active" : ""}`}
          onClick={() => setScreen({ kind: "management" })}
        >
          <Eye size={17} />
          <span>All engagements</span>
        </button>}
        <div className="sidebar-bottom">
          <div className="sidebar-status">
            <span className="status-dot" />
            <div>
              <strong>Local workspace</strong>
              <span>{health.databaseProvider} database</span>
            </div>
          </div>
          <div className="sidebar-version">GDS SECURE PLUS <span>SALES</span></div>
        </div>
      </aside>

      <main className="main-panel">
        <header className="topbar">
          <div className="breadcrumb">
            <span>Sales workspace</span>
            {screen.kind !== "home" && <><span className="crumb-slash">/</span><strong>{screen.kind === "new" ? "New engagement" : screen.kind === "detail" ? "Engagement" : "Deployment workspace"}</strong></>}
          </div>
          <div className="topbar-right">
            <button
              className="theme-toggle"
              type="button"
              onClick={toggleTheme}
              aria-label={`Switch to ${theme === "light" ? "dark" : "light"} mode`}
              aria-pressed={theme === "dark"}
            >
              {theme === "light" ? <Moon size={15} /> : <Sun size={15} />}
              <span>{theme === "light" ? "Dark mode" : "Light mode"}</span>
            </button>
            <div className={`connection-pill ${health.status === "ready" ? "connected" : "disconnected"}`}>
              <span className="status-dot" />
              {health.status === "ready" ? "Local data connected" : "API not connected"}
            </div>
            <span className="session-name">{authSession.name}</span>
            {!authSession.localDevelopment && (
              <button className="button button-quiet" onClick={() => void signOut()}>Sign out</button>
            )}
            <div className="avatar">{(authSession.name ?? "U").slice(0, 1).toUpperCase()}</div>
          </div>
        </header>

        {pageError && (
          <div className="page-alert" role="alert">
            <CircleHelp size={18} />
            <span>{pageError}</span>
            <button className="button button-quiet" onClick={() => void refreshDashboard()}>
              <RefreshCw size={15} /> Retry
            </button>
          </div>
        )}

        {screen.kind === "home" && (
          <Dashboard
            engagements={engagements}
            loading={loading}
            onCreate={() => setScreen({ kind: "new" })}
            onOpen={openEngagement}
          />
        )}
        {screen.kind === "new" && (
          <Intake
            flowConfigured={health.plannerHandoff === "configured"}
            onCancel={() => setScreen({ kind: "home" })}
            onCreated={handleCreated}
          />
        )}
        {screen.kind === "detail" && (
          activeEngagement
            ? <EngagementDetail engagement={activeEngagement} canConfirmHandoff={authSession?.roles.some(role => role === "DeploymentOperator" || role === "DeploymentAdmin") ?? false} onBack={() => setScreen({ kind: "home" })} onUpdated={(updated) => { setActiveEngagement(updated); void refreshDashboard(); }} />
            : !pageError && <div className="loading-view"><LoaderCircle className="spin" /> Loading engagement…</div>
        )}
        {screen.kind === "management" && (canViewAll
          ? <ManagementDashboard onOpen={openEngagement} />
          : <section className="content-card deployment-empty"><h2>Management access required</h2></section>)}
      </main>
    </div>
    )
  );
}

function Dashboard({
  engagements,
  loading,
  onCreate,
  onOpen,
}: {
  engagements: EngagementListItem[];
  loading: boolean;
  onCreate: () => void;
  onOpen: (id: string) => void;
}) {
  const totalOneTime = useMemo(
    () => engagements.reduce((sum, item) => sum + item.oneTimeHoursMax, 0),
    [engagements],
  );
  const configuredCount = engagements.filter((item) => item.plannerHandoffStatus === "Sent").length;

  return (
    <div className="page-content">
      <section className="page-heading">
        <div>
          <div className="eyebrow"><span className="eyebrow-line" /> SALES OPERATIONS</div>
          <h1>Engagements</h1>
          <p>Scope Secure Plus work and build a clear estimate before delivery begins.</p>
        </div>
        <div className="heading-actions">
          <a className="button button-secondary" href="/api/exports/workspace.json" download>
            <Download size={15} /> Export workspace JSON
          </a>
          <button className="button button-primary" onClick={onCreate}>
            <Plus size={17} /> New engagement
          </button>
        </div>
      </section>

      <section className="metric-grid" aria-label="Engagement overview">
        <Metric icon={<UsersRound size={17} />} label="Customer engagements" value={formatNumber(engagements.length)} detail="Saved in this workspace" />
        <Metric icon={<Clock3 size={17} />} label="One-time effort" value={`${formatNumber(totalOneTime)} hrs`} detail="Upper estimate across engagements" />
        <Metric icon={<ClipboardList size={17} />} label="Planner handoffs" value={formatNumber(configuredCount)} detail="Acknowledged by Power Automate" />
      </section>

      <section className="content-card">
        <div className="card-heading">
          <div>
            <h2>Recent engagements</h2>
            <p>Each engagement keeps its own scope and delivery handoff.</p>
          </div>
          <span className="record-count">{engagements.length} records</span>
        </div>
        {loading ? (
          <div className="table-state"><LoaderCircle className="spin" /> Loading engagements</div>
        ) : engagements.length === 0 ? (
          <div className="empty-state">
            <div className="empty-icon"><FileText size={23} /></div>
            <h3>Start with your first customer</h3>
            <p>Capture the sales essentials, then review the matching tasks and hours.</p>
            <button className="button button-primary" onClick={onCreate}><Plus size={16} /> Create engagement</button>
          </div>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Customer</th>
                  <th>Sales owner</th>
                  <th>Meeting</th>
                  <th>One-time estimate</th>
                  <th>Planner handoff</th>
                  <th aria-label="Open" />
                </tr>
              </thead>
              <tbody>
                {engagements.map((item) => (
                  <tr key={item.id} onClick={() => onOpen(item.id)} className="clickable-row">
                    <td>
                      <div className="customer-cell">
                        <span className="customer-avatar">{item.companyName.slice(0, 1).toUpperCase()}</span>
                        <div><strong>{item.companyName}</strong><span>{taskCountLabel(item.taskCount)}</span></div>
                      </div>
                    </td>
                    <td>{item.salesperson}</td>
                    <td>{formatDate(item.meetingDate)}</td>
                    <td><span className="table-hours">{formatHours(item.oneTimeHoursMin, item.oneTimeHoursMax)}</span></td>
                    <td><HandoffBadge status={item.plannerHandoffStatus} /></td>
                    <td><button className="icon-button" aria-label={`Open ${item.companyName}`}><ArrowUpRight size={17} /></button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <div className="notice-strip">
        <Shield size={16} />
        <span><strong>Estimate only.</strong> Intake does not assess or modify a customer tenant. Any future tenant change will require explicit review and approval.</span>
      </div>
    </div>
  );
}

function Metric({ icon, label, value, detail }: { icon: ReactNode; label: string; value: string; detail: string }) {
  return (
    <div className="metric-card">
      <div className="metric-top"><span className="metric-icon">{icon}</span><span>{label}</span></div>
      <strong className="metric-value">{value}</strong>
      <span className="metric-detail">{detail}</span>
    </div>
  );
}

function Intake({
  flowConfigured,
  onCancel,
  onCreated,
}: {
  flowConfigured: boolean;
  onCancel: () => void;
  onCreated: (engagement: Engagement) => Promise<void>;
}) {
  const [customer, setCustomer] = useState<CustomerFields>(emptyCustomer);
  const [answers, setAnswers] = useState<IntakeAnswers>({});
  const [estimate, setEstimate] = useState<Estimate | null>(null);
  const [estimateLoading, setEstimateLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState("");
  const [fieldErrors, setFieldErrors] = useState<Record<string, string[]>>({});
  const [departments, setDepartments] = useState("");
  const [experience, setExperience] = useState<ExperienceSelection>({ groups: [], frameworks: [] });
  const [chartPreview, setChartPreview] = useState<string[] | null>(null);
  const [chartError, setChartError] = useState("");
  const [chartBusy, setChartBusy] = useState(false);
  const [chartVisual, setChartVisual] = useState<{ url: string; kind: "image" | "pdf"; name: string } | null>(null);

  useEffect(() => () => { if (chartVisual) URL.revokeObjectURL(chartVisual.url); }, [chartVisual]);

  useEffect(() => {
    let active = true;
    setEstimateLoading(true);
    const timer = window.setTimeout(() => {
      api<Estimate>("/api/estimate", {
        method: "POST",
        body: JSON.stringify({ answers }),
      })
        .then((result) => {
          if (active) setEstimate(result);
        })
        .catch((requestError: unknown) => {
          if (active) setError(requestError instanceof Error ? requestError.message : "Estimate preview unavailable.");
        })
        .finally(() => {
          if (active) setEstimateLoading(false);
        });
    }, 140);
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [answers]);

  const setField = (key: keyof CustomerFields, value: string) => {
    setCustomer((current) => ({ ...current, [key]: value }));
  };

  const previewOrgChart = async (file: File | undefined) => {
    setChartError("");
    setChartPreview(null);
    if (!file) return;
    if (file.size === 0 || file.size > 2 * 1024 * 1024) {
      setChartError("Choose a non-empty CSV or XLSX file no larger than 2 MB.");
      return;
    }
    setChartBusy(true);
    try {
      const base64 = await new Promise<string>((resolve, reject) => {
        const reader = new FileReader();
        reader.onerror = () => reject(new Error("The file could not be read."));
        reader.onload = () => {
          if (typeof reader.result !== "string" || !reader.result.includes(",")) reject(new Error("The file could not be read."));
          else resolve(reader.result.slice(reader.result.indexOf(",") + 1));
        };
        reader.readAsDataURL(file);
      });
      const result = await api<{ departments: string[] }>("/api/org-chart/preview", {
        method: "POST",
        body: JSON.stringify({ fileName: file.name, contentBase64: base64 }),
      });
      setChartPreview(result.departments);
    } catch (error) {
      setChartError(error instanceof Error ? error.message : "The org chart could not be read.");
    } finally {
      setChartBusy(false);
    }
  };

  const previewVisualChart = (file: File | undefined) => {
    setChartError("");
    setChartVisual(null);
    if (!file) return;
    if (file.size === 0 || file.size > 10 * 1024 * 1024) {
      setChartError("Choose a non-empty PDF or image no larger than 10 MB.");
      return;
    }
    if (!["application/pdf", "image/png", "image/jpeg", "image/webp"].includes(file.type)) {
      setChartError("Choose a PDF, PNG, JPEG, or WebP file.");
      return;
    }
    setChartVisual({ url: URL.createObjectURL(file), kind: file.type === "application/pdf" ? "pdf" : "image", name: file.name });
  };

  const setAnswer = (key: string, value: string) => {
    setAnswers((current) => {
      const next = { ...current };
      if (value) next[key] = [value];
      else delete next[key];
      clearHiddenAnswers(next);
      return next;
    });
  };

  const toggleAnswer = (key: string, value: string) => {
    setAnswers((current) => {
      const currentValues = current[key] ?? [];
      const next = { ...current };
      const values = currentValues.includes(value)
        ? currentValues.filter((item) => item !== value)
        : [...currentValues, value];
      if (values.length) next[key] = values;
      else delete next[key];
      clearHiddenAnswers(next);
      return next;
    });
  };

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setError("");
    setFieldErrors({});
    setSubmitting(true);
    try {
      const created = await api<Engagement>("/api/engagements", {
        method: "POST",
        body: JSON.stringify({
          companyName: customer.companyName,
          salesperson: customer.salesperson,
          contactName: customer.contactName || null,
          contactEmail: customer.contactEmail || null,
          meetingDate: customer.meetingDate || null,
          users: customer.users ? Number(customer.users) : null,
          endpoints: customer.endpoints ? Number(customer.endpoints) : null,
          notes: customer.notes || null,
          experience: experience.groups?.some((g) => g.name.trim()) || experience.industryKey ? { ...experience, groups: (experience.groups ?? []).filter((g) => g.name.trim()) } : null,
          answers: { ...answers, orgDepartments: departments.split(/\r?\n/).map(value => value.trim()).filter(Boolean) },
        }),
      });
      await onCreated(created);
    } catch (requestError) {
      if (requestError instanceof ApiError) {
        setFieldErrors(requestError.fieldErrors);
        setError(requestError.message);
      } else {
        setError(requestError instanceof Error ? requestError.message : "Engagement could not be saved.");
      }
    } finally {
      setSubmitting(false);
    }
  };

  const handleFieldError = (key: string) => fieldErrors[key]?.[0];

  return (
    <div className="page-content intake-page">
      <div className="page-heading intake-heading">
        <div>
          <button className="back-link" onClick={onCancel}><ArrowLeft size={15} /> All engagements</button>
          <div className="eyebrow"><span className="eyebrow-line" /> SECURE PLUS · SALES INTAKE</div>
          <h1>Build an engagement estimate</h1>
          <p>A focused sales intake maps customer needs to the Secure Plus work breakdown.</p>
        </div>
        <div className="intake-length"><Clock3 size={15} /> Guided intake</div>
      </div>

      <div className="intake-layout">
        <form className="intake-form" onSubmit={submit}>
          {error && <div className="form-alert" role="alert"><CircleHelp size={17} />{error}</div>}
          <FormSection number="01" title="Customer & meeting" intro="Create or update the customer profile for this engagement.">
            <div className="form-grid">
              <TextField label="Company" required value={customer.companyName} onChange={(value) => setField("companyName", value)} error={handleFieldError("companyName")} placeholder="Company name" />
              <TextField label="Salesperson" required value={customer.salesperson} onChange={(value) => setField("salesperson", value)} error={handleFieldError("salesperson")} placeholder="Your name" />
              <TextField label="Customer contact" value={customer.contactName} onChange={(value) => setField("contactName", value)} error={handleFieldError("contactName")} placeholder="Contact name" />
              <TextField label="Contact email" type="email" value={customer.contactEmail} onChange={(value) => setField("contactEmail", value)} error={handleFieldError("contactEmail")} placeholder="name@company.com" />
              <TextField label="Meeting date" type="date" value={customer.meetingDate} onChange={(value) => setField("meetingDate", value)} />
              <div className="form-grid-2">
                <TextField label="Users" type="number" min="0" value={customer.users} onChange={(value) => setField("users", value)} error={handleFieldError("users")} placeholder="Reference only" />
                <TextField label="Endpoints" type="number" min="0" value={customer.endpoints} onChange={(value) => setField("endpoints", value)} error={handleFieldError("endpoints")} placeholder="Reference only" />
              </div>
            </div>
          </FormSection>

          <FormSection number="02" title="Organization & operating model" intro="Use the customer's own department names. Unknown or blank means not yet established; these fields do not change WBS hours.">
            <div className="form-grid">
              <TextField label="Industry / type of business" value={answer(answers, "orgIndustry")} onChange={value => setAnswer("orgIndustry", value)} error={handleFieldError("orgIndustry")} placeholder="Optional" />
              <div className="form-grid-2">
                <TextField label="Business locations" type="number" min="0" value={answer(answers, "orgLocations")} onChange={value => setAnswer("orgLocations", value)} error={handleFieldError("orgLocations")} placeholder="Unknown" />
                <TextField label="Company mobile devices" type="number" min="0" value={answer(answers, "orgMobileDevices")} onChange={value => setAnswer("orgMobileDevices", value)} error={handleFieldError("orgMobileDevices")} placeholder="Unknown" />
              </div>
              <SelectField label="IT operating model" value={answer(answers, "orgItModel")} onChange={value => setAnswer("orgItModel", value)} options={[["internal", "Internal"], ["outsourced", "Outsourced"], ["both", "Both"], ["neither", "Neither"], ["unknown", "Unknown"]]} />
              <SelectField label="Work locations" value={answer(answers, "orgWorkModel")} onChange={value => setAnswer("orgWorkModel", value)} options={[["office", "Primarily office"], ["remote", "Primarily remote"], ["hybrid", "Hybrid"], ["unknown", "Unknown"]]} />
              <SelectField label="Server infrastructure" value={answer(answers, "orgInfrastructure")} onChange={value => setAnswer("orgInfrastructure", value)} options={[["on-premises", "On-site / data center"], ["cloud", "Primarily cloud"], ["both", "Both"], ["unknown", "Unknown"]]} />
              <label className="field"><span>Departments or business segments (one per line)</span><textarea rows={4} value={departments} onChange={event => setDepartments(event.target.value)} placeholder="Finance&#10;Operations&#10;Sales" /></label>
              {handleFieldError("orgDepartments") && <p className="form-alert" role="alert">{handleFieldError("orgDepartments")}</p>}
              <p className="deployment-helper">List up to 20 departments, 80 characters each. For PDF or image charts, transcribe and review department names manually; automatic extraction from those formats is not yet supported.</p>
              <label className="field"><span>Preview departments from CSV or Excel</span><input type="file" accept=".csv,.xlsx" disabled={chartBusy} onChange={event => { void previewOrgChart(event.target.files?.[0]); event.currentTarget.value = ""; }} /></label>
              <p className="deployment-helper">Use a single visible Excel sheet or CSV with one column headed Department. The file is processed for preview and not saved. Review before replacing the list.</p>
              {chartError && <p className="form-alert" role="alert">{chartError}</p>}
              {chartPreview && <div><p>Preview: {chartPreview.join(" · ")}</p><button type="button" className="button button-secondary" onClick={() => { setDepartments(chartPreview.join("\n")); setChartPreview(null); }}>Use these departments</button></div>}
              <label className="field"><span>View a PDF or image chart for manual transcription</span><input type="file" accept=".pdf,.png,.jpg,.jpeg,.webp" onChange={event => { previewVisualChart(event.target.files?.[0]); event.currentTarget.value = ""; }} /></label>
              {chartVisual && <div>
                <p className="deployment-helper">{chartVisual.name}: viewed locally only, not saved or automatically extracted. Enter and review the departments above.</p>
                {chartVisual.kind === "image" ? <img src={chartVisual.url} alt="Organization chart for manual transcription" style={{ maxWidth: "100%", maxHeight: 420 }} /> :
                  <iframe title="Organization chart PDF for manual transcription" src={chartVisual.url} sandbox="" style={{ width: "100%", height: 420 }} />}
                <button type="button" className="button button-secondary" onClick={() => setChartVisual(null)}>Close chart</button>
              </div>}
            </div>
          </FormSection>

          <FormSection number="03" title="Identity & infrastructure" intro="Capture the current identity foundation and migration direction.">
            <div className="form-grid">
              <SelectField label="On-prem Active Directory / domain controllers?" value={answer(answers, "onPremAD")} onChange={(value) => setAnswer("onPremAD", value)} options={yesNo} />
              {isYes(answers, "onPremAD") && (
                <div className="form-grid-3">
                  <TextField label="Domain controllers" type="number" min="0" value={answer(answers, "dcCount")} onChange={(value) => setAnswer("dcCount", value)} />
                  <SelectField label="Group Policy Objects in use?" value={answer(answers, "gpos")} onChange={(value) => setAnswer("gpos", value)} options={yesNo} />
                  <SelectField label="Long-term identity goal" value={answer(answers, "identityTarget")} onChange={(value) => setAnswer("identityTarget", value)} options={[["hybrid", "Stay hybrid"], ["cloud-only", "Cloud-only"], ["undecided", "Undecided"]]} />
                </div>
              )}
              <SelectField label="MFA in use today" value={answer(answers, "mfa")} onChange={(value) => setAnswer("mfa", value)} options={[["microsoft", "Microsoft"], ["duo", "Duo"], ["okta", "Okta"], ["other", "Other third party"], ["none", "None / unknown"]]} />
              {isThirdPartyMfa(answers) && <SelectField label="Move to Microsoft Authenticator?" value={answer(answers, "moveToMsMfa")} onChange={(value) => setAnswer("moveToMsMfa", value)} options={yesNo} />}
              <CheckboxField label="Current Microsoft 365 licenses (leave blank if unknown)" values={answers.currentLicenses ?? []} onToggle={(value) => toggleAnswer("currentLicenses", value)} options={[["business-basic", "Business Basic"], ["business-standard", "Business Standard"], ["business-premium", "Business Premium"], ["e3-e5", "E3 / E5"], ["other", "Other"]]} />
              <SelectField label="Current backup approach" value={answer(answers, "backupModel")} onChange={(value) => setAnswer("backupModel", value)} options={[["managed", "Managed backup"], ["native-manual", "Native / manual"], ["none", "None"], ["unknown", "Unknown"]]} />
              <SelectField label="Enterprise password manager in use?" value={answer(answers, "passwordManager")} onChange={(value) => setAnswer("passwordManager", value)} options={[["yes", "Yes"], ["no", "No"], ["unknown", "Unknown"]]} />
              <SelectField label="Voice requirements" value={answer(answers, "voiceNeeds")} onChange={(value) => setAnswer("voiceNeeds", value)} options={[["hosted", "Hosted voice"], ["teams", "Teams Phone"], ["neither", "Neither"], ["undecided", "Undecided / unknown"]]} />
            </div>
          </FormSection>

          <FormSection number="04" title="Email & security" intro="Identify mail services, relays, and the current email security stack.">
            <div className="form-grid">
              <SelectField label="Exchange on-premises?" value={answer(answers, "exchangeOnPrem")} onChange={(value) => setAnswer("exchangeOnPrem", value)} options={yesNo} />
              {isYes(answers, "exchangeOnPrem") && <SelectField label="Migrate mailboxes to Exchange Online?" value={answer(answers, "migrateExchange")} onChange={(value) => setAnswer("migrateExchange", value)} options={yesNo} />}
              <SelectField label="Do devices or apps send email (SMTP relay)?" value={answer(answers, "devicesSendEmail")} onChange={(value) => setAnswer("devicesSendEmail", value)} options={yesNo} />
              <SelectField label="Email security today" value={answer(answers, "emailSecurity")} onChange={(value) => setAnswer("emailSecurity", value)} options={[["microsoft", "Microsoft / Defender"], ["proofpoint", "Proofpoint"], ["mimecast", "Mimecast"], ["egress", "Egress"], ["other", "Other"]]} />
              {isThirdPartyEmail(answers) && <SelectField label="Move mail security to Defender for Office 365?" value={answer(answers, "moveEmailSecurity")} onChange={(value) => setAnswer("moveEmailSecurity", value)} options={yesNo} />}
            </div>
          </FormSection>

          <FormSection number="05" title="Devices & endpoint management" intro="Scope endpoint security, device management, and mobility work.">
            <div className="form-grid">
              <SelectField label="Current endpoint protection (EDR)" value={answer(answers, "edr")} onChange={(value) => setAnswer("edr", value)} options={[["defender", "Microsoft Defender"], ["third-party", "Third-party EDR"], ["none", "None / unknown"]]} />
              <SelectField label="Deploy or standardize on Defender for Endpoint?" value={answer(answers, "deployMde")} onChange={(value) => setAnswer("deployMde", value)} options={yesNo} />
              <SelectField label="Standardize device management on Intune?" value={answer(answers, "intune")} onChange={(value) => setAnswer("intune", value)} options={yesNo} />
              <SelectField label="Adopt Windows Hello for Business?" value={answer(answers, "whfb")} onChange={(value) => setAnswer("whfb", value)} options={yesNo} />
              <SelectField label="Macs or iPads in use?" value={answer(answers, "apple")} onChange={(value) => setAnswer("apple", value)} options={yesNo} />
              {isYes(answers, "apple") && <SelectField label="Company-owned Apple devices?" value={answer(answers, "appleOwned")} onChange={(value) => setAnswer("appleOwned", value)} options={yesNo} />}
              <SelectField label="Personal devices (BYOD) allowed?" value={answer(answers, "byod")} onChange={(value) => setAnswer("byod", value)} options={yesNo} />
            </div>
          </FormSection>

          <FormSection number="06" title="Files, collaboration & governance" intro="Surface data modernization, collaboration, and readiness priorities.">
            <div className="form-grid">
              <CheckboxField label="Where do their files live today?" values={answers.storage ?? []} onToggle={(value) => toggleAnswer("storage", value)} options={[["fileserver", "On-prem file server"], ["egnyte", "Egnyte"], ["box", "Box"], ["dropbox", "Dropbox"], ["other-cloud", "Other cloud storage"]]} />
              {answers.storage?.includes("fileserver") && <TextField label="Approximate file server size (TB)" type="number" min="0" step="0.1" value={answer(answers, "fileServerTB")} onChange={(value) => setAnswer("fileServerTB", value)} />}
              <SelectField label="Existing SharePoint is poorly organized?" value={answer(answers, "poorSharePoint")} onChange={(value) => setAnswer("poorSharePoint", value)} options={yesNo} />
              {(Boolean(answers.storage?.length) || isYes(answers, "poorSharePoint")) && <SelectField label="Migrate or restructure files into SharePoint / Teams?" value={answer(answers, "modernizeData")} onChange={(value) => setAnswer("modernizeData", value)} options={yesNo} />}
              <SelectField label="Use SharePoint / Teams for collaboration?" value={answer(answers, "usesSharePoint")} onChange={(value) => setAnswer("usesSharePoint", value)} options={yesNo} />
              <SelectField label="Collaborate with external guests?" value={answer(answers, "externalCollab")} onChange={(value) => setAnswer("externalCollab", value)} options={yesNo} />
              <CheckboxField label="Purview needs" values={answers.purview ?? []} onToggle={(value) => toggleAnswer("purview", value)} options={[["labels", "Sensitivity labels"], ["retention", "Retention"], ["dlp", "Data loss prevention"]]} />
              <SelectField label="Pursuing Copilot / AI readiness?" value={answer(answers, "aiReadiness")} onChange={(value) => setAnswer("aiReadiness", value)} options={yesNo} />
              {isYes(answers, "aiReadiness") && <SelectField label="Interested in agents or Copilot extensibility?" value={answer(answers, "agents")} onChange={(value) => setAnswer("agents", value)} options={yesNo} />}
              <SelectField label="Paying for tools Microsoft 365 could replace?" value={answer(answers, "redundantTools")} onChange={(value) => setAnswer("redundantTools", value)} options={yesNo} />
              <TextAreaField label="Sales notes" value={customer.notes} onChange={(value) => setField("notes", value)} placeholder="Context, constraints, or follow-up items…" />
            </div>
          </FormSection>

          <FormSection number="07" title="Experience & security" intro="Capture industry and user groups. These sync to the engineer deployment app and drive the experience report.">
            <ExperienceStep value={experience} onChange={setExperience} />
          </FormSection>

          <div className="form-footer">
            <span><Shield size={15} /> No tenant access or changes are requested by this intake.</span>
            <div>
              <button type="button" className="button button-secondary" onClick={onCancel}>Cancel</button>
              <button type="submit" className="button button-primary" disabled={submitting}>
                {submitting ? <><LoaderCircle className="spin" size={16} /> Saving…</> : <>Save & review estimate <ArrowRight size={16} /></>}
              </button>
            </div>
          </div>
        </form>

        <aside className="estimate-rail">
          <div className="estimate-card">
            <div className="estimate-card-top">
              <div className="estimate-symbol"><Sparkles size={17} /></div>
              <div><span>LIVE SCOPE</span><strong>Estimate preview</strong></div>
              {estimateLoading && <LoaderCircle size={16} className="spin estimate-loader" />}
            </div>
            {estimate ? (
              <>
                <div className="estimate-total-label">ONE-TIME SERVICES</div>
                <div className="estimate-total">{formatNumber(estimate.oneTimeHoursMin)}<span>–</span>{formatNumber(estimate.oneTimeHoursMax)} <small>hrs</small></div>
                <div className="estimate-secondary">
                  <span>Monthly operations</span>
                  <strong>{formatHours(estimate.monthlyHoursMin, estimate.monthlyHoursMax)}</strong>
                </div>
                <div className="estimate-task-count"><ClipboardList size={15} /> {taskCountLabel(estimate.taskCount)} currently in scope</div>
                <div className="scope-breakdown">
                  {estimate.byPhase.map((phase) => (
                    <div className="scope-row" key={phase.phase}>
                      <span>{shortPhase(phase.phase)}</span>
                      <span>{phase.taskCount} <small>·</small> {formatHours(phase.minHours, phase.maxHours)}</span>
                    </div>
                  ))}
                </div>
              </>
            ) : (
              <div className="estimate-loading"><LoaderCircle className="spin" /> Calculating from the WBS…</div>
            )}
            <div className="estimate-disclaimer">
              <CircleHelp size={15} />
              <span>User and device counts are reference only and do not scale WBS hours.</span>
            </div>
          </div>

          <div className="handoff-card">
            <div className="handoff-icon"><Cloud size={17} /></div>
            <div>
              <strong>Planner handoff</strong>
              <p>{flowConfigured ? "Power Automate is configured. Saving will send this engagement and its scoped tasks to the flow." : "Not connected yet. Configure the PlannerFlow authentication mode and endpoint on the API host."}</p>
              <span className={`mini-status ${flowConfigured ? "ready" : "pending"}`}><span className="status-dot" />{flowConfigured ? "Flow configured" : "Configuration needed"}</span>
            </div>
          </div>
          <div className="safety-note"><Shield size={15} /><span>Tenant assessment and configuration are later phases. No tenant changes run automatically.</span></div>
        </aside>
      </div>
    </div>
  );
}

function EngagementDetail({
  engagement,
  canConfirmHandoff,
  onBack,
  onUpdated,
}: {
  engagement: Engagement;
  canConfirmHandoff: boolean;
  onBack: () => void;
  onUpdated: (engagement: Engagement) => void;
}) {
  const [handoffBusy, setHandoffBusy] = useState(false);
  const [handoffError, setHandoffError] = useState("");
  const [confirmPlanId, setConfirmPlanId] = useState("");
  const [confirmReference, setConfirmReference] = useState("");
  const [confirmedComplete, setConfirmedComplete] = useState(false);
  const [packageComparison, setPackageComparison] = useState<{
    source: string;
    disclaimer: string;
    signals: { questionnaire: string; value: string; review: string; componentTerms: string[] }[];
    packages: { sheet: string; name: string; components: string[] }[];
  } | null>(null);
  const [packageError, setPackageError] = useState("");
  const [journeyHistory, setJourneyHistory] = useState<{
    id: string; createdUtc: string; salesperson: string;
    oneTimeHoursMin: number; oneTimeHoursMax: number;
    changes: { field: string; before: string[]; after: string[] }[];
  }[] | null>(null);
  const [journeyError, setJourneyError] = useState("");
  const taskGroups = groupTasks(engagement.tasks);

  useEffect(() => {
    let active = true;
    setPackageComparison(null);
    setPackageError("");
    api<NonNullable<typeof packageComparison>>(`/api/engagements/${engagement.id}/package-comparison`)
      .then(result => { if (active) setPackageComparison(result); })
      .catch((error: unknown) => { if (active) setPackageError(error instanceof Error ? error.message : "Package comparison unavailable."); });
    return () => { active = false; };
  }, [engagement.id]);

  useEffect(() => {
    let active = true;
    setJourneyHistory(null);
    setJourneyError("");
    api<NonNullable<typeof journeyHistory>>(`/api/engagements/${engagement.id}/journey-history`)
      .then(result => { if (active) setJourneyHistory(result); })
      .catch((error: unknown) => { if (active) setJourneyError(error instanceof Error ? error.message : "Journey history unavailable."); });
    return () => { active = false; };
  }, [engagement.id]);

  const retryHandoff = async () => {
    setHandoffBusy(true);
    setHandoffError("");
    try {
      const updated = await api<Engagement>(`/api/engagements/${engagement.id}/planner-handoff`, { method: "POST" });
      onUpdated(updated);
    } catch (error) {
      setHandoffError(error instanceof Error ? error.message : "Planner handoff could not be retried.");
    } finally {
      setHandoffBusy(false);
    }
  };

  const confirmHandoff = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setHandoffBusy(true);
    setHandoffError("");
    try {
      const updated = await api<Engagement>(`/api/engagements/${engagement.id}/planner-handoff/confirm`, {
        method: "POST",
        body: JSON.stringify({
          planId: confirmPlanId,
          evidenceReference: confirmReference,
          confirmedTerminalAndComplete: confirmedComplete,
        }),
      });
      onUpdated(updated);
    } catch (error) {
      setHandoffError(error instanceof Error ? error.message : "The confirmation could not be saved.");
    } finally {
      setHandoffBusy(false);
    }
  };

  return (
    <div className="page-content detail-page">
      <div className="page-heading detail-heading">
        <div>
          <button className="back-link" onClick={onBack}><ArrowLeft size={15} /> All engagements</button>
          <div className="eyebrow"><span className="eyebrow-line" /> SECURE PLUS · ESTIMATE</div>
          <h1>{engagement.companyName}</h1>
          <p>Sales owner {engagement.salesperson} <span className="inline-dot">·</span> Meeting {formatDate(engagement.meetingDate)}</p>
        </div>
        <div className="detail-actions">
          <a className="button button-secondary" href={`/api/engagements/${engagement.id}/roadmap.xlsx`} download>
            <FileSpreadsheet size={15} /> Customer roadmap
          </a>
          <a className="button button-secondary" href={`/api/engagements/${engagement.id}/export.json`} download>
            <Download size={15} /> Export JSON
          </a>
          <SyncBadge status={engagement.deploymentSyncStatus} message={engagement.deploymentSyncMessage} />
          <span className="saved-badge"><Check size={15} /> Saved engagement</span>
        </div>
      </div>

      <div className="detail-summary-grid">
        <div className="detail-total-card">
          <span>ONE-TIME SERVICES</span>
          <strong>{formatNumber(engagement.oneTimeHoursMin)}–{formatNumber(engagement.oneTimeHoursMax)} <small>hours</small></strong>
          <p>Phases 0–3 · {taskCountLabel(engagement.tasks.filter((task) => !task.phase.startsWith("Ongoing")).length)}</p>
        </div>
        <div className="detail-total-card monthly-total">
          <span>MONTHLY OPERATIONS</span>
          <strong>{formatNumber(engagement.monthlyHoursMin)}–{formatNumber(engagement.monthlyHoursMax)} <small>hours / mo</small></strong>
          <p>Ongoing Secure Plus operations</p>
        </div>
        <div className="detail-mini-card">
          <span className="metric-icon"><UsersRound size={17} /></span>
          <strong>{engagement.users ?? "—"}</strong>
          <small>Users · reference only</small>
          <span className="detail-mini-divider" />
          <strong>{engagement.endpoints ?? "—"}</strong>
          <small>Endpoints · reference only</small>
        </div>
      </div>

      {engagement.experience && <ExperienceReport experience={engagement.experience} />}

      <div className="detail-content-grid">
        <section className="content-card scope-card">
          <div className="card-heading">
            <div><h2>Tasks in scope</h2><p>{taskCountLabel(engagement.tasks.length)} matched to discovery answers</p></div>
            <span className="record-count">MASTER WBS</span>
          </div>
          {taskGroups.map(([phase, tasks]) => (
            <div className="task-phase" key={phase}>
              <div className="phase-heading">
                <div><span className="phase-dot" /><strong>{phase}</strong><span>{tasks.length}</span></div>
                <span>{formatHours(tasks.reduce((sum, task) => sum + task.minHours, 0), tasks.reduce((sum, task) => sum + task.maxHours, 0))}</span>
              </div>
              {tasks.map((task) => <TaskRow key={task.wbsId} task={task} />)}
            </div>
          ))}
          {engagement.notes && (
            <div className="notes-block"><strong>Sales notes</strong><p>{engagement.notes}</p></div>
          )}
          <div className="card-heading"><div><h2>Secure Plus package comparison</h2><p>Service components, not prices</p></div></div>
          {packageError && <p className="handoff-error" role="alert">{packageError}</p>}
          {packageComparison && <>
            <p className="deployment-helper">{packageComparison.disclaimer} Source: {packageComparison.source}.</p>
            {packageComparison.signals.length === 0 && <p>No comparison signals were selected in the intake.</p>}
            {packageComparison.signals.map(signal => (
              <details key={`${signal.questionnaire}-${signal.value}`}>
                <summary>{signal.questionnaire}: {signal.value}</summary>
                <p>{signal.review}</p>
                {signal.componentTerms.length === 0 ? <p>No direct component mapping has been verified.</p> :
                  <ul>{packageComparison.packages.map((item, index) => {
                    const related = item.components.filter(component => signal.componentTerms.some(term => component.toLowerCase().includes(term.toLowerCase())));
                    return <li key={`${item.name}-${index}`}><strong>{item.name}:</strong> {related.length ? related.join(", ") : "No related component listed; review separately."}</li>;
                  })}</ul>}
              </details>
            ))}
            <details><summary>View all package components</summary>
              {packageComparison.packages.map((item, index) => <div key={`${item.name}-${index}`}><strong>{item.name}</strong> <small>({item.sheet})</small><p>{item.components.join(" · ")}</p></div>)}
            </details>
          </>}
        </section>

        <aside className="detail-side">
          <div className="content-card side-card">
            <div className="card-heading"><div><h2>Engagement</h2><p>Customer profile details</p></div></div>
            <dl className="detail-list">
              <div><dt>Contact</dt><dd>{engagement.contactName || "—"}</dd></div>
              <div><dt>Email</dt><dd>{engagement.contactEmail || "—"}</dd></div>
              <div><dt>Meeting date</dt><dd>{formatDate(engagement.meetingDate)}</dd></div>
              <div><dt>Created</dt><dd>{formatDate(engagement.createdUtc)}</dd></div>
            </dl>
          </div>
          <div className="content-card side-card">
            <div className="card-heading"><div><h2>Organization</h2><p>Intake snapshot</p></div></div>
            <dl className="detail-list">
              <div><dt>Industry</dt><dd>{engagement.answers.orgIndustry?.join(", ") || "Not recorded"}</dd></div>
              <div><dt>Departments</dt><dd>{engagement.answers.orgDepartments?.join(", ") || "Not recorded"}</dd></div>
              <div><dt>Locations</dt><dd>{engagement.answers.orgLocations?.[0] || "Not recorded"}</dd></div>
              <div><dt>Mobile devices</dt><dd>{engagement.answers.orgMobileDevices?.[0] || "Not recorded"}</dd></div>
              <div><dt>IT model</dt><dd>{engagement.answers.orgItModel?.[0] || "Not recorded"}</dd></div>
              <div><dt>Work model</dt><dd>{engagement.answers.orgWorkModel?.[0] || "Not recorded"}</dd></div>
              <div><dt>Infrastructure</dt><dd>{engagement.answers.orgInfrastructure?.[0] || "Not recorded"}</dd></div>
            </dl>
          </div>
          <div className="content-card side-card">
            <div className="card-heading"><div><h2>Planner handoff</h2><p>Separate plan per engagement</p></div></div>
            <HandoffBadge status={engagement.plannerHandoffStatus} />
            {engagement.plannerPlanId && <p className="plan-id">Plan ID <code>{engagement.plannerPlanId}</code></p>}
            {engagement.plannerHandoffMessage && <p className={engagement.plannerHandoffStatus === "ManualConfirmed" ? "side-hint" : "handoff-error"}>{engagement.plannerHandoffMessage}</p>}
            {handoffError && <p className="handoff-error">{handoffError}</p>}
            {engagement.plannerHandoffStatus === "Pending" && (
              <div className="side-hint">The handoff may still be running or may have been interrupted. Check the flow and Planner before taking further action; a pending status does not prove that no plan was created.</div>
            )}
            {engagement.plannerHandoffStatus === "Failed" && (
              <>
                <div className="side-hint">The remote outcome may be incomplete or unknown. Automatic retry is disabled. A flow completion flag alone does not verify the plan or its tasks.</div>
                {canConfirmHandoff && (
                  <form onSubmit={(event) => void confirmHandoff(event)}>
                    <p>After the flow run is terminal, manually compare the plan and every scoped task before recording a confirmation. This does not verify Planner automatically or send another request.</p>
                    <label className="field"><span>Planner plan ID</span><input required maxLength={200} value={confirmPlanId} onChange={(event) => setConfirmPlanId(event.target.value)} /></label>
                    <label className="field"><span>Flow run or reconciliation reference (no secrets)</span><input required maxLength={60} value={confirmReference} onChange={(event) => setConfirmReference(event.target.value)} /></label>
                    <label><input type="checkbox" required checked={confirmedComplete} onChange={(event) => setConfirmedComplete(event.target.checked)} /> I checked the terminal run, matching plan, and every scoped task.</label>
                    <button className="button button-secondary handoff-retry" type="submit" disabled={handoffBusy || !confirmedComplete}>Record manual confirmation</button>
                  </form>
                )}
              </>
            )}
            {engagement.plannerHandoffStatus === "NotConfigured" && (
              <>
                <div className="side-hint">Configure the PlannerFlow authentication mode and endpoint on the API host before sending this engagement to Planner.</div>
                <button className="button button-secondary handoff-retry" onClick={() => void retryHandoff()} disabled={handoffBusy}>
                  {handoffBusy ? <LoaderCircle className="spin" size={15} /> : <RefreshCw size={15} />}
                  Send to Planner
                </button>
              </>
            )}
          </div>
          <div className="approval-boundary">
            <Shield size={17} />
            <div><strong>Tenant changes are not automated</strong><p>This estimate creates no tenant access or configuration changes. Future tenant work requires explicit review and approval.</p></div>
          </div>
          <div className="content-card side-card">
            <div className="card-heading"><div><h2>Customer journey</h2><p>Saved engagement snapshots</p></div></div>
            <p className="deployment-helper">Changes compare consecutive intake snapshots, not tenant configuration or edits made outside this app.</p>
            {journeyError && <p className="handoff-error" role="alert">{journeyError}</p>}
            {journeyHistory?.map((item, index) => <details key={item.id}>
              <summary>{formatDate(item.createdUtc)} · {item.salesperson}{item.id === engagement.id ? " · current" : ""}</summary>
              <p>{formatHours(item.oneTimeHoursMin, item.oneTimeHoursMax)} one-time estimate</p>
              {index === 0 ? <p>First recorded engagement.</p> : item.changes.length === 0 ? <p>No intake-answer changes recorded.</p> :
                <ul>{item.changes.map(change => <li key={change.field}><strong>{change.field}</strong>: {change.before.join(", ") || "not recorded"} → {change.after.join(", ") || "not recorded"}</li>)}</ul>}
            </details>)}
          </div>
        </aside>
      </div>
    </div>
  );
}

function TaskRow({ task }: { task: WbsTask }) {
  return (
    <article className="task-row">
      <div className="task-id">{task.wbsId}</div>
      <div className="task-copy">
        <strong>{task.name}</strong>
        <span>{task.workstream} <span className="inline-dot">·</span> {task.deliverable}</span>
      </div>
      <div className="task-hours">{formatHours(task.minHours, task.maxHours)}</div>
    </article>
  );
}

function FormSection({ number, title, intro, children }: { number: string; title: string; intro: string; children: ReactNode }) {
  return (
    <section className="form-section">
      <div className="form-section-heading">
        <span className="section-number">{number}</span>
        <div><h2>{title}</h2><p>{intro}</p></div>
        <ChevronDown size={16} className="section-chevron" />
      </div>
      <div className="form-section-content">{children}</div>
    </section>
  );
}

function TextField({
  label,
  value,
  onChange,
  type = "text",
  placeholder,
  required = false,
  min,
  step,
  error,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  type?: string;
  placeholder?: string;
  required?: boolean;
  min?: string;
  step?: string;
  error?: string;
}) {
  const id = `field-${label.toLowerCase().replace(/[^a-z0-9]+/g, "-")}`;
  return (
    <label className="field" htmlFor={id}>
      <span>{label}{required && <i>*</i>}</span>
      <input id={id} type={type} value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} required={required} min={min} step={step} aria-invalid={Boolean(error)} />
      {error && <small className="field-error">{error}</small>}
    </label>
  );
}

function SelectField({
  label,
  value,
  onChange,
  options,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  options: [string, string][];
}) {
  const id = `field-${label.toLowerCase().replace(/[^a-z0-9]+/g, "-")}`;
  return (
    <label className="field" htmlFor={id}>
      <span>{label}</span>
      <select id={id} value={value} onChange={(event) => onChange(event.target.value)}>
        <option value="">Select an answer</option>
        {options.map(([key, title]) => <option value={key} key={key}>{title}</option>)}
      </select>
    </label>
  );
}

function CheckboxField({
  label,
  values,
  onToggle,
  options,
}: {
  label: string;
  values: string[];
  onToggle: (value: string) => void;
  options: [string, string][];
}) {
  return (
    <fieldset className="field checkbox-field">
      <legend>{label}</legend>
      <div className="checkbox-options">
        {options.map(([key, title]) => (
          <label className="checkbox-option" key={key}>
            <input type="checkbox" checked={values.includes(key)} onChange={() => onToggle(key)} />
            <span className="checkmark"><Check size={12} /></span>
            <span>{title}</span>
          </label>
        ))}
      </div>
    </fieldset>
  );
}

function TextAreaField({ label, value, onChange, placeholder }: { label: string; value: string; onChange: (value: string) => void; placeholder: string }) {
  return (
    <label className="field text-area-field">
      <span>{label}</span>
      <textarea value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} rows={3} maxLength={4000} />
    </label>
  );
}

function HandoffBadge({ status }: { status: string }) {
  const className = status === "Sent" || status === "ManualConfirmed" ? "sent" : status === "Failed" ? "failed" : status === "Pending" ? "pending" : "not-configured";
  const label = status === "Sent" ? "Handoff sent" : status === "ManualConfirmed" ? "Manually confirmed" : status === "Failed" ? "Handoff failed" : status === "Pending" ? "Sending" : "Not configured";
  return <span className={`handoff-badge ${className}`}><span className="status-dot" />{label}</span>;
}

function clearHiddenAnswers(values: IntakeAnswers) {
  if (!isYes(values, "onPremAD")) {
    delete values.dcCount;
    delete values.gpos;
    delete values.identityTarget;
  }
  if (!isThirdPartyMfa(values)) delete values.moveToMsMfa;
  if (!isYes(values, "exchangeOnPrem")) delete values.migrateExchange;
  if (!isThirdPartyEmail(values)) delete values.moveEmailSecurity;
  if (!isYes(values, "apple")) delete values.appleOwned;
  if (!values.storage?.includes("fileserver")) delete values.fileServerTB;
  if (!values.storage?.length && !isYes(values, "poorSharePoint")) delete values.modernizeData;
  if (!isYes(values, "aiReadiness")) delete values.agents;
}

function answer(answers: IntakeAnswers, key: string) {
  return answers[key]?.[0] ?? "";
}

function isYes(answers: IntakeAnswers, key: string) {
  return answer(answers, key) === "yes";
}

function isThirdPartyMfa(answers: IntakeAnswers) {
  return ["duo", "okta", "other"].includes(answer(answers, "mfa"));
}

function isThirdPartyEmail(answers: IntakeAnswers) {
  return ["proofpoint", "mimecast", "egress", "other"].includes(answer(answers, "emailSecurity"));
}

const yesNo: [string, string][] = [["yes", "Yes"], ["no", "No"]];

function shortPhase(phase: string) {
  return phase
    .replace("Phase 0 - Discovery and Mobilization", "Discovery & mobilization")
    .replace("Phase 1 - Cyber Hygiene", "Cyber hygiene")
    .replace("Phase 2 - Pay the Tech Debt", "Pay the tech debt")
    .replace("Phase 3 - Governance and AI Readiness", "Governance & AI readiness")
    .replace("Ongoing Secure Plus Operations", "Monthly operations");
}

function phaseOrder(phase: string) {
  const phaseNumber = /^Phase ([0-3])/.exec(phase);
  return phaseNumber ? Number(phaseNumber[1]) : 4;
}

function groupTasks(tasks: WbsTask[]) {
  const groups = new Map<string, WbsTask[]>();
  for (const task of tasks) {
    const existing = groups.get(task.phase) ?? [];
    existing.push(task);
    groups.set(task.phase, existing);
  }
  return [...groups.entries()].sort(([left], [right]) => phaseOrder(left) - phaseOrder(right));
}
