import type { AzureComponent, ExecutionResult } from "./types";

export interface SourceArtifact {
  kind: string;
  count: number;
  example: string;
}

export interface SourceWorkspace {
  workspaceId: string;
  origin: string;
  originLabel: string;
  fileCount: number;
  byteCount: number;
  sourceRoot: string;
  artifacts: SourceArtifact[];
  acquiredUtc: string;
  expiresUtc: string;
}

/**
 * What the server says an operation is doing, in its own words.
 *
 * `Completed` and `Failed` are terminal: exactly one of them ends a healthy stream. A stream that
 * stops without either is neither, and the browser reports that difference rather than smoothing it
 * into success.
 */
export type ProgressState = "Running" | "Waiting" | "Completed" | "Failed";

export type ConsoleLevel = "info" | "warn" | "error" | "found" | "skip" | "done" | "keepalive";

/**
 * One frame of a server progress stream.
 *
 * `level` and `text` are the original contract and still carry the raw line. Everything below them
 * is typed framing the server supplies: the browser never reads a count, a state, or an outcome out
 * of `text`. A frame without the typed fields is raw tool output and is shown only in the log.
 */
export interface ConsoleLine {
  level: ConsoleLevel;
  text: string;
  /** Assigned by the endpoint, so a gap is distinguishable from a reorder. */
  sequence?: number;
  timestampUtc?: string;
  operation?: string;
  action?: string;
  state?: ProgressState;
  purpose?: string;
  observed?: string;
  nextAction?: string;
  artifactKind?: string | null;
  /** A total the server measured. Never derived from text, and absent rather than zero. */
  artifactCount?: number | null;
}

export const OPERATION_LABELS: Readonly<Record<string, string>> = {
  "source.acquire": "Copying your source",
  "migration.run": "Running the authorized phases",
};

/** True only for a frame the server marked as an outcome. Used to tell "finished" from "stopped". */
export function isTerminal(line: ConsoleLine): boolean {
  return line.state === "Completed" || line.state === "Failed";
}

export interface RepositoryTarget {
  label: string;
  host: string;
}

const ALLOWED_HOSTS = ["github.com", "www.github.com", "dev.azure.com", "gitlab.com", "bitbucket.org"];

/**
 * Mirrors the server-side check so the operator gets an answer while typing. The server repeats
 * every one of these rules; this copy is a convenience, never the security boundary.
 */
export function parseRepositoryUrl(raw: string): { target?: RepositoryTarget; error?: string } {
  const value = raw.trim();
  if (!value) return {};
  if (value.startsWith("git@") || value.startsWith("ssh://")) {
    return { error: "Paste the https address of the repository instead of the SSH one." };
  }

  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return { error: "That does not look like a web address yet." };
  }

  if (url.protocol !== "https:") return { error: "Only https addresses are supported." };
  if (url.username || url.password) {
    return { error: "Remove the sign-in details from the address. Never paste a token or password here." };
  }

  const allowed = ALLOWED_HOSTS.includes(url.hostname.toLowerCase()) || url.hostname.toLowerCase().endsWith(".visualstudio.com");
  if (!allowed) return { error: "Use GitHub, Azure DevOps, GitLab, or Bitbucket." };

  const path = url.pathname.replace(/\/+$/, "").replace(/^\//, "");
  if (!path) return { error: "Add the organisation and repository to the address." };

  return { target: { label: `${url.hostname}/${path}`, host: url.hostname } };
}

async function* readEvents<TDone>(response: Response): AsyncGenerator<ConsoleLine | TDone> {
  if (!response.body) throw new Error("The server sent no progress stream.");
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    let split = buffer.indexOf("\n\n");
    while (split !== -1) {
      const frame = buffer.slice(0, split).trim();
      buffer = buffer.slice(split + 2);
      split = buffer.indexOf("\n\n");
      if (frame.startsWith("data:")) {
        yield JSON.parse(frame.slice(5).trim());
      }
    }
  }
}

/**
 * Streams a server-side acquisition. Each yielded line is what the server actually did, in order,
 * so the console never shows a step that has not happened.
 */
export const SessionExpired = "Your sign-in session expired. Reload the page to sign in again, then retry.";

