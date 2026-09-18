import { expect, request as playwrightRequest, test, type APIRequestContext } from "@playwright/test";

/**
 * Project membership and the approval state machine, over real HTTP against the real host.
 *
 * Nothing is stubbed. The server runs in explicit Development mode with a durable file-backed platform
 * store and a declared development target, so every record here is written to disk and read back the
 * same way a deployment would read it from PostgreSQL. What is deliberately *not* covered is the
 * Container Apps identity path: forged platform envelopes are exercised offline in the .NET suite,
 * because a browser test that could authenticate as a production tenant would need this host to accept
 * one, which is the thing under test.
 */

const OWNER_HEADER = "X-MS-CLIENT-PRINCIPAL-ID";
const REQUESTER = "approval-requester";
const APPROVER = "approval-approver";
const OUTSIDER = "approval-outsider";

const TINY_FORMS_ZIP = "UEsDBBQAAAAIAByeMV3N+zy2BgAAAAQAAAAQAAAAZm9ybXMvT1JERVJTLmZtYmNkYmYBAFBLAQIUABQAAAAIAByeMV3N+zy2BgAAAAQAAAAQAAAAAAAAAAAAAAAAAAAAAABmb3Jtcy9PUkRFUlMuZm1iUEsFBgAAAAABAAEAPgAAADQAAAAAAA==";

function onlyDesktop(projectName: string) {
  test.skip(projectName !== "desktop-chromium", "The approval API is viewport-independent and runs once.");
}

function runRequest(engagementId: string) {
  return {
    engagementId,
    applicationName: "ORDERS",
    requestedMode: "SandboxMigration",
    target: { frontEnd: "React", backEnd: "JavaSpringBoot", database: "PostgreSql" },
    oracleFormsVersion: "12c",
    oracleDatabaseVersion: "19c",
    sourceRoot: "forms",
    outputRoot: "out/orders",
    evidence: [],
    planApproval: { decision: "Pending", approverId: null, notes: null },
    executionApproval: { decision: "Pending", approverId: null, notes: null },
    productionApproval: { decision: "Pending", approverId: null, notes: null },
    attestations: [],
  };
}

async function upload(request: APIRequestContext, owner: string, projectId: string) {
  const response = await request.post(`/api/workbench/source/upload?projectId=${encodeURIComponent(projectId)}`, {
    headers: { [OWNER_HEADER]: owner },
    multipart: {
      archive: { name: "orders.zip", mimeType: "application/zip", buffer: Buffer.from(TINY_FORMS_ZIP, "base64") },
    },
  });
  expect(response.ok()).toBe(true);

  const workspaceId = (await response.text())
    .split("\n\n")
    .map((value) => value.trim())
    .filter((value) => value.startsWith("data:"))
    .map((value) => JSON.parse(value.slice(5).trim()) as { workspace?: { workspaceId?: string } })
    .reverse()
    .find((frame) => frame.workspace)?.workspace?.workspaceId;

  expect(workspaceId).toMatch(/^[0-9a-f]{32}$/);
  return workspaceId!;
}

async function createProject(request: APIRequestContext, owner: string, name: string) {
  const response = await request.post("/api/workbench/projects", {
    headers: { [OWNER_HEADER]: owner },
    data: { name },
  });
  expect(response.status(), await response.text()).toBe(201);
  return await response.json() as {
    projectId: string;
    targetProfile: { targetProfileId: string; version: number; endpointHost: string; canonicalHash: string } | null;
    targetProfileUnavailable: string | null;
  };
}

interface ApprovalBody {
  approvalId: string;
  state: string;
  version: number;
  canDecide: boolean;
  canRevoke: boolean;
  isRequester: boolean;
  isEffective: boolean;
  targetProfileVersion: number;
}

