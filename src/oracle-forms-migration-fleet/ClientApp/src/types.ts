export type ComponentState = "Active" | "NotConfigured" | "Planned";
export type StepState = "Ready" | "Current" | "Blocked" | "Planned";

export interface MacroStep {
  step: string;
  order: number;
  title: string;
  summary: string;
  phases: string[];
  requiresExecutionAdapter: boolean;
  adapterConnected: boolean;
  azureComponentIds: string[];
}

export interface StepStatus extends Omit<MacroStep, "summary" | "azureComponentIds"> {
  state: StepState;
  blockedPhaseCount: number;
  blockers: string[];
}

export interface EvidenceOption {
  kind: string;
  name: string;
  requiredForGeneration: boolean;
  alternativeRequirement: string | null;
}

export interface DatabaseOption {
  target: string;
  name: string;
  service: string;
  guidance: string;
}

export interface ModeOption {
  mode: string;
  name: string;
  description: string;
  executableHere: boolean;
}

export interface AzureComponent {
  id: string;
  name: string;
  service: string;
  state: ComponentState;
  role: string;
  evidence: string;
}

export interface TopologyHop {
  order: number;
  name: string;
  detail: string;
  state: ComponentState;
}

export type PhaseEngine = "NotImplemented" | "Deterministic" | "DeterministicWithModelReview";

export interface PhaseAttribution {
  phase: string;
  role: string;
  engine: PhaseEngine;
  modelDeployment: string | null;
  summary: string;
}

export interface ModelAttribution {
  capability: string;
  deployment: string | null;
  summary: string;
}

export interface FleetAttribution {
  phases: PhaseAttribution[];
  models: ModelAttribution[];
  disclaimers: string[];
}

export interface Bootstrap {
  steps: MacroStep[];
  evidenceKinds: EvidenceOption[];
  databaseTargets: DatabaseOption[];
  executionModes: ModeOption[];
  azureComponents: AzureComponent[];
  topology: TopologyHop[];
  attribution: FleetAttribution;
  agentChatAvailable: boolean;
  executionBoundary: string;
  disclaimers: string[];
}

export interface Artifact {
  path: string;
  kind: string;
  description: string;
}

export interface Phase {
  phase: string;
  owner: string;
  status: string;
  mutation: string;
  requiredMode: string;
  requiresApproval: boolean;
  objective: string;
  requiredInputs: string[];
  expectedOutputs: Artifact[];
  tooling: string[];
  blockers: string[];
}

export interface RunPlan {
  engagementId: string;
  applicationName: string;
  requestedMode: string;
  authorizedMode: string;
  target: { frontEnd: string; backEnd: string; database: string };
  phases: Phase[];
  assumptions: string[];
  blockers: string[];
  disclaimers: string[];
}

export type ResourceDisposition = "Created" | "Required";

export interface AzureResourceRequirement {
  resourceType: string;
  purpose: string;
  disposition: ResourceDisposition;
  neededFrom: string;
}

export interface AzureRoleRequirement {
  role: string;
  scope: string;
  why: string;
  neededFrom: string;
}

export interface AzureFootprint {
  resources: AzureResourceRequirement[];
  roles: AzureRoleRequirement[];
  tenantModel: string[];
  disclaimers: string[];
}

export interface PlanResponse {
  plan: RunPlan;
  steps: StepStatus[];
  executionBoundary: string;
  azureFootprint?: AzureFootprint;
}

export interface ExecutedArtifact {
  path: string;
  kind: string;
  description: string;
  previewable: boolean;
}

export interface ExecutedPhase {
  phase: string;
  plannedStatus: string;
  state: "Executed" | "SkippedByPlanner" | "AdapterNotImplemented" | "Failed" | "BlockedByDependency";
  detail: string | null;
  artifacts: ExecutedArtifact[];
  findings: string[];
}

export interface ExecutedAttestation {
  kind: string;
  succeeded: boolean;
  summary: string;
  artifacts: string[];
}

export interface ExecutionResult {
  requestedMode: string;
  authorizedMode: string;
  outputRoot: string;
  phases: ExecutedPhase[];
  artifacts: ExecutedArtifact[];
  attestations: ExecutedAttestation[];
  blockers: string[];
}

export interface RunFields {
  engagementId: string;
  applicationName: string;
  sourceRoot: string;
  outputRoot: string;
  oracleFormsVersion: string;
  oracleDatabaseVersion: string;
  executionApprover: string;
  productionApprover: string;
}

export type FieldErrors = Partial<Record<keyof RunFields, string>>;