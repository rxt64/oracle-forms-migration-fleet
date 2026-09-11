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

export interface Bootstrap {
  steps: MacroStep[];
  evidenceKinds: EvidenceOption[];
  databaseTargets: DatabaseOption[];
  executionModes: ModeOption[];
  azureComponents: AzureComponent[];
  topology: TopologyHop[];
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

export interface PlanResponse {
  plan: RunPlan;
  steps: StepStatus[];
  executionBoundary: string;
}

export interface RunFields {
  engagementId: string;
  applicationName: string;
  sourceRoot: string;
  outputRoot: string;
  executionApprover: string;
  productionApprover: string;
}

export type FieldErrors = Partial<Record<keyof RunFields, string>>;