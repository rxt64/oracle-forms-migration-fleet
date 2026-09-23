// Copyright (c) Microsoft. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The trusted builder's own gates, and the host configuration that reaches it.
///
/// The workflow is asserted because its security properties — which job holds a cloud credential, which
/// recipe is built, whether the revision is pinned to a digest — are decisions that live nowhere else, and
/// a workflow is the one part of this product that no other test would execute. Deleting the split between
/// the credentialed and uncredentialed jobs is the single change that would most quietly undo the
/// isolation.
///
/// It is read as structure and as commands, never as text. An earlier version of this class matched
/// substrings against the whole file, and three of its assertions were satisfied by prose: a sentence in a
/// comment saying `docker load` happened, a `cmp` that had been moved to another job, and a registry-side
/// build that no longer existed. A test that a comment can satisfy is a test a comment can also break, and
/// worse, one that keeps passing after the step it was protecting is gone.
/// </summary>
public sealed class GeneratedTargetWorkflowTests
{
    private static readonly string s_workflowText = RepositoryText(".github/workflows/generated-target.yml");
    private static readonly YamlNode s_workflow = YamlNode.Parse(s_workflowText);
    private static readonly string s_dockerfile = RepositoryText(".github/generated-target/Dockerfile");
    private static readonly string s_dockerignore = RepositoryText(".github/generated-target/.dockerignore");
    private static readonly string s_pilotTemplate = RepositoryText("infra/dotnet-pilot/main.bicep");

    private static YamlNode Job(string name)
    {
        YamlNode? job = s_workflow["jobs"]?[name];
        Assert.True(job is not null, $"The workflow has no '{name}' job.");
        return job!;
    }

    private static IReadOnlyList<YamlNode> Steps(string job) => Job(job)["steps"]?.Sequence ?? [];

    /// <summary>The name a step is identified by: its own <c>name</c>, else the action it uses.</summary>
    private static string StepLabel(YamlNode step) => step["name"]?.Text ?? step["uses"]?.Text ?? string.Empty;

    private static YamlNode Step(string job, string nameFragment)
    {
        YamlNode? step = Steps(job).FirstOrDefault(
            candidate => StepLabel(candidate).Contains(nameFragment, StringComparison.Ordinal));

        Assert.True(step is not null, $"The '{job}' job has no step named like '{nameFragment}'.");
        return step!;
    }

    private static int StepIndex(string job, string nameFragment)
    {
        YamlNode target = Step(job, nameFragment);
        return Steps(job).ToList().FindIndex(candidate => ReferenceEquals(candidate, target));
    }

    /// <summary>
    /// The lines of a shell body that are commands. Blank lines and whole-line comments are dropped, so an
    /// assertion about what a step runs cannot be satisfied by a sentence describing what it runs.
    /// </summary>
    private static IReadOnlyList<string> CommandLines(string script) =>
    [
        .. script.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#')),
    ];

    private static IReadOnlyList<string> Commands(string job, string nameFragment) =>
        CommandLines(Step(job, nameFragment)["run"]?.Text ?? string.Empty);

    /// <summary>Every command line of every <c>run</c> step in a job, in order.</summary>
    private static IReadOnlyList<string> Commands(string job) =>
    [
        .. Steps(job).SelectMany(step => CommandLines(step["run"]?.Text ?? string.Empty)),
    ];

    private static bool Runs(IReadOnlyList<string> commands, string program) =>
        commands.Any(line => Invokes(line, program));

