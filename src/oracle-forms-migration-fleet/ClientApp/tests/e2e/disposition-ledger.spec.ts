import { expect, test, type Page } from "@playwright/test";
import {
  CLONE_PATH,
  EXECUTE_PATH,
  RUN,
  RUN_PURPOSE,
  cloneFrames,
  expectNoHorizontalOverflow,
  field,
  frame,
  installStreamStub,
  startAtSetup,
  type StreamStub,
} from "./fixtures";

/**
 * The back-end target choice and the disposition ledger console.
 *
 * The target-selection tests run against the real host: the value the review screen shows is the value
 * `runRequestBody` puts on the wire, so an assertion there is an assertion about the request.
 *
 * The ledger tests stub the ledger API. That is deliberate and limited: what they prove is that the
 * console renders the server's own counts and refusals, never invents a verification, and puts the
 * operator's chosen ledger on both the approval request and the run request. It proves nothing about
 * whether a ledger can be ingested from a real run; ingestion is covered by the server's own tests.
 */

const RULES = [
  { ruleId: "DR-PRESERVE-DECLARED", version: 1, summary: "The declared value is carried across unchanged.", authorizes: ["Preserve"] },
  { ruleId: "DR-TRANSFORM-EQUIVALENT", version: 1, summary: "The declared value has a documented equivalent.", authorizes: ["Transform"] },
  { ruleId: "DR-RETIRE-NO-TARGET", version: 1, summary: "Oracle Forms runtime machinery with no counterpart.", authorizes: ["Retire"] },
  { ruleId: "DR-DEFER-NEEDS-EVIDENCE", version: 1, summary: "Cannot be disposed of without evidence this run lacks.", authorizes: ["Defer"] },
];

const COUNTS = {
  discovered: 4,
  decided: 1,
  generated: 2,
  verified: 0,
  unresolved: 3,
  deferred: 0,
  failedVerification: 0,
};

const SUMMARY = {
  ledgerId: "L1",
  projectId: "P1",
  runId: "run-e2e",
  sourceSnapshotHash: "abcdef0123456789abcdef0123456789",
  sourceRoot: "legacy/forms",
  createdUtc: "2026-01-01T00:00:00Z",
  createdByObjectId: "oid-1",
  moduleCount: 1,
  counts: COUNTS,
  completion: {
    canComplete: false,
    blockers: ["3 properties are unresolved.", "2 generated properties have never been executed."],
  },
  isStale: false,
  staleReason: null,
};

function entry(overrides: Record<string, unknown> = {}) {
  return {
    ledgerId: "L1",
    entryId: "E1",
    identity: {
      moduleName: "ORDERS_ENTRY",
      filePath: "legacy/forms/ORDERS_ENTRY.xml",
      objectPath: "ORDERS_ENTRY/BLOCK/ORDER_ID",
      objectType: "Item",
      propertyName: "Required",
      behaviorGroup: "Validation",
    },
    observedValue: "true",
    observedValueTruncated: false,
    observedEvidence: "Declared in the module export read by SourceNormalization.",
    decision: "Unresolved",
    rationale: null,
    mappingRuleId: null,
    mappingRuleVersion: 0,
    decidedUtc: null,
    generatedRefs: [{ runId: "run-e2e", artifactPath: "backend/Orders.cs", contentSha256: "aa" }],
    testRefs: [],
    verification: "NotExecuted",
    version: 3,
    ...overrides,
  };
}

/** Stubs the whole ledger surface and returns the decision bodies the console actually posted. */
async function stubLedger(page: Page, options: { ledgers?: unknown[]; listed?: () => unknown[] } = {}) {
  const posted: Array<Record<string, unknown>> = [];
  let ledgers = options.ledgers ?? [SUMMARY];
  let decided = false;

  await page.route("**/api/workbench/projects/*/disposition-ledgers", async (route) => {
    if (route.request().method() === "POST") {
      if (!options.listed) ledgers = [SUMMARY];
      await route.fulfill({
        status: 201,
        contentType: "application/json",
        body: JSON.stringify({ summary: SUMMARY, screens: screens(), rules: RULES }),
      });
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ ledgers: options.listed ? options.listed() : ledgers }),
    });
  });

  await page.route("**/api/workbench/disposition-ledgers/*", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ summary: SUMMARY, screens: screens(), rules: RULES }),
    });
  });

  await page.route("**/api/workbench/disposition-ledgers/*/entries**", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        entries: [decided
          ? entry({ decision: "Preserve", rationale: "Carried across unchanged.", mappingRuleId: "DR-PRESERVE-DECLARED", mappingRuleVersion: 1, version: 4 })
          : entry()],
        total: 1,
        skip: 0,
        take: 25,
      }),
    });
  });

  await page.route("**/api/workbench/disposition-ledgers/*/entries/*/decision", async (route) => {
    const body = route.request().postDataJSON() as Record<string, unknown>;
    posted.push(body);
    if (typeof body.rationale !== "string" || body.rationale.trim().length < 10) {
      await route.fulfill({
        status: 400,
        contentType: "application/json",
        body: JSON.stringify({ error: "A disposition carries a reason of at least 10 characters." }),
      });
      return;
    }
    decided = true;
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(entry({ decision: body.decision, rationale: body.rationale, version: 4 })),
    });
  });

  return posted;
}

