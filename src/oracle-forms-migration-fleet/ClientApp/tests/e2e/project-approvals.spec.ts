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
    const declare = await request.post(`/api/workbench/projects/${project.projectId}/source-environments`, {
      headers: { [OWNER_HEADER]: OUTSIDER },
      data: { sourceEnvironmentId: "forbidden", name: "Forbidden", connector: "FormsBuilderWorker", pathAlias: "forbidden" },
    });
    expect(declare.status()).toBe(404);
    const probe = await request.post(`/api/workbench/projects/${project.projectId}/source-environments/forbidden/probe`, {
      headers: { [OWNER_HEADER]: OUTSIDER },
    });
    expect(probe.status()).toBe(404);

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
      expect((await anonymous.post("/api/workbench/projects/prj-1/source-environments", { data: {} })).status()).toBe(401);
      expect((await anonymous.post("/api/workbench/projects/prj-1/source-environments/source-1/probe")).status()).toBe(401);
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
      data: { workspaceId, scope: "ValidationOnly", request: runRequest("ENG-CONSOLE") },
    });
    expect(requested.status(), await requested.text()).toBe(201);
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

  test("a Forms 6i source profile reports native prerequisites instead of simulated readiness", async ({ page, request }, testInfo) => {
    onlyDesktop(testInfo.project.name);
    const project = await createProject(request, REQUESTER, "Forms 6i compatibility");
    const declared = await request.post(`/api/workbench/projects/${project.projectId}/source-environments`, {
      headers: { [OWNER_HEADER]: REQUESTER },
      data: {
        sourceEnvironmentId: "legacy-order-entry",
        name: "Legacy Order Entry",
        connector: "FormsBuilderWorker",
        expectedFormsVersion: "6i",
        expectedDatabaseVersion: "9i",
        pathAlias: "legacy-order-entry",
        schemaAllowlist: ["LEGACY_LAB"],
        secretReferences: [],
        observedFormsVersion: "12.2.1.4",
        readiness: "Verified",
        version: 99,
      },
    });
    expect(declared.status(), await declared.text()).toBe(201);
    const source = await declared.json() as { version: number; readiness: string; observedFormsVersion: string | null };
    expect(source.version).toBe(1);
    expect(source.readiness).toBe("Declared");
    expect(source.observedFormsVersion).toBeNull();

    await page.setExtraHTTPHeaders({ [OWNER_HEADER]: REQUESTER });
    await page.goto("/");
    const panel = page.getByTestId("project-panel");
    await panel.getByTestId("project-select").selectOption({ label: "Forms 6i compatibility" });
    await expect(panel.getByTestId("source-readiness")).toHaveText("Declared");
    await panel.getByTestId("probe-source").click();
    const result = panel.getByTestId("source-probe-result");
    await expect(result).toContainText("BlockedPrerequisite");
    await expect(result).toContainText("OracleFormsInstallation");
    await expect(result).toContainText("6.0.8.22.1");
    await expect(result).toContainText("x86");
    await expect(result).toContainText("WindowsWorker");
    await expect(panel.getByText("Declare a new immutable source version")).toBeVisible();

    await createProject(request, REQUESTER, "Second source project");
    await panel.getByTestId("refresh-context").click();
    await panel.getByTestId("project-select").selectOption({ label: "Second source project" });
    await expect(panel.getByTestId("source-probe-result")).toHaveCount(0);
  });

  test("the chosen project survives every remount of the panel, and creating one selects it", async ({ page, request }, testInfo) => {
    onlyDesktop(testInfo.project.name);

    const owner = "selection-operator";
    await createProject(request, owner, "Selection legacy Java");
    await createProject(request, owner, "Selection dotnet pilot");

    const listed = await request.get("/api/workbench/context", { headers: { [OWNER_HEADER]: owner } });
    const projects = ((await listed.json()) as { projects: { projectId: string; name: string }[] }).projects;
    expect(projects).toHaveLength(2);

    // The panel falls back to the first project the server lists, so the regression is only real for
    // the other one.
    const fallback = projects[0];
    const chosen = projects[1];

    for (const [project, name] of [[fallback, "Fallback source"], [chosen, "Chosen source"]] as const) {
      const declared = await request.post(`/api/workbench/projects/${project.projectId}/source-environments`, {
        headers: { [OWNER_HEADER]: owner },
        data: {
          sourceEnvironmentId: "legacy-order-entry",
          name,
          connector: "FormsBuilderWorker",
          expectedFormsVersion: "6i",
          expectedDatabaseVersion: "9i",
          pathAlias: "legacy-order-entry",
          schemaAllowlist: ["LEGACY_LAB"],
          secretReferences: [],
        },
      });
      expect(declared.status(), await declared.text()).toBe(201);
    }

    await page.setExtraHTTPHeaders({ [OWNER_HEADER]: owner });
    await page.goto("/");
    const panel = page.getByTestId("project-panel");
    await expect(panel.getByTestId("project-select")).toHaveValue(fallback.projectId);
    await panel.getByTestId("project-select").selectOption(chosen.projectId);

    await page.getByRole("button", { name: "New migration", exact: true }).last().click();
    await expect(panel.getByTestId("project-select")).toHaveValue(chosen.projectId);
    await expect(panel.getByTestId("source-environment")).toContainText("Chosen source");

    await page.locator("#engagementId").fill("ENG-SELECTION");
    await page.locator("#applicationName").fill("Orders");
    await page.locator("#outputRoot").fill("out/orders");
    await page.getByText("Upload a zip", { exact: true }).click();
    await page.locator("#zipFile").setInputFiles({
      name: "orders.zip", mimeType: "application/zip", buffer: Buffer.from(TINY_FORMS_ZIP, "base64"),
    });
    await expect(page.getByRole("dialog").getByRole("status")).toContainText("Completed");
    await page.getByRole("button", { name: "Close activity", exact: true }).click();

    // The copy exists and belongs to this project, so the panel must stop asking for one.
    await expect(panel.getByTestId("prepare-needs-copy")).toHaveCount(0);
    await expect(panel.getByTestId("project-select")).toHaveValue(chosen.projectId);

    for (let step = 0; step < 4; step++) {
      await page.getByRole("button", { name: "Continue", exact: true }).click();
    }
    await page.getByRole("button", { name: "Generate migration plan", exact: true }).click();

    await expect(panel.getByTestId("project-select")).toHaveValue(chosen.projectId);
    await expect(panel.getByTestId("source-environment")).toContainText("Chosen source");

    // Preparation is still bound to the project the operator chose, not to the fallback.
    await panel.getByTestId("prepare-modules").fill("ORDERS.fmb");
    await panel.getByTestId("prepare-run").click();
    await expect(page.getByTestId("approval-error")).toContainText("No authorized source gateway is configured");

    await panel.getByTestId("create-project").click();
    // Creating a project selects it, which retires the previous project's plan and returns the
    // operator to the step that takes a copy, so the panel is read at its new mount.
    await expect(panel.getByTestId("project-select").locator("option")).toHaveCount(3);
    const reloaded = await request.get("/api/workbench/context", { headers: { [OWNER_HEADER]: owner } });
    const created = ((await reloaded.json()) as { projects: { projectId: string; name: string }[] }).projects
      .find((item) => item.name.startsWith("Migration "))!;
    expect(created).toBeDefined();
    await expect(panel.getByTestId("project-select")).toHaveValue(created.projectId);
  });

  test("switching or creating a project after a copy retires that copy, its ticks and its plan, and the chosen project can take its own", async ({ page, request }, testInfo) => {
    onlyDesktop(testInfo.project.name);

    const owner = "switch-operator";
    await createProject(request, owner, "Switch first");
    await createProject(request, owner, "Switch second");

    const listed = await request.get("/api/workbench/context", { headers: { [OWNER_HEADER]: owner } });
    const projects = ((await listed.json()) as { projects: { projectId: string; name: string }[] }).projects;
    expect(projects).toHaveLength(2);
    const [first, second] = projects;

    for (const project of projects) {
      const declared = await request.post(`/api/workbench/projects/${project.projectId}/source-environments`, {
        headers: { [OWNER_HEADER]: owner },
        data: {
          sourceEnvironmentId: "legacy-order-entry",
          name: `Source for ${project.name}`,
          connector: "FormsBuilderWorker",
          expectedFormsVersion: "6i",
          expectedDatabaseVersion: "9i",
          pathAlias: "legacy-order-entry",
          schemaAllowlist: ["LEGACY_LAB"],
          secretReferences: [],
        },
      });
      expect(declared.status(), await declared.text()).toBe(201);
    }

    let markExecuteStarted!: () => void;
    let releaseExecute!: () => void;
    const executeStarted = new Promise<void>((resolve) => { markExecuteStarted = resolve; });
    const executeReleased = new Promise<void>((resolve) => { releaseExecute = resolve; });
    await page.route("**/api/workbench/execute", async (route) => {
      markExecuteStarted();
      await executeReleased;
      await route.fulfill({
        status: 202,
        contentType: "application/json",
        body: JSON.stringify({ runId: "run-from-switch-second" }),
      });
    });

    await page.setExtraHTTPHeaders({ [OWNER_HEADER]: owner });
    await page.goto("/");
    const panel = page.getByTestId("project-panel");
    const findings = page.locator(".mf-findings");
    const formsTick = page.getByRole("checkbox", { name: /Forms module source/ });

    await expect(panel.getByTestId("project-select")).toHaveValue(first.projectId);
    await panel.getByTestId("project-select").selectOption(second.projectId);
    await page.getByRole("button", { name: "New migration", exact: true }).last().click();

    await page.locator("#engagementId").fill("ENG-SWITCH");
    await page.locator("#applicationName").fill("Orders");
    await page.locator("#outputRoot").fill("out/orders");
    await page.getByText("Upload a zip", { exact: true }).click();
    await page.locator("#zipFile").setInputFiles({
      name: "orders.zip", mimeType: "application/zip", buffer: Buffer.from(TINY_FORMS_ZIP, "base64"),
    });
    await expect(page.getByRole("dialog").getByRole("status")).toContainText("Completed");
    await page.getByRole("button", { name: "Close activity", exact: true }).click();

    // The copy exists, the panel can see it, and it ticked the source checklist for this project.
    await expect(findings).toBeVisible();
    await expect(panel.getByTestId("prepare-needs-copy")).toHaveCount(0);
    await page.getByRole("button", { name: "Continue", exact: true }).click();
    await page.getByRole("button", { name: "Continue", exact: true }).click();
    await expect(formsTick).toBeChecked();
    await page.getByRole("button", { name: "Continue", exact: true }).click();
    await page.getByRole("button", { name: "Continue", exact: true }).click();
    await page.getByRole("button", { name: "Generate migration plan", exact: true }).click();
    await expect(page.getByRole("heading", { name: /^Migration plan for/ })).toBeVisible();
    await page.getByRole("button", { name: "Run authorized phases", exact: true }).click();
    await executeStarted;
    await page.getByRole("button", { name: "Close activity", exact: true }).click();

    // Switching project makes every one of those a statement about the other project.
    await panel.getByTestId("project-select").selectOption(first.projectId);
    releaseExecute();
    await expect(panel.getByTestId("project-select")).toHaveValue(first.projectId);
    await expect(page.getByRole("heading", { name: /^Migration plan for/ })).toHaveCount(0);
    await expect(findings).toHaveCount(0);
    await expect(page.getByText("Choose a zip file", { exact: true })).toBeVisible();

    // Nothing is prepared against the project that did not take the copy.
    await expect(panel.getByTestId("source-environment")).toContainText("Source for Switch first");
    await expect(panel.getByTestId("prepare-needs-copy")).toBeVisible();
    await expect(panel.getByTestId("prepare-run")).toHaveCount(0);

    // The ticks the workbench made from the outgoing copy went with it, so this project declares no
    // source at all until it has one of its own.
    await page.getByRole("button", { name: "Continue", exact: true }).click();
    await expect(page.locator("#source-error"))
      .toHaveText("Upload a zip before continuing, or pick a different option above.");

    // The project the operator is now in can take its own copy, and that copy ticks its checklist.
    await page.locator("#zipFile").setInputFiles({
      name: "orders.zip", mimeType: "application/zip", buffer: Buffer.from(TINY_FORMS_ZIP, "base64"),
    });
    await expect(page.getByRole("dialog").getByRole("status")).toContainText("Completed");
    await page.getByRole("button", { name: "Close activity", exact: true }).click();
    await expect(findings).toBeVisible();
    await expect(panel.getByTestId("prepare-needs-copy")).toHaveCount(0);
    await page.getByRole("button", { name: "Continue", exact: true }).click();
    await page.getByRole("button", { name: "Continue", exact: true }).click();
    await expect(formsTick).toBeChecked();
    await page.getByRole("button", { name: "Continue", exact: true }).click();
    await page.getByRole("button", { name: "Continue", exact: true }).click();
    await page.getByRole("button", { name: "Generate migration plan", exact: true }).click();
    await expect(page.getByRole("heading", { name: /^Migration plan for/ })).toBeVisible();
    await expect(page.getByTestId("disposition-ledger")).toHaveCount(0);

    // Creating a project is a switch too, so the copy it did not take is retired the same way.
    await panel.getByTestId("create-project").click();
    await expect(panel.getByTestId("project-select").locator("option")).toHaveCount(3);
    await expect(findings).toHaveCount(0);
    await expect(panel.getByTestId("source-environment")).toHaveCount(0);
    await expect(panel.getByTestId("prepare-run")).toHaveCount(0);
  });

  test("a preparation that lands after the project changed cannot report under the project now on screen", async ({ page, request }, testInfo) => {
    onlyDesktop(testInfo.project.name);

    const owner = "prepare-race-operator";
    await createProject(request, owner, "Race first");
    await createProject(request, owner, "Race second");

    const listed = await request.get("/api/workbench/context", { headers: { [OWNER_HEADER]: owner } });
    const projects = ((await listed.json()) as { projects: { projectId: string; name: string }[] }).projects;
    expect(projects).toHaveLength(2);
    const [first, second] = projects;

    for (const project of projects) {
      const declared = await request.post(`/api/workbench/projects/${project.projectId}/source-environments`, {
        headers: { [OWNER_HEADER]: owner },
        data: {
          sourceEnvironmentId: "legacy-order-entry",
          name: `Source for ${project.name}`,
          connector: "FormsBuilderWorker",
          expectedFormsVersion: "6i",
          expectedDatabaseVersion: "9i",
          pathAlias: "legacy-order-entry",
          schemaAllowlist: ["LEGACY_LAB"],
          secretReferences: [],
        },
      });
      expect(declared.status(), await declared.text()).toBe(201);
    }

    // Every gated reply is a success, so a leak would be the panel claiming work that belongs to a
    // project the operator is no longer in.
    const moduleBody = JSON.stringify({
      projectId: first.projectId, sourceEnvironmentId: "legacy-order-entry", profileVersion: 1,
      workspaceId: "held", sourceRoot: "forms", gateway: "held-gateway", preparedUtc: "2026-01-01T00:00:00Z",
      requestedCount: 1, extractedCount: 1, snapshotRefreshed: true, fileCount: 1,
      modules: [{
        moduleAlias: "ORDERS.fmb", status: "Extracted", moduleIdentity: "ORDERS", contentSha256: "a".repeat(64),
        artifactPath: "out/ORDERS.json", artifactSha256: "b".repeat(64), artifactByteCount: 12, detail: "Held reply.",
      }],
    });
    const schemaBody = JSON.stringify({
      projectId: first.projectId, sourceEnvironmentId: "legacy-order-entry", profileVersion: 1,
      workspaceId: "held", sourceRoot: "forms", gateway: "held-gateway", preparedUtc: "2026-01-01T00:00:00Z",
      status: "Extracted", schemas: ["LEGACY_LAB"], schemaDdlPath: "out/schema.sql", programUnitPath: null,
      artifactSha256: "c".repeat(64), artifactByteCount: 24, tables: 3, columns: 9, constraints: 2,
      sequences: 1, programUnits: 0, snapshotRefreshed: true, fileCount: 2, detail: "Held reply.",
    });

    function gates(count: number) {
      return Array.from({ length: count }, () => {
        let begin!: () => void;
        let release!: () => void;
        const begun = new Promise<void>((resolve) => { begin = resolve; });
        const released = new Promise<void>((resolve) => { release = resolve; });
        return { begin, begun, release, released };
      });
    }

    const moduleGates = gates(3);
    const schemaGates = gates(2);
    const contextGates = gates(1);
    let moduleCalls = 0;
    let schemaCalls = 0;
    let holdNextContext = false;

    await page.route("**/source-environments/*/prepare", async (route) => {
      const gate = moduleGates[moduleCalls++];
      gate.begin();
      await gate.released;
      await route.fulfill({ status: 200, contentType: "application/json", body: moduleBody });
    });
    await page.route("**/source-environments/*/prepare-schema", async (route) => {
      const gate = schemaGates[schemaCalls++];
      gate.begin();
      await gate.released;
      await route.fulfill({ status: 200, contentType: "application/json", body: schemaBody });
    });
    await page.route("**/api/workbench/context", async (route) => {
      const response = await route.fetch();
      if (holdNextContext) {
        holdNextContext = false;
        contextGates[0].begin();
        await contextGates[0].released;
      }
      await route.fulfill({ response });
    });

    await page.setExtraHTTPHeaders({ [OWNER_HEADER]: owner });
    await page.goto("/");
    const panel = page.getByTestId("project-panel");
    const status = page.getByTestId("approval-status");
    await expect(panel.getByTestId("project-select")).toHaveValue(first.projectId);
    await page.getByRole("button", { name: "New migration", exact: true }).last().click();

    await page.locator("#engagementId").fill("ENG-RACE");
    await page.locator("#applicationName").fill("Orders");
    await page.locator("#outputRoot").fill("out/orders");
    await page.getByText("Upload a zip", { exact: true }).click();

    async function takeCopy() {
      await page.locator("#zipFile").setInputFiles({
        name: "orders.zip", mimeType: "application/zip", buffer: Buffer.from(TINY_FORMS_ZIP, "base64"),
      });
      await expect(page.getByRole("dialog").getByRole("status")).toContainText("Completed");
      await page.getByRole("button", { name: "Close activity", exact: true }).click();
      await expect(panel.getByTestId("prepare-needs-copy")).toHaveCount(0);
    }

    // Away and back to the same project: the identity on screen reads the same, and the run started
    // before the switch is still a different run.
    async function leaveAndReturn() {
      await panel.getByTestId("project-select").selectOption(second.projectId);
      await expect(panel.getByTestId("prepare-needs-copy")).toBeVisible();
      await panel.getByTestId("project-select").selectOption(first.projectId);
      await expect(panel.getByTestId("project-select")).toHaveValue(first.projectId);
      await expect(panel.getByTestId("create-project")).toBeEnabled();
    }

    await takeCopy();
    await panel.getByTestId("prepare-modules").fill("ORDERS.fmb");
    await panel.getByTestId("prepare-run").click();
    await moduleGates[0].begun;
    await leaveAndReturn();
    await takeCopy();

    // A schema read the operator started after the switch is the run that owns the panel now.
    await panel.getByTestId("prepare-schema-run").click();
    await schemaGates[0].begun;
    const heldModule = page.waitForResponse((response) => response.url().endsWith("/prepare"));
    moduleGates[0].release();
    await heldModule;
    await expect(panel.getByTestId("prepare-schema-run")).toBeDisabled();

    const currentSchema = page.waitForResponse((response) => response.url().endsWith("/prepare-schema"));
    schemaGates[0].release();
    await currentSchema;
    await expect(panel.getByTestId("prepare-schema-result")).toBeVisible();
    await expect(panel.getByTestId("prepare-result")).toHaveCount(0);
    await expect(page.getByTestId("approval-error")).toHaveCount(0);
    await expect(status).toHaveText("Source schema read.");

    // The same, with the roles reversed: a held schema read cannot report under a later copy either.
    await leaveAndReturn();
    await takeCopy();
    await panel.getByTestId("prepare-schema-run").click();
    await schemaGates[1].begun;
    await leaveAndReturn();
    await takeCopy();

    await panel.getByTestId("prepare-modules").fill("ORDERS.fmb");
    await panel.getByTestId("prepare-run").click();
    await moduleGates[1].begun;
    const heldSchema = page.waitForResponse((response) => response.url().endsWith("/prepare-schema"));
    schemaGates[1].release();
    await heldSchema;
    await expect(panel.getByTestId("prepare-run")).toBeDisabled();

    const currentModule = page.waitForResponse((response) => response.url().endsWith("/prepare"));
    moduleGates[1].release();
    await currentModule;
    await expect(panel.getByTestId("prepare-result")).toBeVisible();
    await expect(panel.getByTestId("prepare-schema-result")).toHaveCount(0);
    await expect(page.getByTestId("approval-error")).toHaveCount(0);
    await expect(status).toHaveText("Source preparation finished.");
    await expect(panel.getByTestId("create-project")).toBeEnabled();

    // A response can still become stale while its context refresh is in flight. The refresh and the
    // completion status must both remain owned by the generation that started them.
    await leaveAndReturn();
    await takeCopy();
    await panel.getByTestId("prepare-modules").fill("ORDERS.fmb");
    await panel.getByTestId("prepare-run").click();
    await moduleGates[2].begun;
    holdNextContext = true;
    const moduleBeforeReload = page.waitForResponse((response) => response.url().endsWith("/prepare"));
    moduleGates[2].release();
    await moduleBeforeReload;
    await contextGates[0].begun;

    await panel.getByTestId("project-select").selectOption(second.projectId);
    await expect(panel.getByTestId("prepare-needs-copy")).toBeVisible();
    contextGates[0].release();

    await expect(status).toHaveText("");
    await expect(panel.getByTestId("project-select")).toHaveValue(second.projectId);
    await expect(panel.getByTestId("create-project")).toBeEnabled();
  });
});
