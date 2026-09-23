using System.Globalization;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// Emits the ASP.NET Core tier, its executable acceptance suite, and the packaging around both.
///
/// The wire contract is role-named on purpose. Two estates that bind the same roles to different
/// identifiers get the same routes, the same request and response shapes, and the same generated test
/// suite; only the SQL underneath carries the source identifiers, where it is traceable. Nothing the
/// caller sends reaches a command string: the routine name is a constant, every value is a parameter, and
/// the connection string is read from configuration the host owns and never from a request.
/// </summary>
internal static class DotNetBackendEmitter
{
    private const string Namespace = "GeneratedApplication";

    internal static IReadOnlyList<GeneratedFile> Emit(DotNetGenerationPlan plan) =>
    [
        new GeneratedFile("backend/GeneratedBackend.slnx", Solution(), "Solution over the generated API and its acceptance suite."),
        new GeneratedFile("backend/Api/Api.csproj", ApiProject(), "ASP.NET Core project with pinned Npgsql."),
        new GeneratedFile("backend/Api/Program.cs", Program(), "Entry point. All wiring lives in ApiHost so tests can host the same app."),
        new GeneratedFile("backend/Api/ApiHost.cs", ApiHost(), "Route table, error mapping, and configuration binding."),
        new GeneratedFile("backend/Api/Contracts.cs", Contracts(), "Request and response records for the generated routes."),
        new GeneratedFile("backend/Api/SubmissionStore.cs", Store(plan), "Npgsql data access. Every statement is parameterized."),
        new GeneratedFile("backend/Api/appsettings.json", AppSettings(), "Configuration skeleton. It holds no credential."),
        new GeneratedFile("backend/Api.Tests/Api.Tests.csproj", TestProject(), "Acceptance suite project; copies the generated SQL beside itself."),
        new GeneratedFile("backend/Api.Tests/TargetDatabase.cs", TestHarness(plan), "Creates a disposable schema, applies the generated SQL, and seeds it."),
        new GeneratedFile("backend/Api.Tests/AcceptanceTests.cs", AcceptanceTests(plan), "Executes the generated API over HTTP against PostgreSQL."),
        new GeneratedFile("backend/Dockerfile", Dockerfile(), "Container image for the generated API. Built by CI, never from a workstation."),
        new GeneratedFile("deploy/containerapp.yaml", Deployment(plan), "Azure Container Apps manifest. Secrets are referenced, never embedded."),
        new GeneratedFile("BUILD.md", BuildInstructions(plan), "How to build, test, and package this output, and what remains unverified."),
    ];

    private static string Solution() =>
        """
        <Solution>
          <Project Path="Api/Api.csproj" />
          <Project Path="Api.Tests/Api.Tests.csproj" />
        </Solution>

        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ApiProject() =>
        """
        <Project Sdk="Microsoft.NET.Sdk.Web">

          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <RootNamespace>GeneratedApplication</RootNamespace>
            <AssemblyName>GeneratedApplication.Api</AssemblyName>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <InvariantGlobalization>true</InvariantGlobalization>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
          </PropertyGroup>

          <ItemGroup>
            <!-- Pinned. A floating version would make two builds of the same generated output differ. -->
            <PackageReference Include="Npgsql" Version="9.0.3" />
            <!-- Entra tokens for Azure Database for PostgreSQL. Same version the fleet itself resolves. -->
            <PackageReference Include="Azure.Identity" Version="1.13.2" />
          </ItemGroup>

        </Project>

        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string TestProject() =>
        """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <RootNamespace>GeneratedApplication.Tests</RootNamespace>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <IsPackable>false</IsPackable>
          </PropertyGroup>

          <ItemGroup>
            <FrameworkReference Include="Microsoft.AspNetCore.App" />
          </ItemGroup>

          <ItemGroup>
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
            <PackageReference Include="xunit" Version="2.9.3" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
          </ItemGroup>

          <ItemGroup>
            <ProjectReference Include="..\Api\Api.csproj" />
          </ItemGroup>

          <ItemGroup>
            <!-- The suite applies the same SQL the deployment does; a copy would be able to drift from it. -->
            <Content Include="..\..\database\*.sql" Link="database\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
          </ItemGroup>

          <ItemGroup>
            <Using Include="Xunit" />
          </ItemGroup>

        </Project>

        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Program() =>
        """"
        // Generated by the Oracle Forms Migration Fleet. Do not edit: regenerate from the mapping manifest.

        GeneratedApplication.ApiHost.Build(args).Run();

