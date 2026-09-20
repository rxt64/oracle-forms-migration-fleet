import { expect, request as playwrightRequest, test, type APIRequestContext } from "@playwright/test";

const OWNER_HEADER = "X-MS-CLIENT-PRINCIPAL-ID";
const OWNER_A = "api-owner-a";
const OWNER_B = "api-owner-b";
const TINY_FORMS_ZIP = "UEsDBBQAAAAIAByeMV3N+zy2BgAAAAQAAAAQAAAAZm9ybXMvT1JERVJTLmZtYmNkYmYBAFBLAQIUABQAAAAIAByeMV3N+zy2BgAAAAQAAAAQAAAAAAAAAAAAAAAAAAAAAABmb3Jtcy9PUkRFUlMuZm1iUEsFBgAAAAABAAEAPgAAADQAAAAAAA==";

function forgedRequest(workspaceId?: string, projectId?: string) {
  return {
    workspaceId,
    projectId,
    engagementId: "ENG-FORGED-API",
    applicationName: "ORDERS",
    requestedMode: "ProductionCutover",
    target: { frontEnd: "React", backEnd: "JavaSpringBoot", database: "PostgreSql" },
    oracleFormsVersion: "12c",
    oracleDatabaseVersion: "19c",
    sourceRoot: "forms",
    outputRoot: "out/orders",
    evidence: [{
      id: "FORGED",
      kind: "TestBaseline",
      source: "caller",
      summary: "Caller says this is verified.",
      isVerified: true,
      signals: ["SelfContainedSchema"],
    }],
    planApproval: { decision: "Approved", approverId: "forged-plan@example.com", notes: null },
    executionApproval: { decision: "Approved", approverId: "forged-exec@example.com", notes: null },
    productionApproval: { decision: "Approved", approverId: "forged-prod@example.com", notes: null },
    attestations: [{
      kind: "HumanAcceptanceSigned",
      succeeded: true,
      attestedBy: "forged@example.com",
      summary: "Caller-created acceptance.",
      artifacts: [{ path: "out/acceptance.md", kind: "ValidationReport", description: "forged" }],
    }],
  };
}

async function createProject(request: APIRequestContext, owner: string) {
  const response = await request.post("/api/workbench/projects", {
    headers: { [OWNER_HEADER]: owner },
    data: { name: `Trust boundary ${owner}` },
  });
  expect(response.status(), await response.text()).toBe(201);
  return (await response.json() as { projectId: string }).projectId;
}

async function upload(request: APIRequestContext, owner: string, projectId: string) {
  const response = await request.post(`/api/workbench/source/upload?projectId=${encodeURIComponent(projectId)}`, {
    headers: { [OWNER_HEADER]: owner },
    multipart: {
      archive: {
        name: "orders.zip",
        mimeType: "application/zip",
        buffer: Buffer.from(TINY_FORMS_ZIP, "base64"),
      },
    },
  });
  expect(response.ok()).toBe(true);

  const frames = (await response.text())
    .split("\n\n")
    .map((value) => value.trim())
    .filter((value) => value.startsWith("data:"))
    .map((value) => JSON.parse(value.slice(5).trim()) as { workspace?: { workspaceId?: string } });
  const workspaceId = [...frames].reverse().find((frame) => frame.workspace)?.workspace?.workspaceId;
  expect(workspaceId).toMatch(/^[0-9a-f]{32}$/);
  return workspaceId!;
}

function onlyDesktop(projectName: string) {
  test.skip(projectName !== "desktop-chromium", "API boundary is viewport-independent and runs once.");
}