export async function* acquireSource(
  request: { mode: "repo"; repositoryUrl: string; branch?: string } | { mode: "zip"; file: File },
  projectId: string,
  signal: AbortSignal,
): AsyncGenerator<ConsoleLine | { level: "done"; workspace: SourceWorkspace }> {
  const send = () => request.mode === "repo"
    ? fetch("/api/workbench/source/clone", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ repositoryUrl: request.repositoryUrl, branch: request.branch || null, projectId }),
      signal,
    })
    : (() => {
      const form = new FormData();
      form.append("archive", request.file, request.file.name);
      return fetch(`/api/workbench/source/upload?projectId=${encodeURIComponent(projectId)}`, { method: "POST", body: form, signal });
    })();

  let response: Response;
  try {
    response = await send();
  } catch (error) {
    if (signal.aborted) throw error;
    // A same-origin call only fails outright when the sign-in session lapsed and the
    // redirect to the login service is blocked as cross-origin.
    throw new Error(SessionExpired);
  }

  if (response.status === 401 || response.status === 403) throw new Error(SessionExpired);
  if (!response.ok) throw new Error(`The server refused the request (${response.status}).`);
  yield* readEvents<{ level: "done"; workspace: SourceWorkspace }>(response);
}

/**
 * Streams a run of the phases the server's own planner authorized. Progress lines are the server's,
 * and the closing frame carries what actually ran and what it wrote.
 */
export async function* executeRun(
  body: Record<string, unknown>,
  signal: AbortSignal,
): AsyncGenerator<ConsoleLine | (ConsoleLine & { level: "done" | "error"; result: ExecutionResult })> {
  let response: Response;
  try {
    response = await fetch("/api/workbench/execute", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal,
    });
  } catch (error) {
    if (signal.aborted) throw error;
    throw new Error(SessionExpired);
  }

  if (response.status === 401 || response.status === 403) throw new Error(SessionExpired);
  if (!response.ok) {
    const payload = await response.json().catch(() => null) as { error?: string } | null;
    throw new Error(payload?.error ?? `The server refused the request (${response.status}).`);
  }
  yield* readEvents<ConsoleLine & { level: "done" | "error"; result: ExecutionResult }>(response);
}

export async function fetchArtifact(workspaceId: string, path: string, projectId: string, signal?: AbortSignal) {
  const query = `workspaceId=${encodeURIComponent(workspaceId)}&path=${encodeURIComponent(path)}&projectId=${encodeURIComponent(projectId)}`;
  const response = await fetch(`/api/workbench/artifact?${query}`, { signal });
  if (!response.ok) {
    const payload = await response.json().catch(() => null) as { error?: string } | null;
    throw new Error(payload?.error ?? `The artifact could not be loaded (${response.status}).`);
  }
  return response.text();
}

export function releaseSource(workspaceId: string, projectId: string) {
  return fetch(`/api/workbench/source/${encodeURIComponent(workspaceId)}?projectId=${encodeURIComponent(projectId)}`, { method: "DELETE" });
}

export function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/**
 * Proposed target names using the abbreviations Microsoft publishes in the Cloud Adoption
 * Framework: learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations
 */
export function proposedResourceNames(applicationName: string, database: string) {
  const slug = (applicationName || "app").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 20) || "app";

  // Values come from the server's DatabaseTarget enum: AzureSqlDatabase, AzureSqlManagedInstance, PostgreSql.
  const data = database === "AzureSqlManagedInstance"
    ? { host: `sqlmi-${slug}-prod`, hostLabel: "Managed instance", database: null, service: "Azure SQL Managed Instance" }
    : database === "PostgreSql"
      ? { host: `pgsql-${slug}-prod`, hostLabel: "Flexible server", database: null, service: "Azure Database for PostgreSQL" }
      : { host: `sql-${slug}-prod`, hostLabel: "Logical server", database: `sqldb-${slug}-prod`, service: "Azure SQL Database" };

  return {
    slug,
    resourceGroup: `rg-${slug}-prod`,
    frontEnd: `stapp-${slug}-web-prod`,
    api: `ca-${slug}-api-prod`,
    apiEnvironment: `cae-${slug}-prod`,
    databaseHost: data.host,
    databaseHostLabel: data.hostLabel,
    database: data.database ?? data.host,
    databaseService: data.service,
    storage: `st${slug.replace(/-/g, "")}artifacts`.slice(0, 24),
    vault: `kv-${slug}-prod`,
    identity: `id-${slug}-prod`,
    insights: `appi-${slug}-prod`,
  };
}

export function componentById(components: AzureComponent[], id: string) {
  return components.find((component) => component.id === id);
}
