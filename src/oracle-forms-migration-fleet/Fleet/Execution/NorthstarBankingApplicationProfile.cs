// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// Recognises the retail banking workflow from the schema alone and generates a working replacement for
/// it, instead of the CRUD-over-tables shape the generic emitter produces.
///
/// Recognition is a structural fingerprint and fails closed. Every workflow table has to be present with
/// every column the generated code reads or writes, each at the expected base type, precision, scale, and
/// nullability; the primary keys, the uniqueness, foreign key, and domain check constraints the generated
/// SQL depends on have to be declared; and the identifier sequences have to exist and step by one. The
/// application name is never consulted, because a name proves nothing about what the database holds.
/// Anything missing or differently shaped runs the generic path unchanged.
///
/// Provenance matters here. The endpoints, validation rules, and balance arithmetic come from schema
/// evidence — table shapes, check constraints, and sequences — plus this fleet's own workflow template.
/// No behaviour is claimed to have been recovered from a Forms module: the Forms XML for this estate
/// covers a single block, and nothing in the generated service is derived from it.
/// </summary>
public static class NorthstarBankingApplicationProfile
{
    /// <summary>
    /// A column the generated code depends on, and the shape it has to have. <c>NotNull</c> is satisfied by
    /// a declared NOT NULL or by membership of the primary key, which carries the same guarantee.
    /// </summary>
    private sealed record ColumnShape(string Name, string BaseType, int? Precision, int? Scale, bool NotNull);

    /// <summary>What one workflow table has to declare for the generated SQL against it to be sound.</summary>
    private sealed record TableShape(
        ColumnShape[] Columns,
        string[] PrimaryKey,
        string[][] Unique,
        (string[] Columns, string Table, string[] ReferencedColumns)[] ForeignKeys,
        string[] Checks);

    private static ColumnShape Number(string name, int precision, int? scale, bool notNull) =>
        new(name, "NUMBER", precision, scale, notNull);

    private static ColumnShape Text(string name, int length, bool notNull) =>
        new(name, "VARCHAR2", length, null, notNull);

    private static ColumnShape Fixed(string name, int length, bool notNull) =>
        new(name, "CHAR", length, null, notNull);

    private static ColumnShape Raw(string name, int length, bool notNull) =>
        new(name, "RAW", length, null, notNull);

    private static ColumnShape Date(string name, bool notNull) => new(name, "DATE", null, null, notNull);

    private static ColumnShape Timestamp(string name, bool notNull) => new(name, "TIMESTAMP", null, null, notNull);

