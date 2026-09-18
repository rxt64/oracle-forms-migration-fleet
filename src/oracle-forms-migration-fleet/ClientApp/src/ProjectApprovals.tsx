import { useCallback, useEffect, useState } from "react";
import { LockKeyhole, RefreshCw, ShieldCheck } from "lucide-react";
import { InfoTip } from "./InfoTip";

/**
 * The authenticated project and approval panel.
 *
 * Everything shown here is read from `/api/workbench/context`, which the server computes from the
 * signed-in principal. The browser holds no approval state of its own: after every action it re-reads
 * the server's view, so a button that looked available and was not simply produces the server's reason.
 *
 * `canDecide` and `canRevoke` are computed on the server too. The console renders the actions it is
 * told the caller may take rather than inferring them from a role string, because an inference here
 * would be a second, weaker copy of the rule that actually matters.
 */

export interface TargetProfileView {
  targetProfileId: string;
  version: number;
  environmentName: string;
  azureTenantId: string;
  subscriptionId: string;
  resourceGroup: string;
  resourceId: string;
  region: string;
  endpointHost: string;
  databaseName: string;
  schemaName: string;
  executionIdentity: string;
  canonicalHash: string;
  stack: { database: string; frontEnd: string; backEnd: string };
}

export interface ApprovalView {
  approvalId: string;
  projectId: string;
  state: "Requested" | "Approved" | "Rejected" | "Revoked";
  scope: string;
  engagementId: string;
  requestedByObjectId: string;
  requestedUtc: string;
  decidedByObjectId: string | null;
  revokedByObjectId: string | null;
  expiresUtc: string;
  requestNotes: string | null;
  targetProfileId: string;
  targetProfileVersion: number;
  version: number;
  isRequester: boolean;
  canDecide: boolean;
  canRevoke: boolean;
  isEffective: boolean;
}

export interface ProjectView {
  projectId: string;
  name: string;
  roles: string[];
  targetProfiles: TargetProfileView[];
  approvals: ApprovalView[];
}

export interface WorkbenchContext {
  authentication: { mode: string; tenantId: string; objectId: string; roles: string[] };
  persistence: { configured: boolean; description: string };
  sandbox: { configured: boolean; endpointHost: string; databaseName: string; executionIdentity: string; canWrite: boolean };
  productionApproval: { available: boolean; reason: string };
  projects: ProjectView[];
}

interface Props {
  /** Present only where a copied source exists, which is the only place an approval can be requested. */
  workspaceId?: string;
  /** The same body the planner receives. The server re-derives every binding from it. */
  runRequest?: unknown;
  onProjectChange?: (projectId: string | null) => void;
}