function screens() {
  return [{
    moduleName: "ORDERS_ENTRY",
    filePath: "legacy/forms/ORDERS_ENTRY.xml",
    counts: COUNTS,
    groups: [
      { behaviorGroup: "Validation", counts: { ...COUNTS, discovered: 2, decided: 1, unresolved: 1 } },
      { behaviorGroup: "Navigation", counts: { ...COUNTS, discovered: 2, decided: 0, unresolved: 2 } },
    ],
  }];
}

/** Walks the four setup steps to the review screen. */
async function walkToReview(page: Page) {
  for (let step = 0; step < 4; step += 1) {
    await page.getByRole("button", { name: "Continue" }).click();
  }
}

/**
 * Plans, starts a run, and feeds it the server's own completion frame.
 *
 * `opened` is how many execute streams the page should have opened by the end, so a second run in the
 * same test waits for its own stream rather than matching the first one's.
 */
async function planAndRun(page: Page, streams: StreamStub, opened = 1) {
  await page.getByRole("button", { name: "Generate migration plan" }).click();
  const run = page.getByRole("button", { name: "Run authorized phases", exact: true });
  await expect(run).toBeVisible();
  await run.click();
  await streams.waitForOpen(EXECUTE_PATH, opened);

  // Sequence 1 every time: each run replays from the beginning, and the browser reports a gap rather
  // than smoothing one over.
  await streams.push(EXECUTE_PATH, frame({
    sequence: 1,
    level: "done",
    text: "",
    operation: RUN,
    action: "run.completed",
    state: "Completed",
    purpose: RUN_PURPOSE,
    observed: "1 of 1 phase(s) ran.",
    nextAction: "Read the exported source facts.",
    result: {
      requestedMode: "GenerateArtifacts",
      authorizedMode: "GenerateArtifacts",
      outputRoot: ".fleet-run/out/orders",
      phases: [{
        phase: "SourceNormalization",
        plannedStatus: "Planned",
        state: "Executed",
        detail: "Source facts exported.",
        artifacts: [],
        findings: [],
      }],
      artifacts: [],
      attestations: [],
      blockers: [],
    },
  }));
  await streams.close(EXECUTE_PATH);
  await page.getByRole("button", { name: "Close activity" }).click();
}