    /// <summary>
    /// The fingerprint. Every entry is something the generated service actually relies on: a column it
    /// reads or writes, a key it joins or locks on, a domain the validation layer mirrors, or a sequence it
    /// draws identifiers from. Nothing decorative is required, and nothing required is optional.
    /// </summary>
    private static readonly FrozenDictionary<string, TableShape> s_required = new Dictionary<string, TableShape>(StringComparer.OrdinalIgnoreCase)
    {
        ["BANK_ACCOUNT_REQUEST"] = new(
            [
                Number("REQUEST_ID", 10, null, true),
                Text("BRANCH_CODE", 12, true),
                Text("ACCOUNT_KIND", 12, true),
                Text("HONORIFIC", 8, false),
                Text("GIVEN_NAME", 40, true),
                Text("FAMILY_NAME", 40, true),
                Date("DATE_OF_BIRTH", true),
                Text("WORK_PHONE", 20, false),
                Text("HOME_PHONE", 20, false),
                Text("STREET_ADDRESS", 120, true),
                Text("REGION_CODE", 20, true),
                Text("POSTAL_CODE", 12, true),
                Text("EMAIL_ADDRESS", 120, true),
                Text("REQUEST_STATUS", 12, true),
                Timestamp("SUBMITTED_AT", true),
                Timestamp("DECIDED_AT", false),
            ],
            ["REQUEST_ID"],
            [],
            [],
            ["ACCOUNT_KIND IN('SAVINGS','CHECKING')", "REQUEST_STATUS IN('SUBMITTED','APPROVED','REJECTED')"]),

        ["BANK_ACCOUNT"] = new(
            [
                Number("ACCOUNT_ID", 10, null, true),
                Number("REQUEST_ID", 10, null, true),
                Text("BRANCH_CODE", 12, true),
                Text("ACCOUNT_KIND", 12, true),
                Date("OPENED_ON", true),
                Fixed("ONLINE_ENABLED", 1, true),
                Raw("ONLINE_PASSWORD_HASH", 32, false),
            ],
            ["ACCOUNT_ID"],
            [["REQUEST_ID"]],
            [(["REQUEST_ID"], "BANK_ACCOUNT_REQUEST", ["REQUEST_ID"])],
            ["ONLINE_ENABLED IN('Y','N')"]),

        ["BANK_STAFF_USER"] = new(
            [
                Number("STAFF_ID", 10, null, true),
                Text("USERNAME", 40, true),
                Raw("PASSWORD_HASH", 32, true),
                Text("ROLE_CODE", 12, true),
                Fixed("ACTIVE_FLAG", 1, true),
            ],
            ["STAFF_ID"],
            [["USERNAME"]],
            [],
            ["ROLE_CODE IN('MANAGER','AUDITOR')"]),

        ["BANK_TRANSACTION"] = new(
            [
                Number("TRANSACTION_ID", 10, null, true),
                Number("ACCOUNT_ID", 10, null, true),
                Timestamp("TRANSACTION_TS", true),
                Number("AMOUNT", 12, 2, true),
                Text("REFERENCE_CODE", 30, true),
                Fixed("DIRECTION_CODE", 2, true),
            ],
            ["TRANSACTION_ID"],
            [],
            [(["ACCOUNT_ID"], "BANK_ACCOUNT", ["ACCOUNT_ID"])],
            ["DIRECTION_CODE IN('CR','DR')"]),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Identifier sequences the generated service draws from. Each must step by exactly one.</summary>
    private static readonly string[] s_requiredSequences =
    [
        "BANK_REQUEST_SEQ", "BANK_ACCOUNT_SEQ", "BANK_TRANSACTION_SEQ",
    ];

    /// <summary>
    /// Database objects whose behaviour the generated workflow service reimplements in full, identified by
    /// kind and exact name. Matching is never by substring: a different object whose name merely contains
    /// one of these is not this object, and claiming otherwise would overstate what was migrated.
    /// </summary>
    private static readonly (OracleProgramUnitKind Kind, string Name)[] s_coveredProgramUnits =
    [
        (OracleProgramUnitKind.PackageSpecification, "LEGACY_BANKING_API"),
        (OracleProgramUnitKind.PackageBody, "LEGACY_BANKING_API"),
        (OracleProgramUnitKind.Trigger, "BANK_TRANSACTION_BI"),
        (OracleProgramUnitKind.Trigger, "BANK_ACCOUNT_REQUEST_BI"),
    ];

    /// <summary>Every route the generated service exposes, in the order the source application declared them.</summary>
    public static IReadOnlyList<string> Routes { get; } =
    [
        "GET /healthz",
        "GET /api/health",
        "POST /api/customer/login",
        "POST /api/manager/login",
        "POST /api/online-registration",
        "POST /api/account-requests",
        "POST /api/interest",
        "GET /api/customer/statement",
        "POST /api/customer/transactions",
        "GET /api/manager/requests",
        "POST /api/manager/requests/{requestId}/approve",
        "DELETE /api/session",
    ];

    /// <summary>The browser modules the generated client carries over from the source application.</summary>
    public static IReadOnlyList<string> Modules { get; } =
    [
        "Home", "Open Account", "Online Registration", "Interest Calculator", "Customer Login",
        "Manager Login", "Account Statement", "Transaction Entry", "Account Requests",
    ];

    /// <summary>
    /// True only when the schema carries the complete workflow fingerprint. Fails closed: a table, column,
    /// type, precision, scale, nullability, key, constraint, or sequence that does not match exactly sends
    /// the whole conversion down the generic path.
    /// </summary>
    public static bool Matches(OracleSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        foreach ((string tableName, TableShape shape) in s_required)
        {
            OracleTable? table = schema.Tables.FirstOrDefault(
                candidate => string.Equals(candidate.Name, tableName, StringComparison.OrdinalIgnoreCase));

            if (table is null || !MatchesShape(table, shape))
            {
                return false;
            }
        }

        foreach (string sequenceName in s_requiredSequences)
        {
            OracleSequence? sequence = schema.Sequences.FirstOrDefault(
                candidate => string.Equals(candidate.Name, sequenceName, StringComparison.OrdinalIgnoreCase));

            // Oracle's default increment is one, so an undeclared INCREMENT BY is the step the code assumes.
            if (sequence is null || (sequence.IncrementBy ?? 1L) != 1L)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesShape(OracleTable table, TableShape shape)
    {
        IReadOnlyList<string> primaryKey = Columns(table, OracleConstraintKind.PrimaryKey).FirstOrDefault() ?? [];

        foreach (ColumnShape expected in shape.Columns)
        {
            OracleColumn? column = table.Columns.FirstOrDefault(
                candidate => string.Equals(candidate.Name, expected.Name, StringComparison.OrdinalIgnoreCase));

            if (column is null
                || !string.Equals(column.BaseType, expected.BaseType, StringComparison.OrdinalIgnoreCase)
                || column.Precision != expected.Precision
                || column.Scale != expected.Scale)
            {
                return false;
            }

            bool notNull = column.NotNull || primaryKey.Contains(column.Name, StringComparer.OrdinalIgnoreCase);
            if (expected.NotNull != notNull)
            {
                return false;
            }
        }

        if (!SameColumns(primaryKey, shape.PrimaryKey))
        {
            return false;
        }

        foreach (string[] unique in shape.Unique)
        {
            if (!Columns(table, OracleConstraintKind.Unique).Any(declared => SameColumns(declared, unique)))
            {
                return false;
            }
        }

        foreach ((string[] columns, string referencedTable, string[] referencedColumns) in shape.ForeignKeys)
        {
            bool declared = table.Constraints.Any(constraint =>
                constraint.Kind == OracleConstraintKind.ForeignKey
                && SameColumns(constraint.Columns, columns)
                && string.Equals(constraint.ReferencedTable, referencedTable, StringComparison.OrdinalIgnoreCase)
                && SameColumns(constraint.ReferencedColumns, referencedColumns));

            if (!declared)
            {
                return false;
            }
        }

        HashSet<string> checks =
        [
            .. table.Constraints
                .Where(constraint => constraint.Kind == OracleConstraintKind.Check && constraint.CheckExpression is not null)
                .Select(constraint => NormalizeCheck(constraint.CheckExpression!)),
        ];

        return shape.Checks.All(checks.Contains);
    }

    private static IEnumerable<IReadOnlyList<string>> Columns(OracleTable table, OracleConstraintKind kind) =>
        table.Constraints.Where(constraint => constraint.Kind == kind).Select(constraint => constraint.Columns);

    private static bool SameColumns(IReadOnlyList<string> declared, IReadOnlyList<string> expected) =>
        declared.Count == expected.Count
        && declared.Zip(expected).All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Folds away the layout differences between two spellings of the same domain constraint. The
    /// expressions this fingerprint requires are membership tests over short unquoted codes, so collapsing
    /// whitespace around the punctuation cannot change what is being compared.
    /// </summary>
    private static string NormalizeCheck(string expression)
    {
        StringBuilder builder = new(expression.Length);
        bool pendingSpace = false;

        foreach (char character in expression.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            bool punctuation = character is '(' or ')' or ',';
            if (pendingSpace && !punctuation && builder.Length > 0 && builder[^1] is not ('(' or ','))
            {
                builder.Append(' ');
            }

            pendingSpace = false;
            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// True when the generated workflow service already implements this program unit's behaviour. The unit
    /// is matched on its parsed kind and exact name; nothing is inferred from the text of its body.
    /// </summary>
    public static bool Covers(OracleProgramUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        return s_coveredProgramUnits.Any(covered =>
            covered.Kind == unit.Kind && string.Equals(covered.Name, unit.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Generates the workflow service and the browser client. Pure: no clock, no file, no network.</summary>
    public static IReadOnlyList<GeneratedFile> Generate(
        OracleSchema schema, string basePackage, IReadOnlyList<OracleTable> tables)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrWhiteSpace(basePackage);

        SequenceBinding requests = Bind(schema, "BANK_REQUEST_SEQ", 1001);
        SequenceBinding accounts = Bind(schema, "BANK_ACCOUNT_SEQ", 500001);
        SequenceBinding transactions = Bind(schema, "BANK_TRANSACTION_SEQ", 9001);

        string package = $"{basePackage}.banking";
        string directory = $"backend/src/main/java/{package.Replace('.', '/')}";
        string testDirectory = $"backend/src/test/java/{package.Replace('.', '/')}";

        // The table routes the generic emitter would have published. The generated test asserts none of
        // them is reachable, so a regression that brings the CRUD controllers back fails the build.
        IReadOnlyList<string> absentRoutes =
            [.. tables.Select(table => "/api/" + table.Name.ToLowerInvariant().Replace('_', '-')).Order(StringComparer.Ordinal)];

        return
        [
            new GeneratedFile($"{directory}/BankingContracts.java", Contracts(package), "Request and response shapes carried over from the source application."),
            new GeneratedFile($"{directory}/BankingValidation.java", Validation(package), "Field validation and normalisation carried over from the source application."),
            new GeneratedFile($"{directory}/PasswordHashing.java", PasswordHashing(package), "SHA-256 hashing with a constant-time comparison against the migrated bytea column."),
            new GeneratedFile($"{directory}/BankingSessionStore.java", SessionStore(package), "In-memory opaque bearer sessions with a role and an expiry."),
            new GeneratedFile($"{directory}/BankingSequences.java", Sequences(package, requests, accounts, transactions), "Advances each identifier sequence past the migrated rows under an advisory lock, and fails startup if it cannot."),
            new GeneratedFile($"{directory}/BankingRepository.java", Repository(package), "Transactional PostgreSQL access for every workflow."),
            new GeneratedFile($"{directory}/BankingController.java", Controller(package), "The source application's HTTP routes, status codes, and response shapes."),
            new GeneratedFile($"{directory}/BankingApiFallbackController.java", ApiFallbackController(package), "One JSON 404 shape for any /api path the workflow service does not implement."),
            new GeneratedFile($"{directory}/BankingSpaController.java", SpaController(package), "Serves the browser client for its own routes without ever intercepting /api."),
            new GeneratedFile($"{testDirectory}/BankingControllerTest.java", ControllerTest(package), "MockMvc tests for routing, validation, role protection, health, and the JSON 404."),
            new GeneratedFile($"{testDirectory}/GeneratedSurfaceTest.java", SurfaceTest(package, basePackage, absentRoutes, tables), "Asserts the unauthorized per-table CRUD controllers are absent from the build and the context."),
            new GeneratedFile("frontend/index.html", BrowserShell(), "Browser client shell carrying the source application's nine modules."),
            new GeneratedFile("frontend/public/app.js", BrowserScript(), "Browser client behaviour, calling every workflow route."),
            new GeneratedFile("frontend/public/styles.css", BrowserStyles(), "Browser client presentation, unchanged from the source application."),
            new GeneratedFile("frontend/tests/app.test.js", BrowserInteractionTest(), "Executable browser interaction test for generated Northstar navigation."),
            new GeneratedFile("frontend/package.json", BrowserPackage(), "Vite build for the static browser client. No framework dependency."),
            new GeneratedFile("frontend/package-lock.json", GeneratedApplicationVerificationTemplates.Read("Northstar/package-lock.json"), "Pinned dependency lock for offline generated UI verification."),
            new GeneratedFile("frontend/vite.config.ts", BrowserViteConfig(), "Vite production build configuration."),
        ];
    }

    /// <summary>Findings that replace the generic emitter's "nothing but CRUD was generated" wording.</summary>
    public static IReadOnlyList<ConversionFinding> Findings() =>
    [
        new ConversionFinding(
            ConversionSeverity.Note,
            "Application workflows",
            string.Join(", ", Routes),
            "The schema carries every table and column the retail banking workflows read and write, so a working " +
            "replacement for them was generated rather than CRUD over tables. Each route keeps the source " +
            "application's path, status codes, and response shape, and runs against PostgreSQL."),
        new ConversionFinding(
            ConversionSeverity.Note,
            "Server-side logic",
            "Balance, simple interest, request approval, identifier defaults",
            "These behaviours are reimplemented in the generated workflow service from schema evidence — the " +
            "sequences, the CR/DR check constraint, and the request status constraint. They are not recovered " +
            "from a Forms module, and the approval path runs as one transaction so the account insert and the " +
            "request update cannot land separately."),
        new ConversionFinding(
            ConversionSeverity.Note,
            "User interface",
            string.Join(", ", Modules),
            "The browser client is generated from this fleet's Northstar workflow template and reproduces the " +
            "source application's modules and navigation. It is template-driven, not extracted from a Forms " +
            "module: the only Forms XML available for this estate covers one block, and nothing here derives " +
            "from it. Screens outside these modules are not covered."),
        new ConversionFinding(
            ConversionSeverity.Note,
            "Authorization",
            "Per-table CRUD endpoints",
            "No per-table CRUD controller was generated. Publishing one beside the workflow routes would expose " +
            "every column of every table, including the migrated password hashes, and accept unvalidated writes " +
            "with no session or role check. The only HTTP surface is the workflow routes, the health endpoints, " +
            "and a JSON 404 for anything else under /api."),
        new ConversionFinding(
            ConversionSeverity.ManualReview,
            "Credentials",
            "SHA-256 password hashes",
            "The migrated hashes are unsalted SHA-256, because that is what the source stored. The generated " +
            "service compares them in constant time but cannot strengthen them; re-enrol every credential onto " +
            "a memory-hard hash before this handles real customers."),
    ];

    public static string Readme(string applicationName, int tables)
    {
        StringBuilder builder = new();
        builder.AppendLine($"# {applicationName} — migrated application tier").AppendLine();
        builder.AppendLine("Generated from the converted Oracle schema. Oracle is not in the data path: the back end talks to");
        builder.AppendLine("Azure Database for PostgreSQL using Entra authentication, so no database password exists.").AppendLine();
        builder.AppendLine("## Demonstrated version scope").AppendLine();
        builder.AppendLine("This generated pilot represents an Oracle Forms `12.2.1.4`-style synthetic XML export backed by");
        builder.AppendLine("Oracle Database Free 23, migrated to Azure Database for PostgreSQL 16. The Forms XML is hand-authored");
        builder.AppendLine("test evidence, not an export from a licensed Forms runtime. This is not a general compatibility claim");
        builder.AppendLine("for other Oracle Forms or Oracle Database releases. See `docs/COMPATIBILITY.md` in the fleet repository.").AppendLine();
        builder.AppendLine("## What is here").AppendLine();
        builder.AppendLine($"- {tables.ToString(CultureInfo.InvariantCulture)} JPA entities and Spring Data repositories");
        builder.AppendLine("- A workflow service that reimplements the source application's endpoints over PostgreSQL:").AppendLine();

        foreach (string route in Routes)
        {
            builder.AppendLine($"  - `{route}`");
        }

        builder.AppendLine().AppendLine("- A browser client with the source application's modules: " + string.Join(", ", Modules) + ".");
        builder.AppendLine("- Spring Boot tests over the generated routes, run with `mvn test`.").AppendLine();
        builder.AppendLine("## Where the behaviour came from").AppendLine();
        builder.AppendLine("The schema declares every table and column these workflows use, so the generator recognised the");
        builder.AppendLine("workflow and emitted a working replacement instead of CRUD screens. Balance, simple interest, and");
        builder.AppendLine("approval are reimplemented from schema evidence — sequences, the CR/DR constraint, the request");
        builder.AppendLine("status constraint — and from this fleet's workflow template. **Nothing here was recovered from a");
        builder.AppendLine("Forms module.** The only Forms XML available for this estate covers a single block.").AppendLine();
        builder.AppendLine("## What is deliberately not here").AppendLine();
        builder.AppendLine("- **Per-table CRUD controllers.** They would publish every column of every table, the migrated");
        builder.AppendLine("  password hashes included, and accept unvalidated writes with no session or role check, so none");
        builder.AppendLine("  was generated. Any path under `/api` that is not a workflow route answers 404.");
        builder.AppendLine("- **Stronger credentials.** Passwords stay unsalted SHA-256 because that is what was migrated.");
        builder.AppendLine("- **Screens outside the modules listed above.**").AppendLine();
        builder.AppendLine("## Running it").AppendLine();
        builder.AppendLine("```bash");
        builder.AppendLine("export PGHOST=<server>.postgres.database.azure.com");
        builder.AppendLine("export PGDATABASE=postgres");
        builder.AppendLine("export PGUSER=<managed identity name>");
        builder.AppendLine("cd backend && mvn spring-boot:run");
        builder.AppendLine("```");

        return builder.ToString();
    }

    private sealed record SequenceBinding(string Name, long Floor);

    /// <summary>
    /// Uses the sequence the converter emitted when the schema declared one, and otherwise falls back to the
    /// canonical name. <see cref="SequenceBinding.Floor"/> is the last value handed out before the declared
    /// start, so a freshly created sequence produces the same first identifier the source would have.
    /// </summary>
    private static SequenceBinding Bind(OracleSchema schema, string preferred, long defaultStart)
    {
        OracleSequence? declared = schema.Sequences.FirstOrDefault(
            sequence => string.Equals(sequence.Name, preferred, StringComparison.OrdinalIgnoreCase));

        long start = declared?.StartWith ?? defaultStart;
        return new SequenceBinding((declared?.Name ?? preferred).ToLowerInvariant(), Math.Max(start - 1, 1));
    }

    private static string Contracts(string package) =>
        $$"""
        package {{package}};

        import java.math.BigDecimal;
        import java.time.LocalDate;
        import java.time.LocalDateTime;
        import java.time.OffsetDateTime;
        import java.util.List;

        /**
         * The wire shapes the source application used, field for field. Serialised names are already camel
         * case, so the migrated browser client reads the same JSON it read before.
         */
        public final class BankingContracts {

            private BankingContracts() {
            }

            public record CustomerLoginRequest(Long accountId, String password) {
            }

            public record ManagerLoginRequest(String username, String password) {
            }

            public record OnlineRegistrationRequest(Long accountId, String emailAddress, String password) {
            }

            public record AccountRequestSubmission(
                    String branchCode,
                    String accountKind,
                    String honorific,
                    String givenName,
                    String familyName,
                    LocalDate dateOfBirth,
                    String workPhone,
                    String homePhone,
                    String streetAddress,
                    String regionCode,
                    String postalCode,
                    String emailAddress) {
            }

            public record InterestRequest(BigDecimal principal, BigDecimal annualRate, BigDecimal years) {
            }

            public record TransactionRequest(BigDecimal amount, String directionCode, String referenceCode) {
            }

            public record ErrorResponse(String error) {
            }

            public record HealthResponse(String status, String database) {
            }

            public record CustomerProfile(long accountId, String accountHolder, String branchCode, String accountKind) {
            }

            public record CustomerLoginResponse(String token, OffsetDateTime expiresAt, CustomerProfile profile) {
            }

            public record ManagerLoginResponse(String token, OffsetDateTime expiresAt, String username) {
            }

            public record OnlineRegistrationResponse(long accountId, boolean onlineEnabled) {
            }

            public record AccountRequestCreated(long requestId, String requestStatus) {
            }

            public record InterestResponse(
                    BigDecimal principal,
                    BigDecimal annualRate,
                    BigDecimal years,
                    BigDecimal interest,
                    BigDecimal total) {
            }

            public record StatementLine(
                    long transactionId,
                    LocalDateTime transactionTs,
                    String directionCode,
                    BigDecimal amount,
                    String referenceCode) {
            }

            public record StatementResponse(
                    CustomerProfile profile,
                    BigDecimal currentBalance,
                    List<StatementLine> transactions) {
            }

            public record TransactionCreated(
                    long transactionId,
                    LocalDateTime transactionTs,
                    String directionCode,
                    BigDecimal amount,
                    String referenceCode,
                    BigDecimal currentBalance) {
            }

            public record AccountRequestSummary(
                    long requestId,
                    String branchCode,
                    String accountKind,
                    String honorific,
                    String givenName,
                    String familyName,
                    LocalDateTime dateOfBirth,
                    String emailAddress,
                    String requestStatus,
                    LocalDateTime submittedAt,
                    LocalDateTime decidedAt) {
            }

            public record ApprovalResponse(long requestId, long accountId, String requestStatus) {
            }
        }

        """;

    private static string Validation(string package) =>
        $$"""
        package {{package}};

        import {{package}}.BankingContracts.AccountRequestSubmission;
        import {{package}}.BankingContracts.InterestRequest;

        import java.math.BigDecimal;
        import java.time.LocalDate;
        import java.time.Period;
        import java.util.List;
        import java.util.Locale;
        import java.util.regex.Pattern;

        /**
         * The source application's field rules, reproduced so a request rejected before is still rejected with
         * the same message. Bounds that also exist as column lengths or check constraints in the converted
         * schema are kept here too, because a 400 is a better answer than a constraint violation.
         */
        public final class BankingValidation {

            public static final int MIN_PASSWORD_LENGTH = 8;
            public static final int MAX_PASSWORD_LENGTH = 64;
            public static final BigDecimal MAX_TRANSACTION_AMOUNT = new BigDecimal("1000000");

            private static final List<String> ACCOUNT_KINDS = List.of("SAVINGS", "CHECKING");
            private static final List<String> REQUEST_STATUSES = List.of("SUBMITTED", "APPROVED", "REJECTED");
            private static final Pattern REFERENCE_CODE_PATTERN = Pattern.compile("^[A-Za-z0-9][A-Za-z0-9/_-]*$");
            private static final Pattern EMAIL_PATTERN = Pattern.compile("^[^@\\s]+@[^@\\s.]+(\\.[^@\\s.]+)+$");
            private static final BigDecimal MAX_PRINCIPAL = new BigDecimal("100000000");
            private static final BigDecimal ONE_HUNDRED = new BigDecimal("100");

            private BankingValidation() {
            }

            /** A validated value, or the message explaining why the request was rejected. */
            public record Normalized<T>(String error, T value) {

                public static <T> Normalized<T> rejectedWith(String error) {
                    return new Normalized<>(error, null);
                }

                public static <T> Normalized<T> accepted(T value) {
                    return new Normalized<>(null, value);
                }

                public boolean isRejected() {
                    return error != null;
                }
            }

            public static String validateAccountId(Long accountId) {
                return accountId == null || accountId <= 0L || accountId > 9_999_999_999L
                        ? "accountId must be a positive account number."
                        : null;
            }

            public static String validatePassword(String password) {
                return password == null
                        || password.length() < MIN_PASSWORD_LENGTH
                        || password.length() > MAX_PASSWORD_LENGTH
                        ? "password must be between " + MIN_PASSWORD_LENGTH + " and " + MAX_PASSWORD_LENGTH + " characters."
                        : null;
            }

            public static String validateEmail(String email) {
                return isBlank(email) || email.length() > 120 || !EMAIL_PATTERN.matcher(email.trim()).matches()
                        ? "emailAddress must be a valid email address of at most 120 characters."
                        : null;
            }

            public static String validateUsername(String username) {
                return isBlank(username) || username.trim().length() > 40
                        ? "username is required and must be at most 40 characters."
                        : null;
            }

            public static String validateAmount(BigDecimal amount) {
                if (amount == null || amount.signum() <= 0) {
                    return "amount must be greater than zero.";
                }

                if (amount.compareTo(MAX_TRANSACTION_AMOUNT) > 0) {
                    return "amount must not exceed "
                            + String.format(Locale.ROOT, "%,d", MAX_TRANSACTION_AMOUNT.longValue()) + ".";
                }

                return amount.stripTrailingZeros().scale() > 2 ? "amount must have at most two decimal places." : null;
            }

            public static Normalized<String> directionCode(String directionCode) {
                String normalized = directionCode == null ? "" : directionCode.trim().toUpperCase(Locale.ROOT);
                return "CR".equals(normalized) || "DR".equals(normalized)
                        ? Normalized.accepted(normalized)
                        : Normalized.rejectedWith("directionCode must be either 'CR' or 'DR'.");
            }

            public static Normalized<String> referenceCode(String referenceCode) {
                String normalized = referenceCode == null ? "" : referenceCode.trim();
                if (normalized.isEmpty() || normalized.length() > 30) {
                    return Normalized.rejectedWith("referenceCode is required and must be at most 30 characters.");
                }

                return REFERENCE_CODE_PATTERN.matcher(normalized).matches()
                        ? Normalized.accepted(normalized)
                        : Normalized.rejectedWith("referenceCode may only contain letters, digits, '-', '_' and '/'.");
            }

            public static Normalized<String> requestStatus(String status) {
                String normalized = isBlank(status) ? "SUBMITTED" : status.trim().toUpperCase(Locale.ROOT);
                return REQUEST_STATUSES.contains(normalized)
                        ? Normalized.accepted(normalized)
                        : Normalized.rejectedWith("status must be one of " + String.join(", ", REQUEST_STATUSES) + ".");
            }

            public static String validateInterest(InterestRequest request) {
                if (request.principal() == null
                        || request.principal().signum() <= 0
                        || request.principal().compareTo(MAX_PRINCIPAL) > 0) {
                    return "principal must be greater than zero and at most 100,000,000.";
                }

                if (request.annualRate() == null
                        || request.annualRate().signum() <= 0
                        || request.annualRate().compareTo(ONE_HUNDRED) > 0) {
                    return "annualRate must be greater than zero and at most 100.";
                }

                if (request.years() == null
                        || request.years().signum() <= 0
                        || request.years().compareTo(ONE_HUNDRED) > 0) {
                    return "years must be greater than zero and at most 100.";
                }

                return null;
            }

            public static Normalized<AccountRequestSubmission> accountRequest(AccountRequestSubmission request) {
                AccountRequestSubmission normalized = new AccountRequestSubmission(
                        upper(request.branchCode()),
                        upper(request.accountKind()),
                        nullIfBlank(request.honorific()),
                        trim(request.givenName()),
                        trim(request.familyName()),
                        request.dateOfBirth(),
                        nullIfBlank(request.workPhone()),
                        nullIfBlank(request.homePhone()),
                        trim(request.streetAddress()),
                        upper(request.regionCode()),
                        trim(request.postalCode()),
                        trim(request.emailAddress()));

                String error = required(normalized.branchCode(), "branchCode", 12);
                if (error == null && !ACCOUNT_KINDS.contains(normalized.accountKind())) {
                    error = "accountKind must be one of " + String.join(", ", ACCOUNT_KINDS) + ".";
                }
                if (error == null) {
                    error = optional(normalized.honorific(), "honorific", 8);
                }
                if (error == null) {
                    error = required(normalized.givenName(), "givenName", 40);
                }
                if (error == null) {
                    error = required(normalized.familyName(), "familyName", 40);
                }
                if (error == null) {
                    error = validateDateOfBirth(normalized.dateOfBirth());
                }
                if (error == null) {
                    error = optional(normalized.workPhone(), "workPhone", 20);
                }
                if (error == null) {
                    error = optional(normalized.homePhone(), "homePhone", 20);
                }
                if (error == null) {
                    error = required(normalized.streetAddress(), "streetAddress", 120);
                }
                if (error == null) {
                    error = required(normalized.regionCode(), "regionCode", 20);
                }
                if (error == null) {
                    error = required(normalized.postalCode(), "postalCode", 12);
                }
                if (error == null) {
                    error = validateEmail(normalized.emailAddress());
                }

                return error == null ? Normalized.accepted(normalized) : Normalized.rejectedWith(error);
            }

            private static String validateDateOfBirth(LocalDate dateOfBirth) {
                if (dateOfBirth == null) {
                    return "dateOfBirth is required.";
                }

                int age = Period.between(dateOfBirth, LocalDate.now()).getYears();
                return age < 18 || age > 120
                        ? "dateOfBirth must belong to an applicant aged between 18 and 120."
                        : null;
            }

            private static String required(String value, String field, int maxLength) {
                return isBlank(value) || value.length() > maxLength
                        ? field + " is required and must be at most " + maxLength + " characters."
                        : null;
            }

            private static String optional(String value, String field, int maxLength) {
                return value != null && !value.isEmpty() && value.length() > maxLength
                        ? field + " must be at most " + maxLength + " characters."
                        : null;
            }

            private static boolean isBlank(String value) {
                return value == null || value.trim().isEmpty();
            }

            private static String trim(String value) {
                return value == null ? null : value.trim();
            }

            private static String upper(String value) {
                return value == null ? null : value.trim().toUpperCase(Locale.ROOT);
            }

            private static String nullIfBlank(String value) {
                return isBlank(value) ? null : value.trim();
            }
        }

        """;

    private static string PasswordHashing(string package) =>
        $$"""
        package {{package}};

        import java.nio.charset.StandardCharsets;
        import java.security.MessageDigest;
        import java.security.NoSuchAlgorithmException;

        /**
         * The source stored STANDARD_HASH(password, 'SHA256') in a RAW(32) column, which the schema conversion
         * carried across to bytea unchanged. Hashing the same UTF-8 bytes here keeps every migrated credential
         * valid without a reset, and the comparison is constant time, so a wrong password takes as long as a
         * right one however many leading bytes matched.
         */
        public final class PasswordHashing {

            private PasswordHashing() {
            }

            public static byte[] sha256(String password) {
                try {
                    return MessageDigest.getInstance("SHA-256").digest(password.getBytes(StandardCharsets.UTF_8));
                } catch (NoSuchAlgorithmException cause) {
                    throw new IllegalStateException("SHA-256 is required of every Java platform.", cause);
                }
            }

            public static boolean matches(byte[] stored, String password) {
                if (stored == null || password == null) {
                    return false;
                }

                return MessageDigest.isEqual(stored, sha256(password));
            }
        }

        """;

    private static string SessionStore(string package) =>
        $$"""
        package {{package}};

        import org.springframework.stereotype.Component;

        import java.security.SecureRandom;
        import java.time.Duration;
        import java.time.OffsetDateTime;
        import java.util.Base64;
        import java.util.Map;
        import java.util.concurrent.ConcurrentHashMap;
        import java.util.concurrent.atomic.AtomicInteger;

        /**
         * Bearer sessions held in memory, as the source application held them. The token is opaque random
         * material with nothing encoded inside it, so a client cannot read or forge a role out of one, and
         * every session disappears on restart. Expired entries are swept as tokens are issued rather than on a
         * timer, so an idle process holds nothing.
         */
        @Component
        public class BankingSessionStore {

            public enum Role {
                CUSTOMER,
                MANAGER
            }

            public record Session(Role role, Long accountId, String displayName, OffsetDateTime expiresAt) {
            }

            public record Issued(String token, Session session) {
            }

            private static final Duration LIFETIME = Duration.ofHours(2);
            private static final int SWEEP_INTERVAL = 32;
            private static final int TOKEN_BYTES = 32;

            private final Map<String, Session> sessions = new ConcurrentHashMap<>();
            private final SecureRandom random = new SecureRandom();
            private final AtomicInteger issuedSinceSweep = new AtomicInteger();

            public Issued create(Role role, Long accountId, String displayName) {
                if (issuedSinceSweep.incrementAndGet() >= SWEEP_INTERVAL) {
                    issuedSinceSweep.set(0);
                    removeExpired();
                }

                byte[] material = new byte[TOKEN_BYTES];
                random.nextBytes(material);

                String token = Base64.getUrlEncoder().withoutPadding().encodeToString(material);
                Session session = new Session(role, accountId, displayName, OffsetDateTime.now().plus(LIFETIME));
                sessions.put(token, session);
                return new Issued(token, session);
            }

            public Session resolve(String token) {
                if (token == null || token.isEmpty()) {
                    return null;
                }

                Session session = sessions.get(token);
                if (session == null) {
                    return null;
                }

                if (session.expiresAt().isAfter(OffsetDateTime.now())) {
                    return session;
                }

                sessions.remove(token);
                return null;
            }

            public void revoke(String token) {
                if (token != null && !token.isEmpty()) {
                    sessions.remove(token);
                }
            }

            private void removeExpired() {
                OffsetDateTime now = OffsetDateTime.now();
                sessions.entrySet().removeIf(entry -> !entry.getValue().expiresAt().isAfter(now));
            }
        }

        """;

    private static string Sequences(string package, SequenceBinding requests, SequenceBinding accounts, SequenceBinding transactions) =>
        $$""""
        package {{package}};

        import org.slf4j.Logger;
        import org.slf4j.LoggerFactory;
        import org.springframework.boot.context.event.ApplicationReadyEvent;
        import org.springframework.context.event.EventListener;
        import org.springframework.jdbc.core.JdbcTemplate;
        import org.springframework.stereotype.Component;
        import org.springframework.transaction.annotation.Transactional;

        /**
         * Migrated rows keep the identifiers they had in Oracle, but a sequence restored from DDL starts where
         * the DDL said it started. Left alone it would hand out values that already exist, and the first insert
         * after a data load would fail on the primary key.
         *
         * Alignment therefore runs once at startup, inside a single transaction, and it only ever moves a
         * sequence forward. Three properties matter and each is enforced here rather than assumed:
         *
         * <ul>
         *   <li><b>It never lowers a sequence.</b> Each setval takes GREATEST of the sequence's own last value,
         *       the largest identifier in the table, and the floor the converted DDL declared. A replica that
         *       starts while another replica is already issuing identifiers cannot claw one back.</li>
         *   <li><b>It is safe across rolling replicas.</b> Every replica takes the same advisory transaction
         *       lock first, so alignment is serialised across the whole cluster and released when the
         *       transaction ends, including when it ends by failing.</li>
         *   <li><b>It fails startup rather than continuing.</b> Nothing is caught. A sequence left behind the
         *       migrated rows would fail every insert with a duplicate key, so a process that could not align
         *       one must not go on to serve traffic and be reported healthy.</li>
         * </ul>
         *
         * The sequence and table names below are compile-time constants taken from the converted schema, never
         * from a request, so the statements they are concatenated into cannot be influenced by a caller.
         */
        @Component
        public class BankingSequences {

            public static final String REQUEST_SEQUENCE = "{{requests.Name}}";
            public static final String ACCOUNT_SEQUENCE = "{{accounts.Name}}";
            public static final String TRANSACTION_SEQUENCE = "{{transactions.Name}}";

            /**
             * A constant chosen by this generator and shared by every replica of this application. Advisory
             * locks live in one cluster-wide space, so the value only has to be stable and unlikely to collide.
             */
            public static final long ALIGNMENT_LOCK_KEY = {{AdvisoryLockKey(requests, accounts, transactions).ToString(CultureInfo.InvariantCulture)}}L;

            private static final Logger LOG = LoggerFactory.getLogger(BankingSequences.class);

            private final JdbcTemplate jdbc;

            public BankingSequences(JdbcTemplate jdbc) {
                this.jdbc = jdbc;
            }

            @EventListener(ApplicationReadyEvent.class)
            @Transactional
            public void align() {
                // Held until this transaction ends, so the three setvals below cannot interleave with another
                // replica's. pg_advisory_xact_lock has no unlock call and none is needed.
                jdbc.queryForList("SELECT pg_advisory_xact_lock(?)", ALIGNMENT_LOCK_KEY);

                align(REQUEST_SEQUENCE, "bank_account_request", "request_id", {{requests.Floor.ToString(CultureInfo.InvariantCulture)}}L);
                align(ACCOUNT_SEQUENCE, "bank_account", "account_id", {{accounts.Floor.ToString(CultureInfo.InvariantCulture)}}L);
                align(TRANSACTION_SEQUENCE, "bank_transaction", "transaction_id", {{transactions.Floor.ToString(CultureInfo.InvariantCulture)}}L);
            }

            /**
             * Moves one sequence to the highest of its own last value, the largest identifier in its table, and
             * the floor, then marks it as called so the next value is one past that.
             *
             * pg_sequences.last_value reads null for a sequence that has never been handed out, which is
             * exactly the case where the floor from the converted DDL has to win. The join resolves the
             * sequence through regclass rather than by comparing names, so the current search_path decides
             * which schema is meant and a same-named sequence in another schema cannot be picked up.
             */
            private void align(String sequence, String table, String column, long floor) {
                Long aligned = jdbc.queryForObject("""
                        SELECT setval(
                                   '%s',
                                   GREATEST(
                                       COALESCE((SELECT s.last_value
                                                   FROM pg_sequences s
                                                   JOIN pg_class c ON c.relname = s.sequencename
                                                   JOIN pg_namespace n ON n.oid = c.relnamespace
                                                                     AND n.nspname = s.schemaname
                                                  WHERE c.oid = '%s'::regclass), 0),
                                       COALESCE((SELECT MAX(%s) FROM %s), 0),
                                       %d)::bigint,
                                   true)
                        """.formatted(sequence, sequence, column, table, floor),
                        Long.class);

                if (aligned == null) {
                    throw new IllegalStateException(
                            "Sequence " + sequence + " could not be aligned past the migrated rows.");
                }

                LOG.info("Sequence {} now continues from {}.", sequence, aligned);
            }
        }

        """";

    /// <summary>
    /// A stable advisory lock key derived from the sequence names this application aligns. Deterministic, so
    /// every replica of the same generated application computes the same value and they serialise against
    /// each other; derived from the names, so a different application is unlikely to collide with it.
    /// </summary>
    private static long AdvisoryLockKey(params SequenceBinding[] sequences)
    {
        ulong hash = 14695981039346656037UL;

        foreach (char character in string.Join('|', sequences.Select(sequence => sequence.Name)))
        {
            hash = (hash ^ character) * 1099511628211UL;
        }

        // Folded into a non-negative signed range so the generated Java literal is a plain positive long.
        return (long)(hash & 0x7FFFFFFFFFFFFFFFUL);
    }

    private static string Repository(string package) =>
        $$""""
        package {{package}};

        import {{package}}.BankingContracts.AccountRequestCreated;
        import {{package}}.BankingContracts.AccountRequestSubmission;
        import {{package}}.BankingContracts.AccountRequestSummary;
        import {{package}}.BankingContracts.CustomerProfile;
        import {{package}}.BankingContracts.StatementLine;
        import {{package}}.BankingContracts.StatementResponse;
        import {{package}}.BankingContracts.TransactionCreated;
        import org.springframework.dao.DataAccessException;
        import org.springframework.jdbc.core.JdbcTemplate;
        import org.springframework.jdbc.core.RowMapper;
        import org.springframework.stereotype.Repository;
        import org.springframework.transaction.annotation.Transactional;

        import java.math.BigDecimal;
        import java.math.RoundingMode;
        import java.sql.Date;
        import java.sql.ResultSet;
        import java.sql.SQLException;
        import java.sql.Timestamp;
        import java.time.LocalDateTime;
        import java.util.List;

        /**
         * Every workflow's data access, against PostgreSQL only. Statements are parameterised, and each
         * multi-statement mutation is annotated so it commits or rolls back as one unit — approval in
         * particular must not be able to create an account without also deciding the request.
         */
        @Repository
        public class BankingRepository {

            public enum RegistrationOutcome {
                ENABLED,
                ACCOUNT_NOT_FOUND,
                ALREADY_ENABLED
            }

            public enum ApprovalOutcome {
                APPROVED,
                REQUEST_NOT_FOUND,
                ALREADY_DECIDED
            }

            public record Approval(ApprovalOutcome outcome, Long accountId) {
            }

            private record PendingRequest(String branchCode, String accountKind, String requestStatus) {
            }

            private record Credential(String subject, CustomerProfile profile, byte[] hash) {
            }

            private static final String PROFILE_SQL = """
                    SELECT a.account_id,
                           r.given_name || ' ' || r.family_name AS account_holder,
                           a.branch_code,
                           a.account_kind
                      FROM bank_account a
                      JOIN bank_account_request r ON r.request_id = a.request_id
                     WHERE a.account_id = ?
                    """;

            private static final String CUSTOMER_CREDENTIAL_SQL = """
                    SELECT a.account_id,
                           r.given_name || ' ' || r.family_name AS account_holder,
                           a.branch_code,
                           a.account_kind,
                           a.online_password_hash
                      FROM bank_account a
                      JOIN bank_account_request r ON r.request_id = a.request_id
                     WHERE a.account_id = ?
                       AND a.online_enabled = 'Y'
                    """;

            private static final String MANAGER_CREDENTIAL_SQL = """
                    SELECT username, password_hash
                      FROM bank_staff_user
                     WHERE username = lower(trim(?))
                       AND role_code = 'MANAGER'
                       AND active_flag = 'Y'
                    """;

            private static final RowMapper<CustomerProfile> PROFILE_MAPPER = (resultSet, row) -> readProfile(resultSet);

            private final JdbcTemplate jdbc;

            public BankingRepository(JdbcTemplate jdbc) {
                this.jdbc = jdbc;
            }

            public boolean databaseAvailable() {
                try {
                    return Integer.valueOf(1).equals(jdbc.queryForObject("SELECT 1", Integer.class));
                } catch (DataAccessException cause) {
                    return false;
                }
            }

            /** Reads the stored hash and compares it here, so the supplied password never reaches the database. */
            public CustomerProfile authenticateCustomer(long accountId, String password) {
                Credential credential = single(jdbc.query(
                        CUSTOMER_CREDENTIAL_SQL,
                        (resultSet, row) -> new Credential(
                                null, readProfile(resultSet), resultSet.getBytes("online_password_hash")),
                        accountId));

                return credential != null && PasswordHashing.matches(credential.hash(), password)
                        ? credential.profile()
                        : null;
            }

            public String authenticateManager(String username, String password) {
                Credential credential = single(jdbc.query(
                        MANAGER_CREDENTIAL_SQL,
                        (resultSet, row) -> new Credential(
                                resultSet.getString("username"), null, resultSet.getBytes("password_hash")),
                        username));

                return credential != null && PasswordHashing.matches(credential.hash(), password)
                        ? credential.subject()
                        : null;
            }

            @Transactional
            public RegistrationOutcome enableOnlineAccess(long accountId, String emailAddress, String password) {
                String onlineEnabled = single(jdbc.query("""
                        SELECT a.online_enabled
                          FROM bank_account a
                          JOIN bank_account_request r ON r.request_id = a.request_id
                         WHERE a.account_id = ?
                           AND lower(r.email_address) = lower(?)
                           FOR UPDATE OF a
                        """, (resultSet, row) -> resultSet.getString(1), accountId, emailAddress));

                if (onlineEnabled == null) {
                    return RegistrationOutcome.ACCOUNT_NOT_FOUND;
                }

                if ("Y".equals(onlineEnabled.trim())) {
                    return RegistrationOutcome.ALREADY_ENABLED;
                }

                int updated = jdbc.update("""
                        UPDATE bank_account
                           SET online_enabled = 'Y',
                               online_password_hash = ?
                         WHERE account_id = ?
                           AND online_enabled = 'N'
                        """, PasswordHashing.sha256(password), accountId);

                return updated == 1 ? RegistrationOutcome.ENABLED : RegistrationOutcome.ALREADY_ENABLED;
            }

            @Transactional
            public AccountRequestCreated createAccountRequest(AccountRequestSubmission request) {
                long requestId = nextValue(BankingSequences.REQUEST_SEQUENCE);

                jdbc.update("""
                        INSERT INTO bank_account_request
                            (request_id, branch_code, account_kind, honorific, given_name, family_name,
                             date_of_birth, work_phone, home_phone, street_address, region_code, postal_code,
                             email_address, request_status, submitted_at, decided_at)
                        VALUES
                            (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'SUBMITTED', localtimestamp, NULL)
                        """,
                        requestId,
                        request.branchCode(),
                        request.accountKind(),
                        request.honorific(),
                        request.givenName(),
                        request.familyName(),
                        Date.valueOf(request.dateOfBirth()),
                        request.workPhone(),
                        request.homePhone(),
                        request.streetAddress(),
                        request.regionCode(),
                        request.postalCode(),
                        request.emailAddress());

                return new AccountRequestCreated(requestId, "SUBMITTED");
            }

            /**
             * Simple interest, as LEGACY_BANKING_API.CALCULATE_SIMPLE_INTEREST computed it. Oracle's ROUND is
             * half away from zero; the inputs are validated positive, so HALF_UP produces the same value.
             */
            public BigDecimal simpleInterest(BigDecimal principal, BigDecimal annualRate, BigDecimal years) {
                return principal.multiply(annualRate)
                        .multiply(years)
                        .divide(new BigDecimal("100"), 2, RoundingMode.HALF_UP);
            }

            public StatementResponse statement(long accountId) {
                CustomerProfile profile = single(jdbc.query(PROFILE_SQL, PROFILE_MAPPER, accountId));
                if (profile == null) {
                    return null;
                }

                List<StatementLine> lines = jdbc.query("""
                        SELECT transaction_id, transaction_ts, direction_code, amount, reference_code
                          FROM bank_transaction
                         WHERE account_id = ?
                         ORDER BY transaction_ts, transaction_id
                        """, (resultSet, row) -> new StatementLine(
                        resultSet.getLong("transaction_id"),
                        localDateTime(resultSet.getTimestamp("transaction_ts")),
                        trimmed(resultSet.getString("direction_code")),
                        resultSet.getBigDecimal("amount"),
                        resultSet.getString("reference_code")), accountId);

                return new StatementResponse(profile, currentBalance(accountId), lines);
            }

            @Transactional
            public TransactionCreated recordTransaction(
                    long accountId, BigDecimal amount, String directionCode, String referenceCode) {
                long transactionId = nextValue(BankingSequences.TRANSACTION_SEQUENCE);

                jdbc.update("""
                        INSERT INTO bank_transaction
                            (transaction_id, account_id, transaction_ts, amount, reference_code, direction_code)
                        VALUES
                            (?, ?, localtimestamp, ?, ?, ?)
                        """, transactionId, accountId, amount, referenceCode, directionCode);

                LocalDateTime transactionTs = single(jdbc.query(
                        "SELECT transaction_ts FROM bank_transaction WHERE transaction_id = ?",
                        (resultSet, row) -> localDateTime(resultSet.getTimestamp(1)),
                        transactionId));

                if (transactionTs == null) {
                    return null;
                }

                return new TransactionCreated(
                        transactionId, transactionTs, directionCode, amount, referenceCode, currentBalance(accountId));
            }

            public List<AccountRequestSummary> listAccountRequests(String requestStatus) {
                return jdbc.query("""
                        SELECT request_id, branch_code, account_kind, honorific, given_name, family_name,
                               date_of_birth, email_address, request_status, submitted_at, decided_at
                          FROM bank_account_request
                         WHERE request_status = ?
                         ORDER BY submitted_at, request_id
                        """, (resultSet, row) -> new AccountRequestSummary(
                        resultSet.getLong("request_id"),
                        resultSet.getString("branch_code"),
                        resultSet.getString("account_kind"),
                        resultSet.getString("honorific"),
                        resultSet.getString("given_name"),
                        resultSet.getString("family_name"),
                        startOfDay(resultSet, "date_of_birth"),
                        resultSet.getString("email_address"),
                        trimmed(resultSet.getString("request_status")),
                        localDateTime(resultSet.getTimestamp("submitted_at")),
                        localDateTime(resultSet.getTimestamp("decided_at"))), requestStatus);
            }

            /**
             * LEGACY_BANKING_API.APPROVE_REQUEST, reimplemented. The row is locked before it is read, so two
             * managers cannot both see SUBMITTED, and the account insert and the status update share one
             * transaction, so an approved request can never exist without its account.
             */
            @Transactional
            public Approval approve(long requestId) {
                PendingRequest request = single(jdbc.query("""
                        SELECT branch_code, account_kind, request_status
                          FROM bank_account_request
                         WHERE request_id = ?
                           FOR UPDATE
                        """, (resultSet, row) -> new PendingRequest(
                        resultSet.getString("branch_code"),
                        resultSet.getString("account_kind"),
                        trimmed(resultSet.getString("request_status"))), requestId));

                if (request == null) {
                    return new Approval(ApprovalOutcome.REQUEST_NOT_FOUND, null);
                }

                if (!"SUBMITTED".equals(request.requestStatus())) {
                    return new Approval(ApprovalOutcome.ALREADY_DECIDED, null);
                }

                long accountId = nextValue(BankingSequences.ACCOUNT_SEQUENCE);

                jdbc.update("""
                        INSERT INTO bank_account
                            (account_id, request_id, branch_code, account_kind, opened_on,
                             online_enabled, online_password_hash)
                        VALUES
                            (?, ?, ?, ?, current_date, 'N', NULL)
                        """, accountId, requestId, request.branchCode(), request.accountKind());

                jdbc.update("""
                        UPDATE bank_account_request
                           SET request_status = 'APPROVED',
                               decided_at = localtimestamp
                         WHERE request_id = ?
                        """, requestId);

                return new Approval(ApprovalOutcome.APPROVED, accountId);
            }

            /** LEGACY_BANKING_API.CURRENT_BALANCE: credits add, debits subtract, no rows means zero. */
            private BigDecimal currentBalance(long accountId) {
                BigDecimal balance = jdbc.queryForObject("""
                        SELECT COALESCE(SUM(CASE direction_code WHEN 'CR' THEN amount WHEN 'DR' THEN -amount END), 0)
                          FROM bank_transaction
                         WHERE account_id = ?
                        """, BigDecimal.class, accountId);

                return balance == null ? BigDecimal.ZERO : balance;
            }

            private long nextValue(String sequence) {
                Long value = jdbc.queryForObject("SELECT nextval('" + sequence + "')", Long.class);
                if (value == null) {
                    throw new IllegalStateException("Sequence " + sequence + " did not return a value.");
                }

                return value;
            }

            private static <T> T single(List<T> rows) {
                return rows.isEmpty() ? null : rows.get(0);
            }

            private static CustomerProfile readProfile(ResultSet resultSet) throws SQLException {
                return new CustomerProfile(
                        resultSet.getLong("account_id"),
                        resultSet.getString("account_holder"),
                        resultSet.getString("branch_code"),
                        resultSet.getString("account_kind"));
            }

            private static LocalDateTime startOfDay(ResultSet resultSet, String column) throws SQLException {
                Date value = resultSet.getDate(column);
                return value == null ? null : value.toLocalDate().atStartOfDay();
            }

            private static LocalDateTime localDateTime(Timestamp value) {
                return value == null ? null : value.toLocalDateTime();
            }

            /** Fixed-length character columns arrive space padded; the source compared unpadded codes. */
            private static String trimmed(String value) {
                return value == null ? null : value.trim();
            }
        }

        """";

    private static string Controller(string package) =>
        $$"""
        package {{package}};

        import {{package}}.BankingContracts.AccountRequestCreated;
        import {{package}}.BankingContracts.AccountRequestSubmission;
        import {{package}}.BankingContracts.AccountRequestSummary;
        import {{package}}.BankingContracts.ApprovalResponse;
        import {{package}}.BankingContracts.CustomerLoginRequest;
        import {{package}}.BankingContracts.CustomerLoginResponse;
        import {{package}}.BankingContracts.CustomerProfile;
        import {{package}}.BankingContracts.ErrorResponse;
        import {{package}}.BankingContracts.HealthResponse;
        import {{package}}.BankingContracts.InterestRequest;
        import {{package}}.BankingContracts.InterestResponse;
        import {{package}}.BankingContracts.ManagerLoginRequest;
        import {{package}}.BankingContracts.ManagerLoginResponse;
        import {{package}}.BankingContracts.OnlineRegistrationRequest;
        import {{package}}.BankingContracts.OnlineRegistrationResponse;
        import {{package}}.BankingContracts.StatementResponse;
        import {{package}}.BankingContracts.TransactionCreated;
        import {{package}}.BankingContracts.TransactionRequest;
        import {{package}}.BankingRepository.Approval;
        import {{package}}.BankingRepository.ApprovalOutcome;
        import {{package}}.BankingRepository.RegistrationOutcome;
        import {{package}}.BankingSessionStore.Issued;
        import {{package}}.BankingSessionStore.Role;
        import {{package}}.BankingSessionStore.Session;
        import {{package}}.BankingValidation.Normalized;
        import org.slf4j.Logger;
        import org.slf4j.LoggerFactory;
        import org.springframework.dao.DataAccessException;
        import org.springframework.http.HttpStatus;
        import org.springframework.http.ResponseEntity;
        import org.springframework.web.bind.annotation.DeleteMapping;
        import org.springframework.web.bind.annotation.ExceptionHandler;
        import org.springframework.web.bind.annotation.GetMapping;
        import org.springframework.web.bind.annotation.PathVariable;
        import org.springframework.web.bind.annotation.PostMapping;
        import org.springframework.web.bind.annotation.RequestBody;
        import org.springframework.web.bind.annotation.RequestHeader;
        import org.springframework.web.bind.annotation.RequestParam;
        import org.springframework.web.bind.annotation.RestController;

        import java.math.BigDecimal;
        import java.net.URI;
        import java.util.List;

        /**
         * The source application's HTTP surface, path for path and status code for status code, so the migrated
         * browser client needs no change. A database failure surfaces as a bare 503: no connection string,
         * driver message, or stack frame reaches the caller.
         */
        @RestController
        public class BankingController {

            private static final Logger LOG = LoggerFactory.getLogger(BankingController.class);
            private static final String BEARER = "Bearer ";

            private final BankingRepository repository;
            private final BankingSessionStore sessions;

            public BankingController(BankingRepository repository, BankingSessionStore sessions) {
                this.repository = repository;
                this.sessions = sessions;
            }

            @GetMapping("/healthz")
            public ResponseEntity<HealthResponse> liveness() {
                // Liveness only: the process is up and serving. It deliberately does not touch PostgreSQL, so
                // a database outage does not make an orchestrator restart a container that is working fine.
                return ResponseEntity.ok(new HealthResponse("ok", "not checked"));
            }

            @GetMapping("/api/health")
            public ResponseEntity<HealthResponse> health() {
                return repository.databaseAvailable()
                        ? ResponseEntity.ok(new HealthResponse("ok", "available"))
                        : ResponseEntity.status(HttpStatus.SERVICE_UNAVAILABLE)
                                .body(new HealthResponse("degraded", "unavailable"));
            }

            @PostMapping("/api/customer/login")
            public ResponseEntity<?> customerLogin(@RequestBody CustomerLoginRequest request) {
                String error = BankingValidation.validateAccountId(request.accountId());
                if (error == null) {
                    error = BankingValidation.validatePassword(request.password());
                }
                if (error != null) {
                    return badRequest(error);
                }

                CustomerProfile profile = repository.authenticateCustomer(request.accountId(), request.password());
                if (profile == null) {
                    return unauthorized("Invalid account number or password.");
                }

                Issued issued = sessions.create(Role.CUSTOMER, profile.accountId(), profile.accountHolder());
                return ResponseEntity.ok(
                        new CustomerLoginResponse(issued.token(), issued.session().expiresAt(), profile));
            }

            @PostMapping("/api/manager/login")
            public ResponseEntity<?> managerLogin(@RequestBody ManagerLoginRequest request) {
                String error = BankingValidation.validateUsername(request.username());
                if (error == null) {
                    error = BankingValidation.validatePassword(request.password());
                }
                if (error != null) {
                    return badRequest(error);
                }

                String username = repository.authenticateManager(request.username().trim(), request.password());
                if (username == null) {
                    return unauthorized("Invalid username or password.");
                }

                Issued issued = sessions.create(Role.MANAGER, null, username);
                return ResponseEntity.ok(
                        new ManagerLoginResponse(issued.token(), issued.session().expiresAt(), username));
            }

            @PostMapping("/api/online-registration")
            public ResponseEntity<?> onlineRegistration(@RequestBody OnlineRegistrationRequest request) {
                String error = BankingValidation.validateAccountId(request.accountId());
                if (error == null) {
                    error = BankingValidation.validateEmail(request.emailAddress());
                }
                if (error == null) {
                    error = BankingValidation.validatePassword(request.password());
                }
                if (error != null) {
                    return badRequest(error);
                }

                RegistrationOutcome outcome = repository.enableOnlineAccess(
                        request.accountId(), request.emailAddress().trim(), request.password());

                if (outcome == RegistrationOutcome.ENABLED) {
                    return ResponseEntity.ok(new OnlineRegistrationResponse(request.accountId(), true));
                }

                return outcome == RegistrationOutcome.ALREADY_ENABLED
                        ? conflict("Online banking is already enabled for this account.")
                        : notFound("No account matches that account number and email address.");
            }

            @PostMapping("/api/account-requests")
            public ResponseEntity<?> submitAccountRequest(@RequestBody AccountRequestSubmission request) {
                Normalized<AccountRequestSubmission> normalized = BankingValidation.accountRequest(request);
                if (normalized.isRejected()) {
                    return badRequest(normalized.error());
                }

                AccountRequestCreated created = repository.createAccountRequest(normalized.value());
                return ResponseEntity.created(URI.create("/api/account-requests/" + created.requestId())).body(created);
            }

            @PostMapping("/api/interest")
            public ResponseEntity<?> interest(@RequestBody InterestRequest request) {
                String error = BankingValidation.validateInterest(request);
                if (error != null) {
                    return badRequest(error);
                }

                BigDecimal value = repository.simpleInterest(request.principal(), request.annualRate(), request.years());
                return ResponseEntity.ok(new InterestResponse(
                        request.principal(), request.annualRate(), request.years(), value,
                        request.principal().add(value)));
            }

            @GetMapping("/api/customer/statement")
            public ResponseEntity<?> statement(
                    @RequestHeader(name = "Authorization", required = false) String authorization) {
                Session session = authorize(authorization, Role.CUSTOMER);
                if (session == null || session.accountId() == null) {
                    return unauthorized("A valid customer session is required.");
                }

                StatementResponse statement = repository.statement(session.accountId());
                return statement == null ? notFound("Account not found.") : ResponseEntity.ok(statement);
            }

            @PostMapping("/api/customer/transactions")
            public ResponseEntity<?> postTransaction(
                    @RequestBody TransactionRequest request,
                    @RequestHeader(name = "Authorization", required = false) String authorization) {
                Session session = authorize(authorization, Role.CUSTOMER);
                if (session == null || session.accountId() == null) {
                    return unauthorized("A valid customer session is required.");
                }

                String amountError = BankingValidation.validateAmount(request.amount());
                if (amountError != null) {
                    return badRequest(amountError);
                }

                Normalized<String> direction = BankingValidation.directionCode(request.directionCode());
                if (direction.isRejected()) {
                    return badRequest(direction.error());
                }

                Normalized<String> reference = BankingValidation.referenceCode(request.referenceCode());
                if (reference.isRejected()) {
                    return badRequest(reference.error());
                }

                TransactionCreated created = repository.recordTransaction(
                        session.accountId(), request.amount(), direction.value(), reference.value());

                if (created == null) {
                    return unavailable();
                }

                return ResponseEntity.created(URI.create("/api/customer/transactions/" + created.transactionId()))
                        .body(created);
            }

            @GetMapping("/api/manager/requests")
            public ResponseEntity<?> managerRequests(
                    @RequestParam(name = "status", required = false) String status,
                    @RequestHeader(name = "Authorization", required = false) String authorization) {
                if (authorize(authorization, Role.MANAGER) == null) {
                    return unauthorized("A valid manager session is required.");
                }

                Normalized<String> requestStatus = BankingValidation.requestStatus(status);
                if (requestStatus.isRejected()) {
                    return badRequest(requestStatus.error());
                }

                List<AccountRequestSummary> requests = repository.listAccountRequests(requestStatus.value());
                return ResponseEntity.ok(requests);
            }

            @PostMapping("/api/manager/requests/{requestId}/approve")
            public ResponseEntity<?> approve(
                    @PathVariable long requestId,
                    @RequestHeader(name = "Authorization", required = false) String authorization) {
                if (authorize(authorization, Role.MANAGER) == null) {
                    return unauthorized("A valid manager session is required.");
                }

                if (requestId <= 0L) {
                    return badRequest("requestId must be a positive request number.");
                }

                Approval approval = repository.approve(requestId);
                if (approval.outcome() == ApprovalOutcome.APPROVED && approval.accountId() != null) {
                    return ResponseEntity.ok(new ApprovalResponse(requestId, approval.accountId(), "APPROVED"));
                }

                return approval.outcome() == ApprovalOutcome.ALREADY_DECIDED
                        ? conflict("This request has already been decided.")
                        : notFound("Account request not found.");
            }

            @DeleteMapping("/api/session")
            public ResponseEntity<Void> signOut(
                    @RequestHeader(name = "Authorization", required = false) String authorization) {
                sessions.revoke(bearerToken(authorization));
                return ResponseEntity.noContent().build();
            }

            @ExceptionHandler(DataAccessException.class)
            public ResponseEntity<ErrorResponse> databaseFailure(DataAccessException cause) {
                LOG.error("A banking operation failed against PostgreSQL.", cause);
                return unavailable();
            }

            @ExceptionHandler(IllegalStateException.class)
            public ResponseEntity<ErrorResponse> misconfigured(IllegalStateException cause) {
                LOG.error("The banking backend is not configured correctly.", cause);
                return unavailable();
            }

            private Session authorize(String authorization, Role role) {
                Session session = sessions.resolve(bearerToken(authorization));
                return session != null && session.role() == role ? session : null;
            }

            private static String bearerToken(String authorization) {
                if (authorization == null || !authorization.regionMatches(true, 0, BEARER, 0, BEARER.length())) {
                    return null;
                }

                return authorization.substring(BEARER.length()).trim();
            }

            private static ResponseEntity<ErrorResponse> badRequest(String message) {
                return ResponseEntity.badRequest().body(new ErrorResponse(message));
            }

            private static ResponseEntity<ErrorResponse> unauthorized(String message) {
                return ResponseEntity.status(HttpStatus.UNAUTHORIZED).body(new ErrorResponse(message));
            }

            private static ResponseEntity<ErrorResponse> notFound(String message) {
                return ResponseEntity.status(HttpStatus.NOT_FOUND).body(new ErrorResponse(message));
            }

            private static ResponseEntity<ErrorResponse> conflict(String message) {
                return ResponseEntity.status(HttpStatus.CONFLICT).body(new ErrorResponse(message));
            }

            private static ResponseEntity<ErrorResponse> unavailable() {
                return ResponseEntity.status(HttpStatus.SERVICE_UNAVAILABLE)
                        .body(new ErrorResponse("The banking service is temporarily unavailable."));
            }
        }

        """;

    private static string ApiFallbackController(string package) =>
        $$"""
        package {{package}};

        import {{package}}.BankingContracts.ErrorResponse;
        import org.springframework.http.HttpStatus;
        import org.springframework.http.ResponseEntity;
        import org.springframework.web.bind.annotation.RequestMapping;
        import org.springframework.web.bind.annotation.RestController;

        /**
         * One answer for every path under /api that the workflow service does not implement.
         *
         * Without this, an unknown API path falls through to whatever else is mapped — in a Spring Boot
         * application serving a browser client that is the static resource handler, which answers with the
         * client's HTML. A caller expecting JSON then has to parse a page to discover the route is gone.
         * This returns the same JSON error shape every other failure uses, with a 404.
         *
         * Spring matches the most specific pattern first, so every real route above still wins over this one.
         */
        @RestController
        public class BankingApiFallbackController {

            @RequestMapping({"/api", "/api/**"})
            public ResponseEntity<ErrorResponse> unknown() {
                return ResponseEntity.status(HttpStatus.NOT_FOUND)
                        .body(new ErrorResponse("No such endpoint."));
            }
        }

        """;

    private static string SpaController(string package) =>
        $$""""
        package {{package}};

        import org.springframework.stereotype.Controller;
        import org.springframework.web.bind.annotation.GetMapping;

        /**
         * Serves the browser client's own routes.
         *
         * The client is a single page: a bookmarked or refreshed module path has no file behind it and would
         * otherwise 404. This forwards those paths to the client shell so the page can render the module
         * itself.
         *
         * Two exclusions are deliberate and both are in the pattern rather than in a runtime check:
         *
         * <ul>
         *   <li><b>{@code /api} is never intercepted.</b> The negative lookahead keeps this mapping off the
         *       API namespace entirely, so an unknown API path reaches the JSON 404 rather than being handed
         *       a page of HTML.</li>
         *   <li><b>Files are never intercepted.</b> The pattern excludes any segment containing a dot, so
         *       {@code /app.js} and {@code /styles.css} still come from the static resource handler.</li>
         * </ul>
         */
        @Controller
        public class BankingSpaController {

            @GetMapping("/{route:(?!api$|healthz$)[^.]*}")
            public String clientRoute() {
                return "forward:/index.html";
            }
        }

        """";

    private static string ControllerTest(string package) =>
        $$""""
        package {{package}};

        import {{package}}.BankingSessionStore.Role;
        import {{package}}.BankingSessionStore.Session;
        import org.junit.jupiter.api.Test;
        import org.springframework.beans.factory.annotation.Autowired;
        import org.springframework.boot.test.autoconfigure.web.servlet.WebMvcTest;
        import org.springframework.boot.test.mock.mockito.MockBean;
        import org.springframework.http.MediaType;
        import org.springframework.test.web.servlet.MockMvc;

        import java.time.OffsetDateTime;

        import static org.mockito.ArgumentMatchers.anyLong;
        import static org.mockito.ArgumentMatchers.anyString;
        import static org.mockito.Mockito.never;
        import static org.mockito.Mockito.verify;
        import static org.mockito.Mockito.when;
        import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.delete;
        import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
        import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
        import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.content;
        import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.forwardedUrl;
        import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
        import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

        /**
         * Exercises the generated HTTP surface through the real Spring dispatcher, with the data and session
         * layers mocked. These are assertions about behaviour, not about the text of the generated source: a
         * route that stopped being mapped, a validation rule that stopped rejecting, or a role check that
         * stopped being enforced fails here even though the file still contains the same words.
         *
         * No controller is named, so every controller the application declares is loaded. A per-table CRUD
         * controller reintroduced beside these routes would be loaded too, and would fail this context for
         * want of its repository.
         */
        @WebMvcTest
        class BankingControllerTest {

            private static final String CUSTOMER_TOKEN = "customer-token";
            private static final String MANAGER_TOKEN = "manager-token";

            @Autowired
            private MockMvc mvc;

            @MockBean
            private BankingRepository repository;

            @MockBean
            private BankingSessionStore sessions;

            @Test
            void liveness_reports_up_without_touching_the_database() throws Exception {
                mvc.perform(get("/healthz"))
                        .andExpect(status().isOk())
                        .andExpect(jsonPath("$.status").value("ok"));

                verify(repository, never()).databaseAvailable();
            }

            @Test
            void readiness_reports_the_database() throws Exception {
                when(repository.databaseAvailable()).thenReturn(true);

                mvc.perform(get("/api/health"))
                        .andExpect(status().isOk())
                        .andExpect(jsonPath("$.database").value("available"));
            }

            @Test
            void readiness_is_unavailable_when_the_database_is() throws Exception {
                when(repository.databaseAvailable()).thenReturn(false);

                mvc.perform(get("/api/health"))
                        .andExpect(status().isServiceUnavailable())
                        .andExpect(jsonPath("$.status").value("degraded"));
            }

            @Test
            void an_unknown_api_path_is_a_json_404() throws Exception {
                mvc.perform(get("/api/no-such-thing"))
                        .andExpect(status().isNotFound())
                        .andExpect(content().contentTypeCompatibleWith(MediaType.APPLICATION_JSON))
                        .andExpect(jsonPath("$.error").value("No such endpoint."));
            }

            @Test
            void an_unknown_api_path_is_a_json_404_for_every_method() throws Exception {
                mvc.perform(post("/api/bank-account").contentType(MediaType.APPLICATION_JSON).content("{}"))
                        .andExpect(status().isNotFound())
                        .andExpect(jsonPath("$.error").value("No such endpoint."));
            }

            @Test
            void a_browser_route_is_served_by_the_client_shell() throws Exception {
                mvc.perform(get("/statement"))
                        .andExpect(status().isOk())
                        .andExpect(forwardedUrl("/index.html"));
            }

            @Test
            void the_client_shell_never_intercepts_the_api() throws Exception {
                mvc.perform(get("/api"))
                        .andExpect(status().isNotFound())
                        .andExpect(jsonPath("$.error").value("No such endpoint."));
            }

            @Test
            void a_rejected_login_never_reaches_the_database() throws Exception {
                mvc.perform(post("/api/customer/login")
                                .contentType(MediaType.APPLICATION_JSON)
                                .content("{\"accountId\":500001,\"password\":\"short\"}"))
                        .andExpect(status().isBadRequest())
                        .andExpect(jsonPath("$.error").exists());

                verify(repository, never()).authenticateCustomer(anyLong(), anyString());
            }

            @Test
            void wrong_credentials_are_unauthorized_rather_than_not_found() throws Exception {
                when(repository.authenticateCustomer(500001L, "demo1234")).thenReturn(null);

                mvc.perform(post("/api/customer/login")
                                .contentType(MediaType.APPLICATION_JSON)
                                .content("{\"accountId\":500001,\"password\":\"demo1234\"}"))
                        .andExpect(status().isUnauthorized());
            }

            @Test
            void a_customer_session_opens_the_statement() throws Exception {
                when(sessions.resolve(CUSTOMER_TOKEN)).thenReturn(customerSession());
                when(repository.statement(500001L)).thenReturn(null);

                mvc.perform(get("/api/customer/statement").header("Authorization", "Bearer " + CUSTOMER_TOKEN))
                        .andExpect(status().isNotFound());
            }

            @Test
            void the_statement_is_closed_without_a_session() throws Exception {
                mvc.perform(get("/api/customer/statement"))
                        .andExpect(status().isUnauthorized());

                verify(repository, never()).statement(anyLong());
            }

            @Test
            void a_customer_cannot_use_a_manager_route() throws Exception {
                when(sessions.resolve(CUSTOMER_TOKEN)).thenReturn(customerSession());

                mvc.perform(get("/api/manager/requests").header("Authorization", "Bearer " + CUSTOMER_TOKEN))
                        .andExpect(status().isUnauthorized());

                verify(repository, never()).listAccountRequests(anyString());
            }

            @Test
            void a_manager_cannot_use_a_customer_route() throws Exception {
                when(sessions.resolve(MANAGER_TOKEN)).thenReturn(managerSession());

                mvc.perform(get("/api/customer/statement").header("Authorization", "Bearer " + MANAGER_TOKEN))
                        .andExpect(status().isUnauthorized());
            }

            @Test
            void approval_requires_a_manager_session() throws Exception {
                mvc.perform(post("/api/manager/requests/1006/approve"))
                        .andExpect(status().isUnauthorized());

                verify(repository, never()).approve(anyLong());
            }

            @Test
            void signing_out_revokes_the_presented_token() throws Exception {
                mvc.perform(delete("/api/session").header("Authorization", "Bearer " + CUSTOMER_TOKEN))
                        .andExpect(status().isNoContent());

                verify(sessions).revoke(CUSTOMER_TOKEN);
            }

            private static Session customerSession() {
                return new Session(Role.CUSTOMER, 500001L, "Ada Lovelace", OffsetDateTime.now().plusHours(1));
            }

            private static Session managerSession() {
                return new Session(Role.MANAGER, null, "branch.manager", OffsetDateTime.now().plusHours(1));
            }
        }

        """";

    private static string SurfaceTest(
        string package, string basePackage, IReadOnlyList<string> absentRoutes, IReadOnlyList<OracleTable> tables)
    {
        string absentClasses = string.Join(
            ",\n            ",
            tables
                .Select(table => $"\"{basePackage}.api.{ClassName(table.Name)}Controller\"")
                .Order(StringComparer.Ordinal));

        string absentPaths = string.Join(",\n            ", absentRoutes.Select(route => $"\"{route}\""));

        return $$""""
        package {{package}};

        import org.junit.jupiter.api.Test;
        import org.springframework.beans.factory.annotation.Autowired;
        import org.springframework.boot.test.autoconfigure.web.servlet.WebMvcTest;
        import org.springframework.boot.test.mock.mockito.MockBean;
        import org.springframework.web.servlet.mvc.method.RequestMappingInfo;
        import org.springframework.web.servlet.mvc.method.annotation.RequestMappingHandlerMapping;

        import java.util.List;
        import java.util.Set;
        import java.util.stream.Collectors;

        import static org.junit.jupiter.api.Assertions.assertFalse;
        import static org.junit.jupiter.api.Assertions.assertThrows;
        import static org.junit.jupiter.api.Assertions.assertTrue;

        /**
         * Holds the generated HTTP surface to what the workflow service authorises.
         *
         * The generic emitter would have published a REST controller per table: every column of every row
         * readable without a session, and unvalidated writes accepted on the same paths. For a schema whose
         * tables hold password hashes that is not a rough edge, it is a disclosure. None of those controllers
         * is generated, and this asserts it twice — the classes are not on the classpath, and no path under
         * their routes is mapped in a live application context.
         */
        @WebMvcTest
        class GeneratedSurfaceTest {

            private static final List<String> ABSENT_CONTROLLERS = List.of(
            {{absentClasses}});

            private static final List<String> ABSENT_ROUTES = List.of(
            {{absentPaths}});

            @Autowired
            private RequestMappingHandlerMapping mappings;

            @MockBean
            private BankingRepository repository;

            @MockBean
            private BankingSessionStore sessions;

            @Test
            void no_per_table_crud_controller_was_generated() {
                for (String className : ABSENT_CONTROLLERS) {
                    assertThrows(
                            ClassNotFoundException.class,
                            () -> Class.forName(className),
                            className + " must not be generated: it would expose every column of its table without "
                                    + "a session or role check.");
                }
            }

            @Test
            void no_per_table_route_is_mapped() {
                Set<String> patterns = mappings.getHandlerMethods().keySet().stream()
                        .map(RequestMappingInfo::getPathPatternsCondition)
                        .filter(condition -> condition != null)
                        .flatMap(condition -> condition.getPatternValues().stream())
                        .collect(Collectors.toSet());

                for (String route : ABSENT_ROUTES) {
                    assertFalse(
                            patterns.contains(route),
                            route + " must not be mapped: the workflow routes are the only authorised surface.");
                }
            }

            @Test
            void the_workflow_routes_are_mapped() {
                Set<String> patterns = mappings.getHandlerMethods().keySet().stream()
                        .map(RequestMappingInfo::getPathPatternsCondition)
                        .filter(condition -> condition != null)
                        .flatMap(condition -> condition.getPatternValues().stream())
                        .collect(Collectors.toSet());

                assertTrue(patterns.contains("/healthz"), "The liveness route must be mapped.");
                assertTrue(patterns.contains("/api/health"), "The readiness route must be mapped.");
                assertTrue(patterns.contains("/api/**"), "The JSON 404 fallback must be mapped.");
            }
        }

        """";
    }

    private static string ClassName(string tableName)
    {
        IEnumerable<string> parts = tableName.Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static string BrowserPackage() =>
        GeneratedApplicationVerificationTemplates.Read("Northstar/package.json");

        private static string BrowserInteractionTest() =>
                """
                import { readFileSync } from "node:fs";
                import { afterEach, expect, it, vi } from "vitest";

                afterEach(() => vi.unstubAllGlobals());

                it("opens a generated public workflow module", async () => {
                    const shell = readFileSync("index.html", "utf8");
                    const browserPrelude = "<script>window.matchMedia=()=>({matches:false,addEventListener(){},removeEventListener(){}})</script>";
                    document.open();
                    document.write(shell.replace("<head>", `<head>${browserPrelude}`));
                    document.close();
                    vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ status: "ok" }), {
                        status: 200,
                        headers: { "content-type": "application/json" },
                    })));

                    await import("../public/app.js");
                    document.querySelector('[data-module="interest"]').click();

                    expect(document.querySelector("#panel-interest").hidden).toBe(false);
                    expect(document.querySelector("#tab-interest").getAttribute("aria-selected")).toBe("true");
                });
                """;

    private static string BrowserViteConfig() =>
        """
        import { defineConfig } from "vite";

        // The client is plain HTML, CSS and JavaScript. Vite emits the shell and copies public/ verbatim, so
        // the built output keeps the file names the shell asks for.
        export default defineConfig({ build: { outDir: "dist", emptyOutDir: true } });

        """;

    private static string BrowserShell() =>
        Rewrite(
            Template("index.html"),
            // Vite serves public/ from the site root, so the shell asks for the built file names.
            (
                """<link rel="stylesheet" href="styles.css" />""",
                """<link rel="stylesheet" href="/styles.css" />"""
            ),
            (
                """<script src="app.js"></script>""",
                """<script src="/app.js"></script>"""
            ),
            // Provenance: this is the migrated application, not the replica it was migrated from.
            (
                """<span class="ofx-badge">Oracle Forms workflow replica</span>""",
                """<span class="ofx-badge">Migrated to Azure Database for PostgreSQL</span>"""
            ),
            (
                """<li><button type="button" data-action="about">About This Replica…</button></li>""",
                """<li><button type="button" data-action="about">About This Application…</button></li>"""
            ),
            (
                Block(16,
                    "The original Oracle Forms <span class=\"ofx-mono\">.fmb</span> modules are not running",
                    "here. This browser client re-implements the published workflow requirements against",
                    "the synthetic demo Oracle database."),
                Block(16,
                    "Oracle is no longer in the data path. The migration fleet generated this client from the",
                    "converted schema, and every module below runs against Azure Database for PostgreSQL",
                    "through the generated workflow service.")
            ),
            (
                Block(12,
                    "<strong>The original Oracle Forms <span class=\"ofx-mono\">.fmb</span> source is not",
                    "running here.</strong>",
                    "No Oracle Forms runtime, Forms Services component, or compiled module is loaded by this",
                    "page."),
                Block(12,
                    "<strong>This is the migrated application.</strong>",
                    "Neither an Oracle Forms runtime nor an Oracle database is involved: the back end is Spring",
                    "Boot on Java 21, talking to Azure Database for PostgreSQL with Entra authentication.")
            ),
            (
                Block(12,
                    "This is a browser replica that implements the published workflow requirements of the",
                    "public Oracle Apps case study against the synthetic demo Oracle database exposed by this",
                    "application's HTTP API."),
                Block(12,
                    "The migration fleet generated both tiers from the converted schema. It recognised the retail",
                    "banking workflow from the tables and columns present, then emitted this client and the",
                    "endpoints behind it. Nothing here was recovered from a Forms module.")
            ),
            (
                """<dd>Static HTML, CSS and JavaScript — no framework, no build step</dd>""",
                """<dd>Static HTML, CSS and JavaScript — no framework, bundled by Vite</dd>"""
            ));

    private static string BrowserScript() =>
        Rewrite(
            Template("app.js"),
            (
                Block(0,
                    "   Northstar Online Banking — browser replica of the published Oracle Forms",
                    "   workflow requirements. The original .fmb modules are not executed here."),
                Block(0,
                    "   Northstar Online Banking — generated by the Oracle Forms Migration Fleet from the",
                    "   converted schema. Every call goes to the migrated Spring Boot workflow service over",
                    "   Azure Database for PostgreSQL. No Oracle component is involved.")
            ));

    private static string BrowserStyles() => Template("styles.css");

    /// <summary>Joins lines with the exact leading indentation the template uses, so matching stays literal.</summary>
    private static string Block(int indent, params string[] lines) =>
        string.Join('\n', lines.Select(line => new string(' ', indent) + line));

    /// <summary>
    /// Applies each replacement exactly once and fails loudly when a target is missing or ambiguous, because
    /// a template edit that silently skipped a provenance rewrite would ship a page claiming to be something
    /// it is not.
    /// </summary>
    private static string Rewrite(string template, params (string From, string To)[] replacements)
    {
        string text = template;

        foreach ((string from, string to) in replacements)
        {
            int index = text.IndexOf(from, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"The Northstar template no longer contains the text this generator rewrites: '{Preview(from)}'.");
            }

            if (text.IndexOf(from, index + from.Length, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException(
                    $"The Northstar template contains '{Preview(from)}' more than once, so the rewrite is ambiguous.");
            }

            text = string.Concat(text.AsSpan(0, index), to, text.AsSpan(index + from.Length));
        }

        return text;
    }

    private static string Preview(string text)
    {
        string single = text.ReplaceLineEndings(" ").Trim();
        return single.Length > 60 ? single[..60] + "…" : single;
    }

    /// <summary>
    /// Reads a template out of this assembly. Nothing is read from the repository at run time, and line
    /// endings are normalised so the same input produces the same bytes on every platform.
    /// </summary>
    private static string Template(string fileName)
    {
        string resource = $"OracleFormsMigrationFleet.Fleet.Execution.Templates.Northstar.{fileName}";
        using Stream stream = typeof(NorthstarBankingApplicationProfile).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The embedded Northstar template '{resource}' is missing from the build.");

        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