test.describe("project membership and approvals", () => {
  test("a request is approved only by a different member and the whole life cycle is persisted", async ({ request, baseURL }, testInfo) => {
    onlyDesktop(testInfo.project.name);

    const project = await createProject(request, REQUESTER, "Approval life cycle");
    expect(project.targetProfile, project.targetProfileUnavailable ?? "").not.toBeNull();
    expect(project.targetProfile!.endpointHost).toBe("pg-sandbox.postgres.database.azure.com");
    expect(project.targetProfile!.version).toBe(1);

    const member = await request.post(`/api/workbench/projects/${project.projectId}/members`, {
      headers: { [OWNER_HEADER]: REQUESTER },
      data: { objectId: APPROVER, roles: ["MigrationOperator", "SandboxApprover"] },
    });
    expect(member.status(), await member.text()).toBe(200);

    const workspaceId = await upload(request, REQUESTER, project.projectId);
    const requested = await request.post(`/api/workbench/projects/${project.projectId}/approvals`, {
      headers: { [OWNER_HEADER]: REQUESTER },
      data: { workspaceId, targetProfileId: "sandbox", scope: "SandboxDatabaseWrite", lifetimeMinutes: 60, request: runRequest("ENG-APPROVAL") },
    });
    expect(requested.status(), await requested.text()).toBe(201);

    const approval = await requested.json() as ApprovalBody;
    expect(approval.state).toBe("Requested");
    expect(approval.isRequester).toBe(true);
    expect(approval.canDecide).toBe(false);

    // The requester is refused by identity, not by being asked not to.
    const selfApproval = await request.post(`/api/workbench/approvals/${approval.approvalId}/decision`, {
      headers: { [OWNER_HEADER]: REQUESTER },
      data: { decision: "Approved", expectedVersion: approval.version },
    });
    expect(selfApproval.status()).toBe(403);

    const decided = await request.post(`/api/workbench/approvals/${approval.approvalId}/decision`, {
      headers: { [OWNER_HEADER]: APPROVER },
      data: { decision: "Approved", expectedVersion: approval.version, notes: "Reviewed." },
    });
    expect(decided.status(), await decided.text()).toBe(200);

    const approved = await decided.json() as ApprovalBody;
    expect(approved.state).toBe("Approved");
    expect(approved.isEffective).toBe(true);
    expect(approved.version).toBe(approval.version + 1);

    // The version the first decider consumed cannot be used again.
    const replay = await request.post(`/api/workbench/approvals/${approval.approvalId}/decision`, {
      headers: { [OWNER_HEADER]: APPROVER },
      data: { decision: "Rejected", expectedVersion: approval.version },
    });
    expect(replay.status()).toBe(409);

    const revoked = await request.post(`/api/workbench/approvals/${approval.approvalId}/revoke`, {
      headers: { [OWNER_HEADER]: APPROVER },
      data: { expectedVersion: approved.version, notes: "Window closed." },
    });
    expect(revoked.status(), await revoked.text()).toBe(200);
    expect(((await revoked.json()) as ApprovalBody).state).toBe("Revoked");

    // The record survives as audit and is no longer effective.
    const listed = await request.get(`/api/workbench/projects/${project.projectId}/approvals`, {
      headers: { [OWNER_HEADER]: APPROVER },
    });
    const approvals = ((await listed.json()) as { approvals: ApprovalBody[] }).approvals;
    const found = approvals.find((item) => item.approvalId === approval.approvalId)!;
    expect(found.state).toBe("Revoked");
    expect(found.isEffective).toBe(false);
  });

  test("a non-member reaches nothing in a project and cannot enumerate it", async ({ request }, testInfo) => {
    onlyDesktop(testInfo.project.name);

    const project = await createProject(request, REQUESTER, "Membership boundary");

    for (const path of [
      `/api/workbench/projects/${project.projectId}/approvals`,
    ]) {
      const response = await request.get(path, { headers: { [OWNER_HEADER]: OUTSIDER } });
      expect(response.status()).toBe(404);
    }

    const context = await request.get("/api/workbench/context", { headers: { [OWNER_HEADER]: OUTSIDER } });
    const body = await context.json() as { projects: { projectId: string }[] };
    expect(body.projects.some((item) => item.projectId === project.projectId)).toBe(false);
  });

  test("production scope is refused even when a member asks for it", async ({ request }, testInfo) => {
    onlyDesktop(testInfo.project.name);

    const project = await createProject(request, REQUESTER, "Production refusal");
    const workspaceId = await upload(request, REQUESTER, project.projectId);

    const response = await request.post(`/api/workbench/projects/${project.projectId}/approvals`, {
      headers: { [OWNER_HEADER]: REQUESTER },
      data: { workspaceId, scope: "ProductionWrite", request: runRequest("ENG-PROD") },
    });

    expect(response.status()).toBe(409);
    expect((await response.json() as { error: string }).error).toContain("Production");
  });

  test("an unknown approval scope is rejected rather than converted to sandbox", async ({ request }, testInfo) => {
    onlyDesktop(testInfo.project.name);

    const project = await createProject(request, REQUESTER, "Invalid scope");
    const workspaceId = await upload(request, REQUESTER, project.projectId);
    const response = await request.post(`/api/workbench/projects/${project.projectId}/approvals`, {
      headers: { [OWNER_HEADER]: REQUESTER },
      data: { workspaceId, scope: "ProductionWrit", request: runRequest("ENG-BAD-SCOPE") },
    });

    expect(response.status()).toBe(400);
    expect((await response.json() as { error: string }).error).toContain("scope");
  });

  test("the approval APIs refuse a caller with no principal", async ({ baseURL }, testInfo) => {
    onlyDesktop(testInfo.project.name);

    const anonymous = await playwrightRequest.newContext({ baseURL, extraHTTPHeaders: { [OWNER_HEADER]: "" } });
    try {
      expect((await anonymous.get("/api/workbench/bootstrap")).status()).toBe(401);
      expect((await anonymous.get("/api/workbench/context")).status()).toBe(401);
      expect((await anonymous.post("/api/workbench/projects", { data: { name: "x" } })).status()).toBe(401);
      expect((await anonymous.post("/api/workbench/agent", { data: { message: "inspect this migration" } })).status()).toBe(401);
      expect((await anonymous.post("/api/workbench/approvals/apr-1/revoke", { data: { expectedVersion: 1 } })).status()).toBe(401);
    } finally {
      await anonymous.dispose();
    }
  });

  test("the console shows the real project context, the immutable target, and only permitted actions", async ({ page, request }, testInfo) => {
    onlyDesktop(testInfo.project.name);

    const project = await createProject(request, REQUESTER, "Console context");
    await request.post(`/api/workbench/projects/${project.projectId}/members`, {
      headers: { [OWNER_HEADER]: REQUESTER },
      data: { objectId: APPROVER, roles: ["MigrationOperator", "SandboxApprover"] },
    });
    const workspaceId = await upload(request, REQUESTER, project.projectId);
    const requested = await request.post(`/api/workbench/projects/${project.projectId}/approvals`, {
      headers: { [OWNER_HEADER]: REQUESTER },
      data: { workspaceId, request: runRequest("ENG-CONSOLE") },
    });
    const approval = await requested.json() as ApprovalBody;

    // The requester's console shows the request and refuses to offer them the decision.
    await page.setExtraHTTPHeaders({ [OWNER_HEADER]: REQUESTER });
    await page.goto("/");
    const panel = page.getByTestId("project-panel");
    await expect(panel).toBeVisible();
    await expect(panel.getByTestId("auth-mode")).toHaveText("Development");
    await expect(panel.getByTestId("auth-tenant")).toHaveText("development.localhost");
    await expect(panel.getByTestId("persistence")).toContainText("development file store");

    await panel.getByTestId("project-select").selectOption({ label: "Console context" });
    await expect(panel.getByTestId("project-roles")).toContainText("MigrationOperator");
    await expect(panel.getByTestId("target-endpoint")).toHaveText("pg-sandbox.postgres.database.azure.com");
    await expect(panel.getByTestId("target-version")).toHaveText("1");
    await expect(panel.getByTestId("target-stack")).toHaveText("React / JavaSpringBoot / PostgreSql");
    await expect(panel.getByTestId(`state-${approval.approvalId}`)).toHaveText("Requested");
    await expect(panel.getByTestId(`approve-${approval.approvalId}`)).toHaveCount(0);
    await expect(panel.getByTestId(`self-${approval.approvalId}`)).toBeVisible();
    await expect(panel.getByTestId("production-note")).toContainText("not available");

    // The approver's console offers the decision, and taking it changes the persisted state.
    await page.setExtraHTTPHeaders({ [OWNER_HEADER]: APPROVER });
    await page.reload();
    const approverPanel = page.getByTestId("project-panel");
    await approverPanel.getByTestId("project-select").selectOption({ label: "Console context" });
    await approverPanel.getByTestId(`approve-${approval.approvalId}`).click();
    await expect(approverPanel.getByTestId(`state-${approval.approvalId}`)).toHaveText("Approved");

    const listed = await request.get(`/api/workbench/projects/${project.projectId}/approvals`, {
      headers: { [OWNER_HEADER]: APPROVER },
    });
    const approvals = ((await listed.json()) as { approvals: ApprovalBody[] }).approvals;
    expect(approvals.find((item) => item.approvalId === approval.approvalId)!.state).toBe("Approved");
  });
});
