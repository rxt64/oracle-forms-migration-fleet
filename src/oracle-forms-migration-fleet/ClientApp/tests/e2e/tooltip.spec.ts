import { expect, test } from "@playwright/test";
import { startAtSetup } from "./fixtures";

/**
 * InfoTip behaviour.
 *
 * Hover opens transiently; clicking pins. The distinction is the point: a tip that closes when the
 * pointer drifts is unreadable for anything longer than a phrase, and a tip that never closes is a
 * dialog pretending to be help.
 */

const bubble = (page: import("@playwright/test").Page) => page.locator(".mf-tip-bubble");

test.describe("contextual help", () => {
  test.beforeEach(async ({ page }) => {
    await startAtSetup(page);
    await page.goto("/");
  });

  test("hover opens transiently and closes when the pointer leaves", async ({ page, browserName }) => {
    test.skip(test.info().project.name === "mobile-chromium", "hover has no meaning on a touch profile");
    void browserName;

    const trigger = page.getByRole("button", { name: "More information about reference for this plan" });
    await trigger.hover();
    await expect(bubble(page)).toBeVisible();
    await expect(bubble(page)).not.toHaveAttribute("data-pinned", "true");

    await page.mouse.move(5, 5);
    await expect(bubble(page)).toHaveCount(0);
  });

  test("clicking a hover-opened tip pins it, and it survives the pointer leaving", async ({ page }) => {
    test.skip(test.info().project.name === "mobile-chromium", "hover has no meaning on a touch profile");

    const trigger = page.getByRole("button", { name: "More information about reference for this plan" });
    await trigger.hover();
    await expect(bubble(page)).toBeVisible();

    await trigger.click();
    await expect(bubble(page)).toHaveAttribute("data-pinned", "true");
    await expect(trigger).toHaveAttribute("aria-expanded", "true");

    await page.mouse.move(5, 5);
    await page.waitForTimeout(400);
    await expect(bubble(page)).toBeVisible();
  });

  test("clicking a pinned tip dismisses it", async ({ page }) => {
    const trigger = page.getByRole("button", { name: "More information about reference for this plan" });
    await trigger.click();
    await expect(bubble(page)).toBeVisible();
    await trigger.click();
    await expect(bubble(page)).toHaveCount(0);
    await expect(trigger).toHaveAttribute("aria-expanded", "false");
  });

  test("a pinned tip survives the trigger losing focus", async ({ page }) => {
    const trigger = page.getByRole("button", { name: "More information about application name" });
    await trigger.click();
    await expect(bubble(page)).toBeVisible();

    await page.locator("#engagementId").focus();
    await page.waitForTimeout(400);
    await expect(bubble(page)).toBeVisible();
  });

  test("keyboard opens, pins, and dismisses help without a pointer", async ({ page }) => {
    const trigger = page.getByRole("button", { name: "More information about reference for this plan" });
    await trigger.focus();
    await expect(bubble(page)).toBeVisible();

    await page.keyboard.press("Enter");
    await expect(bubble(page)).toHaveAttribute("data-pinned", "true");

    await page.keyboard.press("Escape");
    await expect(bubble(page)).toHaveCount(0);
    await expect(trigger).toBeFocused();
  });

  test("Tab dismisses keyboard-pinned help before focus moves on", async ({ page }) => {
    const trigger = page.getByRole("button", { name: "More information about reference for this plan" });
    await trigger.focus();
    await page.keyboard.press("Enter");
    await expect(bubble(page)).toHaveAttribute("data-pinned", "true");

    await page.keyboard.press("Tab");
    await expect(bubble(page)).toHaveCount(0);
    await expect(trigger).not.toBeFocused();
  });

  test("tapping opens help on a touch profile and tapping again dismisses it", async ({ page }) => {
    test.skip(test.info().project.name !== "mobile-chromium", "touch behaviour is checked on the touch profile");

    const trigger = page.getByRole("button", { name: "More information about reference for this plan" });
    await trigger.tap();
    await expect(bubble(page)).toBeVisible();
    await expect(bubble(page)).toHaveAttribute("data-pinned", "true");

    await trigger.tap();
    await expect(bubble(page)).toHaveCount(0);
  });

  test("an outside click dismisses a pinned tip", async ({ page }) => {
    const trigger = page.getByRole("button", { name: "More information about reference for this plan" });
    await trigger.click();
    await expect(bubble(page)).toBeVisible();

    await page.locator("h1").click();
    await expect(bubble(page)).toHaveCount(0);
  });

  test("moving the pointer into the panel keeps it readable", async ({ page }) => {
    test.skip(test.info().project.name === "mobile-chromium", "hover has no meaning on a touch profile");

    const trigger = page.getByRole("button", { name: "More information about application name" });
    await trigger.hover();
    await expect(bubble(page)).toBeVisible();

    await bubble(page).hover();
    await page.waitForTimeout(400);
    await expect(bubble(page)).toBeVisible();
  });

  test("a long explanation stays inside the viewport", async ({ page }) => {
    // The Oracle Forms release entry is the longest body in the registry.
    await page.getByRole("button", { name: "More information about oracle forms release" }).click();
    const panel = bubble(page);
    await expect(panel).toBeVisible();

    const box = await panel.boundingBox();
    const viewport = page.viewportSize();
    expect(box).not.toBeNull();
    expect(viewport).not.toBeNull();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.y).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(viewport!.width + 1);
    expect(box!.y + box!.height).toBeLessThanOrEqual(viewport!.height + 1);
    await page.screenshot({ path: `test-results/screens/${test.info().project.name}-tooltip-long.png` });
  });

  test("a tip on the last control on screen flips rather than overflowing", async ({ page }) => {
    await page.getByRole("button", { name: "More information about oracle database release" }).scrollIntoViewIfNeeded();
    await page.getByRole("button", { name: "More information about oracle database release" }).click();

    const box = await bubble(page).boundingBox();
    const viewport = page.viewportSize();
    expect(box!.y).toBeGreaterThanOrEqual(0);
    expect(box!.y + box!.height).toBeLessThanOrEqual(viewport!.height + 1);
  });

  test("Escape closes help before the dialog that contains it", async ({ page }) => {
    await page.getByRole("button", { name: "Azure status" }).click();
    const dialog = page.getByRole("dialog", { name: "Azure readiness" });
    await expect(dialog).toBeVisible();

    const trigger = dialog.getByRole("button", { name: /^More information about / }).first();
    await trigger.click();
    await expect(bubble(page)).toBeVisible();

    await page.keyboard.press("Escape");
    await expect(bubble(page)).toHaveCount(0);
    await expect(dialog).toBeVisible();
    await expect(trigger).toBeFocused();

    await page.keyboard.press("Escape");
    await expect(dialog).toHaveCount(0);
  });
});
