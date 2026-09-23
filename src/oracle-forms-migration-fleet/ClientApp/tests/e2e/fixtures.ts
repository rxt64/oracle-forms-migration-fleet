import { expect, type Page } from "@playwright/test";

/**
 * Test-only fixtures for the guided operator UI.
 *
 * Everything here lives in the test tree. The application has no fixture route, no demo mode, and no
 * flag that makes the server emit invented progress: a stub reachable in production is a way to show
 * an operator a migration that never happened.
 *
 * Two mechanisms are used, and they are deliberately different:
 *
 * - `installStreamStub` replaces `fetch` **in the page** for the two progress endpoints only, and
 *   hands the test a controller it can push frames into one at a time. That is what makes a genuinely
 *   open stream testable: the browser sees frames arrive over time, exactly as it would from Kestrel.
 * - Everything else — the bootstrap catalog, field validation, and the deterministic planner — runs
 *   against the real host started by `playwright.config.ts`. Those are not stubbed at all.
 *
 * What a stubbed stream proves is the browser's handling of a frame sequence. It proves nothing about
 * Git acquisition, archive extraction, adapter execution, or behavioural equivalence.
 */

export const CLONE_PATH = "/api/workbench/source/clone";
export const UPLOAD_PATH = "/api/workbench/source/upload";
export const EXECUTE_PATH = "/api/workbench/execute";

export const ACQUIRE = "source.acquire";
export const RUN = "migration.run";

export const ACQUIRE_PURPOSE =
  "Taking a private read-only copy of your source so the fleet has something it can read.";
export const RUN_PURPOSE =
  "Running the phases the planner authorized and writing their output into your session workspace.";

export interface Frame {
  level: string;
  text?: string;
  sequence?: number;
  timestampUtc?: string;
  operation?: string;
  action?: string;
  state?: "Running" | "Waiting" | "Completed" | "Failed";
  purpose?: string;
  observed?: string;
  nextAction?: string;
  artifactKind?: string | null;
  artifactCount?: number | null;
  workspace?: unknown;
  result?: unknown;
}

let stamp = 0;

/** Mirrors the shape `WorkbenchEndpoints.Frame` puts on the wire. */
export function frame(partial: Frame): Frame {
  stamp += 1;
  return {
    sequence: stamp,
    timestampUtc: new Date(Date.UTC(2026, 0, 1, 0, 0, stamp)).toISOString(),
    ...partial,
  };
}

export function acquiring(action: string, observed: string, nextAction: string, extra: Partial<Frame> = {}): Frame {
  return frame({
    level: "info",
    text: observed,
    operation: ACQUIRE,
    action,
    state: "Running",
    purpose: ACQUIRE_PURPOSE,
    observed,
    nextAction,
    ...extra,
  });
}

export function workspaceSummary(overrides: Record<string, unknown> = {}) {
  const now = new Date();
  return {
    workspaceId: "ws-e2e-0001",
    origin: "https://github.com/contoso/orders",
    originLabel: "github.com/contoso/orders",
    fileCount: 42,
    byteCount: 1_048_576,
    sourceRoot: "legacy/forms",
    artifacts: [
      { kind: "FormsModuleSource", count: 12, example: "ORDERS_ENTRY.fmb" },
      { kind: "PlSqlProgramUnit", count: 30, example: "PKG_ORDERS.sql" },
    ],
    acquiredUtc: now.toISOString(),
    expiresUtc: new Date(now.getTime() + 4 * 60 * 60 * 1000).toISOString(),
    ...overrides,
  };
}

/** The frames a healthy clone produces, ending in the server's own Completed outcome. */
export function cloneFrames(): Frame[] {
  return [
    acquiring("source.workspace.created", "A private session folder was created.", "Connecting to the repository host."),
    acquiring("source.clone", "Copying from github.com.", "Indexing the copied files once the copy finishes."),
    acquiring("source.remote.detached", "Git remote metadata was deleted, so the copy cannot push back to your repository.", "Indexing the copied files."),
    acquiring("source.index", "Reading the copied file names to recognise Oracle artifacts.", "Reporting what was recognised."),
    {
      ...acquiring("source.artifact.counted", "12 FormsModuleSource file(s) recognised by name, for example ORDERS_ENTRY.fmb.", "Matching source-checklist items will be ticked for you when the copy finishes."),
      level: "found",
      artifactKind: "FormsModuleSource",
      artifactCount: 12,
    },
    {
      ...acquiring("source.artifact.counted", "30 PlSqlProgramUnit file(s) recognised by name, for example PKG_ORDERS.sql.", "Matching source-checklist items will be ticked for you when the copy finishes."),
      level: "found",
      artifactKind: "PlSqlProgramUnit",
      artifactCount: 30,
    },
    acquiring("source.locked", "The copy was locked read-only.", "Finishing the copy."),
    frame({
      level: "done",
      text: "",
      operation: ACQUIRE,
      action: "source.ready",
      state: "Completed",
      purpose: ACQUIRE_PURPOSE,
      observed: "42 file(s) copied and locked read-only; 2 Oracle artifact kind(s) recognised.",
      nextAction: "Continue the setup. The copy is deleted automatically four hours from now.",
      artifactKind: "RecognisedArtifactKind",
      artifactCount: 2,
      workspace: workspaceSummary(),
    }),
  ];
}

