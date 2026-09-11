# Changelog

Notable changes to behaviour, with the reasoning that is not visible in a diff.

## Unreleased

### Fixed

**Build descriptors were counted as Oracle Forms XML exports.** `SourceInventory` classified any `.xml`
whose *path* contained `form`, so in a repository with a directory named `OracleFormsTester`, Ant's
`build.xml` was reported as a Forms2XML export. That is not cosmetic: detected artifacts tick evidence
boxes, and evidence gates whether artifact generation is authorized, so the planner was being handed
evidence that did not exist. Classification now matches on the file name or an exact forms directory
segment, and ignores a list of well-known build and framework descriptors. Two tests cover it — one for
the misclassification, one asserting genuine Forms XML is *still* detected, so the fix cannot silently
become "the rule is off".

**Auto-detected evidence ticks were never cleared.** Ticks derived from a copied source were only ever
added to the operator's selections and persisted to local storage, so ticks from an earlier, unrelated
source survived into a new session and could satisfy the generate-versus-plan-only gate on their own.
Detected ticks now carry provenance: they are bound to the live workspace and retired when the source is
replaced or discarded, while ticks the operator made themselves persist untouched. Detected kinds are
also de-duplicated before being counted, so three files of one kind no longer claim three answers.

**An expired sign-in surfaced as "Failed to fetch".** When the auth cookie lapsed, the API call was
answered with a redirect to the login service, which the browser blocked as cross-origin. That rejects
the `fetch` promise itself, so the `response.ok` check never ran and the raw `TypeError` reached the UI.
The client now distinguishes that case and tells the operator their session expired and to reload, and
also treats 401 and 403 the same way.

### Known limitation

**The repository subfolder is not applied to the clone.** The folder entered on step 1 reaches the
*plan*, but never reaches the clone request, so the whole repository is copied and inventoried. In
testing, entering `demoapp` still copied 100 files. The detected `sourceRoot` was nonetheless correct,
because it is derived from where the Forms files actually live rather than from the entered value.
Closing this properly means extending the clone contract and adding sparse checkout; it was left open
deliberately rather than changed unilaterally.

### Verified

Exercised end to end against a real third-party repository (`v-p-b/oracle_forms`) rather than a
synthetic fixture. That repository is a security-testing toolkit rather than a Forms application — about
100 Java files of Burp interface stubs surrounding a single genuine `demoapp/` — which made it a useful
adversarial test, and is what exposed the misclassification above.

The safety gate behaved correctly: asked for **Generate artifacts**, the workbench authorized **Plan
Only** and cited 11 blockers naming the specific missing evidence (`DatabaseSchemaExport`,
`TestBaseline`).