        """".Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string AppSettings() =>
        """
        {
          "Logging": {
            "LogLevel": {
              "Default": "Information",
              "Microsoft.AspNetCore": "Warning"
            }
          },
          "AllowedHosts": "*",
          "ConnectionStrings": {
            "Target": ""
          }
        }

        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Contracts() =>
        """"
        // Generated by the Oracle Forms Migration Fleet. Do not edit: regenerate from the mapping manifest.

        namespace GeneratedApplication;

        /// <summary>A row of the party lookup the operator chooses a submission's party from.</summary>
        public sealed record PartyView(long Id, string Name, bool Available);

        /// <summary>A row of the item lookup, carrying the price and the stock the server will enforce.</summary>
        public sealed record ItemView(long Id, string Name, decimal UnitPrice, long StockOnHand, bool Available);

        /// <summary>One submitted line. No price or amount is accepted here; the server computes both.</summary>
        public sealed record SubmissionLineRequest(long ItemId, long Quantity);

        /// <summary>A whole submission. Lines are applied together or not at all.</summary>
        public sealed record SubmissionRequest(long PartyId, IReadOnlyList<SubmissionLineRequest> Lines);

        /// <summary>A stored line, read back from the database rather than echoed from the request.</summary>
        public sealed record SubmissionLineView(
            long Id,
            long ItemId,
            string ItemName,
            long Quantity,
            decimal UnitPrice,
            decimal LineAmount);

        /// <summary>A stored submission and its lines.</summary>
        public sealed record SubmissionView(
            long Id,
            long PartyId,
            string PartyName,
            DateTime CreatedAt,
            decimal Total,
            string? Status,
            long? Version,
            IReadOnlyList<SubmissionLineView> Lines);

        /// <summary>A refusal. The code is stable; the message is for a human and is never parsed.</summary>
        public sealed record ProblemView(string Code, string Message);

        /// <summary>Raised when the database refused a submission for a reason the API reports as its own status.</summary>
        public sealed class SubmissionRefusedException(string code, string message, int status)
            : Exception(message)
        {
            public string Code { get; } = code;

            public int Status { get; } = status;
        }

        """".Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ApiHost()
    {
        return $$""""
        // Generated by the Oracle Forms Migration Fleet. Do not edit: regenerate from the mapping manifest.

        using System.Text.Json;
        using System.Text.Json.Serialization;
        using Azure.Core;
        using Azure.Identity;
        using Npgsql;

        namespace GeneratedApplication;

        /// <summary>
        /// The generated HTTP surface.
        ///
        /// Routes and payloads are named for the roles the mapping bound, never for the source tables behind
        /// them, so the contract does not change when the estate does and no source identifier is published to
        /// a caller. The source names live in the generated SQL, where they are traceable.
        ///
        /// These routes are the whole surface. There is no per-table CRUD endpoint, so no column that no
        /// screen needs is published, and every path under /api that is not listed answers a JSON 404.
        /// </summary>
        public static class ApiHost
        {
            internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            };

            public static WebApplication Build(string[] args)
            {
                WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

                builder.Services.ConfigureHttpJsonOptions(options =>
                {
                    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                });

                builder.Services.AddSingleton(provider =>
                {
                    IConfiguration configuration = provider.GetRequiredService<IConfiguration>();
                    string? connection = configuration.GetConnectionString("Target");

                    if (string.IsNullOrWhiteSpace(connection))
                    {
                        connection = configuration["TARGET_POSTGRES_CONNECTION"];
                    }

                    if (string.IsNullOrWhiteSpace(connection))
                    {
                        throw new InvalidOperationException(
                            "No target connection string is configured. Supply ConnectionStrings:Target or " +
                            "TARGET_POSTGRES_CONNECTION. This application never accepts one from a request.");
                    }

                    return BuildDataSource(connection, configuration);
                });

                builder.Services.AddSingleton<SubmissionStore>();

                WebApplication app = builder.Build();

                app.MapGet("/healthz", () => Results.Text("ok"));

                app.MapGet("/api/parties", async (
                    SubmissionStore store,
                    string? search,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await store.ListPartiesAsync(search, cancellationToken)));

                app.MapGet("/api/items", async (
                    SubmissionStore store,
                    string? search,
                    CancellationToken cancellationToken) =>
                    Results.Ok(await store.ListItemsAsync(search, cancellationToken)));

                app.MapPost("/api/submissions", async (
                    HttpContext context,
                    SubmissionStore store,
                    CancellationToken cancellationToken) =>
                {
                    // The body is read here rather than bound to the request record, because binding cannot
                    // tell the two failures apart. A body the parser cannot read is a syntax fault the caller
                    // must fix before anything can be said about its contents, and answers 400. A body that
                    // parses but carries something the domain refuses — a fractional quantity, a number
                    // outside what the column holds, a value of the wrong JSON type — has been understood and
                    // rejected on its merits, and answers 422 with the code the database would have used.
                    // Bound to the record, a quantity of 2.5 fails inside the deserializer and is reported as
                    // a transport error, which hides a rule this application is supposed to state.
                    if (!context.Request.HasJsonContentType())
                    {
                        return Problem("OFM00", "A submission must be sent as application/json.", 415);
                    }

                    JsonDocument body;
                    try
                    {
                        body = await JsonDocument.ParseAsync(context.Request.Body, default, cancellationToken);
                    }
                    catch (JsonException malformed)
                    {
                        return Problem("OFM00", $"The request body is not valid JSON: {malformed.Message}", 400);
                    }

                    SubmissionRequest? request;
                    string? fault;
                    using (body)
                    {
                        request = ReadSubmission(body.RootElement, out fault);
                    }

                    if (request is null)
                    {
                        return Problem("OFM03", fault ?? "The submission could not be read.", 422);
                    }

                    // Shape is checked here so an obviously malformed body never reaches the database; the
                    // database re-checks everything anyway, because it is the only place the check and the
                    // write happen in the same transaction.
                    if (request.Lines.Count == 0)
                    {
                        return Problem("OFM03", "A submission must carry at least one line.", 422);
                    }

                    if (request.Lines.Count > MaxLines)
                    {
                        return Problem("OFM03", $"A submission carries at most {MaxLines} lines.", 422);
                    }

                    if (request.Lines.Any(line => line.Quantity < 1 || line.ItemId < 1))
                    {
                        return Problem("OFM03", "Every line needs a positive item reference and a positive quantity.", 422);
                    }

                    try
                    {
                        SubmissionView created = await store.PlaceAsync(request, cancellationToken);
                        return Results.Created($"/api/submissions/{created.Id}", created);
                    }
                    catch (SubmissionRefusedException refused)
                    {
                        return Problem(refused.Code, refused.Message, refused.Status);
                    }
                });

                app.MapGet("/api/submissions/{id:long}", async (
                    long id,
                    SubmissionStore store,
                    CancellationToken cancellationToken) =>
                    await store.ReadAsync(id, cancellationToken) is { } submission
                        ? Results.Ok(submission)
                        : Problem("NOTFOUND", "No submission carries that identifier.", 404));

                app.Map("/api/{**rest}", () => Problem("NOTFOUND", "This API serves no such route.", 404));

                return app;
            }

            /// <summary>Upper bound on lines in one submission, so one request cannot hold a transaction open.</summary>
            public const int MaxLines = 200;

            /// <summary>Scope Azure Database for PostgreSQL issues access tokens under.</summary>
            private const string EntraScope = "https://ossrdbms-aad.database.windows.net/.default";

            /// <summary>
            /// Builds the data source for the configured target, choosing how it authenticates from the
            /// connection string it was given and never from anything a caller sent.
            ///
            /// A connection string that already carries a password names a local or development target and is
            /// used as it stands. That is the only path the generated acceptance suite takes, so the runtime
            /// credential and the test credential are never the same thing and no password is written into
            /// this output.
            ///
            /// A connection string without one is an Azure target authenticating with Microsoft Entra ID, and
            /// the password becomes a token. It is fetched through a periodic provider rather than once at
            /// startup because these tokens expire in about an hour: a data source that captured one when the
            /// process started would work all through a smoke test and then begin failing unattended.
            /// </summary>
            internal static NpgsqlDataSource BuildDataSource(string connectionString, IConfiguration configuration)
            {
                NpgsqlDataSourceBuilder builder = new(connectionString);

                if (!string.IsNullOrEmpty(builder.ConnectionStringBuilder.Password))
                {
                    return builder.Build();
                }

                // Named explicitly: a container carrying more than one assigned identity cannot be told which
                // one to present, and that failure surfaces as an authentication timeout rather than a clear
                // error. AZURE_CLIENT_ID is the client id of the identity granted access to the database.
                DefaultAzureCredentialOptions options = new();
                if (configuration["AZURE_CLIENT_ID"] is { Length: > 0 } clientId)
                {
                    options.ManagedIdentityClientId = clientId;
                }

                TokenCredential credential = new DefaultAzureCredential(options);
                string[] scopes = [EntraScope];

                builder.UsePeriodicPasswordProvider(
                    async (_, cancellationToken) =>
                    {
                        AccessToken token = await credential
                            .GetTokenAsync(new TokenRequestContext(scopes), cancellationToken)
                            .ConfigureAwait(false);
                        return token.Token;
                    },
                    TimeSpan.FromMinutes(50),
                    TimeSpan.FromSeconds(5));

                return builder.Build();
            }

            private static IResult Problem(string code, string message, int status) =>
                Results.Json(new ProblemView(code, message), Json, contentType: null, statusCode: status);

            /// <summary>
            /// Reads a submission out of a parsed body, or returns null and says why it could not.
            ///
            /// Everything refused here parsed as JSON, so none of it is a transport fault. The caller is
            /// told which field the application would not accept, and the submission is reported as
            /// unprocessable rather than unreadable.
            /// </summary>
            private static SubmissionRequest? ReadSubmission(JsonElement root, out string? fault)
            {
                fault = null;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    fault = "A submission must be a JSON object.";
                    return null;
                }

                if (!TryReadWholeNumber(root, "partyId", out long partyId, out fault))
                {
                    return null;
                }

                if (!root.TryGetProperty("lines", out JsonElement lines) || lines.ValueKind != JsonValueKind.Array)
                {
                    fault = "A submission must carry a lines array.";
                    return null;
                }

                List<SubmissionLineRequest> read = [];
                foreach (JsonElement line in lines.EnumerateArray())
                {
                    if (line.ValueKind != JsonValueKind.Object)
                    {
                        fault = "Every submission line must be a JSON object.";
                        return null;
                    }

                    if (!TryReadWholeNumber(line, "itemId", out long itemId, out fault) ||
                        !TryReadWholeNumber(line, "quantity", out long quantity, out fault))
                    {
                        return null;
                    }

                    read.Add(new SubmissionLineRequest(itemId, quantity));
                }

                return new SubmissionRequest(partyId, read);
            }

            /// <summary>
            /// Reads one whole number. A missing property, a value that is not a JSON number, and a number
            /// that is fractional or outside the range the target column holds are each a refusal, never a
            /// rounded or clamped value: a quantity of 2.5 that became 2 would store a line the operator
            /// never submitted and decrement stock for it.
            /// </summary>
            private static bool TryReadWholeNumber(JsonElement owner, string name, out long value, out string? fault)
            {
                value = 0;
                fault = null;

                if (!owner.TryGetProperty(name, out JsonElement element))
                {
                    fault = $"A submission needs a whole number for {name}.";
                    return false;
                }

                if (element.ValueKind != JsonValueKind.Number)
                {
                    fault = $"{name} must be a JSON number.";
                    return false;
                }

                if (!element.TryGetInt64(out value))
                {
                    fault = $"{name} must be a whole number the target column holds; {element.GetRawText()} is not.";
                    return false;
                }

                return true;
            }
        }

        """".Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Store(DotNetGenerationPlan plan)
    {
        ResolvedField partyKey = plan.Party.Require(MappedFieldRole.Identifier);
        ResolvedField partyName = plan.Party.Require(MappedFieldRole.DisplayName);
        ResolvedField itemKey = plan.Item.Require(MappedFieldRole.Identifier);
        ResolvedField itemName = plan.Item.Require(MappedFieldRole.DisplayName);
        ResolvedField itemPrice = plan.Item.Require(MappedFieldRole.UnitPrice);
        ResolvedField itemStock = plan.Item.Require(MappedFieldRole.StockOnHand);
        ResolvedField headerKey = plan.Header.Require(MappedFieldRole.Identifier);
        ResolvedField headerParty = plan.Header.Require(MappedFieldRole.PartyReference);
        ResolvedField headerTotal = plan.Header.Require(MappedFieldRole.TotalAmount);
        ResolvedField headerCreated = plan.Header.Require(MappedFieldRole.CreatedAt);
        ResolvedField detailKey = plan.Detail.Require(MappedFieldRole.Identifier);
        ResolvedField detailParent = plan.Detail.Require(MappedFieldRole.ParentReference);
        ResolvedField detailItem = plan.Detail.Require(MappedFieldRole.ItemReference);
        ResolvedField detailQuantity = plan.Detail.Require(MappedFieldRole.Quantity);
        ResolvedField detailPrice = plan.Detail.Require(MappedFieldRole.UnitPrice);
        ResolvedField detailAmount = plan.Detail.Require(MappedFieldRole.LineAmount);

        string q(string name) => DotNetGenerationPlan.Sql(name);

        string partyAvailable = plan.Party.Optional(MappedFieldRole.ActiveFlag) is { } partyFlag
            ? $"({q(partyFlag.Column.Name)} = {DotNetApplicationEmitter.Literal(plan.Party.Constant(MappedFieldRole.ActiveFlag))})"
            : "true";
        string itemAvailable = plan.Item.Optional(MappedFieldRole.ActiveFlag) is { } itemFlag
            ? $"({q(itemFlag.Column.Name)} = {DotNetApplicationEmitter.Literal(plan.Item.Constant(MappedFieldRole.ActiveFlag))})"
            : "true";
        // Whole select expressions, not bare names: an unbound optional role has no column to qualify, and
        // `h.null::text` does not parse. The alias belongs to the identifier, never to the literal.
        string headerStatus = plan.Header.Optional(MappedFieldRole.Status) is { } status
            ? $"h.{q(status.Column.Name)}"
            : "null::text";
        string headerVersion = plan.Header.Optional(MappedFieldRole.ConcurrencyVersion) is { } version
            ? $"h.{q(version.Column.Name)}"
            : "null::bigint";

        return $$""""
        // Generated by the Oracle Forms Migration Fleet. Do not edit: regenerate from the mapping manifest.

        using System.Globalization;
        using System.Text.Json;
        using Npgsql;
        using NpgsqlTypes;

        namespace GeneratedApplication;

        /// <summary>
        /// Data access over the generated schema. Every value the caller supplied travels as a parameter, and
        /// the only routine this type invokes is the generated one named by a constant here, so no request can
        /// choose what runs.
        /// </summary>
        public sealed class SubmissionStore(NpgsqlDataSource source)
        {
            private const string PartyQuery = """
                SELECT {{q(partyKey.Column.Name)}}, {{q(partyName.Column.Name)}}, {{partyAvailable}}
                  FROM {{plan.SqlTable(plan.Party)}}
                 WHERE (@search IS NULL OR {{q(partyName.Column.Name)}} ILIKE '%' || @search || '%')
                 ORDER BY {{q(partyName.Column.Name)}}, {{q(partyKey.Column.Name)}}
                 LIMIT 200
                """;

            private const string ItemQuery = """
                SELECT {{q(itemKey.Column.Name)}}, {{q(itemName.Column.Name)}}, {{q(itemPrice.Column.Name)}},
                       {{q(itemStock.Column.Name)}}, {{itemAvailable}}
                  FROM {{plan.SqlTable(plan.Item)}}
                 WHERE (@search IS NULL OR {{q(itemName.Column.Name)}} ILIKE '%' || @search || '%')
                 ORDER BY {{q(itemName.Column.Name)}}, {{q(itemKey.Column.Name)}}
                 LIMIT 200
                """;

            private const string PlaceCall = """
                SELECT header_id, header_total
                  FROM {{DotNetGenerationPlan.Sql(DotNetApplicationEmitter.RoutineName)}}(
                       @party::{{partyKey.PostgreSqlType}}, @lines::jsonb)
                """;

            private const string HeaderQuery = """
                SELECT h.{{q(headerKey.Column.Name)}}, h.{{q(headerParty.Column.Name)}}, p.{{q(partyName.Column.Name)}},
                       h.{{q(headerCreated.Column.Name)}}, h.{{q(headerTotal.Column.Name)}},
                       {{headerStatus}}, {{headerVersion}}
                  FROM {{plan.SqlTable(plan.Header)}} AS h
                  JOIN {{plan.SqlTable(plan.Party)}} AS p
                    ON p.{{q(partyKey.Column.Name)}} = h.{{q(headerParty.Column.Name)}}
                 WHERE h.{{q(headerKey.Column.Name)}} = @id
                """;

            private const string LineQuery = """
                SELECT d.{{q(detailKey.Column.Name)}}, d.{{q(detailItem.Column.Name)}}, i.{{q(itemName.Column.Name)}},
                       d.{{q(detailQuantity.Column.Name)}}, d.{{q(detailPrice.Column.Name)}}, d.{{q(detailAmount.Column.Name)}}
                  FROM {{plan.SqlTable(plan.Detail)}} AS d
                  JOIN {{plan.SqlTable(plan.Item)}} AS i
                    ON i.{{q(itemKey.Column.Name)}} = d.{{q(detailItem.Column.Name)}}
                 WHERE d.{{q(detailParent.Column.Name)}} = @id
                 ORDER BY d.{{q(detailKey.Column.Name)}}
                """;

            public async Task<IReadOnlyList<PartyView>> ListPartiesAsync(string? search, CancellationToken cancellationToken)
            {
                await using NpgsqlCommand command = source.CreateCommand(PartyQuery);
                command.Parameters.Add(new NpgsqlParameter("search", NpgsqlDbType.Text)
                {
                    Value = string.IsNullOrWhiteSpace(search) ? DBNull.Value : search,
                });

                List<PartyView> rows = [];
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new PartyView(Whole(reader, 0), reader.GetString(1).TrimEnd(), reader.GetBoolean(2)));
                }

                return rows;
            }

            public async Task<IReadOnlyList<ItemView>> ListItemsAsync(string? search, CancellationToken cancellationToken)
            {
                await using NpgsqlCommand command = source.CreateCommand(ItemQuery);
                command.Parameters.Add(new NpgsqlParameter("search", NpgsqlDbType.Text)
                {
                    Value = string.IsNullOrWhiteSpace(search) ? DBNull.Value : search,
                });

                List<ItemView> rows = [];
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new ItemView(
                        Whole(reader, 0),
                        reader.GetString(1).TrimEnd(),
                        Money(reader, 2),
                        Whole(reader, 3),
                        reader.GetBoolean(4)));
                }

                return rows;
            }

            public async Task<SubmissionView> PlaceAsync(SubmissionRequest request, CancellationToken cancellationToken)
            {
                string lines = JsonSerializer.Serialize(
                    request.Lines.Select(line => new { itemId = line.ItemId, quantity = line.Quantity }));

                long id;
                try
                {
                    await using NpgsqlCommand command = source.CreateCommand(PlaceCall);
                    command.Parameters.Add(new NpgsqlParameter("party", NpgsqlDbType.Bigint) { Value = request.PartyId });
                    command.Parameters.Add(new NpgsqlParameter("lines", NpgsqlDbType.Jsonb) { Value = lines });

                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (!await reader.ReadAsync(cancellationToken))
                    {
                        throw new SubmissionRefusedException(
                            "OFM03", "The submission produced no record.", 422);
                    }

                    id = Whole(reader, 0);
                }
                catch (PostgresException failure) when (Refusal(failure) is { } refusal)
                {
                    throw refusal;
                }

                return await ReadAsync(id, cancellationToken)
                    ?? throw new SubmissionRefusedException("OFM03", "The submission was not readable after it was written.", 409);
            }

            public async Task<SubmissionView?> ReadAsync(long id, CancellationToken cancellationToken)
            {
                await using NpgsqlConnection connection = await source.OpenConnectionAsync(cancellationToken);

                long partyId;
                string partyName;
                DateTime createdAt;
                decimal total;
                string? status;
                long? version;

                await using (NpgsqlCommand header = new(HeaderQuery, connection))
                {
                    header.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = id });
                    await using NpgsqlDataReader reader = await header.ExecuteReaderAsync(cancellationToken);
                    if (!await reader.ReadAsync(cancellationToken))
                    {
                        return null;
                    }

                    partyId = Whole(reader, 1);
                    partyName = reader.GetString(2).TrimEnd();
                    createdAt = reader.GetDateTime(3);
                    total = Money(reader, 4);
                    status = reader.IsDBNull(5) ? null : reader.GetString(5).TrimEnd();
                    version = reader.IsDBNull(6) ? null : Whole(reader, 6);
                }

                List<SubmissionLineView> lines = [];
                await using (NpgsqlCommand detail = new(LineQuery, connection))
                {
                    detail.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = id });
                    await using NpgsqlDataReader reader = await detail.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        lines.Add(new SubmissionLineView(
                            Whole(reader, 0),
                            Whole(reader, 1),
                            reader.GetString(2).TrimEnd(),
                            Whole(reader, 3),
                            Money(reader, 4),
                            Money(reader, 5)));
                    }
                }

                return new SubmissionView(id, partyId, partyName, createdAt, total, status, version, lines);
            }

            /// <summary>Maps a generated SQLSTATE to the refusal the API reports. Anything else is left to fail.</summary>
            private static SubmissionRefusedException? Refusal(PostgresException failure) => failure.SqlState switch
            {
        {{string.Join(
            "\n",
            DotNetApplicationEmitter.RoutineErrors.Select(error =>
                $"            \"{error.SqlState}\" => new SubmissionRefusedException(\"{error.SqlState}\", failure.MessageText, {error.HttpStatus.ToString(CultureInfo.InvariantCulture)}),"))}}
                _ => null,
            };

            /// <summary>Reads a whole number whatever width the mapped column resolved to.</summary>
            private static long Whole(NpgsqlDataReader reader, int ordinal) =>
                Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

            /// <summary>Reads a scaled number as decimal, so no value passes through binary floating point.</summary>
            private static decimal Money(NpgsqlDataReader reader, int ordinal) =>
                Convert.ToDecimal(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        """".Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string TestHarness(DotNetGenerationPlan plan)
    {
        ResolvedField partyKey = plan.Party.Require(MappedFieldRole.Identifier);
        ResolvedField partyName = plan.Party.Require(MappedFieldRole.DisplayName);
        ResolvedField itemKey = plan.Item.Require(MappedFieldRole.Identifier);
        ResolvedField itemName = plan.Item.Require(MappedFieldRole.DisplayName);
        ResolvedField itemPrice = plan.Item.Require(MappedFieldRole.UnitPrice);
        ResolvedField itemStock = plan.Item.Require(MappedFieldRole.StockOnHand);

        string q(string name) => DotNetGenerationPlan.Sql(name);

        List<string> partyColumns = [q(partyName.Column.Name)];
        List<string> partyValues = ["@name"];
        if (plan.Party.Optional(MappedFieldRole.ActiveFlag) is { } partyFlag)
        {
            partyColumns.Add(q(partyFlag.Column.Name));
            partyValues.Add("@available");
        }

        List<string> itemColumns = [q(itemName.Column.Name), q(itemPrice.Column.Name), q(itemStock.Column.Name)];
        List<string> itemValues = ["@name", "@price", "@stock"];
        if (plan.Item.Optional(MappedFieldRole.ActiveFlag) is { } itemFlag)
        {
            itemColumns.Add(q(itemFlag.Column.Name));
            itemValues.Add("@available");
        }

        if (plan.Item.Optional(MappedFieldRole.ConcurrencyVersion) is { } itemVersion)
        {
            itemColumns.Add(q(itemVersion.Column.Name));
            itemValues.Add("1");
        }

        string partyActiveLiteral = plan.Party.Optional(MappedFieldRole.ActiveFlag) is not null
            ? DotNetApplicationEmitter.Literal(plan.Party.Constant(MappedFieldRole.ActiveFlag))
            : "''";
        string itemActiveLiteral = plan.Item.Optional(MappedFieldRole.ActiveFlag) is not null
            ? DotNetApplicationEmitter.Literal(plan.Item.Constant(MappedFieldRole.ActiveFlag))
            : "''";

        return $$""""
        // Generated by the Oracle Forms Migration Fleet. Do not edit: regenerate from the mapping manifest.

        using System.Globalization;
        using Npgsql;
        using NpgsqlTypes;

        namespace GeneratedApplication.Tests;

        /// <summary>
        /// A disposable PostgreSQL schema holding the generated tables and routine.
        ///
        /// The suite refuses to invent a result when no target is configured: without
        /// TARGET_POSTGRES_CONNECTION there is nothing to execute against, and a test that passed anyway
        /// would be asserting that a database it never opened behaved correctly.
        /// </summary>
        public sealed class TargetDatabase : IAsyncDisposable
        {
            private readonly string _administrative;

            private TargetDatabase(string administrative, string schema, string connectionString)
            {
                _administrative = administrative;
                Schema = schema;
                ConnectionString = connectionString;
            }

            public string Schema { get; }

            /// <summary>Connection string scoped to this run's schema. It is never written to a file.</summary>
            public string ConnectionString { get; }

            public static string? ConfiguredTarget =>
                Environment.GetEnvironmentVariable("TARGET_POSTGRES_CONNECTION") is { Length: > 0 } value
                    ? value
                    : null;

            /// <summary>
            /// Whether a target is supposed to exist. Set GENERATED_SUITE_REQUIRE_TARGET=true wherever one is
            /// provisioned — CI — so a missing or unreachable database empties the run loudly instead of
            /// quietly.
            /// </summary>
            public static bool TargetRequired =>
                string.Equals(
                    Environment.GetEnvironmentVariable("GENERATED_SUITE_REQUIRE_TARGET"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);

            public static async Task<TargetDatabase> CreateAsync()
            {
                string administrative = ConfiguredTarget
                    ?? throw new InvalidOperationException("TARGET_POSTGRES_CONNECTION is not set.");
                string schema = "gen_" + Guid.NewGuid().ToString("N")[..16];

                await using (NpgsqlConnection connection = new(administrative))
                {
                    await connection.OpenAsync();
                    await ExecuteAsync(connection, $"CREATE SCHEMA \"{schema}\"");
                }

                NpgsqlConnectionStringBuilder scoped = new(administrative) { SearchPath = schema };
                TargetDatabase database = new(administrative, schema, scoped.ConnectionString);

                await using (NpgsqlConnection connection = new(database.ConnectionString))
                {
                    await connection.OpenAsync();
                    await ExecuteAsync(connection, ReadSql("schema.sql"));
                    await ExecuteAsync(connection, ReadSql("routines.sql"));
                }

                return database;
            }

            public async Task<long> AddPartyAsync(string name, bool available = true)
            {
                await using NpgsqlConnection connection = new(ConnectionString);
                await connection.OpenAsync();
                await using NpgsqlCommand command = new(
                    """
                    INSERT INTO {{plan.SqlTable(plan.Party)}} ({{string.Join(", ", partyColumns)}})
                    VALUES ({{string.Join(", ", partyValues)}})
                    RETURNING {{q(partyKey.Column.Name)}}
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });
                command.Parameters.Add(new NpgsqlParameter("available", NpgsqlDbType.Text)
                {
                    Value = available ? {{partyActiveLiteral}} : UnavailableParty,
                });

                return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }

