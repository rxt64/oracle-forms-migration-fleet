/**
 * The browser half of source preparation.
 *
 * Preparing sources is the operation that actually reaches the customer's Oracle Forms environment. The
 * browser contributes three things and nothing else: the project, the source environment and the
 * immutable profile version it was looking at, and the names of modules already present in the
 * operator's own session copy. It never sends a path, a host, a connection string, or a credential —
 * the gateway destination and the token audience are server configuration, and the content digests that
 * pin each module are computed by the server from its own copy.
 *
 * `validateModuleNames` repeats the server's alias rule so the operator gets an answer while typing. It
 * is a convenience; the server applies the same rule again and is the boundary that matters.
 */

export const MAX_MODULES_PER_REQUEST = 16;

export const SUPPORTED_MODULE_EXTENSIONS = [".fmb", ".mmb", ".pll", ".olb"];

export type PreparedModuleStatus = "Extracted" | "BlockedPrerequisite" | "Rejected" | "ExtractionFailed";

export interface PreparedModuleReport {
  moduleAlias: string;
  status: PreparedModuleStatus;
  moduleIdentity: string | null;
  contentSha256: string;
  artifactPath: string | null;
  artifactSha256: string | null;
  artifactByteCount: number;
  detail: string;
}

export interface SourcePreparationReport {
  projectId: string;
  sourceEnvironmentId: string;
  profileVersion: number;
  workspaceId: string;
  sourceRoot: string;
  gateway: string;
  preparedUtc: string;
  requestedCount: number;
  extractedCount: number;
  /** True only when bytes were written and the copy was re-indexed. Never inferred from a count. */
  snapshotRefreshed: boolean;
  fileCount: number;
  modules: PreparedModuleReport[];
}

export function validateModuleNames(raw: string): { modules: string[]; error?: string } {
  const modules = raw.split(",").map((value) => value.trim()).filter(Boolean);
  if (modules.length === 0) return { modules, error: "Name at least one Forms module to prepare." };
  if (modules.length > MAX_MODULES_PER_REQUEST) {
    return { modules, error: `Prepare at most ${MAX_MODULES_PER_REQUEST} modules at a time.` };
  }

  const seen = new Set<string>();
  for (const name of modules) {
    if (seen.has(name.toLowerCase())) return { modules, error: `'${name}' is listed more than once.` };
    seen.add(name.toLowerCase());
    if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(name) || name.includes("..")) {
      return { modules, error: `'${name}' is not a module file name. Use the name only, with no folder.` };
    }
    if (!SUPPORTED_MODULE_EXTENSIONS.some((extension) => name.toLowerCase().endsWith(extension))) {
      return { modules, error: `'${name}' is not a Forms module. Supported kinds are ${SUPPORTED_MODULE_EXTENSIONS.join(", ")}.` };
    }
  }

  return { modules };
}

export interface PrepareSourcesInput {
  projectId: string;
  sourceEnvironmentId: string;
  profileVersion: number;
  workspaceId: string;
  sourceRoot: string;
  modules: string[];
}

export async function prepareSources(input: PrepareSourcesInput): Promise<SourcePreparationReport> {
  const response = await fetch(
    `/api/workbench/projects/${encodeURIComponent(input.projectId)}` +
    `/source-environments/${encodeURIComponent(input.sourceEnvironmentId)}/prepare`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        profileVersion: input.profileVersion,
        workspaceId: input.workspaceId,
        sourceRoot: input.sourceRoot,
        modules: input.modules,
      }),
    },
  );

  const text = await response.text();
  const payload = text ? JSON.parse(text) as Record<string, unknown> : {};
  if (!response.ok) {
    throw new Error(typeof payload.error === "string" ? payload.error : `Preparing sources was refused (${response.status}).`);
  }

  return payload as unknown as SourcePreparationReport;
}

/**
 * One sentence an operator can act on, derived from typed fields only. A run with nothing extracted is
 * reported as such rather than smoothed into a partial success.
 */
export function summarizePreparation(report: SourcePreparationReport): string {
  if (report.extractedCount === 0) {
    return `Nothing was extracted from ${report.requestedCount} requested module(s). No artifact was written and your copy is unchanged.`;
  }

  return `${report.extractedCount} of ${report.requestedCount} module(s) extracted and written into your copy. ` +
    "These are extraction artifacts: they have not been normalized, and no behaviour has been compared.";
}

/**
 * The read-only Oracle schema half.
 *
 * The browser contributes nothing about the database. It names the project, the source environment and
 * the immutable profile version it was looking at, plus the session copy the statements should land in.
 * The schemas that get read are the ones stored on that profile, and the connection identity belongs to
 * the gateway — so there is no field here for a schema name, a host, a DSN, a user, or a password, and
 * adding one would move the decision about what may be read out of the server.
 */
export interface SourceSchemaPreparationReport {
  projectId: string;
  sourceEnvironmentId: string;
  profileVersion: number;
  workspaceId: string;
  sourceRoot: string;
  gateway: string;
  preparedUtc: string;
  status: PreparedModuleStatus;
  schemas: string[];
  schemaDdlPath: string | null;
  programUnitPath: string | null;
  artifactSha256: string | null;
  artifactByteCount: number;
  tables: number;
  columns: number;
  constraints: number;
  sequences: number;
  programUnits: number;
  snapshotRefreshed: boolean;
  fileCount: number;
  detail: string;
}

export interface PrepareSchemaInput {
  projectId: string;
  sourceEnvironmentId: string;
  profileVersion: number;
  workspaceId: string;
  sourceRoot: string;
}

export async function prepareOracleSchema(input: PrepareSchemaInput): Promise<SourceSchemaPreparationReport> {
  const response = await fetch(
    `/api/workbench/projects/${encodeURIComponent(input.projectId)}` +
    `/source-environments/${encodeURIComponent(input.sourceEnvironmentId)}/prepare-schema`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        profileVersion: input.profileVersion,
        workspaceId: input.workspaceId,
        sourceRoot: input.sourceRoot,
      }),
    },
  );

  const text = await response.text();
  const payload = text ? JSON.parse(text) as Record<string, unknown> : {};
  if (!response.ok) {
    throw new Error(typeof payload.error === "string" ? payload.error : `Reading the source schema was refused (${response.status}).`);
  }

  return payload as unknown as SourceSchemaPreparationReport;
}

/** Typed fields only. A refusal says what was not written rather than reporting a count of zero as progress. */
export function summarizeSchemaPreparation(report: SourceSchemaPreparationReport): string {
  if (report.status !== "Extracted" || !report.schemaDdlPath) {
    return "No schema was read. Nothing was written and your copy is unchanged.";
  }

  return `${report.schemas.length} schema(s) read: ${report.tables} table(s), ${report.constraints} constraint(s), ` +
    `${report.sequences} sequence(s) and ${report.programUnits} program unit(s). These are the source's own statements — ` +
    "nothing has been converted and nothing has been executed against any database.";
}