    /// <summary>
    /// Whether a line invokes a program, rather than merely mentioning it. A command may open a line, or
    /// follow a pipe, a separator, a redirection of control, or a command substitution.
    /// </summary>
    private static bool Invokes(string line, string program)
    {
        foreach (string fragment in line.Split(['|', ';', '&', '(', '`'], StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = fragment.Trim();
            candidate = candidate.StartsWith("$(", StringComparison.Ordinal) ? candidate[2..].Trim() : candidate;
            candidate = candidate.StartsWith("! ", StringComparison.Ordinal) ? candidate[2..].Trim() : candidate;

            if (candidate.Equals(program, StringComparison.Ordinal) ||
                candidate.StartsWith(program + " ", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int LineOf(IReadOnlyList<string> commands, string fragment) =>
        commands.ToList().FindIndex(line => line.Contains(fragment, StringComparison.Ordinal));

    [Fact]
    public void The_workflow_is_dispatched_by_the_product_and_never_by_a_branch_push()
    {
        // Structural, so a `push:` trigger cannot hide behind an assertion about the absence of a word.
        Assert.Equal(["workflow_dispatch"], s_workflow["on"]!.Keys);

        // No job inherits a token it was not granted. Everything below is then an explicit decision.
        Assert.Empty(s_workflow["permissions"]!.ScalarFields);
    }

    [Fact]
    public void Every_dispatch_input_the_product_binds_is_declared_and_required()
    {
        YamlNode inputs = s_workflow["on"]!["workflow_dispatch"]!["inputs"]!;

        Assert.Equal(
            [
                "approval_expires_utc", "artifact_sha256", "artifact_uri", "binding_digest", "operation_id",
                "plan_digest", "run_id", "source_snapshot_sha256", "target", "target_digest",
                "target_resource_id", "workbench_sha",
            ],
            inputs.Keys.Order(StringComparer.Ordinal));

        foreach (string name in inputs.Keys)
        {
            Assert.Equal("true", inputs[name]!["required"]!.Text);
            Assert.Equal("string", inputs[name]!["type"]!.Text);
        }
    }

    /// <summary>
    /// The one input that becomes an outbound request. Loose handling here is a server-side request forgery
    /// with a build agent behind it, so the workflow re-applies the product's allowlist rather than
    /// trusting that the caller already did.
    /// </summary>
    [Fact]
    public void The_artifact_location_is_re_checked_inside_the_workflow()
    {
        IReadOnlyList<string> commands = Commands("fetch", "well-formed dispatch");

        Assert.Equal("${{ inputs.artifact_uri }}", Step("fetch", "well-formed dispatch")["env"]!["ARTIFACT_URI"]!.Text);
        Assert.Contains(commands, line => line.Contains("https://*", StringComparison.Ordinal));
        Assert.Contains(commands, line => line.Contains("*'?'*", StringComparison.Ordinal));
        Assert.Contains(commands, line => line.Contains("$ALLOWED_ARTIFACT_HOST_SUFFIX", StringComparison.Ordinal));
        Assert.Contains(commands, line => line.Contains("\"$host\" != *\":\"*", StringComparison.Ordinal));
        Assert.Equal(".blob.core.windows.net", s_workflow["env"]!["ALLOWED_ARTIFACT_HOST_SUFFIX"]!.Text);
    }

    /// <summary>
    /// A dispatch input is attacker-shaped data arriving in a job that holds an Azure token, and the host
    /// and path derived from it are written to <c>$GITHUB_ENV</c>. A newline in the URI would append the
    /// dispatcher's own assignments to the environment of every later step in that job, which is how a
    /// validated input becomes an unvalidated one. The character allowlist has to run before the write.
    /// </summary>
    [Fact]
    public void A_dispatch_input_cannot_append_assignments_to_the_environment_of_a_credentialed_job()
    {
        IReadOnlyList<string> commands = Commands("fetch", "well-formed dispatch");

        int allowlisted = LineOf(commands, "[[ \"$ARTIFACT_URI\" =~ ^[A-Za-z0-9._~%:/-]+$ ]]");
        int bounded = LineOf(commands, "${#ARTIFACT_URI}");
        int written = LineOf(commands, ">> \"$GITHUB_ENV\"");

        Assert.True(allowlisted >= 0, "artifact_uri is not reduced to a character allowlist.");
        Assert.True(bounded >= 0, "artifact_uri has no length bound.");
        Assert.True(written >= 0, "The fetch job no longer writes the parsed location to $GITHUB_ENV.");
        Assert.True(allowlisted < written, "The character allowlist must run before anything is exported.");
        Assert.True(bounded < written, "The length bound must run before anything is exported.");

        // The bound is a separate test rather than a regex repetition: `{n,m}` past RE_DUP_MAX fails the
        // match instead of the input, which turns a guard into an outage.
        Assert.DoesNotContain(commands, line =>
            line.Contains("ARTIFACT_URI", StringComparison.Ordinal) &&
            line.Contains("]+$ ]]", StringComparison.Ordinal) is false &&
            line.Contains(",2048}$", StringComparison.Ordinal));
    }

    [Fact]
    public void The_bundle_is_pinned_to_the_digest_the_workbench_recorded()
    {
        IReadOnlyList<string> download = Commands("fetch", "pin it to the digest");
        Assert.True(Runs(download, "az"), "The bundle is no longer downloaded by the credentialed job.");
        Assert.Contains(download, line => line.Contains("sha256sum bundle/generated-application.zip", StringComparison.Ordinal));
        Assert.Contains(download, line => line.Contains("\"$actual\" != \"$ARTIFACT_SHA256\"", StringComparison.Ordinal));

        // Re-checked after the artifact crosses a job boundary, so a swap between jobs fails too.
        Assert.Contains(
            Commands("build", "Stage the generated tree"),
            line => line.Contains("\"$actual\" == \"$ARTIFACT_SHA256\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// The isolation this whole workflow is shaped around: the job that compiles and runs code generated
    /// from a customer estate holds no cloud credential at all.
    /// </summary>
    [Fact]
    public void The_job_that_runs_generated_code_holds_no_azure_credential()
    {
        YamlNode build = Job("build");

        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["contents"] = "read" },
            build["permissions"]!.ScalarFields);

        Assert.DoesNotContain(
            Steps("build"),
            step => step["uses"]?.Text.StartsWith("azure/", StringComparison.Ordinal) == true);

        Assert.DoesNotContain(Commands("build"), line => Invokes(line, "az"));

        foreach (YamlNode step in Steps("build"))
        {
            Assert.DoesNotContain(
                step["env"]?.ScalarFields ?? new Dictionary<string, string>(StringComparer.Ordinal),
                entry => entry.Key.StartsWith("AZURE_", StringComparison.Ordinal) ||
                         entry.Value.Contains("AZURE_CLIENT_ID", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The change that makes the split above mean anything: the image itself is produced in the
    /// uncredentialed job and leaves as a tar.
    ///
    /// A registry-side build, or a `docker build` in the credentialed job, would execute the Dockerfile —
    /// and therefore `npm ci` and `dotnet publish` over generated code — inside a job holding an Azure
    /// token. That is the isolation undone, however carefully the rest of the workflow is written. So it
    /// is asserted over every job, by what the jobs run, rather than over the job that has it today.
    /// </summary>
    [Fact]
    public void An_image_is_only_ever_built_by_a_job_that_holds_no_credential()
    {
        IReadOnlyList<string> build = Commands("build");
        Assert.Contains(build, line => line.Contains("docker build ", StringComparison.Ordinal));
        Assert.Contains(build, line => line.Contains("docker save ", StringComparison.Ordinal));

        foreach (string name in s_workflow["jobs"]!.Keys)
        {
            bool credentialed =
                Job(name)["permissions"]?.ScalarFields.ContainsKey("id-token") == true ||
                Steps(name).Any(step => step["uses"]?.Text.StartsWith("azure/login", StringComparison.Ordinal) == true);

            IReadOnlyList<string> commands = Commands(name);
            bool builds =
                commands.Any(line => line.Contains("docker build", StringComparison.Ordinal)) ||
                commands.Any(line => line.Contains("acr build", StringComparison.Ordinal)) ||
                commands.Any(line => line.Contains("acr task", StringComparison.Ordinal));

            Assert.False(credentialed && builds, $"The '{name}' job both holds a credential and builds an image.");
        }
    }

    /// <summary>
    /// Nothing out of the artifact executes while a token exists. `publish` receives an opaque archive, so
    /// it has no reason to run a toolchain, and running one would mean it was building something.
    /// </summary>
    [Fact]
    public void The_credentialed_job_runs_no_generated_code_and_no_toolchain()
    {
        IReadOnlyList<string> publish = Commands("publish");

        Assert.DoesNotContain(publish, line => Invokes(line, "npm"));
        Assert.DoesNotContain(publish, line => Invokes(line, "dotnet"));
        Assert.DoesNotContain(publish, line => Invokes(line, "python3"));
        Assert.DoesNotContain(publish, line => line.Contains("stage/app", StringComparison.Ordinal));

        Assert.DoesNotContain(
            Steps("publish"),
            step => step["uses"]?.Text.Contains("setup-dotnet", StringComparison.Ordinal) == true ||
                    step["uses"]?.Text.Contains("setup-node", StringComparison.Ordinal) == true);

        Assert.Contains(publish, line => line.Contains("docker load", StringComparison.Ordinal));
        Assert.Contains(publish, line => line.Contains("docker push", StringComparison.Ordinal));
    }

    /// <summary>
    /// The archive crosses a job boundary, so it is re-digested before it is opened, and its entry names
    /// are inspected before `docker load` reads it as an image.
    ///
    /// Ordered by step and by command line. The prose above `docker load` names the command it is about,
    /// so a text search finds the comment first and concludes the load happens before the checks that the
    /// comment exists to explain.
    /// </summary>
    [Fact]
    public void The_image_archive_is_verified_and_its_entries_are_scanned_before_it_is_loaded()
    {
        IReadOnlyList<string> verification = Commands("publish", "Verify the archive");

        Assert.True(LineOf(verification, "\"$ACTUAL\" != \"$EXPECTED\"") >= 0, "The archive is not re-digested.");
        Assert.True(LineOf(verification, "tar -tf image/generated-target.tar") >= 0, "The entry names are not listed.");
        Assert.True(LineOf(verification, "Refused archive entry") >= 0, "No entry name is refused.");

        int verified = StepIndex("publish", "Verify the archive");
        int loaded = StepIndex("publish", "Load, tag, and push");
        Assert.True(verified < loaded, "The archive must be verified in an earlier step than the one that loads it.");

        // And no earlier step loaded it anyway.
        foreach (YamlNode step in Steps("publish").Take(loaded))
        {
            Assert.DoesNotContain(
                CommandLines(step["run"]?.Text ?? string.Empty),
                line => line.Contains("docker load", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Only_the_fetch_and_publish_jobs_are_credentialed_and_publish_runs_after_the_tests()
    {
        Dictionary<string, string> credentialed = new(StringComparer.Ordinal)
        {
            ["contents"] = "read",
            ["id-token"] = "write",
        };

        Assert.Equal(credentialed, Job("fetch")["permissions"]!.ScalarFields);
        Assert.Equal(credentialed, Job("publish")["permissions"]!.ScalarFields);

        // Ordering, not just presence: the credentialed publish is gated behind the uncredentialed build.
        Assert.Equal("fetch", Job("build")["needs"]!.Text);
        Assert.Equal("build", Job("publish")["needs"]!.Text);
    }

    /// <summary>
    /// The generated tier ships its own Dockerfile and deployment manifest. Building either would let the
    /// estate under migration choose what runs on the builder, so both are stripped and the host's recipe
    /// is staged in their place — in the build job, which is the only job that ever executes a Dockerfile.
    /// </summary>
    [Fact]
    public void The_image_is_built_from_the_host_recipe_and_never_from_the_artifact()
    {
        IReadOnlyList<string> staging = Commands("build", "Stage the generated tree");
        Assert.Contains(staging, line => line.Contains("find stage/app -iname 'Dockerfile' -delete", StringComparison.Ordinal));
        Assert.Contains(staging, line => line.Contains("rm -rf stage/app/deploy", StringComparison.Ordinal));

        IReadOnlyList<string> assembly = Commands("build", "host-owned recipe");
        Assert.Contains(assembly, line => line.Contains("cp .github/generated-target/Dockerfile stage/Dockerfile", StringComparison.Ordinal));
        Assert.Contains(assembly, line => line.Contains("The generated tree still carries a Dockerfile", StringComparison.Ordinal));

        // The recipe is named explicitly, so a Dockerfile anywhere in the context cannot be picked up.
        Assert.Contains(
            Commands("build", "Build the container image"),
            line => line.Contains("--file stage/Dockerfile", StringComparison.Ordinal));

        // And the credentialed job has no part in any of it.
        Assert.DoesNotContain(Commands("publish"), line => line.Contains("generated-target/Dockerfile", StringComparison.Ordinal));
    }

    /// <summary>
    /// Why the recipe is checked against a pinned digest rather than against itself.
    ///
    /// By the time the recipe is staged, the estate's MSBuild targets and test code have already run as
    /// this user in this workspace, so every file the runner owns is suspect — including the recipe. A
    /// `cmp` of the staged copy against its own source compares a tampered file with itself and passes.
    /// The digests are declared on the step, not on the job, because a step's own env outranks anything an
    /// earlier step appended to `$GITHUB_ENV`, and an earlier step in this job ran the estate's code.
    /// </summary>
    [Fact]
    public void The_host_recipe_is_verified_against_a_digest_the_generated_tree_cannot_rewrite()
    {
        YamlNode step = Step("build", "host-owned recipe");
        IReadOnlyDictionary<string, string> pinned = step["env"]!.ScalarFields;

        Assert.Equal(Sha256OfCheckout(s_dockerfile), pinned["EXPECTED_DOCKERFILE_SHA256"]);
        Assert.Equal(Sha256OfCheckout(s_dockerignore), pinned["EXPECTED_DOCKERIGNORE_SHA256"]);

        // On the step, so a $GITHUB_ENV write from an earlier step cannot shadow the reference.
        Assert.Null(Job("build")["env"]);
        Assert.Null(s_workflow["env"]!["EXPECTED_DOCKERFILE_SHA256"]);

        IReadOnlyList<string> commands = Commands("build", "host-owned recipe");
        int checkedAt = LineOf(commands, "sha256sum --check");
        int copiedAt = LineOf(commands, "cp .github/generated-target/Dockerfile");
        Assert.True(checkedAt >= 0, "The staged recipe is not checked against the pinned digests.");
        Assert.True(checkedAt < copiedAt, "The recipe must be verified before it is staged.");

        // A comparison of the copy against its own source proves nothing here and must not come back.
        Assert.DoesNotContain(commands, line => Invokes(line, "cmp"));

        // The pin is only needed because generated code ran first. If that stopped being true the
        // reasoning above would change, so the ordering is asserted rather than assumed.
        Assert.True(
            StepIndex("build", "Build and test the generated API") < StepIndex("build", "host-owned recipe"),
            "The recipe is staged before the generated tests run; re-derive why the pin is needed.");
    }

    [Fact]
    public void The_archive_is_expanded_without_honouring_traversal_absolute_or_link_entries()
    {
        IReadOnlyList<string> staging = Commands("build", "Stage the generated tree");

        Assert.Contains(staging, line => line.Contains("Refused traversal entry", StringComparison.Ordinal));
        Assert.Contains(staging, line => line.Contains("Refused absolute or windows-rooted entry", StringComparison.Ordinal));
        Assert.Contains(staging, line => line.Contains("Refused symbolic link entry", StringComparison.Ordinal));

        // Entry by entry, so nothing decides where an entry lands except this code.
        Assert.DoesNotContain(staging, line => Invokes(line, "unzip"));
        Assert.DoesNotContain(staging, line => Invokes(line, "tar"));
    }

    [Fact]
    public void Package_scripts_from_the_generated_tree_are_never_executed()
    {
        Assert.Contains(
            Commands("build", "generated browser client"),
            line => line.Contains("npm ci --ignore-scripts", StringComparison.Ordinal));

        Assert.Contains("npm ci --ignore-scripts", s_dockerfile, StringComparison.Ordinal);
    }

    /// <summary>A tag can be moved after it was verified; a digest cannot. Only a digest is deployed.</summary>
    [Fact]
    public void The_revision_is_pinned_to_the_digest_the_registry_returned()
    {
        IReadOnlyList<string> push = Commands("publish", "Load, tag, and push");
        Assert.Contains(push, line => line.Contains(@"[[ ""$DIGEST"" =~ ^sha256:[0-9a-f]{64}$ ]]", StringComparison.Ordinal));
        Assert.Contains(push, line => line.Contains("IMAGE_REF=$REGISTRY.azurecr.io/$IMAGE@$DIGEST", StringComparison.Ordinal));

        IReadOnlyList<string> deploy = Commands("publish", "Deploy the revision");
        Assert.Contains(deploy, line => line.Contains("--image \"$IMAGE_REF\"", StringComparison.Ordinal));
        Assert.Contains(deploy, line => line.Contains("The revision is running $DEPLOYED, not $IMAGE_REF", StringComparison.Ordinal));
    }

    /// <summary>
    /// `az acr build --no-logs` exits zero even when the build failed, so the exit code alone is not a
    /// result. That was learned the hard way on the workbench image. The lesson is kept, but the shape it
    /// was pinned in is gone: there is no registry-side build here any more, because building in the
    /// registry would mean building under a credential. So the assertion is that no result anywhere in
    /// this workflow is inferred from an exit code — each is read back from the thing that holds it.
    /// </summary>
    [Fact]
    public void No_outcome_is_inferred_from_an_exit_code()
    {
        foreach (string name in s_workflow["jobs"]!.Keys)
        {
            Assert.DoesNotContain(Commands(name), line => line.Contains("acr build", StringComparison.Ordinal));
            Assert.DoesNotContain(Commands(name), line => line.Contains("--no-logs", StringComparison.Ordinal));
        }

        // The push: what the daemon says it sent, against what the registry says it holds.
        IReadOnlyList<string> push = Commands("publish", "Load, tag, and push");
        Assert.Contains(push, line => line.Contains("az acr manifest show-metadata", StringComparison.Ordinal));
        Assert.Contains(push, line => line.Contains(@"[[ ""$PUSHED"" == ""$DIGEST"" ]]", StringComparison.Ordinal));

        // The deployment: the image is read back off the revision rather than assumed from a zero exit.
        Assert.Contains(
            Commands("publish", "Deploy the revision"),
            line => line.Contains("az containerapp show", StringComparison.Ordinal));

        // And the application: answered, not merely updated.
        Assert.Contains(Commands("publish", "Confirm the deployed revision"), line => Invokes(line, "curl"));
    }

    [Fact]
    public void Only_the_trusted_branch_may_build_a_customer_artifact()
    {
        IReadOnlyList<string> commands = Commands("fetch", "well-formed dispatch");

        Assert.Contains(commands, line => line.Contains(@"""$GITHUB_REF"" != ""refs/heads/main""", StringComparison.Ordinal));
        Assert.Contains(commands, line => line.Contains("This workflow only runs from the trusted main branch", StringComparison.Ordinal));
    }

    /// <summary>
    /// The builder authenticates by federation alone. A repository secret would be a long-lived credential
    /// that every workflow in the repository could read, and the first thing generated code would look for.
    /// </summary>
    [Fact]
    public void The_workflow_authenticates_by_federation_and_carries_no_repository_secret()
    {
        Assert.DoesNotContain("secrets.", s_workflowText, StringComparison.Ordinal);

        foreach (string name in s_workflow["jobs"]!.Keys)
        {
            foreach (YamlNode step in Steps(name).Where(
                step => step["uses"]?.Text.StartsWith("azure/login", StringComparison.Ordinal) == true))
            {
                // Federated: the login carries identifiers, never a client secret or a password.
                Assert.Equal(
                    ["client-id", "subscription-id", "tenant-id"],
                    step["with"]!.Keys.Order(StringComparer.Ordinal));

                foreach (string value in step["with"]!.ScalarFields.Values)
                {
                    Assert.StartsWith("${{ vars.", value, StringComparison.Ordinal);
                }

                Assert.Equal("write", Job(name)["permissions"]!.ScalarFields["id-token"]);
            }
        }
    }

    /// <summary>
    /// No dispatch input is interpolated into a shell body. Every one arrives through the environment, so
    /// a quote in an input is a character in a variable rather than a token in a script.
    /// </summary>
    [Fact]
    public void No_step_interpolates_a_dispatch_input_into_its_script()
    {
        foreach (string name in s_workflow["jobs"]!.Keys)
        {
            foreach (YamlNode step in Steps(name))
            {
                Assert.DoesNotContain("${{", step["run"]?.Text ?? string.Empty, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Two_attempts_at_the_same_operation_do_not_produce_two_builds()
    {
        Assert.Equal("generated-target-${{ inputs.operation_id }}", s_workflow["concurrency"]!["group"]!.Text);
        Assert.Equal("false", s_workflow["concurrency"]!["cancel-in-progress"]!.Text);

        // The result document is written once; a second writer fails rather than overwriting a deployment.
        Assert.Contains(
            Commands("publish", "Record the result"),
            line => line.Contains("--overwrite false", StringComparison.Ordinal));
    }

    [Fact]
    public void The_deployed_revision_is_confirmed_to_answer_before_a_result_is_recorded() =>
        Assert.True(
            StepIndex("publish", "Confirm the deployed revision answers") <
            StepIndex("publish", "Record the result where the workbench reads it"),
            "The result must not be recorded before the revision has answered.");

    /// <summary>
    /// The destination is the Container App the pilot template provisions and grants a narrow role over,
    /// and it is addressed by the ARM identifier the run was approved for rather than by name plus
    /// whatever subscription the runner happened to log into.
    /// </summary>
    [Fact]
    public void The_only_destination_is_the_container_app_the_pilot_template_provisions()
    {
        string target = s_workflow["env"]!["CONTAINER_APP"]!.Text;

        Assert.Contains(
            "var targetContainerAppName = 'ca-ofmfleet-dotnet-${environmentName}-${uniqueSuffix}'",
            s_pilotTemplate,
            StringComparison.Ordinal);
        Assert.Matches("^ca-ofmfleet-dotnet-dev-[a-z0-9]{8}$", target);

        // The template grants only read, write and revision read, over that one app.
        Assert.Contains("'Microsoft.App/containerApps/write'", s_pilotTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("'Microsoft.App/containerApps/delete'", s_pilotTemplate, StringComparison.Ordinal);

        IReadOnlyList<string> deploy = Commands("publish", "Deploy the revision");
        Assert.Contains(deploy, line => line.Contains("--ids \"$TARGET_RESOURCE_ID\"", StringComparison.Ordinal));
        Assert.DoesNotContain(deploy, line => line.Contains("az containerapp update --name", StringComparison.Ordinal));

        // And the dispatched identifier is re-derived here rather than believed.
        Assert.Contains(
            Commands("fetch", "well-formed dispatch"),
            line => line.Contains("\"$TARGET_RESOURCE_ID\" != \"$EXPECTED_RESOURCE_ID\"", StringComparison.Ordinal));
    }

    [Fact]
    public void No_credential_is_written_into_the_generated_image()
    {
        Assert.Contains("No credential is baked in", s_dockerfile, StringComparison.Ordinal);
        Assert.Contains("USER $APP_UID", s_dockerfile, StringComparison.Ordinal);

        // The generated API authenticates to PostgreSQL with a managed identity: its connection string
        // carries no password, and Npgsql takes a token instead.
        Assert.Contains("AZURE_CLIENT_ID", s_dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("PASSWORD=", s_dockerfile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_build_context_excludes_everything_the_generated_tree_could_smuggle_in()
    {
        foreach (string pattern in new[]
        {
            "app/**/Dockerfile", "app/**/node_modules", "app/**/.env", "app/**/*.pem", "app/deploy",
        })
        {
            Assert.Contains(pattern, s_dockerignore, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The digest a Linux runner would read. The repository stores these files with LF under
    /// <c>.gitattributes</c>, so a developer's CRLF working tree must not change what the pin means.
    /// </summary>
    private static string Sha256OfCheckout(string content) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n", StringComparison.Ordinal))));

    private static string RepositoryText(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "oracle-forms-migration-fleet.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root was not found above the test assembly.");
    }
}

/// <summary>
/// What the host must be told before the product can deploy anything.
///
/// An unconfigured host must produce no gateway at all. A gateway that defaulted its way to a repository
/// or a storage account would dispatch a customer's generated code somewhere nobody approved, so the
/// missing keys are reported and registration is skipped.
/// </summary>
public sealed class GitHubActionsDeploymentOptionsTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] overrides)
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal)
        {
            ["TargetDeployment:GitHub:Owner"] = "rxt64",
            ["TargetDeployment:GitHub:Repository"] = "oracle-forms-migration-fleet",
            ["TargetDeployment:GitHub:AppId"] = "1234567",
            ["TargetDeployment:GitHub:InstallationId"] = "89012345",
            ["TargetDeployment:GitHub:PrivateKeySecretUri"] = "https://kv-ofmfleet.vault.azure.net/secrets/generated-target-builder",
            ["TargetDeployment:Storage:AccountUri"] = "https://stofmfleetdev.blob.core.windows.net",
            ["TargetDeployment:WorkbenchCommitSha"] = "46bcd13000000000000000000000000000000000",
            ["TargetDeployment:TargetName"] = "dev-generated-target",
        };

        foreach ((string key, string value) in overrides)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Fact]
    public void A_fully_configured_host_reads_its_builder()
    {
        Assert.True(GitHubActionsDeploymentOptions.TryRead(Configuration(), out GitHubActionsDeploymentOptions? options, out _));

        Assert.NotNull(options);
        Assert.Equal("generated-target.yml", options!.WorkflowFile);
        Assert.Equal("main", options.Ref);
        Assert.Equal("generated-target", options.Container);
    }

    [Theory]
    [InlineData("TargetDeployment:GitHub:Owner")]
    [InlineData("TargetDeployment:GitHub:AppId")]
    [InlineData("TargetDeployment:GitHub:PrivateKeySecretUri")]
    [InlineData("TargetDeployment:Storage:AccountUri")]
    [InlineData("TargetDeployment:WorkbenchCommitSha")]
    [InlineData("TargetDeployment:TargetName")]
    public void A_missing_key_is_named_and_no_builder_is_produced(string key)
    {
        bool read = GitHubActionsDeploymentOptions.TryRead(
            Configuration((key, string.Empty)),
            out GitHubActionsDeploymentOptions? options,
            out IReadOnlyList<string> missing);

        Assert.False(read);
        Assert.Null(options);
        Assert.Contains(key, missing);
    }

    [Theory]
    [InlineData("TargetDeployment:Storage:AccountUri", "https://stofmfleetdev.example.com")]
    [InlineData("TargetDeployment:Storage:AccountUri", "http://stofmfleetdev.blob.core.windows.net")]
    [InlineData("TargetDeployment:GitHub:PrivateKeySecretUri", "file:///etc/key.pem")]
    [InlineData("TargetDeployment:WorkbenchCommitSha", "not-a-sha")]
    [InlineData("TargetDeployment:GitHub:AppId", "app-name")]
    public void A_malformed_value_is_refused_rather_than_defaulted(string key, string value)
    {
        Assert.False(GitHubActionsDeploymentOptions.TryRead(
            Configuration((key, value)),
            out GitHubActionsDeploymentOptions? options,
            out IReadOnlyList<string> missing));

        Assert.Null(options);
        Assert.NotEmpty(missing);
    }
}

/// <summary>
/// How the generated tier is packed for the builder.
///
/// The archive digest is the dedupe key and the integrity check at once, so packing has to be a function
/// of the bytes alone. If it varied with the clock, every retry would look like a different operation and
/// the same output would be built again.
/// </summary>
public sealed class GitHubActionsBundlePackagingTests
{
    private const string OutputRoot = "out/pilot";

    private static (TemporaryWorkspace Workspace, TargetDeploymentBundle Bundle) Tier()
    {
        TemporaryWorkspace workspace = new();
        Dictionary<string, string> tier = new(StringComparer.Ordinal)
        {
            ["application/backend/Api/Api.csproj"] = "<Project />",
            ["application/frontend/package.json"] = "{}",
        };

        foreach ((string path, string content) in tier)
        {
            workspace.WriteFile($"{OutputRoot}/{path}", content);
        }

        WorkspaceWriter writer = new(workspace.Root);
        TargetDeploymentFile[] files =
        [
            .. tier.Keys.Order(StringComparer.Ordinal).Select(path => new TargetDeploymentFile(
                path,
                writer.Sha256($"{OutputRoot}/{path}", 1024 * 1024),
                new FileInfo(workspace.Absolute($"{OutputRoot}/{path}")).Length)),
        ];

        return (workspace, new TargetDeploymentBundle(
            workspace.Root,
            OutputRoot,
            files,
            GenerationCoverage.OutputSetDigest(files.Select(file => (file.Path, file.ContentSha256)))));
    }

    [Fact]
    public void The_same_tier_always_packs_to_the_same_bytes()
    {
        (TemporaryWorkspace workspace, TargetDeploymentBundle bundle) = Tier();
        using (workspace)
        {
            Assert.Equal(
                GitHubActionsTargetDeploymentGateway.Package(bundle),
                GitHubActionsTargetDeploymentGateway.Package(bundle));
        }
    }

    /// <summary>The builder stages the tier where the host recipe expects it, so the prefix is dropped.</summary>
    [Fact]
    public void The_archive_holds_the_tier_without_its_workspace_prefix()
    {
        (TemporaryWorkspace workspace, TargetDeploymentBundle bundle) = Tier();
        using (workspace)
        {
            using System.IO.Compression.ZipArchive archive = new(
                new MemoryStream(GitHubActionsTargetDeploymentGateway.Package(bundle)));

            Assert.Equal(
                ["backend/Api/Api.csproj", "frontend/package.json"],
                archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void A_file_changed_between_digesting_and_packing_refuses_the_publish()
    {
        (TemporaryWorkspace workspace, TargetDeploymentBundle bundle) = Tier();
        using (workspace)
        {
            workspace.WriteFile($"{OutputRoot}/application/frontend/package.json", "{\"swapped\":true}");

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                () => GitHubActionsTargetDeploymentGateway.Package(bundle));

            Assert.Contains("changed between being digested and being packaged", refused.Message, StringComparison.Ordinal);
        }
    }
}