/** A clone the server declared failed. Terminal, and explicitly not a success. */
export function cloneFailureFrames(): Frame[] {
  return [
    acquiring("source.workspace.created", "A private session folder was created.", "Connecting to the repository host."),
    frame({
      level: "error",
      text: "Clone failed. Private repositories are not supported yet; export a zip instead.",
      operation: ACQUIRE,
      action: "source.failed",
      state: "Failed",
      purpose: ACQUIRE_PURPOSE,
      observed: "Clone failed. Private repositories are not supported yet; export a zip instead.",
      nextAction: "Nothing was kept. Correct the problem above and start the copy again.",
    }),
  ];
}

/** Frames that stop without any outcome. The browser must call this interrupted, never finished. */
export function cloneInterruptedFrames(): Frame[] {
  return [
    acquiring("source.workspace.created", "A private session folder was created.", "Connecting to the repository host."),
    acquiring("source.clone", "Copying from github.com.", "Indexing the copied files once the copy finishes."),
    {
      ...acquiring("source.artifact.counted", "12 FormsModuleSource file(s) recognised by name, for example ORDERS_ENTRY.fmb.", "Matching source-checklist items will be ticked for you when the copy finishes."),
      level: "found",
      artifactKind: "FormsModuleSource",
      artifactCount: 12,
    },
  ];
}

export function runWaitingFrame(): Frame {
  return frame({
    level: "keepalive",
    text: "Build or migration work is still running.",
    operation: RUN,
    action: "run.heartbeat",
    state: "Waiting",
    purpose: RUN_PURPOSE,
    observed: "The run is still open but has reported nothing new for twenty seconds.",
    nextAction: "Leave this open, or close it and come back — the run is not affected either way.",
  });
}

export interface StreamStub {
  /** Waits until the page has actually issued the request this stub answers. */
  waitForOpen(path: string, count?: number): Promise<void>;
  push(path: string, value: Frame): Promise<void>;
  pushAll(path: string, values: Frame[]): Promise<void>;
  close(path: string): Promise<void>;
  /** Every body posted to the execute endpoint, in order, parsed. */
  executeBodies(): Promise<Array<Record<string, unknown>>>;
  /** Makes the next execute post answer like a server refusal, or clears one with null. */
  refuseExecute(refusal: { status: number; error: string } | null): Promise<void>;
}

declare global {
  interface Window {
    __fleetStreamStub?: {
      opened: string[];
      executed: string[];
      refusal: { status: number; error: string } | null;
      push(path: string, payload: string): boolean;
      close(path: string): boolean;
    };
  }
}

/**
 * Installs a page-side `fetch` shim for the progress endpoints and returns a controller.
 *
 * The shim answers only the two streaming paths and forwards everything else to the real host, so
 * the plan endpoint, the bootstrap catalog, and asset loads stay genuine.
 */