async function send(path: string, method: string, body?: unknown) {
  const response = await fetch(path, {
    method,
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  const payload = text ? JSON.parse(text) as Record<string, unknown> : {};
  if (!response.ok) throw new Error(typeof payload.error === "string" ? payload.error : `Request failed (${response.status}).`);
  return payload;
}

// A <dl> may group each pair in one <div> and no deeper: a second wrapper makes the <dt> an orphan as
// far as assistive technology is concerned, which axe reports as a serious barrier.
function Row({ label, value, testId }: { label: string; value: string; testId?: string }) {
  return <div className="mf-review-row"><dt>{label}</dt><dd data-testid={testId}>{value}</dd></div>;
}

export function ProjectApprovals({ workspaceId, runRequest, onProjectChange }: Props) {
  const [context, setContext] = useState<WorkbenchContext | null>(null);
  const [selected, setSelected] = useState<string>("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [status, setStatus] = useState("");

  const load = useCallback(async () => {
    try {
      const payload = await send("/api/workbench/context", "GET") as unknown as WorkbenchContext;
      setContext(payload);
      setSelected((current) => {
        const next = payload.projects.some((project) => project.projectId === current)
          ? current
          : payload.projects[0]?.projectId ?? "";
        onProjectChange?.(next || null);
        return next;
      });
      setError("");
    } catch (failure) {
      setContext(null);
      setError(failure instanceof Error ? failure.message : "The project context could not be read.");
    }
  }, [onProjectChange]);

  useEffect(() => { void load(); }, [load]);

  async function act(action: () => Promise<void>, done: string) {
    setBusy(true);
    setError("");
    setStatus("");
    try {
      await action();
      await load();
      setStatus(done);
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : "That action was refused.");
    } finally {
      setBusy(false);
    }
  }

  if (error && !context) {
    return <section className="mf-result-section" data-testid="project-panel">
      <p className="mf-kicker">Project and approvals</p>
      <h2>Sign-in context</h2>
      <p className="mf-error" role="alert">{error}</p>
    </section>;
  }

  if (!context) return null;

  const project = context.projects.find((item) => item.projectId === selected) ?? null;
  const profile = project?.targetProfiles[0] ?? null;

  return <section className="mf-result-section" aria-labelledby="mf-project-title" data-testid="project-panel">
    <p className="mf-kicker">Project and approvals</p>
    <h2 id="mf-project-title">
      Who you are signed in as, and what has actually been approved
      <InfoTip label="project and approvals">
        A sandbox write needs a persisted approval that names this project, this target, this source copy, and these run
        inputs. Nothing you type in the browser creates one; a second project member has to approve it.
      </InfoTip>
    </h2>

    <div className="mf-review" data-testid="project-identity"><dl>
      <Row label="Authentication mode" value={context.authentication.mode} testId="auth-mode" />
      <Row label="Tenant" value={context.authentication.tenantId || "none"} testId="auth-tenant" />
      <Row label="Signed-in object ID" value={context.authentication.objectId || "none"} testId="auth-object" />
      <Row label="Identity claims" value={context.authentication.roles.length ? context.authentication.roles.join(", ") : "none"} />
      <Row label="Persisted state" value={context.persistence.description} testId="persistence" />
    </dl></div>

    {context.projects.length === 0
      ? <p className="mf-help" data-testid="no-projects">
        You are not a member of any project yet. A project is what membership, the target profile, and every approval hang
        off; without one, planning still works and no mutation can be authorized.
      </p>
      : <label className="mf-field">
        <span>Project</span>
        <select
          data-testid="project-select"
          value={selected}
          onChange={(event) => { setSelected(event.target.value); onProjectChange?.(event.target.value || null); }}
        >
          {context.projects.map((item) => <option key={item.projectId} value={item.projectId}>{item.name}</option>)}
        </select>
      </label>}

    <div className="mf-intro-actions">
      <button
        type="button"
        className="mf-secondary"
        disabled={busy || !context.persistence.configured}
        data-testid="create-project"
        onClick={() => act(async () => { await send("/api/workbench/projects", "POST", { name: `Migration ${new Date().toISOString().slice(0, 16)}` }); }, "Project created.")}
      >
        Create a project
      </button>
      <button type="button" className="mf-secondary" disabled={busy} data-testid="refresh-context" onClick={() => void load()}>
        <RefreshCw aria-hidden="true" />Refresh
      </button>
    </div>

    {project && <>
      <div className="mf-review" data-testid="project-membership"><dl>
        <Row label="Project roles" value={project.roles.length ? project.roles.join(", ") : "none"} testId="project-roles" />
      </dl></div>
      <h3>Target this project is bound to</h3>
      {profile
        ? <div className="mf-review" data-testid="target-profile"><dl>
          <Row label="Environment" value={profile.environmentName} />
          <Row label="Endpoint host" value={profile.endpointHost} testId="target-endpoint" />
          <Row label="Database" value={profile.databaseName} />
          <Row label="Schema" value={profile.schemaName} />
          <Row label="Writes as" value={profile.executionIdentity} />
          <Row label="Target stack" value={`${profile.stack.frontEnd} / ${profile.stack.backEnd} / ${profile.stack.database}`} testId="target-stack" />
          <Row label="Resource group" value={profile.resourceGroup} />
          <Row label="Region" value={profile.region} />
          <Row label="Version" value={String(profile.version)} testId="target-version" />
          <Row label="Identity digest" value={`${profile.canonicalHash.slice(0, 16)}\u2026`} />
        </dl></div>
        : <p className="mf-help" data-testid="no-target-profile">
          This server has no configured sandbox database, so there is no target identity to record and no sandbox write can
          be authorized. Planning and artifact generation are unaffected.
        </p>}

      <h3>Approvals</h3>
      {project.approvals.length === 0
        ? <p className="mf-help" data-testid="no-approvals">No approval has been requested for this project.</p>
        : <ul className="mf-lifecycle" data-testid="approval-list">
          {project.approvals.map((approval) => <li key={approval.approvalId} data-testid={`approval-${approval.approvalId}`}>
            <span><ShieldCheck aria-hidden="true" /></span>
            <div>
              <strong>{approval.engagementId} · {approval.scope}</strong>
              <small>
                Requested by {approval.requestedByObjectId} · target {approval.targetProfileId} v{approval.targetProfileVersion} ·
                {approval.state === "Approved" ? ` expires ${new Date(approval.expiresUtc).toLocaleString()}` : ` requested ${new Date(approval.requestedUtc).toLocaleString()}`}
              </small>
              {approval.requestNotes && <small>{approval.requestNotes}</small>}
              <div className="mf-intro-actions">
                {approval.canDecide && <>
                  <button
                    type="button"
                    className="mf-primary"
                    disabled={busy}
                    data-testid={`approve-${approval.approvalId}`}
                    onClick={() => act(async () => { await send(`/api/workbench/approvals/${approval.approvalId}/decision`, "POST", { decision: "Approved", expectedVersion: approval.version }); }, "Approved.")}
                  >
                    Approve
                  </button>
                  <button
                    type="button"
                    className="mf-secondary"
                    disabled={busy}
                    data-testid={`reject-${approval.approvalId}`}
                    onClick={() => act(async () => { await send(`/api/workbench/approvals/${approval.approvalId}/decision`, "POST", { decision: "Rejected", expectedVersion: approval.version }); }, "Rejected.")}
                  >
                    Reject
                  </button>
                </>}
                {approval.canRevoke && <button
                  type="button"
                  className="mf-secondary"
                  disabled={busy}
                  data-testid={`revoke-${approval.approvalId}`}
                  onClick={() => act(async () => { await send(`/api/workbench/approvals/${approval.approvalId}/revoke`, "POST", { expectedVersion: approval.version }); }, "Revoked.")}
                >
                  Revoke
                </button>}
                {approval.isRequester && approval.state === "Requested" &&
                  <span className="mf-help" data-testid={`self-${approval.approvalId}`}>You requested this, so someone else has to decide it.</span>}
              </div>
            </div>
            <em className={approval.isEffective ? "mf-pill neutral" : "mf-pill danger"} data-testid={`state-${approval.approvalId}`}>{approval.state}</em>
          </li>)}
        </ul>}

      {workspaceId && runRequest !== undefined && <div className="mf-intro-actions">
        <button
          type="button"
          className="mf-secondary"
          disabled={busy || !profile}
          data-testid="request-approval"
          onClick={() => act(async () => {
            await send(`/api/workbench/projects/${project.projectId}/approvals`, "POST", {
              workspaceId,
              targetProfileId: profile?.targetProfileId ?? "sandbox",
              scope: "SandboxDatabaseWrite",
              lifetimeMinutes: 60,
              request: runRequest,
            });
          }, "Sandbox approval requested.")}
        >
          Request sandbox approval for this run
        </button>
        <p className="mf-help">
          The request records the source copy this server indexed, the run inputs after every claim of authority was stripped
          from them, and the stored target. Change any of those and the approval stops covering the run.
        </p>
      </div>}
    </>}

    <p className="mf-status" role="status" aria-live="polite" data-testid="approval-status">{status}</p>
    {error && <p className="mf-error" role="alert" data-testid="approval-error">{error}</p>}

    <div className="mf-boundary compact" data-testid="production-note">
      <LockKeyhole aria-hidden="true" />
      <p>
        <strong>Production approval is not available in this build.</strong>
        {context.productionApproval.reason} The named contacts on the setup pages remain planning notes and authorize nothing.
      </p>
    </div>
  </section>;
}
