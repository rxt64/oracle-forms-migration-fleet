import { expect, test } from "@playwright/test";
import {
  CLONE_PATH,
  activityCommand,
  cloneFrames,
  completeSetup,
  expectNoHorizontalOverflow,
  field,
  installStreamStub,
  startAtSetup,
} from "./fixtures";

const TINY_FORMS_ZIP = "UEsDBBQAAAAIAByeMV3N+zy2BgAAAAQAAAAQAAAAZm9ybXMvT1JERVJTLmZtYmNkYmYBAFBLAQIUABQAAAAIAByeMV3N+zy2BgAAAAQAAAAQAAAAAAAAAAAAAAAAAAAAAABmb3Jtcy9PUkRFUlMuZm1iUEsFBgAAAAABAAEAPgAAADQAAAAAAA==";

test.describe("guided setup", () => {
  test.beforeEach(async ({ page }) => {
    await startAtSetup(page);
  });

  test("overview and every setup step render without page-level horizontal overflow", async ({ page }) => {
    await page.addInitScript(() => window.localStorage.removeItem("ofm-workbench-intro-v1"));
    await page.goto("/");

    await expect(page.getByRole("heading", { name: "Plan a move off Oracle Forms" })).toBeVisible();
    await expectNoHorizontalOverflow(page);
    await page.screenshot({ path: `test-results/screens/${test.info().project.name}-overview.png`, fullPage: true });

    await page.getByRole("button", { name: "New migration" }).first().click();

    const headings = [
      "Which application are you moving?",
      "Where should the migrated application land?",
      "What source material do you already have?",
      "Who would approve changes?",
      "Check the setup, then generate the plan",
    ];

    await expect(page.getByRole("heading", { name: headings[0] })).toBeVisible();
    await expectNoHorizontalOverflow(page);
    await page.screenshot({ path: `test-results/screens/${test.info().project.name}-step-1.png`, fullPage: true });

    await field.engagementId(page).fill("ENG-0042");
    await field.applicationName(page).fill("ORDERS");
    await field.outputRoot(page).fill("out/orders");
    await page.getByRole("radio", { name: "Describe a folder path" }).check();
    await field.sourceRoot(page).fill("legacy/forms");

    for (let index = 1; index < headings.length; index += 1) {
      await page.getByRole("button", { name: "Continue" }).click();
      await expect(page.getByRole("heading", { name: headings[index] })).toBeVisible();
      await expectNoHorizontalOverflow(page);
      await page.screenshot({ path: `test-results/screens/${test.info().project.name}-step-${index + 1}.png`, fullPage: true });
    }
  });

  test("every step states its goal, input, platform behaviour, and output", async ({ page }) => {
    await page.goto("/");
    const purpose = page.locator(".mf-step-purpose").first();
    for (const term of ["Goal", "What you provide", "What the workbench does", "What you get"]) {
      await expect(purpose.getByText(term, { exact: true })).toBeVisible();
    }
  });

  test("empty step 1 blocks continue and moves focus to the first invalid field", async ({ page }) => {
    await page.goto("/");
    await page.getByRole("button", { name: "Continue" }).click();

    await expect(page.getByRole("heading", { name: "Which application are you moving?" })).toBeVisible();
    await expect(page.locator("#engagementId")).toBeFocused();
    await expect(page.getByRole("alert")).not.toHaveCount(0);
    await page.screenshot({ path: `test-results/screens/${test.info().project.name}-validation.png`, fullPage: true });
  });

  test("capabilities with no backing configuration are disabled and say why", async ({ page }) => {
    await page.goto("/");

    const activity = activityCommand(page);
    await expect(activity).toBeDisabled();
    await expect(page.getByText("Activity opens once something has run in this tab.")).toBeVisible();

    // This host has no Foundry deployment configured, so the control is refused rather than offered.
    const askAi = page.getByRole("button", { name: "Ask AI" });
    await expect(askAi).toBeDisabled();
    await expect(page.getByText("Ask AI needs a configured Foundry deployment; this host has none.")).toBeVisible();
  });

  test("a completed source copy ticks matching checklist items and reports the real file count", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");

    await field.engagementId(page).fill("ENG-0042");
    await field.applicationName(page).fill("ORDERS");
    await field.outputRoot(page).fill("out/orders");
    await page.getByRole("radio", { name: "Clone a Git repository" }).check();
    await field.repositoryUrl(page).fill("https://github.com/contoso/orders");
    await page.getByRole("button", { name: "Copy this repository" }).click();

    await stub.waitForOpen(CLONE_PATH);
    await stub.pushAll(CLONE_PATH, cloneFrames());
    await stub.close(CLONE_PATH);

    await expect(page.getByText("42 files copied (1.0 MB).")).toBeVisible();

    // Two kinds were recognised, so exactly two answers move. Counts come from typed fields.
    await page.getByRole("button", { name: "Close activity" }).click();
    await page.getByRole("button", { name: "Continue" }).click();
    await page.getByRole("button", { name: "Continue" }).click();
    await expect(page.getByRole("heading", { name: "What source material do you already have?" })).toBeVisible();
    await expect(page.getByRole("checkbox", { name: /Forms module source/ })).toBeChecked();
    await expect(page.getByRole("checkbox", { name: /PL\/SQL program units/ })).toBeChecked();
  });

  test("a real uploaded workspace is carried into the real plan boundary", async ({ page }) => {
    await page.goto("/");
    await field.engagementId(page).fill("ENG-REAL-UPLOAD");
    await field.applicationName(page).fill("ORDERS");
    await field.outputRoot(page).fill("out/orders");
    await page.getByRole("radio", { name: "Upload a zip" }).check();
    await page.locator("#zipFile").setInputFiles({
      name: "orders.zip",
      mimeType: "application/zip",
      buffer: Buffer.from(TINY_FORMS_ZIP, "base64"),
    });

    const activity = page.getByRole("dialog", { name: /Expanding your upload/ });
    await expect(activity.locator(".mf-activity-summary .mf-pill")).toHaveText("Completed");
    await activity.getByRole("button", { name: "Close activity" }).click();

    for (let step = 0; step < 4; step += 1) {
      await page.getByRole("button", { name: "Continue" }).click();
    }

    const planRequest = page.waitForRequest((request) => request.url().endsWith("/api/workbench/plan"));
    await page.getByRole("button", { name: "Generate migration plan" }).click();
    const payload = (await planRequest).postDataJSON() as { workspaceId?: string; sourceRoot?: string };

    expect(payload.workspaceId).toMatch(/^[0-9a-f]{32}$/);
    expect(payload.sourceRoot).toBe("forms");
    await expect(page.getByRole("heading", { name: /^Migration plan for ORDERS/ })).toBeVisible();
    await expect(page.getByText(/Verified FormsModuleSource evidence was not supplied/)).toHaveCount(0);
  });

  test("switching source mode after a completed copy releases it and clears detected ticks", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");

    await field.engagementId(page).fill("ENG-0042");
    await field.applicationName(page).fill("ORDERS");
    await field.outputRoot(page).fill("out/orders");
    await page.getByRole("radio", { name: "Clone a Git repository" }).check();
    await field.repositoryUrl(page).fill("https://github.com/contoso/orders");
    await page.getByRole("button", { name: "Copy this repository" }).click();
    await stub.waitForOpen(CLONE_PATH);
    await stub.pushAll(CLONE_PATH, cloneFrames());
    await stub.close(CLONE_PATH);
    await expect(page.getByText("42 files copied (1.0 MB).")).toBeVisible();
    await page.getByRole("button", { name: "Close activity" }).click();

    await page.getByRole("radio", { name: "Describe a folder path" }).check();
    await field.sourceRoot(page).fill("legacy/forms");

    await expect(page.getByText("42 files copied (1.0 MB).")).toHaveCount(0);
    await expect(activityCommand(page)).toBeDisabled();

    await page.getByRole("button", { name: "Continue" }).click();
    await page.getByRole("button", { name: "Continue" }).click();
    await expect(page.getByRole("heading", { name: "What source material do you already have?" })).toBeVisible();
    await expect(page.getByRole("checkbox", { checked: true })).toHaveCount(0);
  });

  test("switching source mode while a copy is still open abandons the late response", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");

    await field.engagementId(page).fill("ENG-0042");
    await field.applicationName(page).fill("ORDERS");
    await field.outputRoot(page).fill("out/orders");
    await page.getByRole("radio", { name: "Clone a Git repository" }).check();
    await field.repositoryUrl(page).fill("https://github.com/contoso/orders");
    await page.getByRole("button", { name: "Copy this repository" }).click();

    await stub.waitForOpen(CLONE_PATH);
    const frames = cloneFrames();
    await stub.push(CLONE_PATH, frames[0]);
    await expect(page.getByRole("dialog", { name: /Copying your repository/ })).toBeVisible();

    // The race: the operator changes their mind before the copy finishes.
    await page.getByRole("button", { name: "Close activity" }).click();
    await page.getByRole("radio", { name: "Describe a folder path" }).check();
    await field.sourceRoot(page).fill("legacy/forms");

    // Releasing the rest of the stale response afterwards must change nothing.
    for (const late of frames.slice(1)) {
      await page.evaluate(
        ([path, payload]) => window.__fleetStreamStub?.push(path, payload) ?? false,
        [CLONE_PATH, `data: ${JSON.stringify(late)}\n\n`] as const,
      );
    }

    await expect(page.getByText("42 files copied (1.0 MB).")).toHaveCount(0);
    await expect(activityCommand(page)).toBeDisabled();
    await page.getByRole("button", { name: "Continue" }).click();
    await page.getByRole("button", { name: "Continue" }).click();
    await expect(page.getByRole("heading", { name: "What source material do you already have?" })).toBeVisible();
    await expect(page.getByRole("checkbox", { checked: true })).toHaveCount(0);
  });

  test("a described folder path cannot reach a run, and the reason is stated", async ({ page }) => {
    await page.goto("/");
    await completeSetup(page);
    await page.getByRole("button", { name: "Generate migration plan" }).click();
    await expect(page.getByRole("heading", { name: /^Migration plan for/ })).toBeVisible();

    await expect(page.getByRole("button", { name: "Edit setup" }).last()).toBeEnabled();
    await expect(page.getByRole("button", { name: "Run authorized phases" })).toHaveCount(0);
    await expect(page.locator("#next-action-hint")).toContainText("A typed folder path only describes where the code lives");
  });
});