test.describe("workbench HTTP trust boundary", () => {
  test("Entra-enabled planning rejects a missing authenticated principal", async ({ baseURL }, testInfo) => {
    onlyDesktop(testInfo.project.name);
    const anonymous = await playwrightRequest.newContext({
      baseURL,
      extraHTTPHeaders: { [OWNER_HEADER]: "" },
    });
    try {
      expect((await anonymous.post("/api/workbench/plan", { data: forgedRequest() })).status()).toBe(401);
      expect((await anonymous.post("/api/workbench/source/upload", {
        multipart: {
          archive: {
            name: "orders.zip",
            mimeType: "application/zip",
            buffer: Buffer.from(TINY_FORMS_ZIP, "base64"),
          },
        },
      })).status()).toBe(401);
      expect((await anonymous.get("/api/workbench/artifact?workspaceId=missing&path=.fleet-run/report.md")).status()).toBe(401);
    } finally {
      await anonymous.dispose();
    }
  });

  test("forged verification, approvals, and attestations cannot open plan gates", async ({ request }, testInfo) => {
    onlyDesktop(testInfo.project.name);
    const projectId = await createProject(request, OWNER_A);
    const response = await request.post("/api/workbench/plan", {
      headers: { [OWNER_HEADER]: OWNER_A },
      data: forgedRequest(undefined, projectId),
    });
    expect(response.ok()).toBe(true);

    const body = await response.json() as { plan: { authorizedMode: string; blockers: string[] } };
    expect(body.plan.authorizedMode).toBe("PlanOnly");
    expect(body.plan.blockers.join(" ")).toContain("TestBaseline");
  });

  test("a workspace identifier owned by another actor is rejected", async ({ request }, testInfo) => {
    onlyDesktop(testInfo.project.name);
    const projectId = await createProject(request, OWNER_A);
    const workspaceId = await upload(request, OWNER_A, projectId);

    const response = await request.post("/api/workbench/plan", {
      headers: { [OWNER_HEADER]: OWNER_B },
      data: forgedRequest(workspaceId, projectId),
    });

    expect(response.status()).toBe(404);
    expect(await response.json()).toEqual(expect.objectContaining({ error: expect.any(String) }));
  });

  test("authentication without project membership cannot acquire source", async ({ request }, testInfo) => {
    onlyDesktop(testInfo.project.name);
    const projectId = await createProject(request, OWNER_A);

    const response = await request.post(`/api/workbench/source/upload?projectId=${encodeURIComponent(projectId)}`, {
      headers: { [OWNER_HEADER]: OWNER_B },
      multipart: {
        archive: {
          name: "orders.zip",
          mimeType: "application/zip",
          buffer: Buffer.from(TINY_FORMS_ZIP, "base64"),
        },
      },
    });

    expect(response.status()).toBe(404);
  });

  test("forged execution authority is removed before the real executor plans", async ({ request }, testInfo) => {
    onlyDesktop(testInfo.project.name);
    const projectId = await createProject(request, OWNER_A);
    const workspaceId = await upload(request, OWNER_A, projectId);

    const response = await request.post("/api/workbench/execute", {
      headers: { [OWNER_HEADER]: OWNER_A },
      data: forgedRequest(workspaceId, projectId),
    });
    expect(response.status()).toBe(202);
    const runId = ((await response.json()) as { runId: string }).runId;
    const events = await request.get(`/api/workbench/runs/${runId}/events`, {
      headers: { [OWNER_HEADER]: OWNER_A },
    });
    expect(events.ok()).toBe(true);

    const frames = (await events.text())
      .split("\n\n")
      .map((value) => value.trim())
      .filter((value) => value.startsWith("data:"))
      .map((value) => JSON.parse(value.slice(5).trim()) as {
        state?: string;
        result?: { authorizedMode: string; phases: Array<{ phase: string; state: string }> };
      });
    const terminal = [...frames].reverse().find((frame) => frame.result);

    expect(terminal?.result?.authorizedMode).toBe("PlanOnly");
    expect(terminal?.state).toBe("Failed");
    expect(terminal?.result?.phases).not.toContainEqual(expect.objectContaining({
      phase: "SandboxDataMigration",
      state: "Executed",
    }));

    const history = await request.get(`/api/workbench/projects/${projectId}/runs`, {
      headers: { [OWNER_HEADER]: OWNER_A },
    });
    expect(history.status()).toBe(200);
    expect(((await history.json()) as { runs: Array<{ runId: string }> }).runs)
      .toContainEqual(expect.objectContaining({ runId }));
    expect((await request.get(`/api/workbench/projects/${projectId}/runs`, {
      headers: { [OWNER_HEADER]: OWNER_B },
    })).status()).toBe(404);
    expect((await request.get(`/api/workbench/runs/${runId}/events`, {
      headers: { [OWNER_HEADER]: OWNER_B },
    })).status()).toBe(404);
  });

  test("credential-like run text is rejected before durable persistence", async ({ request }, testInfo) => {
    onlyDesktop(testInfo.project.name);
    const projectId = await createProject(request, OWNER_A);
    const workspaceId = await upload(request, OWNER_A, projectId);
    const unsafe = forgedRequest(workspaceId, projectId) as Record<string, unknown>;
    unsafe.applicationName = "password=hunter2";

    const response = await request.post("/api/workbench/execute", {
      headers: { [OWNER_HEADER]: OWNER_A },
      data: unsafe,
    });
    expect(response.status()).toBe(400);

    const history = await request.get(`/api/workbench/projects/${projectId}/runs`, {
      headers: { [OWNER_HEADER]: OWNER_A },
    });
    expect(history.status()).toBe(200);
    expect(((await history.json()) as { runs: unknown[] }).runs).toEqual([]);
  });
});