export async function installStreamStub(page: Page): Promise<StreamStub> {
  await page.addInitScript(({ paths, executePath }: { paths: string[]; executePath: string }) => {
    const controllers = new Map<string, ReadableStreamDefaultController<Uint8Array>>();
    const encoder = new TextEncoder();
    const opened: string[] = [];
    const original = window.fetch.bind(window);

    window.__fleetStreamStub = {
      opened,
      executed: [],
      refusal: null,
      push(path, payload) {
        const controller = controllers.get(path);
        if (!controller) return false;
        controller.enqueue(encoder.encode(payload));
        return true;
      },
      close(path) {
        const controller = controllers.get(path);
        if (!controller) return false;
        controllers.delete(path);
        controller.close();
        return true;
      },
    };

    window.fetch = (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
      if (url.includes(executePath) && (init?.method ?? "GET") === "POST") {
        const stub = window.__fleetStreamStub!;
        // Recorded before anything is answered, so a refused post is still observable as a post.
        stub.executed.push(typeof init?.body === "string" ? init.body : "");
        if (stub.refusal) {
          return Promise.resolve(new Response(JSON.stringify({ error: stub.refusal.error }), {
            status: stub.refusal.status,
            headers: { "content-type": "application/json" },
          }));
        }
        return Promise.resolve(new Response(JSON.stringify({ runId: "run-e2e" }), {
          status: 202,
          headers: { "content-type": "application/json" },
        }));
      }
      if (url.includes("/api/workbench/runs/run-e2e/events")) {
        const body = new ReadableStream<Uint8Array>({
          start(controller) {
            controllers.set(executePath, controller);
            const signal = init?.signal ?? (input instanceof Request ? input.signal : undefined);
            signal?.addEventListener("abort", () => {
              if (controllers.get(executePath) !== controller) return;
              controllers.delete(executePath);
              controller.error(new DOMException("Aborted", "AbortError"));
            });
          },
        });
        opened.push(executePath);
        return Promise.resolve(new Response(body, {
          status: 200,
          headers: { "content-type": "text/event-stream" },
        }));
      }
      const matched = paths.find((path) => url.includes(path));
      if (!matched) return original(input, init);

      const body = new ReadableStream<Uint8Array>({
        start(controller) {
          controllers.set(matched, controller);
          const signal = init?.signal ?? (input instanceof Request ? input.signal : undefined);
          signal?.addEventListener("abort", () => {
            if (controllers.get(matched) !== controller) return;
            controllers.delete(matched);
            controller.error(new DOMException("Aborted", "AbortError"));
          });
        },
      });

      opened.push(matched);
      return Promise.resolve(new Response(body, {
        status: 200,
        headers: { "content-type": "text/event-stream" },
      }));
    };
  }, { paths: [CLONE_PATH, UPLOAD_PATH], executePath: EXECUTE_PATH });

  const push = async (path: string, value: Frame) => {
    const delivered = await page.evaluate(
      ([target, payload]) => window.__fleetStreamStub?.push(target, payload) ?? false,
      [path, `data: ${JSON.stringify(value)}\n\n`] as const,
    );
    expect(delivered, `no open stream for ${path}`).toBe(true);
  };

  return {
    async waitForOpen(path, count = 1) {
      await page.waitForFunction(
        ([target, minimum]) => (window.__fleetStreamStub?.opened ?? []).filter((value) => value === target).length >= minimum,
        [path, count] as const,
        { timeout: 15_000 },
      );
    },
    push,
    async pushAll(path, values) {
      for (const value of values) {
        await push(path, value);
      }
    },
    async close(path) {
      await page.evaluate((target) => window.__fleetStreamStub?.close(target) ?? false, path);
    },
    async executeBodies() {
      const bodies = await page.evaluate(() => window.__fleetStreamStub?.executed ?? []);
      return bodies.map((body) => JSON.parse(body) as Record<string, unknown>);
    },
    async refuseExecute(refusal) {
      await page.evaluate((value) => {
        if (window.__fleetStreamStub) window.__fleetStreamStub.refusal = value;
      }, refusal);
    },
  };
}

/** Skips the overview so a test starts on step 1 without clicking through it. */
export async function startAtSetup(page: Page) {
  await page.addInitScript(() => window.localStorage.setItem("ofm-workbench-intro-v1", "1"));
  const context = await page.request.get("/api/workbench/context");
  expect(context.ok(), await context.text()).toBe(true);
  const projects = (await context.json() as { projects: unknown[] }).projects;
  if (projects.length === 0) {
    const project = await page.request.post("/api/workbench/projects", {
      data: { name: "Guided UI" },
    });
    expect(project.status(), await project.text()).toBe(201);
  }
}

/**
 * Field locators by id.
 *
 * `getByLabel` is unusable for these: every field carries an InfoTip whose `aria-label` contains the
 * field's own label text, so a label lookup matches the input and its help button.
 */
export const field = {
  engagementId: (page: Page) => page.locator("#engagementId"),
  applicationName: (page: Page) => page.locator("#applicationName"),
  outputRoot: (page: Page) => page.locator("#outputRoot"),
  sourceRoot: (page: Page) => page.locator("#sourceRoot"),
  repositoryUrl: (page: Page) => page.locator("#repoUrl"),
};

/** The service navigation and the command bar both offer "Activity"; this is the command bar one. */
export const activityCommand = (page: Page) =>
  page.locator(".mf-commandbar").getByRole("button", { name: "Activity", exact: true });

/** Fills step 1 with values the server accepts, then walks to the review step. */
export async function completeSetup(page: Page, options: { sourceMode?: "manual" | "repo" } = {}) {
  await field.engagementId(page).fill("ENG-0042");
  await field.applicationName(page).fill("ORDERS");
  await field.outputRoot(page).fill("out/orders");

  if (options.sourceMode === "manual" || !options.sourceMode) {
    await page.getByRole("radio", { name: "Describe a folder path" }).check();
    await field.sourceRoot(page).fill("legacy/forms");
  }

  for (let step = 0; step < 4; step += 1) {
    await page.getByRole("button", { name: "Continue" }).click();
  }
  await expect(page.getByRole("heading", { name: "Check the setup, then generate the plan" })).toBeVisible();
}

export async function generatePlan(page: Page) {
  await page.getByRole("button", { name: "Generate migration plan" }).click();
  await expect(page.getByRole("heading", { name: /^Migration plan for/ })).toBeVisible();
}

/** No page-level horizontal scrolling. Overflow inside a designed scroller is allowed. */
export async function expectNoHorizontalOverflow(page: Page) {
  const overflow = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    clientWidth: document.documentElement.clientWidth,
  }));
  expect(overflow.scrollWidth, "page-level horizontal overflow").toBeLessThanOrEqual(overflow.clientWidth + 1);
}