            public async Task<long> AddItemAsync(string name, decimal unitPrice, long stockOnHand, bool available = true)
            {
                await using NpgsqlConnection connection = new(ConnectionString);
                await connection.OpenAsync();
                await using NpgsqlCommand command = new(
                    """
                    INSERT INTO {{plan.SqlTable(plan.Item)}} ({{string.Join(", ", itemColumns)}})
                    VALUES ({{string.Join(", ", itemValues)}})
                    RETURNING {{q(itemKey.Column.Name)}}
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });
                command.Parameters.Add(new NpgsqlParameter("price", NpgsqlDbType.Numeric) { Value = unitPrice });
                command.Parameters.Add(new NpgsqlParameter("stock", NpgsqlDbType.Bigint) { Value = stockOnHand });
                command.Parameters.Add(new NpgsqlParameter("available", NpgsqlDbType.Text)
                {
                    Value = available ? {{itemActiveLiteral}} : UnavailableItem,
                });

                return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }

            public async Task<long> StockOnHandAsync(long itemId)
            {
                await using NpgsqlConnection connection = new(ConnectionString);
                await connection.OpenAsync();
                await using NpgsqlCommand command = new(
                    """
                    SELECT {{q(itemStock.Column.Name)}} FROM {{plan.SqlTable(plan.Item)}}
                     WHERE {{q(itemKey.Column.Name)}} = @id
                    """,
                    connection);
                command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = itemId });
                return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }

            public async Task<long> CountAsync(string table)
            {
                await using NpgsqlConnection connection = new(ConnectionString);
                await connection.OpenAsync();
                await using NpgsqlCommand command = new($"SELECT count(*) FROM \"{table}\"", connection);
                return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            }

            public string HeaderTable { get; } = {{Quote(plan.Header.Table.Name.ToLowerInvariant())}};

            public string DetailTable { get; } = {{Quote(plan.Detail.Table.Name.ToLowerInvariant())}};

            private static string UnavailableParty => {{Quote(Unavailable(plan.Party))}};

            private static string UnavailableItem => {{Quote(Unavailable(plan.Item))}};

            public async ValueTask DisposeAsync()
            {
                await using NpgsqlConnection connection = new(_administrative);
                await connection.OpenAsync();
                await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE");
            }

            private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
            {
                await using NpgsqlCommand command = new(sql, connection);
                await command.ExecuteNonQueryAsync();
            }

            private static string ReadSql(string name) =>
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "database", name));
        }

        """".Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// A flag value the generated tests can use to make a lookup row unavailable. When the mapping declared
    /// a domain, it is any admitted value other than the available one; otherwise it is a single space,
    /// which the available constant is never allowed to be.
    /// </summary>
    private static string Unavailable(ResolvedObject entry)
    {
        if (entry.Optional(MappedFieldRole.ActiveFlag) is not { } flag)
        {
            return " ";
        }

        string available = entry.Constant(MappedFieldRole.ActiveFlag);

        foreach (OracleConstraint constraint in entry.Table.Constraints
            .Where(constraint => constraint.Kind == OracleConstraintKind.Check))
        {
            if (constraint.CheckExpression is not { Length: > 0 } expression ||
                !expression.Contains(flag.Column.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string candidate in expression.Split('\'', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = candidate.Trim();
                if (trimmed.Length == available.Length &&
                    !string.Equals(trimmed, available, StringComparison.Ordinal) &&
                    trimmed.All(char.IsLetterOrDigit))
                {
                    return trimmed;
                }
            }
        }

        return available.Length == 1 && available != " " ? " " : available + "X";
    }

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string AcceptanceTests(DotNetGenerationPlan plan)
    {
        string scale = (plan.Detail.Require(MappedFieldRole.LineAmount).Column.Scale ?? 2)
            .ToString(CultureInfo.InvariantCulture);
        string conflictCodes = plan.Optimistic ? "\"OFM04\", \"OFM05\"" : "\"OFM04\"";

        return $$""""
        // Generated by the Oracle Forms Migration Fleet. Do not edit: regenerate from the mapping manifest.

        using System.Net;
        using System.Net.Http.Json;
        using System.Text.Json;
        // The suite hosts the generated application in-process, so it needs the builder types. The test
        // project is not a web SDK project, so nothing imports them implicitly.
        using Microsoft.AspNetCore.Builder;

        namespace GeneratedApplication.Tests;

        /// <summary>
        /// Executes the generated API over HTTP against a real PostgreSQL target.
        ///
        /// Nothing here is a string assertion over generated text and nothing is replaced by an in-memory
        /// double: the assertions read rows back out of the database the API wrote to. When no target is
        /// configured the suite reports that it did not run rather than passing.
        /// </summary>
        public sealed class AcceptanceTests
        {
            /// <summary>
            /// Whether there is a database to execute against.
            ///
            /// When GENERATED_SUITE_REQUIRE_TARGET says a target is supposed to exist and none does, this
            /// fails the case rather than returning false. A suite that reports a pass for behaviour it never
            /// exercised is worse than no suite at all, because the pipeline then treats an empty run as a
            /// verified one.
            /// </summary>
            private static bool Configured
            {
                get
                {
                    if (TargetDatabase.ConfiguredTarget is not null)
                    {
                        return true;
                    }

                    Assert.False(
                        TargetDatabase.TargetRequired,
                        "GENERATED_SUITE_REQUIRE_TARGET is set and TARGET_POSTGRES_CONNECTION is not, so this " +
                        "case executed nothing against a database and must not be reported as a pass.");
                    return false;
                }
            }

            /// <summary>
            /// Reports, as a case of its own, whether this run had a database. A pipeline asserting on the
            /// result file can then require this name to have passed, which it cannot do if the suite was
            /// never built or never ran.
            /// </summary>
            [Fact]
            public void The_suite_had_a_target_to_execute_against()
            {
                Assert.False(
                    TargetDatabase.TargetRequired && TargetDatabase.ConfiguredTarget is null,
                    "GENERATED_SUITE_REQUIRE_TARGET is set and TARGET_POSTGRES_CONNECTION is not: this run " +
                    "exercised no database and verified none of the behaviour below.");
            }

            [Fact]
            public async Task A_valid_submission_stores_lines_server_computed_totals_and_decrements_stock()
            {
                if (!Configured)
                {
                    return;
                }

                await using Harness harness = await Harness.StartAsync();
                long party = await harness.Database.AddPartyAsync("Acceptance party");
                long first = await harness.Database.AddItemAsync("First item", 12.34m, 50);
                long second = await harness.Database.AddItemAsync("Second item", 0.07m, 50);

                HttpResponseMessage response = await harness.Client.PostAsJsonAsync(
                    "/api/submissions",
                    new SubmissionRequest(party, [new SubmissionLineRequest(first, 3), new SubmissionLineRequest(second, 7)]));

                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                SubmissionView created = await Read(response);

                Assert.Equal(2, created.Lines.Count);
                Assert.Equal(decimal.Round(12.34m * 3, {{scale}}) + decimal.Round(0.07m * 7, {{scale}}), created.Total);
                Assert.Equal(created.Lines.Sum(line => line.LineAmount), created.Total);
                Assert.All(created.Lines, line => Assert.Equal(decimal.Round(line.UnitPrice * line.Quantity, {{scale}}), line.LineAmount));

                Assert.Equal(47, await harness.Database.StockOnHandAsync(first));
                Assert.Equal(43, await harness.Database.StockOnHandAsync(second));

                SubmissionView reread = await Read(await harness.Client.GetAsync($"/api/submissions/{created.Id}"));
                Assert.Equal(created.Total, reread.Total);
                Assert.Equal(created.Lines.Count, reread.Lines.Count);
            }

            [Theory]
            [InlineData(0)]
            [InlineData(-4)]
            public async Task A_non_positive_quantity_changes_nothing(long quantity)
            {
                if (!Configured)
                {
                    return;
                }

                await using Harness harness = await Harness.StartAsync();
                long party = await harness.Database.AddPartyAsync("Party");
                long item = await harness.Database.AddItemAsync("Item", 5.00m, 10);

                HttpResponseMessage response = await harness.Client.PostAsJsonAsync(
                    "/api/submissions",
                    new SubmissionRequest(party, [new SubmissionLineRequest(item, quantity)]));

                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                await AssertNothingWritten(harness, item, 10);
            }

            [Fact]
            public async Task A_fractional_quantity_changes_nothing()
            {
                if (!Configured)
                {
                    return;
                }

                await using Harness harness = await Harness.StartAsync();
                long party = await harness.Database.AddPartyAsync("Party");
                long item = await harness.Database.AddItemAsync("Item", 5.00m, 10);

                // Sent as raw JSON: the typed request record would round it before it left the client.
                HttpResponseMessage response = await harness.Client.PostAsync(
                    "/api/submissions",
                    JsonContent(
                        "{\"partyId\":" + party + ",\"lines\":[{\"itemId\":" + item + ",\"quantity\":2.5}]}"));

                // The body parsed. The quantity is the thing the application refuses, so this is a rule
                // refusal with a code, not a transport error.
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                Assert.Equal("OFM03", await Code(response));
                await AssertNothingWritten(harness, item, 10);
            }

            [Theory]
            // A number no 64-bit column holds, a number written in a form this API does not accept as a
            // whole one, and values of the wrong JSON type. Each one parses, so each one is answered on
            // its merits rather than as a syntax error.
            [InlineData("99999999999999999999")]
            [InlineData("1e3")]
            [InlineData("\"2\"")]
            [InlineData("null")]
            [InlineData("true")]
            public async Task A_quantity_that_is_not_a_whole_number_changes_nothing(string quantity)
            {
                if (!Configured)
                {
                    return;
                }

                await using Harness harness = await Harness.StartAsync();
                long party = await harness.Database.AddPartyAsync("Party");
                long item = await harness.Database.AddItemAsync("Item", 5.00m, 10);

                HttpResponseMessage response = await harness.Client.PostAsync(
                    "/api/submissions",
                    JsonContent(
                        "{\"partyId\":" + party + ",\"lines\":[{\"itemId\":" + item + ",\"quantity\":" + quantity + "}]}"));

                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                Assert.Equal("OFM03", await Code(response));
                await AssertNothingWritten(harness, item, 10);
            }

            [Theory]
            // Not JSON at all. Nothing can be said about what these asked for, so they are transport
            // errors — the one case this API answers with 400 rather than 422.
            [InlineData("")]
            [InlineData("{")]
            [InlineData("{\"partyId\":1,}")]
            [InlineData("not json")]
            public async Task A_body_that_is_not_json_is_a_bad_request(string malformed)
            {
                if (!Configured)
                {
                    return;
                }

                await using Harness harness = await Harness.StartAsync();
                long item = await harness.Database.AddItemAsync("Item", 5.00m, 10);

                HttpResponseMessage response = await harness.Client.PostAsync("/api/submissions", JsonContent(malformed));

                // The code proves this application answered, rather than the host rejecting the body
                // before any of the generated code ran.
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("OFM00", await Code(response));
                await AssertNothingWritten(harness, item, 10);
            }

            [Fact]
            public async Task A_submission_missing_a_required_field_changes_nothing()
            {
                if (!Configured)
                {
                    return;
                }

                await using Harness harness = await Harness.StartAsync();
                long item = await harness.Database.AddItemAsync("Item", 5.00m, 10);

                HttpResponseMessage response = await harness.Client.PostAsync(
                    "/api/submissions",
                    JsonContent("{\"lines\":[{\"itemId\":" + item + ",\"quantity\":1}]}"));

                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                Assert.Equal("OFM03", await Code(response));
                await AssertNothingWritten(harness, item, 10);
            }

            [Fact]
            public async Task An_unknown_party_changes_nothing()
            {
                if (!Configured)
                {
                    return;
                }

                await using Harness harness = await Harness.StartAsync();
                long item = await harness.Database.AddItemAsync("Item", 5.00m, 10);

                HttpResponseMessage response = await harness.Client.PostAsJsonAsync(
                    "/api/submissions",
                    new SubmissionRequest(987654321, [new SubmissionLineRequest(item, 1)]));

                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                Assert.Equal("OFM01", await Code(response));
                await AssertNothingWritten(harness, item, 10);
            }

            [Fact]
            public async Task An_unknown_item_changes_nothing()
            {
                if (!Configured)
                {
                    return;
                }

                await using Harness harness = await Harness.StartAsync();
                long party = await harness.Database.AddPartyAsync("Party");
                long item = await harness.Database.AddItemAsync("Item", 5.00m, 10);

                HttpResponseMessage response = await harness.Client.PostAsJsonAsync(
                    "/api/submissions",
                    new SubmissionRequest(party, [new SubmissionLineRequest(item, 1), new SubmissionLineRequest(987654321, 1)]));

                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                Assert.Equal("OFM02", await Code(response));

                // The valid line came first. If the refusal did not roll the whole submission back, this
                // item's stock would already have been decremented.
                await AssertNothingWritten(harness, item, 10);
            }

            [Fact]
            public async Task Insufficient_stock_changes_nothing()
            {
                if (!Configured)
                {
                    return;
                }

                await using Harness harness = await Harness.StartAsync();
                long party = await harness.Database.AddPartyAsync("Party");
                long plentiful = await harness.Database.AddItemAsync("Plentiful", 1.00m, 100);
                long scarce = await harness.Database.AddItemAsync("Scarce", 1.00m, 2);

                HttpResponseMessage response = await harness.Client.PostAsJsonAsync(
                    "/api/submissions",
                    new SubmissionRequest(party, [new SubmissionLineRequest(plentiful, 5), new SubmissionLineRequest(scarce, 3)]));

                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("OFM04", await Code(response));
                Assert.Equal(100, await harness.Database.StockOnHandAsync(plentiful));
                Assert.Equal(2, await harness.Database.StockOnHandAsync(scarce));
                Assert.Equal(0, await harness.Database.CountAsync(harness.Database.HeaderTable));
                Assert.Equal(0, await harness.Database.CountAsync(harness.Database.DetailTable));
            }

            [Fact]
            public async Task Concurrent_submissions_cannot_oversell()
            {
                if (!Configured)
                {
                    return;
                }

                await using Harness harness = await Harness.StartAsync();
                long party = await harness.Database.AddPartyAsync("Party");
                const long stock = 20;
                const long perSubmission = 3;
                long item = await harness.Database.AddItemAsync("Contended", 2.50m, stock);

                HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
                    harness.Client.PostAsJsonAsync(
                        "/api/submissions",
                        new SubmissionRequest(party, [new SubmissionLineRequest(item, perSubmission)]))));

                int accepted = responses.Count(response => response.StatusCode == HttpStatusCode.Created);
                foreach (HttpResponseMessage refused in responses.Where(response => response.StatusCode != HttpStatusCode.Created))
                {
                    Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                    Assert.Contains(await Code(refused), new[] { {{conflictCodes}} });
                }

                long remaining = await harness.Database.StockOnHandAsync(item);
                Assert.True(remaining >= 0, $"Stock went negative: {remaining}.");
                Assert.Equal(stock - (accepted * perSubmission), remaining);
                Assert.Equal(accepted, await harness.Database.CountAsync(harness.Database.HeaderTable));
                Assert.Equal(accepted, await harness.Database.CountAsync(harness.Database.DetailTable));
            }

            [Fact]
            public async Task Lookups_exclude_rows_the_mapping_marks_unavailable()
            {
                if (!Configured)
                {
                    return;
                }

                await using Harness harness = await Harness.StartAsync();
                await harness.Database.AddPartyAsync("Available party");
                await harness.Database.AddItemAsync("Available item", 1.00m, 1);

                PartyView[] parties = await harness.Client.GetFromJsonAsync<PartyView[]>("/api/parties") ?? [];
                ItemView[] items = await harness.Client.GetFromJsonAsync<ItemView[]>("/api/items") ?? [];

                Assert.Contains(parties, party => party.Name == "Available party" && party.Available);
                Assert.Contains(items, item => item.Name == "Available item" && item.Available && item.UnitPrice == 1.00m);
            }

            [Fact]
            public async Task An_unrouted_api_path_answers_a_json_404()
            {
                if (!Configured)
                {
                    return;
                }

                await using Harness harness = await Harness.StartAsync();
                HttpResponseMessage response = await harness.Client.GetAsync("/api/anything-else");

                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal("NOTFOUND", await Code(response));
            }

            private static async Task AssertNothingWritten(Harness harness, long item, long expectedStock)
            {
                Assert.Equal(expectedStock, await harness.Database.StockOnHandAsync(item));
                Assert.Equal(0, await harness.Database.CountAsync(harness.Database.HeaderTable));
                Assert.Equal(0, await harness.Database.CountAsync(harness.Database.DetailTable));
            }

            private static StringContent JsonContent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");

            private static async Task<SubmissionView> Read(HttpResponseMessage response)
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<SubmissionView>()
                    ?? throw new InvalidOperationException("The API returned an empty body.");
            }

            private static async Task<string> Code(HttpResponseMessage response)
            {
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return document.RootElement.GetProperty("code").GetString() ?? string.Empty;
            }
        }

        /// <summary>Starts the generated application on a loopback port bound to a disposable schema.</summary>
        public sealed class Harness : IAsyncDisposable
        {
            private readonly WebApplication _app;

            private Harness(WebApplication app, TargetDatabase database, HttpClient client)
            {
                _app = app;
                Database = database;
                Client = client;
            }

            public TargetDatabase Database { get; }

            public HttpClient Client { get; }

            public static async Task<Harness> StartAsync()
            {
                TargetDatabase database = await TargetDatabase.CreateAsync();
                WebApplication app = ApiHost.Build([
                    "--urls=http://127.0.0.1:0",
                    $"--ConnectionStrings:Target={database.ConnectionString}",
                ]);

                await app.StartAsync();
                string address = app.Urls.First();

                return new Harness(app, database, new HttpClient { BaseAddress = new Uri(address) });
            }

            public async ValueTask DisposeAsync()
            {
                Client.Dispose();
                await _app.StopAsync();
                await _app.DisposeAsync();
                await Database.DisposeAsync();
            }
        }

        """".Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Dockerfile() =>
        """
        # Generated by the Oracle Forms Migration Fleet.
        # Build this on CI from a commit. An image that exists only because someone ran this on a workstation
        # cannot be reproduced, reviewed, or rolled back.
        FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
        WORKDIR /src
        COPY backend/Api/Api.csproj Api/
        RUN dotnet restore Api/Api.csproj
        COPY backend/Api/ Api/
        RUN dotnet publish Api/Api.csproj -c Release -o /app --no-restore

        FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
        WORKDIR /app
        COPY --from=build /app ./
        # No credential is baked in. The connection string arrives as configuration at run time.
        ENV ASPNETCORE_URLS=http://+:8080
        EXPOSE 8080
        USER $APP_UID
        ENTRYPOINT ["dotnet", "GeneratedApplication.Api.dll"]

        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Deployment(DotNetGenerationPlan plan) =>
        $"""
        # Azure Container Apps manifest generated by the Oracle Forms Migration Fleet.
        # Fill in the registry, environment, and identity for the target subscription before applying.
        # The connection string is referenced as a secret; it is never written into this file.
        properties:
          managedEnvironmentId: <managed-environment-resource-id>
          configuration:
            ingress:
              external: true
              targetPort: 8080
              transport: auto
            secrets:
              - name: target-postgres-connection
                keyVaultUrl: <key-vault-secret-uri>
                identity: <user-assigned-identity-resource-id>
            registries:
              - server: <registry>.azurecr.io
                identity: <user-assigned-identity-resource-id>
          template:
            containers:
              - name: generated-api
                image: <registry>.azurecr.io/{plan.ApplicationName.ToLowerInvariant().Replace(' ', '-')}-api:<commit-sha>
                env:
                  - name: TARGET_POSTGRES_CONNECTION
                    secretRef: target-postgres-connection
                  # Client id of the identity above. Without it a host carrying several identities cannot
                  # tell which one to present, and the failure looks like a connection timeout.
                  - name: AZURE_CLIENT_ID
                    value: <user-assigned-identity-client-id>
                probes:
                  - type: Liveness
                    httpGet:
                      path: /healthz
                      port: 8080
                resources:
                  cpu: 0.5
                  memory: 1Gi
            scale:
              minReplicas: 1
              maxReplicas: 3

        """.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string BuildInstructions(DotNetGenerationPlan plan) =>
        $"""
        # Building and verifying this generated output

        Generated by `{DotNetApplicationEmitter.GeneratorVersion}` from mapping manifest
        `{plan.Mapping.Declaration.SchemaVersion}` (fixture label `{plan.Mapping.Declaration.FixtureLabel}`).
        Nothing below has been run by the generator.

        ## Dependencies

        Every dependency is pinned. `backend/Api/Api.csproj` pins Npgsql, `backend/Api.Tests/Api.Tests.csproj`
        pins the test runner, and `frontend/package-lock.json` locks the whole front-end tree. Do not relax a
        version to make a build pass; two builds of the same generated output have to produce the same result.

        ## Back end

        ```
        dotnet build backend/GeneratedBackend.slnx -c Release
        ```

        ## Target database

        `database/schema.sql` creates the mapped tables. `database/routines.sql` creates
        `{DotNetApplicationEmitter.RoutineName}`, which performs a whole submission in one transaction. Apply
        both, in that order, to the schema the application will use.

        ## Executable acceptance tests

        The suite starts this application and drives it over HTTP against a real PostgreSQL instance. Point it
        at a database you are willing to have schemas created and dropped in. Supply the connection string
        through the environment; never write one into this file, into `appsettings.json`, or into the repository:

        ```
        TARGET_POSTGRES_CONNECTION="Host=<host>;Database=<database>;Username=<principal>;SSL Mode=Require" \
        GENERATED_SUITE_REQUIRE_TARGET=true \
          dotnet test backend/GeneratedBackend.slnx -c Release --logger "trx;LogFileName=generated.trx"
        ```

        `GENERATED_SUITE_REQUIRE_TARGET=true` makes a missing or unset connection string a failure instead of
        an empty run. Set it everywhere a database is supposed to exist. Without it the suite still refuses to
        invent a result, but it reports nothing, and a pipeline reading only the exit code would take that
        silence for success. Read the `.trx` and require the named cases to have passed rather than trusting
        the exit code.

        The connection string is for a test target only. Point it at a database you are willing to have
        schemas created in and dropped from, never at the one this application serves in production, and keep
        it in the environment — it must not be written into this output or into a build artifact.

        ## Runtime credentials

        The deployed application does not use that variable and holds no password. When
        `ConnectionStrings:Target` (or `TARGET_POSTGRES_CONNECTION`) carries no password, `ApiHost` treats the
        target as Azure Database for PostgreSQL with Microsoft Entra authentication and supplies the password
        as a token from `DefaultAzureCredential`, refreshed periodically for the life of the process.

        | Variable | Purpose |
        | --- | --- |
        | `ConnectionStrings:Target` / `TARGET_POSTGRES_CONNECTION` | Host, database, and the Entra principal in `Username`. No password. |
        | `AZURE_CLIENT_ID` | Client id of the user-assigned managed identity granted access. Required whenever the host carries more than one identity. |

        Grant that identity a PostgreSQL role before the first deployment; the application cannot create one
        for itself.

        ## Front end

        ```
        cd frontend && npm ci && npm run build && npm run test
        ```

        `npm ci` installs exactly what the lock file names and runs no install script.

        ## Container image

        `backend/Dockerfile` builds the API. Build it on CI from a commit, tagged with that commit, so the
        deployed tag maps back to source someone can read. `deploy/containerapp.yaml` references the
        connection string as a secret; it holds no credential and must not be edited to hold one.

        ## What is still unverified

        - No behaviour was recovered from an Oracle Forms runtime. The generated screens and rules come from
          the roles the mapping manifest bound and from this generator's template.
        - Columns the manifest bound no role to are created but never read or written. See
          `mapping-manifest.json`.
        - Passing these tests says this application behaves as specified. It does not say it behaves as the
          Oracle original did; nothing here was compared against a running source system.

        """.Replace("\r\n", "\n", StringComparison.Ordinal);
}
