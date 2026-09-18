import { expect, test } from "@playwright/test";
import { completeSetup, expectNoHorizontalOverflow, field, generatePlan, startAtSetup } from "./fixtures";

/**
 * Results ordering.
 *
 * The plan on this page comes from the real deterministic planner on the local host. Only the order,
 * the disclosures, and the single primary action are under test here.
 */
test.describe("plan and results", () => {
  test.beforeEach(async ({ page }) => {
    await startAtSetup(page);
    await page.goto("/");
    await completeSetup(page);
    await generatePlan(page);
  });

  test("the page reads outcome, blockers, next action, then run results", async ({ page }) => {
    const order = await page.evaluate(() => {
      const main = document.querySelector("main.mf-results");
      if (!main) return [];
      return Array.from(main.children).map((child) => child.className.split(" ")[0]);
    });

    const outcome = order.indexOf("mf-outcome");
    const blockers = order.indexOf("mf-alert");
    const next = order.indexOf("mf-next-action");
    const results = order.indexOf("mf-result-section");
    const firstDisclosure = order.indexOf("mf-disclosure");

    expect(outcome).toBeGreaterThanOrEqual(0);
    expect(next).toBeGreaterThan(outcome);
    if (blockers >= 0) {
      expect(blockers).toBeGreaterThan(outcome);
      expect(blockers).toBeLessThan(next);
    }
    expect(results).toBeGreaterThan(next);
    expect(firstDisclosure).toBeGreaterThan(results);

    await expectNoHorizontalOverflow(page);
    await page.screenshot({ path: `test-results/screens/${test.info().project.name}-results.png`, fullPage: true });
  });

  test("the outcome is one short claim and does not call a plan a migration", async ({ page }) => {
    const outcome = page.locator(".mf-outcome");
    await expect(outcome.getByRole("heading")).toBeVisible();
    await expect(outcome).toContainText(/Plan generated/);
    await expect(outcome).not.toContainText(/Migration complete/i);
  });

  test("architecture, lifecycle, phase detail, attribution and Azure footprint are collapsed by default", async ({ page }) => {
    const names = [
      "Target architecture diagram",
      "Six-stage modernization lifecycle",
      "Full phase detail: inputs, outputs, and blockers",
      "Which model and which code runs each step",
      "Azure footprint this plan would need",
    ];

    for (const name of names) {
      const disclosure = page.locator("details.mf-disclosure", { has: page.getByText(name, { exact: true }) });
      await expect(disclosure, `${name} should exist as its own named disclosure`).toHaveCount(1);
      await expect(disclosure, `${name} should start collapsed`).not.toHaveAttribute("open", /.*/);
    }
  });

  test("critical write effects stay visible without expanding anything", async ({ page }) => {
    const boundary = page.locator(".mf-next-action .mf-boundary");
    await expect(boundary).toBeVisible();
    await expect(boundary).toContainText("What running actually writes.");
    await expect(boundary).toContainText("your source repository is never modified");
    await expect(boundary).toContainText("no Azure resource is created");
  });

  test("the capability list stays visible and refuses to claim more than it can", async ({ page }) => {
    const capabilities = page.locator(".mf-capabilities");
    await expect(capabilities).toBeVisible();
    await expect(capabilities.getByText("Deployed", { exact: true })).toBeVisible();
    await expect(capabilities.getByText("Behaviour verified", { exact: true })).toBeVisible();
    await expect(capabilities.locator("li", { hasText: "Deployed" })).toContainText("Not available here");
  });

  test("there is exactly one primary action on the page", async ({ page }) => {
    await expect(page.locator("main.mf-results .mf-primary")).toHaveCount(1);
    await expect(page.locator(".mf-next-action .mf-primary")).toHaveCount(1);
  });

  test("metric help is reachable from the run results", async ({ page }) => {
    const metrics = page.locator(".mf-metrics");
    for (const label of ["Stages ready", "Inputs provided", "Still missing", "Phases that ran", "Files written"]) {
      await expect(metrics.getByText(label, { exact: true })).toBeVisible();
    }
    await page.getByRole("button", { name: "More information about phases that ran" }).click();
    await expect(page.locator(".mf-tip-bubble")).toContainText("an adapter actually executed");
  });

  test("a blocker list longer than three items collapses the remainder", async ({ page }) => {
    // Re-plan at the deepest planning depth. Assessment-only planning raises very few blockers, so
    // testing the collapse on it would assert nothing.
    await page.getByRole("button", { name: "New migration" }).first().click();
    await field.engagementId(page).fill("ENG-BLOCKERS");
    await field.applicationName(page).fill("ORDERS");
    await field.outputRoot(page).fill("out/orders");
    await page.getByRole("radio", { name: "Describe a folder path" }).check();
    await field.sourceRoot(page).fill("legacy/forms");
    await page.getByRole("button", { name: "Continue" }).click();
    await expect(page.getByRole("heading", { name: "Where should the migrated application land?" })).toBeVisible();

    const depth = page.locator("fieldset.mf-choices", { has: page.getByText("Planning depth", { exact: true }) });
    await depth.getByRole("radio").last().check();

    for (let step = 0; step < 3; step += 1) {
      await page.getByRole("button", { name: "Continue" }).click();
    }
    await generatePlan(page);

    const alert = page.locator("section.mf-alert");
    await expect(alert).toHaveCount(1);
    expect(await alert.locator("li").count(), "the deepest depth should raise more than three blockers")
      .toBeGreaterThan(3);

    await expect(alert.locator("> ul > li")).toHaveCount(3);
    await expect(alert.locator("details.mf-optional")).toHaveCount(1);
  });
});
