import { expect, test, type APIRequestContext } from "@playwright/test";

test.use({ screenshot: "off", trace: "off" });

/**
 * Source preparation over real HTTP against the real host.
 *
 * The development host has no source gateway configured, which is the supported default, so the
 * operation must refuse and write nothing. That is the behaviour worth pinning in the browser suite:
 * the endpoint is reachable, it authorizes before it describes the host, it validates module names
 * before it would contact anything, and it never invents a result to look finished.
 *
 * What is deliberately NOT covered here is a successful extraction. That needs a deployed source
 * gateway holding a licensed Forms installation; the admission rules that decide whether its answer may
 * be believed are exercised offline in the .NET suite instead.
 */

const OWNER_HEADER = "X-MS-CLIENT-PRINCIPAL-ID";
const MEMBER = "prepare-member";
const OUTSIDER = "prepare-outsider";

const TINY_FORMS_ZIP = "UEsDBBQAAAAIAByeMV3N+zy2BgAAAAQAAAAQAAAAZm9ybXMvT1JERVJTLmZtYmNkYmYBAFBLAQIUABQAAAAIAByeMV3N+zy2BgAAAAQAAAAQAAAAAAAAAAAAAAAAAAAAAABmb3Jtcy9PUkRFUlMuZm1iUEsFBgAAAAABAAEAPgAAADQAAAAAAA==";

function onlyDesktop(projectName: string) {
  test.skip(projectName !== "desktop-chromium", "The preparation API is viewport-independent and runs once.");
}

async function createProject(request: APIRequestContext, owner: string, name: string) {
  const response = await request.post("/api/workbench/projects", {
    headers: { [OWNER_HEADER]: owner },
    data: { name },
  });
  expect(response.status(), await response.text()).toBe(201);
  return (await response.json() as { projectId: string }).projectId;
}

async function declareSource(request: APIRequestContext, owner: string, projectId: string) {
  const response = await request.post(`/api/workbench/projects/${projectId}/source-environments`, {
    headers: { [OWNER_HEADER]: owner },
    data: {
      sourceEnvironmentId: "legacy-order-entry",
      name: "Legacy Order Entry",
      connector: "FormsBuilderWorker",
      expectedFormsVersion: "6i",
      expectedDatabaseVersion: "9i",
      pathAlias: "legacy-order-entry",
      schemaAllowlist: ["LEGACY_LAB"],
      secretReferences: [],
    },
  });
  expect(response.status(), await response.text()).toBe(201);
  return await response.json() as { sourceEnvironmentId: string; version: number };
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

test.describe("source preparation", () => {
  test("the browser exposes preparation and reports an unconfigured gateway without claiming success", async ({ page, request }) => {
    const projectId = await createProject(request, MEMBER, "Preparation browser");
    await declareSource(request, MEMBER, projectId);
    await page.setExtraHTTPHeaders({ [OWNER_HEADER]: MEMBER });
    await page.goto("/");
    await page.getByRole("combobox", { name: "Project", exact: true }).selectOption(projectId);
    await page.getByRole("button", { name: "New migration", exact: true }).last().click();
    await page.locator("#engagementId").fill("PREPARE-BROWSER");
    await page.locator("#applicationName").fill("Orders");
    await page.locator("#outputRoot").fill("out/orders");
    await page.getByText("Upload a zip", { exact: true }).click();
    await page.locator("#zipFile").setInputFiles({
      name: "orders.zip", mimeType: "application/zip", buffer: Buffer.from(TINY_FORMS_ZIP, "base64"),
    });
    await expect(page.getByRole("dialog").getByRole("status")).toContainText("Completed");
    await page.getByRole("button", { name: "Close activity", exact: true }).click();
    for (let step = 0; step < 4; step++) {
      await page.getByRole("button", { name: "Continue", exact: true }).click();
    }
    await page.getByRole("button", { name: "Generate migration plan", exact: true }).click();
    await page.getByTestId("prepare-modules").fill("ORDERS.fmb");
    await page.getByTestId("prepare-run").click();
    await expect(page.getByTestId("approval-error")).toContainText("No authorized source gateway is configured");
    await expect(page.getByTestId("prepare-result")).toHaveCount(0);
    await expect(page.getByText("No run has been queued for this project.", { exact: true })).toBeVisible();
  });

  test("the operation is reachable, authorizes first, and refuses without a configured gateway", async ({ request }, testInfo) => {
    onlyDesktop(testInfo.project.name);

    const projectId = await createProject(request, MEMBER, "Preparation pilot");
    const source = await declareSource(request, MEMBER, projectId);
    const workspaceId = await upload(request, MEMBER, projectId);
    const url = `/api/workbench/projects/${projectId}/source-environments/${source.sourceEnvironmentId}/prepare`;
    const body = {
      profileVersion: source.version,
      workspaceId,
      sourceRoot: "forms",
      modules: ["ORDERS.fmb"],
    };

    // A non-member is refused before the host says anything about itself.
    const outsider = await request.post(url, { headers: { [OWNER_HEADER]: OUTSIDER }, data: body });
    expect(outsider.status()).toBe(404);
    expect(await outsider.text()).not.toContain("source gateway");

    // A member reaches the operation and gets the host's honest state.
    const member = await request.post(url, { headers: { [OWNER_HEADER]: MEMBER }, data: body });
    expect(member.status()).toBe(503);
    expect((await member.json() as { error: string }).error)
      .toContain("No authorized source gateway is configured");
  });

  test("a module name that is not a plain Forms file name is refused before anything is contacted", async ({ request }, testInfo) => {
    onlyDesktop(testInfo.project.name);

    const projectId = await createProject(request, MEMBER, "Preparation validation");
    const source = await declareSource(request, MEMBER, projectId);
    const workspaceId = await upload(request, MEMBER, projectId);
    const url = `/api/workbench/projects/${projectId}/source-environments/${source.sourceEnvironmentId}/prepare`;

    for (const alias of ["../../etc/passwd", "forms/ORDERS.fmb", "ORDERS.exe"]) {
      const response = await request.post(url, {
        headers: { [OWNER_HEADER]: MEMBER },
        data: { profileVersion: source.version, workspaceId, sourceRoot: "forms", modules: [alias] },
      });

      // Validation is reached only after authorization, and 503 would mean the host answered about
      // itself before it judged the input. Neither a 200 nor a 503 is acceptable here.
      expect(response.status(), `alias ${alias}`).toBe(400);
    }
  });

  test("preparing against a superseded source profile version is refused", async ({ request }, testInfo) => {
    onlyDesktop(testInfo.project.name);

    const projectId = await createProject(request, MEMBER, "Preparation version");
    const source = await declareSource(request, MEMBER, projectId);
    const workspaceId = await upload(request, MEMBER, projectId);

    const response = await request.post(
      `/api/workbench/projects/${projectId}/source-environments/${source.sourceEnvironmentId}/prepare`,
      {
        headers: { [OWNER_HEADER]: MEMBER },
        data: { profileVersion: source.version + 1, workspaceId, sourceRoot: "forms", modules: ["ORDERS.fmb"] },
      },
    );

    expect(response.status()).toBe(409);
    expect((await response.json() as { error: string }).error).toContain("immutable version");
  });
});
