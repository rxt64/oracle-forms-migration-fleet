import AxeBuilder from "@axe-core/playwright";
import { expect, test, type Page } from "@playwright/test";
import {
  CLONE_PATH,
  cloneFrames,
  cloneInterruptedFrames,
  completeSetup,
  expectNoHorizontalOverflow,
  field,
  generatePlan,
  installStreamStub,
  startAtSetup,
} from "./fixtures";

/**
 * Automated accessibility checks.
 *
 * Axe finds a subset of barriers. A clean run is not a conformance claim: it means no rule in this
 * ruleset fired on the states listed below. Screen-reader behaviour, native high-contrast rendering,
 * and real browser zoom are not covered here and are not asserted anywhere in this suite.
 */

async function scan(page: Page, label: string) {
  const results = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"])
    .analyze();

  const serious = results.violations.filter((violation) => violation.impact === "serious" || violation.impact === "critical");
  expect(
    serious,
    `${label}: ${serious.map((violation) => `${violation.id} (${violation.nodes.length} nodes)`).join(", ")}`,
  ).toEqual([]);
  expect(results.violations, `${label} had axe violations`).toEqual([]);
}

test.describe("accessibility", () => {
  test("overview", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByRole("heading", { name: "Plan a move off Oracle Forms" })).toBeVisible();
    await scan(page, "overview");
  });

  test("setup step one and its validation failure", async ({ page }) => {
    await startAtSetup(page);
    await page.goto("/");
    await expect(page.getByRole("heading", { name: "Which application are you moving?" })).toBeVisible();
    await scan(page, "setup step one");

    await page.getByRole("button", { name: "Continue" }).click();
    await expect(page.locator("#engagementId")).toBeFocused();
    await scan(page, "validation failure");
  });

  test("help panel open", async ({ page }) => {
    await startAtSetup(page);
    await page.goto("/");
    await page.getByRole("button", { name: "More information about reference for this plan" }).click();
    await expect(page.locator(".mf-tip-bubble")).toBeVisible();
    await scan(page, "help panel");
  });

  test("glossary dialog", async ({ page }) => {
    await page.goto("/");
    await page.getByRole("button", { name: "Help" }).click();
    await expect(page.getByRole("dialog", { name: "What these words mean" })).toBeVisible();
    await scan(page, "glossary dialog");
  });

  test("activity in its completed state", async ({ page }) => {
    await startAtSetup(page);
    const stub = await installStreamStub(page);
    await page.goto("/");
    await field.engagementId(page).fill("ENG-0042");
    await field.applicationName(page).fill("ORDERS");
    await page.getByRole("radio", { name: "Clone a Git repository" }).check();
    await field.repositoryUrl(page).fill("https://github.com/contoso/orders");
    await page.getByRole("button", { name: "Copy this repository" }).click();
    await stub.waitForOpen(CLONE_PATH);
    await stub.pushAll(CLONE_PATH, cloneFrames());
    await stub.close(CLONE_PATH);
    await expect(page.locator(".mf-activity-summary .mf-pill")).toHaveText("Completed");
    await scan(page, "activity completed");
  });

  test("activity in its interrupted state", async ({ page }) => {
    await startAtSetup(page);
    const stub = await installStreamStub(page);
    await page.goto("/");
    await field.engagementId(page).fill("ENG-0042");
    await field.applicationName(page).fill("ORDERS");
    await page.getByRole("radio", { name: "Clone a Git repository" }).check();
    await field.repositoryUrl(page).fill("https://github.com/contoso/orders");
    await page.getByRole("button", { name: "Copy this repository" }).click();
    await stub.waitForOpen(CLONE_PATH);
    await stub.pushAll(CLONE_PATH, cloneInterruptedFrames());
    await stub.close(CLONE_PATH);
    await expect(page.locator(".mf-activity-summary .mf-pill")).toHaveText("Interrupted or unknown");
    await scan(page, "activity interrupted");
  });

  test("results, collapsed and expanded", async ({ page }) => {
    await startAtSetup(page);
    await page.goto("/");
    await completeSetup(page);
    await generatePlan(page);
    await expectNoHorizontalOverflow(page);
    await scan(page, "results collapsed");

    for (const disclosure of await page.locator("details.mf-disclosure > summary").all()) {
      await disclosure.click();
    }
    await scan(page, "results expanded");
  });
});
