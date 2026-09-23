// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics;
using System.IO.Compression;
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
/// recipe is built, where generated code is allowed to execute, whether the revision is pinned to a digest
/// — are decisions that live nowhere else, and a workflow is the one part of this product that no other
/// test would execute. Deleting the split between the credentialed and uncredentialed jobs, or moving one
/// `dotnet test` back onto the runner, is the kind of change that would most quietly undo the isolation.
///
/// It is read as structure and as commands, never as text. An earlier version of this class matched
/// substrings against the whole file, and three of its assertions were satisfied by prose: a sentence in a
/// comment saying `docker load` happened, a `cmp` that had been moved to another job, and a registry-side
/// build that no longer existed. A test that a comment can satisfy is a test a comment can also break, and
/// worse, one that keeps passing after the step it was protecting is gone.
///
/// Two tests at the bottom go further and execute the workflow's own code against hostile input, because
/// the properties they cover — an expander that refuses a traversing entry, a container that cannot reach
/// the runner — are claims about behaviour rather than about shape.
/// </summary>
public sealed class GeneratedTargetWorkflowTests
{
    private static readonly string s_workflowText = RepositoryText(".github/workflows/generated-target.yml");
    private static readonly YamlNode s_workflow = YamlNode.Parse(s_workflowText);
    private static readonly string s_dockerfile = RepositoryText(".github/generated-target/Dockerfile");
    private static readonly string s_dockerignore = RepositoryText(".github/generated-target/.dockerignore");
    private static readonly string s_pilotTemplate = RepositoryText("infra/dotnet-pilot/main.bicep");

    /// <summary>
    /// The ordered fields the product hashes into <c>binding_digest</c>, mirroring
    /// <see cref="TargetDeploymentPolicy.BindingDigest"/>. The workflow derives the same digest in two
    /// jobs, and both are compared against this list rather than against each other alone.
    /// </summary>
    private static readonly string[] s_bindingFields =
    [
        "fleet.target-deployment-binding/1", "OPERATION_ID", "ARTIFACT_URI", "ARTIFACT_SHA256", "RUN_ID",
        "WORKBENCH_SHA", "SOURCE_SNAPSHOT_SHA256", "PLAN_DIGEST", "TARGET_DIGEST", "TARGET_RESOURCE_ID",
        "APPROVAL_EXPIRES_UTC",
    ];

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

    /// <summary>The body of a shell here-document, so the workflow's own embedded program can be run.</summary>
    private static string HereDocument(string script, string marker)
    {
        string[] lines = script.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int open = Array.FindIndex(lines, line => line.Contains($"<<'{marker}'", StringComparison.Ordinal));
        Assert.True(open >= 0, $"No here-document opened with <<'{marker}'.");

        int close = Array.FindIndex(lines, open + 1, line => line.Trim() == marker);
        Assert.True(close > open, $"The here-document opened with <<'{marker}' is never closed.");

        return string.Join('\n', lines[(open + 1)..close]);
    }

    /// <summary>
    /// The instructions of each named Dockerfile stage, and which stages the final stage copies from.
    ///
    /// Stage membership is what makes a test un-skippable assertion possible: BuildKit does not run a
    /// stage nothing depends on, so "the suite runs" means "the suite runs in a stage the shipped image
    /// copies from", not merely "a `dotnet test` line exists somewhere in the file".
    /// </summary>
    private static (Dictionary<string, List<string>> Stages, List<string> Order) DockerfileStages()
    {
        Dictionary<string, List<string>> stages = new(StringComparer.Ordinal);
        List<string> order = [];
        string current = string.Empty;
        string pending = string.Empty;

        foreach (string raw in s_dockerfile.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith('#') || line.Trim().Length == 0)
            {
                continue;
            }

            pending = pending.Length == 0 ? line.TrimStart() : pending + " " + line.TrimStart();
            if (pending.EndsWith('\\'))
            {
                pending = pending[..^1].TrimEnd();
                continue;
            }

            string instruction = pending;
            pending = string.Empty;

            if (instruction.StartsWith("FROM ", StringComparison.Ordinal))
            {
                string[] parts = instruction.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                current = parts.Length >= 4 && parts[^2].Equals("AS", StringComparison.OrdinalIgnoreCase)
                    ? parts[^1]
                    : $"<anonymous-{order.Count}>";
                stages[current] = [];
                order.Add(current);
                continue;
            }

            if (current.Length > 0)
            {
                stages[current].Add(instruction);
            }
        }

