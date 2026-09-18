import { expect, test } from "@playwright/test";
import {
  CLONE_PATH,
  activityCommand,
  cloneFailureFrames,
  cloneFrames,
  cloneInterruptedFrames,
  field,
  installStreamStub,
  runWaitingFrame,
  startAtSetup,
  type Frame,
} from "./fixtures";

/**
 * Activity behaviour.
 *
 * The frames are stubbed in the browser, so these assertions are about the operator console's
 * handling of a frame sequence. They are not evidence that a clone, an extraction, or an adapter ran.
 */

async function beginClone(page: import("@playwright/test").Page) {
  await field.engagementId(page).fill("ENG-0042");
  await field.applicationName(page).fill("ORDERS");
  await page.getByRole("radio", { name: "Clone a Git repository" }).check();
  await field.repositoryUrl(page).fill("https://github.com/contoso/orders");
  await page.getByRole("button", { name: "Copy this repository" }).click();
}

const state = (page: import("@playwright/test").Page) => page.locator(".mf-activity-summary .mf-pill");
const announcement = (page: import("@playwright/test").Page) => page.locator(".mf-activity-body > .mf-sr-only");

test.describe("activity pane", () => {
  test.beforeEach(async ({ page }) => {
    await startAtSetup(page);
  });

  test("raw server output is collapsed until it is asked for", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");
    await beginClone(page);
    await stub.waitForOpen(CLONE_PATH);
    await stub.pushAll(CLONE_PATH, cloneFrames());
    await stub.close(CLONE_PATH);

    const raw = page.locator("details.mf-activity-raw");
    await expect(raw).toHaveCount(1);
    await expect(raw).not.toHaveAttribute("open", /.*/);
    await expect(page.locator(".mf-activity-log")).toBeHidden();

    await raw.locator("summary").click();
    await expect(page.locator(".mf-activity-log")).toBeVisible();
    await expect(page.locator(".mf-activity-line")).toHaveCount(8);
  });

  test("the summary names the operation, its purpose, what was observed, and what happens next", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");
    await beginClone(page);
    await stub.waitForOpen(CLONE_PATH);
    await stub.pushAll(CLONE_PATH, cloneFrames());
    await stub.close(CLONE_PATH);

    const summary = page.locator(".mf-activity-summary");
    await expect(summary.locator(".mf-activity-operation")).toHaveText("Copying your source");
    await expect(summary.getByText(/Taking a private read-only copy of your source/)).toBeVisible();
    await expect(summary.getByText(/42 file\(s\) copied and locked read-only/)).toBeVisible();
    await expect(summary.getByText(/deleted automatically four hours from now/)).toBeVisible();
    await page.screenshot({ path: `test-results/screens/${test.info().project.name}-activity-completed.png` });
  });

  test("actual artifact counts come from typed fields, not from the message text", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");
    await beginClone(page);
    await stub.waitForOpen(CLONE_PATH);
    await stub.pushAll(CLONE_PATH, cloneFrames());
    await stub.close(CLONE_PATH);

    const measured = page.locator(".mf-activity-measured li");
    await expect(measured).toHaveCount(3);
    await expect(measured.filter({ hasText: "Forms module source" })).toContainText("12");
    await expect(measured.filter({ hasText: "PL/SQL program units" })).toContainText("30");

    // The message counters stay message counters and say so in their labels.
    const counters = page.locator(".mf-activity-counts li");
    await expect(counters.nth(0)).toContainText("Activity messages");
    await expect(counters.nth(1)).toContainText("Discovery messages");
  });

  test("a stream that ends without an outcome is reported as interrupted, not completed", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");
    await beginClone(page);
    await stub.waitForOpen(CLONE_PATH);
    await stub.pushAll(CLONE_PATH, cloneInterruptedFrames());
    await stub.close(CLONE_PATH);

    await expect(state(page)).toHaveText("Interrupted or unknown");
    await expect(page.getByText(/stopped without the server reporting an outcome/)).toBeVisible();
    await expect(page.getByText(/These are partial findings/)).toBeVisible();

    // Partial findings survive: the twelve modules the server did count are still shown.
    await expect(page.locator(".mf-activity-measured li").filter({ hasText: "Forms module source" })).toContainText("12");
    await expect(state(page)).not.toHaveText("Completed");
    await page.screenshot({ path: `test-results/screens/${test.info().project.name}-activity-interrupted.png` });
  });

  test("a declared failure is reported as failed", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");
    await beginClone(page);
    await stub.waitForOpen(CLONE_PATH);
    await stub.pushAll(CLONE_PATH, cloneFailureFrames());
    await stub.close(CLONE_PATH);

    await expect(state(page)).toHaveText("Failed");
    await expect(page.getByText(/Nothing was kept\. Correct the problem above/)).toBeVisible();
    await page.screenshot({ path: `test-results/screens/${test.info().project.name}-activity-failed.png` });
  });

  test("a heartbeat is reported as waiting rather than as progress", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");
    await beginClone(page);
    await stub.waitForOpen(CLONE_PATH);
    await stub.push(CLONE_PATH, cloneFrames()[0]);
    await expect(state(page)).toHaveText("Running");

    // Reuses the run heartbeat shape on the acquisition stream: the state is what is under test.
    const heartbeat: Frame = { ...runWaitingFrame(), operation: "source.acquire" };
    await stub.push(CLONE_PATH, heartbeat);
    await expect(state(page)).toHaveText("Waiting for updates");
  });

  test("the screen reader is given a concise status, and the log does not announce each line", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");
    await beginClone(page);
    await stub.waitForOpen(CLONE_PATH);
    await stub.push(CLONE_PATH, cloneFrames()[0]);

    const live = announcement(page);
    await expect(live).toHaveAttribute("aria-live", "polite");
    await expect(live).toHaveText("Copying your source: Running.");

    await stub.pushAll(CLONE_PATH, cloneFrames().slice(1));
    await stub.close(CLONE_PATH);
    await expect(live).toContainText("Copying your source: Completed.");

    // The transcript is inspectable but is not a live region, so it announces nothing on its own.
    await page.locator("details.mf-activity-raw summary").click();
    const log = page.locator(".mf-activity-log");
    await expect(log).toBeVisible();
    await expect(log).not.toHaveAttribute("aria-live", /.*/);
    await expect(log).not.toHaveAttribute("role", "log");
  });

  test("focus is not stolen while many events arrive", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");
    await beginClone(page);
    await stub.waitForOpen(CLONE_PATH);

    const rawSummary = page.locator("details.mf-activity-raw summary");
    await rawSummary.focus();
    await expect(rawSummary).toBeFocused();

    for (const value of cloneFrames()) {
      await stub.push(CLONE_PATH, value);
      await expect(rawSummary).toBeFocused();
    }
    await stub.close(CLONE_PATH);
    await expect(rawSummary).toBeFocused();
  });

  test("keyboard navigation stays inside the pane while the stream is open", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");
    await beginClone(page);
    await stub.waitForOpen(CLONE_PATH);
    await stub.push(CLONE_PATH, cloneFrames()[0]);

    await expect(page.getByRole("button", { name: "Close activity" })).toBeFocused();

    const inside = async () => page.evaluate(() => Boolean(document.querySelector(".mf-activity")?.contains(document.activeElement)));
    for (let press = 0; press < 12; press += 1) {
      await page.keyboard.press("Tab");
      expect(await inside(), `focus escaped the activity dialog after ${press + 1} tabs`).toBe(true);
    }
    for (let press = 0; press < 6; press += 1) {
      await page.keyboard.press("Shift+Tab");
      expect(await inside(), "focus escaped the activity dialog on reverse tabbing").toBe(true);
    }

    await stub.close(CLONE_PATH);
  });

  test("closing the pane returns focus to the control that opened it", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");
    await beginClone(page);
    await stub.waitForOpen(CLONE_PATH);
    await stub.pushAll(CLONE_PATH, cloneFrames());
    await stub.close(CLONE_PATH);

    await page.getByRole("button", { name: "Close activity" }).click();
    await activityCommand(page).click();
    await expect(page.getByRole("dialog", { name: /Copying your repository/ })).toBeVisible();

    await page.keyboard.press("Escape");
    await expect(activityCommand(page)).toBeFocused();
  });

  test("the footer states the cooperative, request-bound cancellation boundary", async ({ page }) => {
    const stub = await installStreamStub(page);
    await page.goto("/");
    await beginClone(page);
    await stub.waitForOpen(CLONE_PATH);

    const footer = page.locator(".mf-activity footer");
    await expect(footer).toContainText("Closing this pane does not stop the work.");
    await expect(footer).toContainText("cancellation here is cooperative and bound to this browser request");
    await expect(footer).toContainText("work already done is not undone");
    await expect(footer).toContainText("no durable job resumes it");

    await stub.close(CLONE_PATH);
  });
});