/** One planned phase, so the review step has something the run button can be offered for. */
async function stubPlan(page: Page) {
  await page.route("**/api/workbench/plan", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        plan: {
          engagementId: "ENG-LEDGER",
          applicationName: "ORDERS",
          requestedMode: "GenerateArtifacts",
          authorizedMode: "GenerateArtifacts",
          target: { frontEnd: "React", backEnd: "JavaSpringBoot", database: "PostgreSql" },
          phases: [{
            phase: "SourceNormalization",
            owner: "DiscoveryAnalyst",
            status: "Planned",
            mutation: "WorkspaceArtifactWrite",
            requiredMode: "GenerateArtifacts",
            requiresApproval: false,
            objective: "Export the source facts.",
            requiredInputs: [],
            expectedOutputs: [],
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
}

/** Fills step 1 and copies a stubbed repository, so a session workspace exists. */
async function cloneStubbedSource(page: Page, streams: StreamStub, engagementId: string) {
  await field.engagementId(page).fill(engagementId);
  await field.applicationName(page).fill("ORDERS");
  await field.outputRoot(page).fill("out/orders");
  await page.getByRole("radio", { name: "Clone a Git repository" }).check();
  await field.repositoryUrl(page).fill("https://github.com/contoso/orders");
  await page.getByRole("button", { name: "Copy this repository" }).click();
  await streams.waitForOpen(CLONE_PATH);
  await streams.pushAll(CLONE_PATH, cloneFrames());
  await streams.close(CLONE_PATH);
  await page.getByRole("button", { name: "Close activity" }).click();
}

/** Clones a stubbed source, walks to the review step, plans, and starts a run so a run id exists. */
async function runToResults(page: Page): Promise<StreamStub> {
  const streams = await installStreamStub(page);
  await stubPlan(page);

  await page.goto("/");
  await cloneStubbedSource(page, streams, "ENG-LEDGER");

  await walkToReview(page);
  await planAndRun(page, streams);
  return streams;
}

test.describe("back-end target selection", () => {
  test.beforeEach(async ({ page }) => {
    await startAtSetup(page);
  });

  test("Java Spring Boot is preselected and is what an untouched run sends", async ({ page }) => {
    await page.goto("/");
    await field.engagementId(page).fill("ENG-DEFAULT");
    await field.applicationName(page).fill("ORDERS");
    await field.outputRoot(page).fill("out/orders");
    await page.getByRole("radio", { name: "Describe a folder path" }).check();
    await field.sourceRoot(page).fill("legacy/forms");
    await page.getByRole("button", { name: "Continue" }).click();

    await expect(page.getByRole("radio", { name: /Java \(Spring Boot\)/ })).toBeChecked();
    await expect(page.getByRole("radio", { name: /\.NET \(ASP\.NET Core\)/ })).not.toBeChecked();

    for (let step = 0; step < 3; step += 1) {
      await page.getByRole("button", { name: "Continue" }).click();
    }
    await page.locator("details.mf-optional > summary").last().click();
    await expect(page.getByTestId("review-backend")).toHaveText("JavaSpringBoot");
  });

  test("choosing .NET changes the value the request carries and the review reports it", async ({ page }) => {
    await page.goto("/");
    await field.engagementId(page).fill("ENG-DOTNET");
    await field.applicationName(page).fill("ORDERS");
    await field.outputRoot(page).fill("out/orders");
    await page.getByRole("radio", { name: "Describe a folder path" }).check();
    await field.sourceRoot(page).fill("legacy/forms");
    await page.getByRole("button", { name: "Continue" }).click();

    await page.getByRole("radio", { name: /\.NET \(ASP\.NET Core\)/ }).check();
    await expect(page.getByText("target.backEnd = AspNetCore")).toBeVisible();

    for (let step = 0; step < 3; step += 1) {
      await page.getByRole("button", { name: "Continue" }).click();
    }
    await expect(page.getByText(".NET (ASP.NET Core)").first()).toBeVisible();
    await page.locator("details.mf-optional > summary").last().click();
    await expect(page.getByTestId("review-backend")).toHaveText("AspNetCore");
  });

  test("a Java-configured target profile is reported as a mismatch, never relabelled", async ({ page }) => {
    await page.goto("/");
    const profileBackEnd = await page.getByTestId("target-stack").first()
      .textContent({ timeout: 5_000 }).catch(() => null);
    test.skip(!profileBackEnd?.includes("JavaSpringBoot"), "This host's project has no Java-configured target profile.");

    await field.engagementId(page).fill("ENG-MISMATCH");
    await field.applicationName(page).fill("ORDERS");
    await field.outputRoot(page).fill("out/orders");
    await page.getByRole("radio", { name: "Describe a folder path" }).check();
    await field.sourceRoot(page).fill("legacy/forms");
    await page.getByRole("button", { name: "Continue" }).click();

    await page.getByRole("radio", { name: /\.NET \(ASP\.NET Core\)/ }).check();
    await expect(page.getByTestId("backend-profile-mismatch")).toContainText("JavaSpringBoot");
  });

  test("a stack the profile does not name stops the run and offers the configured one", async ({ page }) => {
    await page.goto("/");
    const stack = await page.getByTestId("target-stack").first()
      .textContent({ timeout: 5_000 }).catch(() => null);
    test.skip(!stack?.includes("JavaSpringBoot"), "This host's project has no Java-configured target profile.");

    await field.engagementId(page).fill("ENG-BLOCKED");
    await field.applicationName(page).fill("ORDERS");
    await field.outputRoot(page).fill("out/orders");
    await page.getByRole("radio", { name: "Describe a folder path" }).check();
    await field.sourceRoot(page).fill("legacy/forms");
    await page.getByRole("button", { name: "Continue" }).click();
    await page.getByRole("radio", { name: /\.NET \(ASP\.NET Core\)/ }).check();

    for (let step = 0; step < 3; step += 1) {
      await page.getByRole("button", { name: "Continue" }).click();
    }
    // The server answers 409 to both the approval and the run while this differs, so the console says
    // so rather than repeating the old claim that approvals stay adjudicated against the profile.
    const notice = page.getByTestId("stack-mismatch-review");
    await expect(notice).toContainText("back end is fixed at JavaSpringBoot");
    await expect(notice).toContainText("refuses the sandbox approval");

    // The configured destination is applied only on this explicit click, and on the step that owns it.
    await page.getByTestId("use-configured-destination").click();
    await expect(page.getByRole("radio", { name: /Java \(Spring Boot\)/ })).toBeChecked();

    for (let step = 0; step < 3; step += 1) {
      await page.getByRole("button", { name: "Continue" }).click();
    }
    await expect(page.getByTestId("stack-mismatch-review")).toHaveCount(0);
  });

  test("a stack the profile does not name also refuses the sandbox approval", async ({ page }) => {
    const streams = await installStreamStub(page);
    await stubPlan(page);
    await page.goto("/");

    const stack = await page.getByTestId("target-stack").first()
      .textContent({ timeout: 5_000 }).catch(() => null);
    test.skip(!stack?.includes("JavaSpringBoot"), "This host's project has no Java-configured target profile.");

    await cloneStubbedSource(page, streams, "ENG-BLOCKED-APPROVAL");
    await page.getByRole("button", { name: "Continue" }).click();
    await page.getByRole("radio", { name: /\.NET \(ASP\.NET Core\)/ }).check();
    for (let step = 0; step < 3; step += 1) {
      await page.getByRole("button", { name: "Continue" }).click();
    }
    await page.getByRole("button", { name: "Generate migration plan" }).click();

    await expect(page.getByTestId("request-approval")).toBeDisabled();
    await expect(page.getByTestId("approval-stack-mismatch")).toContainText("back end is fixed at JavaSpringBoot");
    await expect(page.getByRole("button", { name: "Run authorized phases", exact: true })).toHaveCount(0);
    await expect(page.getByTestId("stack-mismatch-results")).toContainText("Nothing can start against this destination.");
  });
});

test.describe("disposition ledger console", () => {
  test.beforeEach(async ({ page }) => {
    await startAtSetup(page);
  });

  test("discovered, decided, generated and verified are reported separately", async ({ page }) => {
    await stubLedger(page);
    await runToResults(page);

    const ledger = page.getByTestId("disposition-ledger");
    await expect(ledger).toBeVisible();
    await expect(page.getByTestId("ledger-discovered")).toHaveText("4");
    await expect(page.getByTestId("ledger-decided")).toHaveText("1");
    await expect(page.getByTestId("ledger-generated")).toHaveText("2");
    await expect(page.getByTestId("ledger-verified")).toHaveText("0");
    await expect(page.getByTestId("ledger-unresolved")).toHaveText("3");
    await expect(ledger).toContainText("This ledger cannot be reported complete.");
  });

  test("a ledger the server has not built is offered rather than invented", async ({ page }) => {
    await stubLedger(page, { ledgers: [] });
    await runToResults(page);

    await expect(page.getByText("No disposition ledger has been built from this run yet.")).toBeVisible();
    await page.getByTestId("build-ledger").click();
    await expect(page.getByTestId("ledger-discovered")).toHaveText("4");
  });

  test("a generated but unexecuted property is shown as not executed", async ({ page }) => {
    await stubLedger(page);
    await runToResults(page);

    await page.locator(".mf-ledger-screen > summary").click();
    await expect(page.getByTestId("entry-verification")).toHaveText("Not executed");
    await expect(page.locator(".mf-ledger-facts").getByText("None executed")).toBeVisible();
  });

  test("a disposition cannot be recorded without a rationale and a rule that authorizes it", async ({ page }) => {
    const posted = await stubLedger(page);
    await runToResults(page);

    await page.locator(".mf-ledger-screen > summary").click();
    // Located by test id: a wrapping label's text content includes the select's own option text, so
    // getByLabel cannot address these controls.
    const decide = page.locator(".mf-ledger-decide");
    const record = decide.getByRole("button", { name: "Record disposition" });
    await expect(record).toBeDisabled();

    await decide.getByTestId("entry-decision").selectOption("Preserve");
    await expect(record).toBeDisabled();

    // Only the rule that authorizes Preserve is offered.
    const rules = decide.getByTestId("entry-rule");
    await expect(rules.locator("option")).toHaveCount(2);
    await rules.selectOption("DR-PRESERVE-DECLARED@1");
    await expect(record).toBeDisabled();

    await decide.getByTestId("entry-rationale").fill("The declared requirement is carried across unchanged.");
    await expect(record).toBeEnabled();
    await record.click();

    await expect.poll(() => posted.length).toBe(1);
    expect(posted[0]).toMatchObject({
      decision: "Preserve",
      mappingRuleId: "DR-PRESERVE-DECLARED",
      mappingRuleVersion: 1,
      expectedVersion: 3,
    });
    // Nothing a caller may not set is sent.
    expect(Object.keys(posted[0]).sort()).toEqual(
      ["decision", "expectedVersion", "mappingRuleId", "mappingRuleVersion", "rationale"]);
  });

  test("a refused disposition shows the server's reason and records nothing", async ({ page }) => {
    await stubLedger(page);
    // Registered after the base stub so it wins: Playwright matches routes in reverse order.
    await page.route("**/api/workbench/disposition-ledgers/*/entries/*/decision", async (route) => {
      await route.fulfill({
        status: 409,
        contentType: "application/json",
        body: JSON.stringify({ error: "This property changed since you read it. Re-read the ledger and decide again." }),
      });
    });
    await runToResults(page);

    await page.locator(".mf-ledger-screen > summary").click();
    const decide = page.locator(".mf-ledger-decide");
    await decide.getByTestId("entry-decision").selectOption("Transform");
    await decide.getByTestId("entry-rule").selectOption("DR-TRANSFORM-EQUIVALENT@1");
    await decide.getByTestId("entry-rationale").fill("A documented equivalent exists on the target stack.");
    await decide.getByRole("button", { name: "Record disposition" }).click();

    await expect(page.locator(".mf-ledger-screen .mf-error")).toContainText("This property changed since you read it.");
    await expect(page.getByTestId("entry-verification")).toHaveText("Not executed");
  });
});

/** A superseded copy of the stubbed summary, as the server would report one. */
const STALE = {
  ...SUMMARY,
  ledgerId: "L2",
  runId: "run-older",
  isStale: true,
  staleReason: "This ledger describes a superseded source snapshot.",
};

/** Accepts the approval post and records the body, so the request can be compared with the run's. */
async function captureApproval(page: Page) {
  const requested: Array<Record<string, unknown>> = [];
  await page.route("**/api/workbench/projects/*/approvals", async (route) => {
    if (route.request().method() !== "POST") {
      await route.continue();
      return;
    }
    requested.push(route.request().postDataJSON() as Record<string, unknown>);
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({
        approvalId: "apr-e2e",
        projectId: "P1",
        state: "Requested",
        scope: "SandboxDatabaseWrite",
        engagementId: "ENG-LEDGER",
        requestedByObjectId: "playwright-operator",
        requestedUtc: new Date().toISOString(),
        decidedByObjectId: null,
        revokedByObjectId: null,
        expiresUtc: new Date(Date.now() + 3_600_000).toISOString(),
        requestNotes: null,
        targetProfileId: "sandbox",
        targetProfileVersion: 1,
        version: 1,
        isRequester: true,
        canDecide: false,
        canRevoke: true,
        isEffective: false,
      }),
    });
  });
  return requested;
}

test.describe("decisions the next run generates under", () => {
  test.beforeEach(async ({ page }) => {
    await startAtSetup(page);
  });

  test("nothing is selected until the operator selects it", async ({ page }) => {
    await stubLedger(page);
    await runToResults(page);

    const select = page.getByTestId("run-ledger-select");
    await expect(select).toBeVisible();
    // A ledger exists and a run has finished, and still nothing was chosen on the operator's behalf.
    await expect(select).toHaveValue("");
    await expect(page.getByTestId("ledger-selection-summary")).toHaveCount(0);
  });

  test("the chosen ledger reaches the approval request and the run request as the same value", async ({ page }) => {
    await stubLedger(page);
    const approvals = await captureApproval(page);
    const streams = await runToResults(page);

    // The first run carried no ledger: one is only selectable after the run that produced it.
    const before = await streams.executeBodies();
    expect(before).toHaveLength(1);
    expect(before[0].dispositionLedgerId).toBeNull();

    await page.getByTestId("run-ledger-select").selectOption("L1");
    await expect(page.getByTestId("ledger-selection-summary")).toContainText("L1");

    await page.getByTestId("request-approval").click();
    await expect.poll(() => approvals.length).toBe(1);
    const approvalRequest = approvals[0].request as Record<string, unknown>;
    expect(approvalRequest.dispositionLedgerId).toBe("L1");

    // Back through the wizard and run again. The same body builds both requests, so the second run has
    // to carry the identical locator; a run generating under decisions nobody approved is the defect.
    await page.locator("#workspace").getByRole("button", { name: "Edit setup", exact: true }).click();
    await walkToReview(page);
    await page.locator("details.mf-optional > summary").last().click();
    await expect(page.getByTestId("review-ledger")).toHaveText("L1");
    await planAndRun(page, streams, 2);

    const after = await streams.executeBodies();
    expect(after).toHaveLength(2);
    expect(after[1].dispositionLedgerId).toBe("L1");

    // Every field the server hashes into the run inputs is identical across the two requests.
    for (const key of Object.keys(approvalRequest)) {
      expect({ key, value: after[1][key] }).toEqual({ key, value: approvalRequest[key] });
    }
  });

  test("a superseded ledger is not offered and is reported as withheld", async ({ page }) => {
    await stubLedger(page, { ledgers: [SUMMARY, STALE] });
    await runToResults(page);

    const select = page.getByTestId("run-ledger-select");
    // The empty choice and L1 only. L2 is the server's superseded one and is never selectable.
    await expect(select.locator("option")).toHaveCount(2);
    expect(await select.locator("option").evaluateAll((options) =>
      options.map((option) => (option as HTMLOptionElement).value))).toEqual(["", "L1"]);
    await expect(page.getByTestId("ledger-superseded-count")).toContainText("1 ledger is not offered");
  });

  test("a selection the server stops listing is dropped, with its reason left on screen", async ({ page }) => {
    let superseded = false;
    await stubLedger(page, {
      listed: () => [{
        ...SUMMARY,
        isStale: superseded,
        staleReason: superseded ? "This ledger describes a superseded source snapshot." : null,
      }],
    });
    await runToResults(page);

    await page.getByTestId("run-ledger-select").selectOption("L1");
    await expect(page.getByTestId("ledger-selection-summary")).toContainText("L1");

    superseded = true;
    await page.getByRole("button", { name: "Refresh the disposition ledger" }).click();

    await expect(page.getByTestId("ledger-selection-invalid")).toContainText("superseded source snapshot");
    await expect(page.getByTestId("run-ledger-select")).toHaveValue("");
  });

  test("the server's refusal to bind the ledger is reported and no run is reported as started", async ({ page }) => {
    await stubLedger(page);
    const streams = await runToResults(page);

    await page.getByTestId("run-ledger-select").selectOption("L1");
    await streams.refuseExecute({
      status: 409,
      error: "That ledger records decisions about a different source snapshot than this run would read, so they are not " +
        "decisions about what it would generate. Record a ledger from a run over this source, or start this run over the " +
        "source those decisions were made against.",
    });

    await page.locator("#workspace").getByRole("button", { name: "Edit setup", exact: true }).click();
    await walkToReview(page);
    await page.getByRole("button", { name: "Generate migration plan" }).click();
    await page.getByRole("button", { name: "Run authorized phases", exact: true }).click();

    await expect(page.locator(".mf-next-action .mf-error"))
      .toContainText("a different source snapshot than this run would read");
    // The refusal is the server's words, and nothing is offered as output of a run that never started.
    await expect(page.getByRole("link", { name: "Download what this run generated" })).toHaveCount(0);
  });

  test("the choice is labelled, described, and operable from the keyboard", async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== "desktop-chromium", "Native select key handling is a desktop assertion.");
    await stubLedger(page);
    await runToResults(page);

    const select = page.getByTestId("run-ledger-select");
    await expect(select).toHaveAccessibleName(/Decisions the next run generates under/);
    await expect(select).toHaveAccessibleDescription(/approval request and on the run request/);

    await select.focus();
    await expect(select).toBeFocused();
    await page.keyboard.press("ArrowDown");
    await expect(select).toHaveValue("L1");
  });

  test("the ledger console fits a small screen without page-level horizontal scrolling", async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== "mobile-chromium", "This is the small-viewport check.");
    await stubLedger(page);
    await runToResults(page);

    await expect(page.getByTestId("run-ledger-select")).toBeVisible();
    await page.getByTestId("run-ledger-select").selectOption("L1");
    await expect(page.getByTestId("ledger-selection-summary")).toBeVisible();
    await expectNoHorizontalOverflow(page);
  });
});