        return (stages, order);
    }

    /// <summary>The keys of the object literal a <c>jq -n</c> program builds, in order.</summary>
    private static IReadOnlyList<string> JqKeys(IReadOnlyList<string> commands) =>
    [
        .. commands
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && char.IsLower(line[0]))
            .Select(line => line.Split(':')[0].Trim())
            .Where(key => key.Length > 0 && key.All(char.IsLetterOrDigit)),
    ];

    /// <summary>
    /// The ordered fields a step feeds into <c>printf ... | sha256sum</c>, as their variable names.
    /// </summary>
    private static IReadOnlyList<string> BindingDigestFields(string job, string nameFragment)
    {
        IReadOnlyList<string> commands = Commands(job, nameFragment);
        int open = LineOf(commands, "DERIVED=\"$(printf ");
        Assert.True(open >= 0, $"The '{nameFragment}' step in '{job}' derives no binding digest.");

        int close = commands.ToList().FindIndex(open, line => line.Contains("| sha256sum |", StringComparison.Ordinal));
        Assert.True(close >= open, "The binding digest derivation is never piped into sha256sum.");

        List<string> fields = [];
        foreach (string line in commands.Skip(open).Take(close - open + 1))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('\''))
            {
                fields.Add(trimmed.Split('\'')[1]);
            }
            else if (trimmed.StartsWith("\"$", StringComparison.Ordinal))
            {
                fields.Add(trimmed[2..].Split('"')[0]);
            }
        }

        return fields;
    }

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
    /// The isolation this whole workflow is shaped around: the job that builds code generated from a
    /// customer estate holds no cloud credential at all.
    /// </summary>
    [Fact]
    public void The_job_that_builds_generated_code_holds_no_azure_credential()
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
    /// The finding this workflow was reshaped around: generated code used to run as the runner's own user.
    ///
    /// A digest over the image recipe does not bound that. A process running as the runner can append to
    /// <c>$GITHUB_ENV</c> and <c>$GITHUB_PATH</c>, write <c>$BASH_ENV</c>, drop a shim earlier on
    /// <c>$PATH</c>, or fork and return — and every one of those changes what a later step does without
    /// changing any file that gets hashed. So the property asserted here is not "the recipe is verified",
    /// it is "no job on this runner ever invokes a toolchain over the artifact at all".
    /// </summary>
    [Fact]
    public void No_generated_code_is_executed_on_the_runner_by_any_job()
    {
        foreach (string job in s_workflow["jobs"]!.Keys)
        {
            IReadOnlyList<string> commands = Commands(job);

            foreach (string toolchain in new[] { "dotnet", "npm", "npx", "yarn", "pnpm", "node", "msbuild" })
            {
                Assert.False(
                    Runs(commands, toolchain),
                    $"The '{job}' job invokes '{toolchain}' on the runner. Generated code runs only inside docker build.");
            }

            foreach (YamlNode step in Steps(job))
            {
                string uses = step["uses"]?.Text ?? string.Empty;
                Assert.False(
                    uses.Contains("setup-dotnet", StringComparison.Ordinal) ||
                    uses.Contains("setup-node", StringComparison.Ordinal) ||
                    uses.Contains("setup-java", StringComparison.Ordinal),
                    $"The '{job}' job installs a toolchain, which it would only need in order to run the artifact.");

                // A working directory inside the staged tree is how a step ends up running from it.
                Assert.DoesNotContain("stage/app", step["working-directory"]?.Text ?? string.Empty, StringComparison.Ordinal);
            }
        }

        // And the runner no longer stands up the database the host-side suite needed, because there is no
        // host-side suite. A service container here would be a service reachable from generated code.
        Assert.Null(Job("build")["services"]);
    }

    /// <summary>
    /// Where the generated code does run: as `RUN` instructions of the host-owned recipe, in stages the
    /// shipped image copies from.
    ///
    /// The second half matters as much as the first. BuildKit does not execute a stage nothing depends on,
    /// so a test stage that the runtime stage never copies from is a suite that silently never runs.
    /// </summary>
    [Fact]
    public void The_generated_suites_run_inside_the_image_build_in_stages_that_cannot_be_pruned()
    {
        (Dictionary<string, List<string>> stages, List<string> order) = DockerfileStages();
        string final = order[^1];

        List<string> copiedFrom =
        [
            .. stages[final]
                .Where(instruction => instruction.StartsWith("COPY --from=", StringComparison.Ordinal))
                .Select(instruction => instruction.Split(' ')[1]["--from=".Length..]),
        ];

        (string Command, string Stage)[] required =
        [
            ("npm run test", "client"),
            ("dotnet test", "api"),
        ];

        foreach ((string command, string stage) in required)
        {
            Assert.Contains(stage, copiedFrom);
            Assert.Contains(
                stages[stage],
                instruction => instruction.StartsWith("RUN ", StringComparison.Ordinal) &&
                               instruction.Contains(command, StringComparison.Ordinal));
        }

        // The published binaries come out of the same stage that ran the suite, so a green image cannot be
        // produced from a tree whose tests were never executed.
        Assert.Contains(
            stages["api"],
            instruction => instruction.StartsWith("RUN ", StringComparison.Ordinal) &&
                           instruction.Contains("dotnet publish", StringComparison.Ordinal));

        // No `--target`, so the default target is the final stage and the graph above is the graph built.
        Assert.DoesNotContain(
            Commands("build", "Build the container image"),
            line => line.Contains("--target", StringComparison.Ordinal));
    }

    /// <summary>
    /// What the container the generated code runs in is not given: a credential, a socket, the host's
    /// network, or a writable path outside the build.
    /// </summary>
    [Fact]
    public void The_container_build_is_given_no_secret_socket_or_host_network()
    {
        string invocation = Commands("build", "Build the container image")
            .Single(line => line.Contains("docker build", StringComparison.Ordinal));

        foreach (string forbidden in new[]
        {
            "--secret", "--ssh", "--network", "--add-host", "--security-opt", "--privileged", "--volume",
            "--build-arg", "--output", "-v ",
        })
        {
            Assert.DoesNotContain(forbidden, invocation, StringComparison.Ordinal);
        }

        // Instructions, not the file: the recipe's own header explains that it declares no secret or ssh
        // mount, and a test a comment can satisfy is a test a comment can also break.
        (Dictionary<string, List<string>> stages, _) = DockerfileStages();
        foreach (string instruction in stages.Values.SelectMany(stage => stage))
        {
            foreach (string forbidden in new[] { "type=secret", "type=ssh", "docker.sock" })
            {
                Assert.DoesNotContain(forbidden, instruction, StringComparison.Ordinal);
            }
        }

        foreach (string job in s_workflow["jobs"]!.Keys)
        {
            Assert.DoesNotContain(Commands(job), line => line.Contains("docker.sock", StringComparison.Ordinal));
        }

        // The context is the staging directory and the recipe is named explicitly within it.
        Assert.EndsWith(" stage", invocation, StringComparison.Ordinal);
        Assert.Contains("--file stage/Dockerfile", invocation, StringComparison.Ordinal);
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

        // Ordering, not just presence: the credentialed publish is gated behind the uncredentialed build,
        // and it also depends on fetch directly so it can read the payload fetch validated.
        Assert.Equal("fetch", Job("build")["needs"]!.Text);
        Assert.Equal(
            ["build", "fetch"],
            Job("publish")["needs"]!.Sequence.Select(item => item.Text).Order(StringComparer.Ordinal));
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
        Assert.Contains(staging, line => line.Contains("The generated tree still carries a Dockerfile", StringComparison.Ordinal));

        IReadOnlyList<string> assembly = Commands("build", "host-owned recipe");
        Assert.Contains(assembly, line => line.Contains("cp .github/generated-target/Dockerfile stage/Dockerfile", StringComparison.Ordinal));

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
    /// The pin is the one reference in the build job that nothing on the runner can reach: it is part of
    /// the workflow definition, which GitHub loads from the trusted ref. A `cmp` of the staged copy against
    /// its own source would compare two files an attacker who reached the workspace could change together.
    /// The digests are declared on the step rather than on the job so that no `$GITHUB_ENV` write from an
    /// earlier step can shadow the reference.
    ///
    /// It is checked twice: once before the artifact has been written anywhere, and once immediately before
    /// the build. The second check is what would catch an expansion that escaped `app/`.
    /// </summary>
    [Fact]
    public void The_host_recipe_is_verified_against_a_digest_nothing_on_the_runner_can_rewrite()
    {
        foreach (string stepName in new[] { "host-owned recipe", "Build the container image" })
        {
            IReadOnlyDictionary<string, string> pinned = Step("build", stepName)["env"]!.ScalarFields;
            Assert.Equal(Sha256OfCheckout(s_dockerfile), pinned["EXPECTED_DOCKERFILE_SHA256"]);
            Assert.Equal(Sha256OfCheckout(s_dockerignore), pinned["EXPECTED_DOCKERIGNORE_SHA256"]);
        }

        // On the step, so a $GITHUB_ENV write from an earlier step cannot shadow the reference.
        Assert.Null(Job("build")["env"]);
        Assert.Null(s_workflow["env"]!["EXPECTED_DOCKERFILE_SHA256"]);

        IReadOnlyList<string> staging = Commands("build", "host-owned recipe");
        int checkedAt = LineOf(staging, "sha256sum --check");
        int copiedAt = LineOf(staging, "cp .github/generated-target/Dockerfile");
        Assert.True(checkedAt >= 0, "The recipe is not checked against the pinned digests.");
        Assert.True(checkedAt < copiedAt, "The recipe must be verified before it is staged.");

        // Re-checked on the staged copy, before the build reads it.
        IReadOnlyList<string> building = Commands("build", "Build the container image");
        int recheckedAt = LineOf(building, "stage/.dockerignore");
        int builtAt = LineOf(building, "docker build");
        Assert.True(recheckedAt >= 0, "The staged recipe is not re-checked before the build.");
        Assert.True(recheckedAt < builtAt, "The staged recipe must be re-checked before it is built.");

        // A comparison of the copy against its own source proves nothing here and must not come back.
        Assert.DoesNotContain(staging, line => Invokes(line, "cmp"));

        // The recipe is staged before anything out of the artifact is written, so the file that is hashed
        // has never shared a directory with the generated tree.
        Assert.True(
            StepIndex("build", "host-owned recipe") < StepIndex("build", "Stage the generated tree"),
            "The recipe must be staged before the artifact is expanded.");
    }

    [Fact]
    public void The_archive_is_expanded_without_honouring_traversal_absolute_or_link_entries()
    {
        IReadOnlyList<string> staging = Commands("build", "Stage the generated tree");

        Assert.Contains(staging, line => line.Contains("Refused traversal entry", StringComparison.Ordinal));
        Assert.Contains(staging, line => line.Contains("Refused absolute or windows-rooted entry", StringComparison.Ordinal));
        Assert.Contains(staging, line => line.Contains("Refused symbolic link entry", StringComparison.Ordinal));

        // And the tree on disk is inspected afterwards, because a link is how a build context ends up
        // carrying a file from outside itself no matter what the expander believed it wrote.
        Assert.Contains(staging, line => line.Contains("find stage/app -type l", StringComparison.Ordinal));
        Assert.Contains(staging, line => line.Contains("! -type f ! -type d", StringComparison.Ordinal));

        // Entry by entry, so nothing decides where an entry lands except this code.
        Assert.DoesNotContain(staging, line => Invokes(line, "unzip"));
        Assert.DoesNotContain(staging, line => Invokes(line, "tar"));
    }

    [Fact]
    public void Package_scripts_from_the_generated_tree_are_never_executed()
    {
        (Dictionary<string, List<string>> stages, _) = DockerfileStages();
        List<string> instructions = [.. stages.Values.SelectMany(stage => stage)];

        Assert.Contains(
            instructions,
            instruction => instruction.StartsWith("RUN ", StringComparison.Ordinal) &&
                           instruction.Contains("npm ci --ignore-scripts", StringComparison.Ordinal));

        // No other `npm ci` anywhere, in the recipe or on the runner, that could omit the flag. Counted
        // over instructions rather than over the file, whose header discusses the flag in prose.
        Assert.DoesNotContain(
            instructions,
            instruction => instruction.Contains("npm ci", StringComparison.Ordinal) &&
                           !instruction.Contains("npm ci --ignore-scripts", StringComparison.Ordinal));
        Assert.DoesNotContain(
            s_workflow["jobs"]!.Keys.SelectMany(Commands),
            line => line.Contains("npm ci", StringComparison.Ordinal));
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
        (Dictionary<string, List<string>> stages, List<string> order) = DockerfileStages();
        List<string> runtime = stages[order[^1]];

        Assert.Contains("No credential is baked into the shipped image", s_dockerfile, StringComparison.Ordinal);
        Assert.Contains(runtime, instruction => instruction.Contains("USER $APP_UID", StringComparison.Ordinal));

        // The generated API authenticates to PostgreSQL with a managed identity: its connection string
        // carries no password, and Npgsql takes a token instead. The throwaway password the acceptance
        // suite needs lives in the `api` build stage and is discarded with it, so the assertion is scoped
        // to the stage that ships rather than to the file.
        Assert.Contains("AZURE_CLIENT_ID", s_dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain(
            runtime,
            instruction => instruction.Contains("PASSWORD=", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            runtime,
            instruction => instruction.StartsWith("COPY --from=", StringComparison.Ordinal) &&
                           instruction.Contains("postgresql", StringComparison.OrdinalIgnoreCase));
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
    /// The approval is re-read at every point where something irreversible is about to happen, and each
    /// time against the payload the fetch job published rather than against anything the build produced.
    ///
    /// The build sits between the first check and the push, and it runs generated code. A file, an
    /// environment variable, or an artifact that the build wrote is therefore not an authority on how long
    /// the approval holds or on where the deployment is allowed to land.
    /// </summary>
    [Fact]
    public void The_approval_is_revalidated_before_the_push_and_again_before_the_container_app_is_updated()
    {
        // Every value the credentialed job decides with arrives from `fetch`, not from `inputs` and not
        // from a build artifact. Reading `inputs` directly would be safe, but it would also be the shape
        // that makes reading a build artifact look equally safe later.
        IReadOnlyDictionary<string, string> payload = Job("publish")["env"]!.ScalarFields;
        foreach (string key in new[]
        {
            "OPERATION_ID", "ARTIFACT_URI", "ARTIFACT_SHA256", "RUN_ID", "WORKBENCH_SHA",
            "SOURCE_SNAPSHOT_SHA256", "PLAN_DIGEST", "TARGET_DIGEST", "TARGET_RESOURCE_ID",
            "APPROVAL_EXPIRES_UTC", "BINDING_DIGEST", "ARTIFACT_ACCOUNT", "ARTIFACT_CONTAINER",
            "ARTIFACT_BLOB",
        })
        {
            Assert.StartsWith("${{ needs.fetch.outputs.", payload[key], StringComparison.Ordinal);
        }

        foreach (YamlNode step in Steps("publish"))
        {
            Assert.DoesNotContain(
                step["env"]?.ScalarFields ?? new Dictionary<string, string>(StringComparer.Ordinal),
                entry => entry.Value.Contains("${{ inputs.", StringComparison.Ordinal));
        }

        // Before the push.
        IReadOnlyList<string> recheck = Commands("publish", "Re-check the binding and the approval");
        Assert.Contains(recheck, line => line.Contains("[[ \"$DERIVED\" == \"$BINDING_DIGEST\" ]]", StringComparison.Ordinal));
        Assert.Contains(recheck, line => line.Contains("date -u -d \"$APPROVAL_EXPIRES_UTC\"", StringComparison.Ordinal));
        Assert.True(
            StepIndex("publish", "Re-check the binding and the approval") <
            StepIndex("publish", "Load, tag, and push"),
            "The approval must be re-checked before the registry push.");

        // And again before the only step that changes a customer-facing resource, inside that step so no
        // later edit can reorder a separate check away from what it guards.
        IReadOnlyList<string> deploy = Commands("publish", "Deploy the revision");
        int expiryAt = LineOf(deploy, "date -u -d \"$APPROVAL_EXPIRES_UTC\"");
        int updateAt = LineOf(deploy, "az containerapp update");
        Assert.True(expiryAt >= 0, "The deploy step does not re-read the approval expiry.");
        Assert.True(expiryAt < updateAt, "The approval must be re-read before the Container App is updated.");

        // The expiry is shaped before a clock is asked about it, because `date -d` also parses English.
        Assert.Contains(
            Commands("fetch", "well-formed dispatch"),
            line => line.Contains("APPROVAL_EXPIRES_UTC\" =~ ^[0-9]{4}", StringComparison.Ordinal));
    }

    /// <summary>
    /// The binding digest is derived twice, in two jobs, and the two derivations have to be the same
    /// statement as the product's. A field dropped from one of them is a rewritten dispatch that one job
    /// would accept.
    /// </summary>
    [Fact]
    public void Both_jobs_derive_the_binding_digest_from_the_same_fields_in_the_same_order()
    {
        Assert.Equal(s_bindingFields, BindingDigestFields("fetch", "rewritten dispatch"));
        Assert.Equal(s_bindingFields, BindingDigestFields("publish", "Re-check the binding"));

        // The canonical form is NUL-separated with one trailing newline, which is what
        // TargetDeploymentPolicy.BindingDigest hashes. Counted rather than eyeballed: eleven fields, ten
        // separators, one newline.
        foreach ((string job, string step) in new[] { ("fetch", "rewritten dispatch"), ("publish", "Re-check the binding") })
        {
            string format = Commands(job, step).First(line => line.Contains("printf '%s", StringComparison.Ordinal));
            Assert.Equal(s_bindingFields.Length, format.Split("%s").Length - 1);
            Assert.Equal(s_bindingFields.Length - 1, format.Split("\\0").Length - 1);
            Assert.Contains("%s\\n'", format, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A tag can be moved after it was verified; a digest cannot. The revision that is verified is then
    /// selected by that digest rather than by being the newest one.
    ///
    /// "Newest active revision" is the revision this run replaced for as long as the new one is still
    /// provisioning, and it stays that revision forever if the new image never pulls. Probing the
    /// application FQDN in that state reports a healthy deployment of an image that never started, which
    /// is the failure this test exists to keep closed.
    /// </summary>
    [Fact]
    public void The_verified_revision_is_the_one_running_this_digest_and_it_is_probed_on_its_own_endpoint()
    {
        IReadOnlyList<string> select = Commands("publish", "Select the revision running this digest");

        Assert.Contains(select, line => line.Contains("az containerapp revision list", StringComparison.Ordinal));
        Assert.Contains(select, line => line.Contains("containers[0].image=='$IMAGE_REF'", StringComparison.Ordinal));
        Assert.DoesNotContain(select, line => line.Contains("[?properties.active], &properties.createdTime", StringComparison.Ordinal));

        // Image pull and start failures end the wait rather than being retried until a timeout hides them.
        foreach (string terminal in new[] { "Failed|Deprovisioning|Deprovisioned", "Failed|Degraded|Stopped" })
        {
            Assert.Contains(select, line => line.Contains(terminal, StringComparison.Ordinal));
        }

        // Readiness, the image still on the revision, and the traffic that reaches it are all read back.
        Assert.Contains(select, line => line.Contains("\"$PROVISIONING\" == \"Provisioned\"", StringComparison.Ordinal));
        Assert.Contains(select, line => line.Contains("\"$RUNNING\" == \"Running\"", StringComparison.Ordinal));
        Assert.Contains(select, line => line.Contains("\"$REVISION_IMAGE\" == \"$IMAGE_REF\"", StringComparison.Ordinal));
        Assert.Contains(select, line => line.Contains("\"$WEIGHT\" == \"100\"", StringComparison.Ordinal));
        Assert.Contains(select, line => line.Contains("REVISION_URL=https://$REVISION_FQDN", StringComparison.Ordinal));

        // The probe reaches the revision's own endpoint first, before the application endpoint whose
        // answer only means something once the revision above was established as the one serving.
        IReadOnlyList<string> probe = Commands("publish", "Confirm the deployed revision answers");
        int revisionProbe = LineOf(probe, "\"$REVISION_URL/healthz\"");
        int applicationProbe = LineOf(probe, "\"$APPLICATION_URL/healthz\"");
        Assert.True(revisionProbe >= 0, "The revision's own endpoint is never probed.");
        Assert.True(applicationProbe > revisionProbe, "The revision endpoint must be probed before the application endpoint.");

        Assert.True(
            StepIndex("publish", "Select the revision running this digest") <
            StepIndex("publish", "Confirm the deployed revision answers"),
            "The revision must be selected and ready before it is probed.");
    }

    /// <summary>
    /// Where the result is written. It used to be whichever storage account in the resource group sorted
    /// first under the name prefix `stofmfleet`, which is not necessarily the account the product is
    /// configured with and is not necessarily the account the bundle was read from.
    /// </summary>
    [Fact]
    public void The_result_is_written_to_the_account_and_container_the_bundle_came_from()
    {
        // The prefix search is gone, from every job.
        foreach (string job in s_workflow["jobs"]!.Keys)
        {
            Assert.DoesNotContain(Commands(job), line => line.Contains("az storage account list", StringComparison.Ordinal));
            Assert.DoesNotContain(Commands(job), line => line.Contains("starts_with(name,", StringComparison.Ordinal));
        }

        IReadOnlyList<string> record = Commands("publish", "Record the result");
        Assert.Contains(record, line => line.Contains("--account-name \"$ARTIFACT_ACCOUNT\"", StringComparison.Ordinal));
        Assert.Contains(record, line => line.Contains("--container-name \"$ARTIFACT_CONTAINER\"", StringComparison.Ordinal));
        Assert.Contains(record, line => line.Contains("--name \"$RESULT_BLOB\"", StringComparison.Ordinal));

        // The bundle is read from the same coordinates, so the two cannot drift apart.
        IReadOnlyList<string> download = Commands("fetch", "pin it to the digest");
        Assert.Contains(download, line => line.Contains("--account-name \"$ARTIFACT_ACCOUNT\"", StringComparison.Ordinal));
        Assert.Contains(download, line => line.Contains("--container-name \"$ARTIFACT_CONTAINER\"", StringComparison.Ordinal));

        // Each field is checked, and every check runs before the blob name is exported to $GITHUB_ENV.
        IReadOnlyList<string> recheck = Commands("publish", "Re-check the binding and the approval");
        int accountShape = LineOf(recheck, "\"$ARTIFACT_ACCOUNT\" =~ ^[a-z0-9]{3,24}$");
        int containerMatch = LineOf(recheck, "\"$ARTIFACT_CONTAINER\" == \"$RESULT_CONTAINER\"");
        int blobMatch = LineOf(recheck, "operations/$OPERATION_ID/generated-application.zip");
        int reassembled = LineOf(recheck, "https://$ARTIFACT_ACCOUNT$ALLOWED_ARTIFACT_HOST_SUFFIX");
        int exported = LineOf(recheck, "RESULT_BLOB=operations/$OPERATION_ID/result.json");

        foreach ((string what, int at) in new[]
        {
            ("account shape", accountShape), ("container", containerMatch), ("blob", blobMatch),
            ("reassembled URI", reassembled),
        })
        {
            Assert.True(at >= 0, $"The {what} is not checked in the credentialed job.");
            Assert.True(at < exported, $"The {what} must be checked before anything is written to $GITHUB_ENV.");
        }

        // The same parsing happened in fetch, on the input, before it wrote those fields to $GITHUB_ENV.
        IReadOnlyList<string> parse = Commands("fetch", "well-formed dispatch");
        Assert.True(
            LineOf(parse, "\"$host\" == \"$account$ALLOWED_ARTIFACT_HOST_SUFFIX\"") <
            LineOf(parse, "ARTIFACT_ACCOUNT=$account"),
            "The artifact host must be checked before the account is exported.");
    }

    /// <summary>
    /// The wire contract between this builder and the product.
    ///
    /// <see cref="TargetDeploymentReport"/> is the shape the gateway deserializes and
    /// <see cref="TargetDeploymentPolicy.RejectReport"/> is what it holds it to, so a field dropped here is
    /// a deployment the product refuses to record no matter how well it went. The schema version is part
    /// of the agreement rather than decoration: the product reads exactly one.
    /// </summary>
    [Fact]
    public void The_result_document_carries_every_field_the_product_validates()
    {
        IReadOnlyList<string> record = Commands("publish", "Record the result");
        IReadOnlyList<string> keys = JqKeys(record);

        foreach (string required in new[]
        {
            "schemaVersion", "state", "operationId", "runId", "buildRunId", "artifactSha256", "workbenchSha",
            "sourceSnapshotSha256", "planDigest", "targetDigest", "bindingDigest", "imageDigest",
            "revisionName", "applicationUrl", "deployedResourceId", "readinessProbeUrl", "readinessOutcome",
            "readinessObservedUtc",
        })
        {
            Assert.Contains(required, keys);
        }

        // Fields this builder has always written, kept so an existing reader does not lose provenance.
        foreach (string preserved in new[] { "imageTarSha256", "equivalenceClaim" })
        {
            Assert.Contains(preserved, keys);
        }

        // Added for the operator and for the gateway: the approval this ran under, the revision's own
        // endpoint, and a `status` alongside `state`.
        foreach (string added in new[] { "status", "approvalExpiresUtc", "revisionUrl", "revisionProbeUrl" })
        {
            Assert.Contains(added, keys);
        }

        Assert.Contains(record, line => line.Contains($"schemaVersion: \"{TargetDeploymentPolicy.ResultSchemaVersion}\"", StringComparison.Ordinal));
        Assert.Contains(record, line => line.Contains("state: \"Deployed\"", StringComparison.Ordinal));
        Assert.Contains(record, line => line.Contains("status: \"Deployed\"", StringComparison.Ordinal));
        Assert.NotNull(TargetDeploymentPolicy.ReportedState("Deployed"));

        // RejectReadiness requires the probe host to equal the host of the URL the report names, so the
        // application endpoint is both what is probed last and what is reported.
        Assert.Contains(record, line => line.Contains("--arg readinessProbeUrl \"$APPLICATION_URL/healthz\"", StringComparison.Ordinal));
        Assert.Contains(record, line => line.Contains("--arg applicationUrl \"$APPLICATION_URL\"", StringComparison.Ordinal));

        // The blob name is the one the product reads back.
        Assert.Contains(
            Commands("publish", "Re-check the binding and the approval"),
            line => line.Contains(TargetDeploymentPolicy.ResultBlobName("$OPERATION_ID"), StringComparison.Ordinal));
    }

    /// <summary>
    /// Adversarial, and executed: the workflow's own expander is run against bundles built to escape it.
    ///
    /// The program under test is lifted out of the workflow rather than reimplemented here, so this fails
    /// if the step is weakened, not merely if a copy of it is. The benign case is asserted too, because a
    /// refusal that refuses everything is not a guard.
    /// </summary>
    [Fact]
    public void The_expander_in_this_workflow_refuses_a_bundle_built_to_escape_the_staging_directory()
    {
        string program = HereDocument(Step("build", "Stage the generated tree")["run"]!.Text, "PY");
        string python = PythonExecutable();

        string root = Path.Combine(Path.GetTempPath(), "ofm-expander-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        try
        {
            string script = Path.Combine(root, "expand.py");
            File.WriteAllText(script, program);

            (int benign, string benignError) = RunPython(
                python,
                script,
                Bundle(root, "benign.zip", entry => entry("backend/Api/Api.csproj", "<Project />")),
                Path.Combine(root, "benign"));
            Assert.True(benign == 0, $"The expander refused a well-formed bundle: {benignError}");
            Assert.True(File.Exists(Path.Combine(root, "benign", "backend", "Api", "Api.csproj")));

            (string Name, string Refusal, Action<Action<string, string>> Build)[] hostile =
            [
                ("traversal", "Refused traversal entry",
                    entry => entry("../../stage/Dockerfile", "FROM scratch")),
                ("absolute", "Refused absolute or windows-rooted entry",
                    entry => entry("/etc/cron.d/owned", "* * * * * root id")),

                // Python's zipfile turns backslashes into separators as it reads the central directory,
                // so a windows-rooted name arrives here already normalized and is caught by the traversal
                // rule rather than by the backslash one. Refused either way; the message says which rule.
                ("windows-rooted", "Refused traversal entry",
                    entry => entry("..\\..\\Dockerfile", "FROM scratch")),
            ];

            foreach ((string name, string refusal, Action<Action<string, string>> build) in hostile)
            {
                string destination = Path.Combine(root, name);
                (int exit, string error) = RunPython(python, script, Bundle(root, name + ".zip", build), destination);

                Assert.True(exit != 0, $"The expander accepted a '{name}' entry.");
                Assert.Contains(refusal, error, StringComparison.Ordinal);
                Assert.Empty(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
            }

            // A link entry, which is how a context ends up referring to a file outside it.
            string linkArchive = Path.Combine(root, "link.zip");
            using (ZipArchive archive = ZipFile.Open(linkArchive, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("frontend/package.json");
                entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using StreamWriter writer = new(entry.Open());
                writer.Write("/etc/passwd");
            }

            (int linkExit, string linkError) = RunPython(python, script, linkArchive, Path.Combine(root, "link"));
            Assert.True(linkExit != 0, "The expander accepted a symbolic link entry.");
            Assert.Contains("Refused symbolic link entry", linkError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Adversarial, and executed only where a container runtime exists: a <c>RUN</c> that behaves like a
    /// hostile generated build cannot reach the runner's command files, its workspace, or a Docker socket.
    ///
    /// It builds a deliberately non-deployable image. Nothing here is tagged, pushed, or saved, and the
    /// recipe it uses is a fixture rather than the shipping one, because a shipping artifact built on a
    /// workstation is an artifact nobody can reproduce. Set RUN_GENERATED_TARGET_CONTAINER_ATTACK=true on
    /// a machine with Docker to run it; without that it does not run, and this comment is the statement
    /// that the containment was not demonstrated on that machine rather than that it was.
    /// </summary>
    [Fact]
    public void A_hostile_run_instruction_cannot_reach_the_runners_command_files_or_workspace()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_GENERATED_TARGET_CONTAINER_ATTACK"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "ofm-attack-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path.Combine(root, "app"));
        try
        {
            string commandFile = Path.Combine(root, "github-env");
            string pathFile = Path.Combine(root, "github-path");
            string recipe = Path.Combine(root, "host-recipe");
            File.WriteAllText(commandFile, "UNTOUCHED=1\n");
            File.WriteAllText(pathFile, "/usr/bin\n");
            File.WriteAllText(recipe, "FROM trusted\n");

            // Exactly the moves the finding describes, aimed at this machine's real paths.
            File.WriteAllText(
                Path.Combine(root, "app", "attack.sh"),
                string.Join(
                    '\n',
                    "set -u",
                    $"echo 'OWNED=1' >> '{Posix(commandFile)}' 2>/dev/null || true",
                    $"echo '/tmp/evil' >> '{Posix(pathFile)}' 2>/dev/null || true",
                    $"echo 'FROM attacker' > '{Posix(recipe)}' 2>/dev/null || true",
                    "echo 'BASH_ENV=/tmp/evil.sh' >> \"${GITHUB_ENV:-/tmp/no-command-file}\" 2>/dev/null || true",
                    "test -S /var/run/docker.sock && echo 'SOCKET REACHED' && exit 3",
                    "exit 0",
                    string.Empty));

            File.WriteAllText(
                Path.Combine(root, "Dockerfile"),
                string.Join(
                    '\n',
                    "FROM alpine:3.20",
                    "COPY app/ /app/",
                    "RUN sh /app/attack.sh",
                    string.Empty));

            (int exit, string output) = Run(
                "docker",
                ["build", "--pull", "--no-cache", "--file", Path.Combine(root, "Dockerfile"), root],
                root);

            Assert.True(exit == 0, $"The attack image did not build, so nothing was demonstrated:\n{output}");
            Assert.DoesNotContain("SOCKET REACHED", output, StringComparison.Ordinal);

            Assert.Equal("UNTOUCHED=1\n", File.ReadAllText(commandFile).Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Equal("/usr/bin\n", File.ReadAllText(pathFile).Replace("\r\n", "\n", StringComparison.Ordinal));
            Assert.Equal("FROM trusted\n", File.ReadAllText(recipe).Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Posix(string path) => path.Replace('\\', '/');

    private static string Bundle(string root, string name, Action<Action<string, string>> build)
    {
        string archive = Path.Combine(root, name);
        using ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create);

        build((entryName, content) =>
        {
            using StreamWriter writer = new(zip.CreateEntry(entryName).Open());
            writer.Write(content);
        });

        return archive;
    }

    private static (int Exit, string Output) RunPython(string python, string script, string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        return Run(python, [script, archive, destination], Path.GetDirectoryName(script)!);
    }

    private static (int Exit, string Output) Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ProcessStartInfo start = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"'{fileName}' did not start.");

        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, standardOutput + standardError);
    }

    /// <summary>
    /// Python is a prerequisite of the expander test rather than an optional convenience: the step under
    /// test is a Python program, and the trusted builder's runner always has one.
    /// </summary>
    private static string PythonExecutable()
    {
        foreach (string candidate in new[] { "python3", "python" })
        {
            try
            {
                if (Run(candidate, ["--version"], Path.GetTempPath()).Exit == 0)
                {
                    return candidate;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not on PATH under this name.
            }
        }

        throw new InvalidOperationException(
            "No python3 was found. The staging step of generated-target.yml is a Python program, and this " +
            "test runs it against hostile bundles rather than restating what it is supposed to do.");
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
