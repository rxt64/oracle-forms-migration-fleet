import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import {
  CLONE_PATH,
  EXECUTE_PATH,
  RUN,
  RUN_PURPOSE,
  cloneFrames,
  field,
  frame,
  installStreamStub,
  startAtSetup,
} from "./fixtures";

const diagnostic = {
  path: ".fleet-run/out/orders/reports/source-analysis.json",
  kind: "ValidationReport",
  description: "Diagnostic retained from the failed phase.",
  previewable: true,
};

test.describe("failed execution results", () => {
  test.beforeEach(async ({ page }) => {
    await startAtSetup(page);
  });

  test("a failed zero-work terminal stays failed across Activity and Results", async ({ page }) => {
    const streams = await installStreamStub(page);
    await page.route("**/api/workbench/plan", async (route) => {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          plan: {
            engagementId: "ENG-FAILED-RUN",
            applicationName: "ORDERS",
            requestedMode: "GenerateArtifacts",
            authorizedMode: "GenerateArtifacts",
            target: { frontEnd: "React", backEnd: "JavaSpringBoot", database: "PostgreSql" },
            phases: [{
              phase: "SourceAnalysis",
              owner: "DiscoveryAnalyst",
              status: "Planned",
              mutation: "WorkspaceArtifactWrite",
              requiredMode: "GenerateArtifacts",
              requiresApproval: false,
              objective: "Read the owned source copy.",
              requiredInputs: [],
              expectedOutputs: [diagnostic],
              tooling: ["Deterministic source reader"],
              blockers: [],
            }],
            assumptions: [],
            blockers: [],
            disclaimers: [],
          },
          steps: [],
          executionBoundary: "Workspace writes only.",
        }),
      });
    });

    await page.goto("/");
    await field.engagementId(page).fill("ENG-FAILED-RUN");
    await field.applicationName(page).fill("ORDERS");
    await field.outputRoot(page).fill("out/orders");
    await page.getByRole("radio", { name: "Clone a Git repository" }).check();
    await field.repositoryUrl(page).fill("https://github.com/contoso/orders");
    await page.getByRole("button", { name: "Copy this repository" }).click();
    await streams.waitForOpen(CLONE_PATH);
    await streams.pushAll(CLONE_PATH, cloneFrames());
    await streams.close(CLONE_PATH);
    await page.getByRole("button", { name: "Close activity" }).click();

    for (let step = 0; step < 4; step += 1) {
      await page.getByRole("button", { name: "Continue" }).click();
    }
    await page.getByRole("button", { name: "Generate migration plan" }).click();
    const run = page.getByRole("button", { name: "Run authorized phases", exact: true });
    await expect(run).toBeVisible();
    await run.click();
    await streams.waitForOpen(EXECUTE_PATH);

    await streams.push(EXECUTE_PATH, frame({
      level: "error",
      text: "SourceAnalysis failed before an adapter completed.",
      operation: RUN,
      action: "run.failed",
      state: "Failed",
      purpose: RUN_PURPOSE,
      observed: "0 of 1 phase(s) ran and 1 diagnostic file was retained.",
      nextAction: "Read the failed phase before trying again.",
      artifactKind: "GeneratedFile",
      artifactCount: 1,
      result: {
        requestedMode: "GenerateArtifacts",
        authorizedMode: "GenerateArtifacts",
        outputRoot: ".fleet-run/out/orders",
        phases: [{
          phase: "SourceAnalysis",
          plannedStatus: "Planned",
          state: "Failed",
          detail: "The source reader failed before completion.",
          artifacts: [diagnostic],
          findings: ["The diagnostic was retained."],
        }],
        artifacts: [diagnostic],
        attestations: [],
        blockers: [],
      },
    }));
    await streams.close(EXECUTE_PATH);

    const activity = page.getByRole("dialog", { name: "Running the authorized phases" });
    await expect(activity.locator(".mf-activity-summary .mf-pill")).toHaveText("Failed");
    await expect(activity).toContainText("0 of 1 phase(s) ran");
    await activity.getByRole("button", { name: "Close activity" }).click();

    await expect(page.locator(".mf-outcome")).toHaveClass(/danger/);
    await expect(page.getByRole("heading", { name: "The run finished without completing any phase." })).toBeVisible();
    await expect(page.locator("main.mf-results .mf-primary")).toHaveCount(1);
    await page.getByText("Files this run wrote", { exact: false }).click();
    await expect(page.getByRole("button", { name: "More information about opening a generated file" })).toHaveCount(1);
    await expect(page.locator("button button")).toHaveCount(0);

    const axe = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"])
      .analyze();
    expect(axe.violations).toEqual([]);
  });
});
