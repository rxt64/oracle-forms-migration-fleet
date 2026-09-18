import { defineConfig, devices } from "@playwright/test";
import { existsSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const projectDirectory = path.resolve(here, "..");
const port = Number(process.env.WORKBENCH_PORT ?? 8088);
const baseURL = `http://127.0.0.1:${port}`;

/** A fresh durable platform store per run. Reused within the run so restart behaviour is observable. */
const platformStateFile =
  process.env.PLATFORM_STATE_FILE ??
  path.join(os.tmpdir(), `ofm-playwright-platform-${Date.now()}`, "platform-state.json");

/**
 * `dotnet` is not on PATH on every machine this runs on, so the location is resolved rather than
 * assumed. `DOTNET_EXE` wins when it is set; CI relies on the PATH entry `setup-dotnet` installs.
 */
function dotnetExecutable(): string {
  if (process.env.DOTNET_EXE) return process.env.DOTNET_EXE;
  const home = process.env.USERPROFILE ?? process.env.HOME;
  if (home) {
    const local = path.join(home, ".dotnet", process.platform === "win32" ? "dotnet.exe" : "dotnet");
    if (existsSync(local)) return local;
  }
  return "dotnet";
}

/**
 * These tests drive the real ASP.NET host from this repository. Nothing is stubbed at the server:
 * the bootstrap catalog, validation, and the deterministic planner are the production code paths.
 *
 * Progress streams are the exception, and they are stubbed in the browser by a test-only `fetch`
 * shim installed with `addInitScript` — see `tests/e2e/fixtures.ts`. The application ships no
 * fixture route, no demo mode, and no query parameter that produces fake server events; a stub that
 * lived in the product could be reached in production, which is exactly the kind of false progress
 * this workbench exists to avoid.
 */
export default defineConfig({
  testDir: "./tests/e2e",
  fullyParallel: false,
  workers: 1,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  outputDir: "test-results",
  reporter: [
    ["list"],
    ["html", { open: "never", outputFolder: "playwright-report" }],
  ],
  use: {
    baseURL,
    extraHTTPHeaders: { "X-MS-CLIENT-PRINCIPAL-ID": "playwright-operator" },
    screenshot: "on",
    trace: "retain-on-failure",
    video: "off",
  },
  projects: [
    {
      name: "desktop-chromium",
      use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } },
    },
    {
      // A 390 x 844 viewport. This is a small-screen check, not a zoom check: browser zoom changes
      // CSS pixel density and font scaling, which a viewport size does not reproduce. Narrow
      // viewports are used below as a *proxy* for reflow at high zoom and are labelled as such.
      name: "mobile-chromium",
      use: { ...devices["Pixel 5"], viewport: { width: 390, height: 844 }, isMobile: false, hasTouch: true },
    },
  ],
  webServer: {
    command: `"${dotnetExecutable()}" run --project "${path.join(projectDirectory, "oracle-forms-migration-fleet.csproj")}"`,
    url: `${baseURL}/api/workbench/bootstrap`,
    cwd: projectDirectory,
    reuseExistingServer: !process.env.CI,
    timeout: 300_000,
    stdout: "pipe",
    stderr: "pipe",
    env: {
      PORT: String(port),
      ASPNETCORE_ENVIRONMENT: "Development",
      DOTNET_NOLOGO: "1",

      // Development mode is named rather than inferred, and it names no default actor, so a request
      // that carries no principal header is unauthenticated here exactly as it would be in Azure.
      WORKBENCH_AUTH_MODE: "Development",

      // A durable platform store in a fresh file per run, so approvals persist across a restart within
      // the run and never leak between runs.
      PLATFORM_STATE_FILE: platformStateFile,

      // A declared development target so the approval chain can be exercised end to end. No data
      // migration gateway is registered without SANDBOX_PGHOST, so an approved run still writes nothing
      // to any database — the gate opens and the adapter reports it has nothing to write to.
      WORKBENCH_DEV_SANDBOX_TARGET: "pg-sandbox.postgres.database.azure.com|ofm_sandbox|id-ofmfleet-web-dev",
      SANDBOX_AZURE_RESOURCE_GROUP: "rg-oracle-forms-migration-fleet-dev",
      SANDBOX_AZURE_REGION: "eastus2",
    },
  },
});
