// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;

namespace OracleFormsMigrationFleet.Fleet.Execution;

public sealed record GeneratedFile(string Path, string Contents, string Description);

public sealed record ApplicationConversion(
    IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<ConversionFinding> Findings);

/// <summary>
/// Generates the Azure-targeted application tier from the parsed Oracle schema: a Spring Boot back end
/// bound to Azure Database for PostgreSQL with Entra authentication, and a front end over it.
///
/// What this is derived from matters. Every entity, field, endpoint, and screen here comes from DDL the
/// parser actually read. Nothing is inferred from a `.fmb`, because their contents are a proprietary
/// binary this build cannot open; the generic screens produced are CRUD over the real tables, not a
/// reproduction of the original forms. Anything that lived only in Forms triggers or PL/SQL bodies is
/// reported as outstanding work rather than invented.
///
/// One exception is deliberate. When the schema carries a workflow this fleet has a generator for — see
/// <see cref="NorthstarBankingApplicationProfile"/> — a working replacement for that workflow is emitted
/// instead of CRUD. Recognition is structural and needs the whole schema; it never depends on a name.
/// </summary>
public static class ApplicationCodeEmitter
{
    private const string BasePackage = "com.northstar.migrated";

    public static ApplicationConversion Convert(
        OracleSchema schema,
        string applicationName,
        DatabaseTarget target,
        IReadOnlyList<FormsModule>? forms = null)
    {
        ArgumentNullException.ThrowIfNull(schema);

        List<GeneratedFile> files = [];
        List<ConversionFinding> findings = [];

        if (target != DatabaseTarget.PostgreSql)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported,
                "Target",
                target.ToString(),
                "Only PostgreSQL data access is generated. The entities would compile but the driver, dialect, and " +
                "authentication wiring would be wrong for this target, so no back end was emitted."));

            return new ApplicationConversion([], findings);
        }

        IReadOnlyList<OracleTable> tables = [.. schema.Tables.Where(table => table.Columns.Count > 0)];
        if (tables.Count == 0)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported,
                "Schema",
                "No tables",
                "No table definition was parsed, so there is no entity to generate an application over."));

            return new ApplicationConversion([], findings);
        }

        files.Add(BuildPom(applicationName));
        files.Add(BuildApplicationYaml());
        files.Add(BuildMainClass());

        bool recognized = NorthstarBankingApplicationProfile.Matches(schema);

        foreach (OracleTable table in tables)
        {
            files.Add(BuildEntity(table, findings));
            files.Add(BuildRepository(table));

            // A recognised workflow gets its own authorized routes. Emitting the generic controllers beside
            // them would publish every column of every table — password hashes included — and accept
            // unvalidated writes, with no session or role check anywhere. They are not emitted at all.
            if (!recognized)
            {
                files.Add(BuildController(table));
            }
        }

        if (!recognized)
        {
            files.Add(BuildControllerTest(tables[0]));
        }

        if (recognized)
        {
            files.AddRange(NorthstarBankingApplicationProfile.Generate(schema, BasePackage, tables));
        }
        else
        {
            files.Add(BuildReactTypes(tables));
            files.Add(BuildReactClient(tables));
            files.Add(BuildReactPackage());
            files.Add(new GeneratedFile(
                "frontend/package-lock.json",
                GeneratedApplicationVerificationTemplates.Read("Generic/package-lock.json"),
                "Pinned dependency lock for offline generated UI verification."));
            files.Add(BuildTypeScriptConfig());
            files.Add(BuildViteConfig());
            files.Add(BuildReactIndex());
            files.Add(BuildReactMain());
            files.Add(BuildViteTypes());

            IReadOnlyList<FormsScreen> screens =
            [
                .. (forms ?? [])
                    .SelectMany(module => module.Blocks
                        .Where(block => block.BaseTable is not null)
                        .Select(block => new FormsScreen(module, block)))
                    .OrderBy(screen => screen.Identity, StringComparer.Ordinal),
            ];

            // One App.tsx is emitted, so the block it comes from is chosen by directory-qualified identity
            // rather than by the order the modules happened to arrive in. Every other readable screen is
            // reported below: two directories carrying one module name are two modules, and dropping
            // either without a word would understate what the estate holds.
            foreach (FormsScreen unrendered in screens.Skip(1))
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.ManualReview,
                    "Forms module",
                    unrendered.Identity,
                    "This block was read from the normalized representation but is not the one the single generated screen " +
                    "was built from. It was not dropped: build it as its own screen against the generated endpoints before " +
                    "this tier replaces the Forms client."));
            }

            files.Add(screens.Count > 0
                ? BuildReactAppFromForms(screens[0], tables, findings)
                : BuildReactApp(tables));
            files.Add(BuildReactInteractionTest(tables[0]));
        }

        files.Add(BuildDockerfile());
        files.Add(BuildDockerIgnore());
        files.Add(new GeneratedFile(
            "README.md",
            recognized
                ? NorthstarBankingApplicationProfile.Readme(applicationName, tables.Count)
                : BuildReadme(applicationName, tables),
            "What was generated, and what still has to be built by hand."));

        Dictionary<string, OracleProgramUnit> identities = [];
        foreach (OracleProgramUnit unit in schema.ProgramUnits)
        {
            identities.TryAdd(unit.Statement, unit);
        }

        foreach (string unparsed in schema.Unparsed)
        {
            string head = unparsed.Trim().Split('\n')[0].Trim();
            if (head.Length > 90)
            {
                head = head[..90];
            }

            if (!LooksLikeProgramUnit(unparsed))
            {
                continue;
            }

            // A covered unit is reported as done, not as outstanding: claiming otherwise would understate the
            // output as badly as claiming untranslated logic had been carried across would overstate it.
            // Coverage is decided on the unit's parsed kind and exact name; a unit the parser could not
            // identify is never claimed, however much its text resembles one that is covered.
            bool covered = recognized
                && identities.TryGetValue(unparsed, out OracleProgramUnit? unit)
                && NorthstarBankingApplicationProfile.Covers(unit);

            findings.Add(covered
                ? new ConversionFinding(
                    ConversionSeverity.Note,
                    "Server-side logic",
                    head,
                    "This program unit's behaviour is implemented by the generated workflow service, so the tier " +
                    "does not depend on the translated PL/pgSQL for it.")
                : new ConversionFinding(
                    ConversionSeverity.ManualReview,
                    "Server-side logic",
                    head,
                    "The database conversion translates this program unit into PL/pgSQL, so the rule moves with the " +
                    "schema rather than into Java. The generated back end exposes CRUD over the tables and does not " +
                    "call it yet; wire it up, or reimplement it here, before this tier replaces the Forms client."));
        }

        if (recognized)
        {
            findings.AddRange(NorthstarBankingApplicationProfile.Findings());
            return new ApplicationConversion(files, findings);
        }

        findings.Add(new ConversionFinding(
            ConversionSeverity.ManualReview,
            "User interface",
            "Screen layout and navigation",
            forms is { Count: > 0 }
                ? "The React screens follow the blocks, item order, and prompts in the Forms XML export. Layout " +
                  "geometry, canvases, navigation between windows, and every trigger-driven behaviour are not carried " +
                  "across and must be rebuilt against the real application before this replaces it."
                : "The React screens are generated from table structure, not from the original Forms modules, whose binary " +
                  "contents this build cannot read. Field order, grouping, navigation, and any trigger-driven behaviour must " +
                  "be rebuilt against the real application before this replaces it."));

        findings.Add(new ConversionFinding(
            ConversionSeverity.ManualReview,
            "Authorization",
            "Endpoint access control",
            "Every generated endpoint is unauthenticated. Role checks that lived in Forms or PL/SQL are not present, so " +
            "the API must not be exposed before authentication and authorization are added."));

        return new ApplicationConversion(files, findings);
    }

    private static bool LooksLikeProgramUnit(string statement)
    {
        string upper = statement.ToUpperInvariant();
        return upper.Contains("PACKAGE", StringComparison.Ordinal)
            || upper.Contains("PROCEDURE", StringComparison.Ordinal)
            || upper.Contains("FUNCTION", StringComparison.Ordinal)
            || upper.Contains("TRIGGER", StringComparison.Ordinal);
    }

    private static GeneratedFile BuildPom(string applicationName) => new(
        "backend/pom.xml",
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <project xmlns="http://maven.apache.org/POM/4.0.0"
                 xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                 xsi:schemaLocation="http://maven.apache.org/POM/4.0.0 https://maven.apache.org/xsd/maven-4.0.0.xsd">
          <modelVersion>4.0.0</modelVersion>
          <parent>
            <groupId>org.springframework.boot</groupId>
            <artifactId>spring-boot-starter-parent</artifactId>
            <version>3.3.5</version>
            <relativePath/>
          </parent>
          <groupId>com.northstar</groupId>
          <artifactId>migrated-backend</artifactId>
          <version>1.0.0</version>
          <name>{Escape(applicationName)} (migrated)</name>
          <properties>
            <java.version>21</java.version>
          </properties>
          <dependencies>
            <dependency>
              <groupId>org.springframework.boot</groupId>
              <artifactId>spring-boot-starter-web</artifactId>
            </dependency>
            <dependency>
              <groupId>org.springframework.boot</groupId>
              <artifactId>spring-boot-starter-data-jpa</artifactId>
            </dependency>
            <dependency>
              <groupId>org.postgresql</groupId>
              <artifactId>postgresql</artifactId>
              <scope>runtime</scope>
            </dependency>
            <!-- Entra authentication to Azure Database for PostgreSQL: no password is stored anywhere. -->
            <dependency>
              <groupId>com.azure</groupId>
              <artifactId>azure-identity-extensions</artifactId>
              <version>1.2.9</version>
            </dependency>
            <dependency>
              <groupId>org.springframework.boot</groupId>
              <artifactId>spring-boot-starter-test</artifactId>
              <scope>test</scope>
            </dependency>
          </dependencies>
          <build>
            <plugins>
              <plugin>
                <groupId>org.springframework.boot</groupId>
                <artifactId>spring-boot-maven-plugin</artifactId>
                <executions>
                  <execution>
                    <goals>
                      <goal>repackage</goal>
                    </goals>
                  </execution>
                </executions>
              </plugin>
            </plugins>
          </build>
        </project>
        """,
        "Spring Boot build for the migrated back end, with the Azure PostgreSQL Entra JDBC authentication plugin.");

    private static GeneratedFile BuildApplicationYaml() => new(
        "backend/src/main/resources/application.yml",
        """
        # Passwordless by design: the JDBC plugin exchanges the app's managed identity for a PostgreSQL token.
        # No credential appears in this file, in configuration, or in a container image.
        spring:
          datasource:
            url: jdbc:postgresql://${PGHOST}:5432/${PGDATABASE}?sslmode=require&authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin
            username: ${PGUSER}
          jpa:
            hibernate:
              ddl-auto: validate
            properties:
              hibernate:
                dialect: org.hibernate.dialect.PostgreSQLDialect
        server:
          port: ${PORT:8080}
        """,
        "Datasource bound to Azure Database for PostgreSQL using Entra, with schema validation rather than generation.");

    private static GeneratedFile BuildMainClass() => new(
        $"backend/src/main/java/{BasePackage.Replace('.', '/')}/MigratedApplication.java",
        $$"""
        package {{BasePackage}};

        import org.springframework.boot.SpringApplication;
        import org.springframework.boot.autoconfigure.SpringBootApplication;

        @SpringBootApplication
        public class MigratedApplication {
            public static void main(String[] args) {
                SpringApplication.run(MigratedApplication.class, args);
            }
        }
        """,
        "Spring Boot entry point.");

    private static GeneratedFile BuildEntity(OracleTable table, List<ConversionFinding> findings)
    {
        string className = ClassName(table.Name);
        IReadOnlyList<string> keyColumns = PrimaryKeyColumns(table);

        StringBuilder builder = new();
        builder.AppendLine($"package {BasePackage}.domain;").AppendLine();
        builder.AppendLine("import jakarta.persistence.*;");
        builder.AppendLine("import org.hibernate.annotations.JdbcTypeCode;");
        builder.AppendLine("import org.hibernate.type.SqlTypes;");
        builder.AppendLine("import java.math.BigDecimal;");
        builder.AppendLine("import java.time.LocalDate;");
        builder.AppendLine("import java.time.LocalDateTime;").AppendLine();
        builder.AppendLine("@Entity");
        builder.AppendLine($"@Table(name = \"{table.Name.ToLowerInvariant()}\")");
        builder.AppendLine($"public class {className} {{");

        if (keyColumns.Count == 0)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.ManualReview,
                "Entity",
                table.Name,
                "The table declares no primary key, so the first column was mapped as the identifier. JPA requires one; " +
                "confirm it is actually unique before relying on this entity."));
        }

        foreach (OracleColumn column in table.Columns)
        {
            bool isKey = keyColumns.Count == 0
                ? ReferenceEquals(column, table.Columns[0])
                : keyColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase);

            if (isKey)
            {
                builder.AppendLine("    @Id");
            }

            if (column.BaseType.Equals("CHAR", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine("    @JdbcTypeCode(SqlTypes.CHAR)");
            }

            builder.Append("    @Column(name = \"").Append(column.Name.ToLowerInvariant()).Append('"');
            if (column.NotNull)
            {
                builder.Append(", nullable = false");
            }

            if (column.BaseType.Equals("CHAR", StringComparison.OrdinalIgnoreCase) && column.Precision is int length)
            {
                builder.Append($", columnDefinition = \"char({length})\"");
            }

            builder.AppendLine(")");
            builder.AppendLine($"    private {JavaType(column)} {FieldName(column.Name)};").AppendLine();
        }

        foreach (OracleColumn column in table.Columns)
        {
            string field = FieldName(column.Name);
            string type = JavaType(column);
            string suffix = char.ToUpperInvariant(field[0]) + field[1..];

            builder.AppendLine($"    public {type} get{suffix}() {{ return {field}; }}");
            builder.AppendLine($"    public void set{suffix}({type} value) {{ this.{field} = value; }}").AppendLine();
        }

        builder.AppendLine("}");

        return new GeneratedFile(
            $"backend/src/main/java/{BasePackage.Replace('.', '/')}/domain/{className}.java",
            builder.ToString(),
            $"JPA entity for {table.Name}.");
    }

    private static GeneratedFile BuildRepository(OracleTable table)
    {
        string className = ClassName(table.Name);
        string keyType = PrimaryKeyType(table);

        return new GeneratedFile(
            $"backend/src/main/java/{BasePackage.Replace('.', '/')}/repository/{className}Repository.java",
            $$"""
            package {{BasePackage}}.repository;

            import {{BasePackage}}.domain.{{className}};
            import org.springframework.data.jpa.repository.JpaRepository;

            public interface {{className}}Repository extends JpaRepository<{{className}}, {{keyType}}> {
            }
            """,
            $"Spring Data repository for {table.Name}.");
    }

    private static GeneratedFile BuildController(OracleTable table)
    {
        string className = ClassName(table.Name);
        string route = RouteName(table.Name);
        string keyType = PrimaryKeyType(table);
        string field = char.ToLowerInvariant(className[0]) + className[1..];

        return new GeneratedFile(
            $"backend/src/main/java/{BasePackage.Replace('.', '/')}/api/{className}Controller.java",
            $$"""
            package {{BasePackage}}.api;

            import {{BasePackage}}.domain.{{className}};
            import {{BasePackage}}.repository.{{className}}Repository;
            import org.springframework.http.ResponseEntity;
            import org.springframework.web.bind.annotation.*;

            import java.util.List;

            @RestController
            @RequestMapping("/api/{{route}}")
            public class {{className}}Controller {

                private final {{className}}Repository repository;

                public {{className}}Controller({{className}}Repository repository) {
                    this.repository = repository;
                }

                @GetMapping
                public List<{{className}}> list() {
                    return repository.findAll();
                }

                @GetMapping("/{id}")
                public ResponseEntity<{{className}}> get(@PathVariable {{keyType}} id) {
                    return repository.findById(id).map(ResponseEntity::ok).orElseGet(() -> ResponseEntity.notFound().build());
                }

                @PostMapping
                public {{className}} create(@RequestBody {{className}} {{field}}) {
                    return repository.save({{field}});
                }
            }
            """,
            $"REST endpoints for {table.Name}.");
    }

    private static GeneratedFile BuildControllerTest(OracleTable table)
    {
        string className = ClassName(table.Name);
        string route = RouteName(table.Name);
        string field = FieldName(table.Columns[0].Name);
        string value = JavaSampleValue(table.Columns[0]);

        return new GeneratedFile(
            $"backend/src/test/java/{BasePackage.Replace('.', '/')}/api/{className}ControllerTest.java",
            $$"""
            package {{BasePackage}}.api;

            import {{BasePackage}}.domain.{{className}};
            import {{BasePackage}}.repository.{{className}}Repository;
            import org.junit.jupiter.api.Test;
            import org.springframework.beans.factory.annotation.Autowired;
            import org.springframework.boot.test.autoconfigure.web.servlet.WebMvcTest;
            import org.springframework.boot.test.mock.mockito.MockBean;
            import org.springframework.test.web.servlet.MockMvc;

            import java.util.List;

            import static org.mockito.Mockito.when;
            import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
            import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
            import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

            @WebMvcTest({{className}}Controller.class)
            class {{className}}ControllerTest {
                @Autowired MockMvc mvc;
                @MockBean {{className}}Repository repository;

                @Test
                void lists_generated_entities() throws Exception {
                    {{className}} row = new {{className}}();
                    row.set{{char.ToUpperInvariant(field[0]) + field[1..]}}({{value}});
                    when(repository.findAll()).thenReturn(List.of(row));

                    mvc.perform(get("/api/{{route}}"))
                        .andExpect(status().isOk())
                        .andExpect(jsonPath("$[0].{{field}}").exists());
                }
            }
            """,
            $"Executable Spring MVC contract test for {table.Name}.");
    }

    private static GeneratedFile BuildReactTypes(IReadOnlyList<OracleTable> tables)
    {
        StringBuilder builder = new();
        builder.AppendLine("// Generated from the converted Oracle schema. Field names follow the JPA entities.").AppendLine();

        foreach (OracleTable table in tables)
        {
            builder.AppendLine($"export interface {ClassName(table.Name)} {{");
            foreach (OracleColumn column in table.Columns)
            {
                builder.AppendLine($"  {FieldName(column.Name)}{(column.NotNull ? string.Empty : "?")}: {TypeScriptType(column)};");
            }

            builder.AppendLine("}").AppendLine();
        }

        return new GeneratedFile("frontend/src/types.ts", builder.ToString(), "TypeScript shapes matching the generated entities.");
    }

    private static GeneratedFile BuildReactClient(IReadOnlyList<OracleTable> tables)
    {
        StringBuilder builder = new();
        builder.AppendLine("import type { " + string.Join(", ", tables.Select(table => ClassName(table.Name))) + " } from \"./types\";").AppendLine();
        builder.AppendLine("const base = import.meta.env.VITE_API_BASE ?? \"\";").AppendLine();
        builder.AppendLine("async function get<T>(path: string): Promise<T> {");
        builder.AppendLine("  const response = await fetch(`${base}${path}`, { headers: { accept: \"application/json\" } });");
        builder.AppendLine("  if (!response.ok) {");
        builder.AppendLine("    throw new Error(`${path} failed: ${response.status}`);");
        builder.AppendLine("  }");
        builder.AppendLine("  return (await response.json()) as T;");
        builder.AppendLine("}").AppendLine();

        foreach (OracleTable table in tables)
        {
            string className = ClassName(table.Name);
            builder.AppendLine($"export const list{className} = () => get<{className}[]>(\"/api/{RouteName(table.Name)}\");");
        }

        return new GeneratedFile("frontend/src/api.ts", builder.ToString(), "Typed fetch client for the generated endpoints.");
    }

    /// <summary>
    /// A block that can back a screen, carried with the module it came from so it stays attributable to a
    /// directory-qualified module rather than to a bare block name two modules could share.
    /// </summary>
    private sealed record FormsScreen(FormsModule Module, FormsBlock Block)
    {
        public string Identity => $"{Module.QualifiedName}.{Block.Name}";
    }

    /// <summary>
    /// Builds the screen from the Forms block: its item order, prompts, and required flags, not the table's.
    /// </summary>
    private static GeneratedFile BuildReactAppFromForms(
        FormsScreen screen, IReadOnlyList<OracleTable> tables, List<ConversionFinding> findings)
    {
        FormsBlock block = screen.Block;
        OracleTable? table = tables.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, block.BaseTable, StringComparison.OrdinalIgnoreCase));

        if (table is null)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported,
                "Forms module",
                $"{screen.Identity} over {block.BaseTable}",
                "The block's base table was not found in the supplied schema, so the screen fell back to table structure."));

            return BuildReactApp(tables);
        }

        HashSet<string> columns = new(table.Columns.Select(column => column.Name), StringComparer.OrdinalIgnoreCase);

        // Only displayed items that map to a real column can be rendered from the API response.
        List<FormsItem> rendered = [];
        foreach (FormsItem item in block.Items.Where(item => item.Visible))
        {
            if (item.ColumnName is { Length: > 0 } column && columns.Contains(column))
            {
                rendered.Add(item);
            }
            else
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.ManualReview,
                    "Forms module",
                    $"{screen.Identity}.{item.Name}",
                    "The item is not backed by a column in the supplied schema, so it was left off the screen. It was " +
                    "populated by Forms logic that has not been translated."));
            }
        }

        if (rendered.Count == 0)
        {
            return BuildReactApp(tables);
        }

        string className = ClassName(table.Name);
        StringBuilder builder = new();

        builder.AppendLine("import { useEffect, useState } from \"react\";");
        builder.AppendLine($"import {{ list{className} }} from \"./api\";");
        builder.AppendLine($"import type {{ {className} }} from \"./types\";").AppendLine();
        builder.AppendLine($"// Generated from Forms block {block.Name} over {block.BaseTable}, in module {screen.Module.QualifiedName}.");
        builder.AppendLine("// Column order and labels follow the form; trigger behaviour does not.");
        builder.AppendLine("export default function App() {");
        builder.AppendLine($"  const [rows, setRows] = useState<{className}[]>([]);");
        builder.AppendLine("  const [error, setError] = useState<string | null>(null);").AppendLine();
        builder.AppendLine("  useEffect(() => {");
        builder.AppendLine($"    list{className}().then(setRows).catch((cause: Error) => setError(cause.message));");
        builder.AppendLine("  }, []);").AppendLine();
        builder.AppendLine("  if (error) {");
        builder.AppendLine("    return <p role=\"alert\">{error}</p>;");
        builder.AppendLine("  }").AppendLine();
        builder.AppendLine("  return (");
        builder.AppendLine("    <section>");
        builder.AppendLine($"      <h1>{JsxText(block.Name)}</h1>");
        builder.AppendLine("      <table>");
        builder.AppendLine("        <thead>");
        builder.AppendLine("          <tr>");

        foreach (FormsItem item in rendered)
        {
            string label = JsxText(item.Prompt ?? item.Name);
            string required = item.Required ? " <abbr title=\"Required\">*</abbr>" : string.Empty;
            builder.AppendLine($"            <th scope=\"col\">{label}{required}</th>");
        }

        builder.AppendLine("          </tr>");
        builder.AppendLine("        </thead>");
        builder.AppendLine("        <tbody>");
        builder.AppendLine("          {rows.map((row, index) => (");
        builder.AppendLine("            <tr key={index}>");

        foreach (FormsItem item in rendered)
        {
            builder.AppendLine($"              <td>{{String(row.{FieldName(item.ColumnName!)} ?? \"\")}}</td>");
        }

        builder.AppendLine("            </tr>");
        builder.AppendLine("          ))}");
        builder.AppendLine("        </tbody>");
        builder.AppendLine("      </table>");
        builder.AppendLine("    </section>");
        builder.AppendLine("  );");
        builder.AppendLine("}");

        return new GeneratedFile(
            "frontend/src/App.tsx",
            builder.ToString(),
            $"React screen generated from Forms block {screen.Identity}.");
    }

    /// <summary>Escapes for JSX text, where a brace opens an expression.</summary>
    private static string JsxText(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("{", "&#123;", StringComparison.Ordinal)
        .Replace("}", "&#125;", StringComparison.Ordinal);

    private static GeneratedFile BuildReactApp(IReadOnlyList<OracleTable> tables)
    {
        OracleTable first = tables[0];
        StringBuilder builder = new();

        builder.AppendLine("import { useEffect, useState } from \"react\";");
        builder.AppendLine($"import {{ list{ClassName(first.Name)} }} from \"./api\";");
        builder.AppendLine($"import type {{ {ClassName(first.Name)} }} from \"./types\";").AppendLine();
        builder.AppendLine("export default function App() {");
        builder.AppendLine($"  const [rows, setRows] = useState<{ClassName(first.Name)}[]>([]);");
        builder.AppendLine("  const [error, setError] = useState<string | null>(null);").AppendLine();
        builder.AppendLine("  useEffect(() => {");
        builder.AppendLine($"    list{ClassName(first.Name)}().then(setRows).catch((cause: Error) => setError(cause.message));");
        builder.AppendLine("  }, []);").AppendLine();
        builder.AppendLine("  if (error) {");
        builder.AppendLine("    return <p role=\"alert\">{error}</p>;");
        builder.AppendLine("  }").AppendLine();
        builder.AppendLine("  return (");
        builder.AppendLine("    <table>");
        builder.AppendLine("      <thead>");
        builder.AppendLine("        <tr>");

        foreach (OracleColumn column in first.Columns)
        {
            builder.AppendLine($"          <th>{column.Name}</th>");
        }

        builder.AppendLine("        </tr>");
        builder.AppendLine("      </thead>");
        builder.AppendLine("      <tbody>");
        builder.AppendLine("        {rows.map((row, index) => (");
        builder.AppendLine("          <tr key={index}>");

        foreach (OracleColumn column in first.Columns)
        {
            builder.AppendLine($"            <td>{{String(row.{FieldName(column.Name)} ?? \"\")}}</td>");
        }

        builder.AppendLine("          </tr>");
        builder.AppendLine("        ))}");
        builder.AppendLine("      </tbody>");
        builder.AppendLine("    </table>");
        builder.AppendLine("  );");
        builder.AppendLine("}");

        return new GeneratedFile("frontend/src/App.tsx", builder.ToString(), $"React screen over {first.Name}.");
    }

        private static GeneratedFile BuildReactPackage() => new(
                "frontend/package.json",
                GeneratedApplicationVerificationTemplates.Read("Generic/package.json"),
                "Pinned React, TypeScript, Vite, and interaction-test dependencies.");

                private static GeneratedFile BuildReactInteractionTest(OracleTable table)
                {
                        string className = ClassName(table.Name);
                        string field = FieldName(table.Columns[0].Name);
                        string value = TypeScriptSampleValue(table.Columns[0]);

                        return new GeneratedFile(
                                "frontend/src/App.test.tsx",
                                $$"""
                                import { render, screen } from "@testing-library/react";
                                import { afterEach, describe, expect, it, vi } from "vitest";
                                import App from "./App";
                                import type { {{className}} } from "./types";

                                afterEach(() => vi.unstubAllGlobals());

                                describe("generated {{className}} screen", () => {
                                    it("loads and renders target API data", async () => {
                                        const row = { {{field}}: {{value}} } as {{className}};
                                        vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify([row]), {
                                            status: 200,
                                            headers: { "content-type": "application/json" },
                                        })));

                                        render(<App />);

                                        expect(await screen.findByText(String(row.{{field}}))).toBeTruthy();
                                    });
                                });
                                """,
                                $"Executable React interaction test for {table.Name}.");
                }

        private static GeneratedFile BuildTypeScriptConfig() => new(
                "frontend/tsconfig.json",
                """
                {
                    "compilerOptions": {
                        "target": "ES2022",
                        "useDefineForClassFields": true,
                        "lib": ["ES2022", "DOM", "DOM.Iterable"],
                        "allowJs": false,
                        "skipLibCheck": true,
                        "esModuleInterop": true,
                        "allowSyntheticDefaultImports": true,
                        "strict": true,
                        "forceConsistentCasingInFileNames": true,
                        "module": "ESNext",
                        "moduleResolution": "Bundler",
                        "resolveJsonModule": true,
                        "isolatedModules": true,
                        "noEmit": true,
                        "jsx": "react-jsx"
                    },
                    "include": ["src"],
                    "references": []
                }
                """,
                "Strict TypeScript compiler configuration for the generated UI.");

        private static GeneratedFile BuildViteConfig() => new(
                "frontend/vite.config.ts",
                """
                import { defineConfig } from "vite";
                import react from "@vitejs/plugin-react";

                export default defineConfig({ plugins: [react()] });
                """,
                "Vite production build configuration.");

        private static GeneratedFile BuildReactIndex() => new(
                "frontend/index.html",
                """
                <!doctype html>
                <html lang="en">
                    <head><meta charset="UTF-8" /><meta name="viewport" content="width=device-width, initial-scale=1.0" /><title>Migrated application</title></head>
                    <body><div id="root"></div><script type="module" src="/src/main.tsx"></script></body>
                </html>
                """,
                "React application host page.");

        private static GeneratedFile BuildReactMain() => new(
                "frontend/src/main.tsx",
                """
                import { StrictMode } from "react";
                import { createRoot } from "react-dom/client";
                import App from "./App";

                const root = document.getElementById("root");
                if (!root) throw new Error("React root element was not found.");
                createRoot(root).render(<StrictMode><App /></StrictMode>);
                """,
                "React browser entry point.");

            private static GeneratedFile BuildViteTypes() => new(
                "frontend/src/vite-env.d.ts",
                """
                /// <reference types="vite/client" />
                """,
                "Vite ambient types for import.meta.env.");

    private static GeneratedFile BuildDockerfile() => new(
        "backend/Dockerfile",
        """
        # Build and run the migrated back end. No credential is baked in: the running container gets a
        # token from its managed identity, so the image is safe to store in a registry.
        FROM mcr.microsoft.com/openjdk/jdk:21-mariner AS build
        WORKDIR /src
        COPY pom.xml .
        RUN --mount=type=cache,target=/root/.m2 \
            curl -fsSL https://archive.apache.org/dist/maven/maven-3/3.9.9/binaries/apache-maven-3.9.9-bin.tar.gz \
            | tar -xz -C /opt && ln -s /opt/apache-maven-3.9.9/bin/mvn /usr/local/bin/mvn && mvn -B dependency:go-offline
        COPY src ./src
        RUN mvn -B -DskipTests package

        FROM mcr.microsoft.com/openjdk/jdk:21-distroless
        WORKDIR /app
        COPY --from=build /src/target/*.jar app.jar
        EXPOSE 8080
        ENTRYPOINT ["java", "-jar", "/app/app.jar"]
        """,
        "Container build for the migrated back end. No credential is baked into the image.");

    private static GeneratedFile BuildDockerIgnore() => new(
        "backend/.dockerignore",
        """
        target/
        .git/
        *.md
        """,
        "Keeps build output and notes out of the image context.");

    private static string BuildReadme(string applicationName, IReadOnlyList<OracleTable> tables)
    {
        StringBuilder builder = new();
        builder.AppendLine($"# {applicationName} — migrated application tier").AppendLine();
        builder.AppendLine("Generated from the converted Oracle schema. Oracle is not in the data path: the back end talks to");
        builder.AppendLine("Azure Database for PostgreSQL using Entra authentication, so no database password exists.").AppendLine();
        builder.AppendLine("## What is here").AppendLine();
        builder.AppendLine($"- {tables.Count.ToString(CultureInfo.InvariantCulture)} JPA entities, repositories, and REST controllers");
        builder.AppendLine("- A typed React client and a table screen over the first entity").AppendLine();
        builder.AppendLine("## What is deliberately not here").AppendLine();
        builder.AppendLine("- **Forms behaviour.** `.fmb` modules are a proprietary binary this build cannot read, so no trigger,");
        builder.AppendLine("  block, or navigation rule was extracted. The screens are CRUD over tables, not the original forms.");
        builder.AppendLine("- **PL/SQL logic.** Package and trigger bodies were not translated; see the conversion report.");
        builder.AppendLine("- **Authorization.** Every endpoint is open. Do not expose this until access control is added.").AppendLine();
        builder.AppendLine("## Running it").AppendLine();
        builder.AppendLine("```bash");
        builder.AppendLine("export PGHOST=<server>.postgres.database.azure.com");
        builder.AppendLine("export PGDATABASE=postgres");
        builder.AppendLine("export PGUSER=<managed identity name>");
        builder.AppendLine("cd backend && mvn spring-boot:run");
        builder.AppendLine("```");

        return builder.ToString();
    }

    private static IReadOnlyList<string> PrimaryKeyColumns(OracleTable table) =>
        table.Constraints.FirstOrDefault(constraint => constraint.Kind == OracleConstraintKind.PrimaryKey)?.Columns ?? [];

    private static string PrimaryKeyType(OracleTable table)
    {
        IReadOnlyList<string> keys = PrimaryKeyColumns(table);
        OracleColumn? column = keys.Count > 0
            ? table.Columns.FirstOrDefault(candidate => string.Equals(candidate.Name, keys[0], StringComparison.OrdinalIgnoreCase))
            : table.Columns.FirstOrDefault();

        return column is null ? "Long" : JavaType(column);
    }

    private static string JavaType(OracleColumn column) => column.BaseType.ToUpperInvariant() switch
    {
        "NUMBER" or "INTEGER" or "INT" => column.Scale is > 0 ? "BigDecimal" : "Long",
        "FLOAT" or "BINARY_FLOAT" or "BINARY_DOUBLE" => "Double",
        "DATE" => "LocalDate",
        "TIMESTAMP" => "LocalDateTime",
        "RAW" or "BLOB" => "byte[]",
        _ => "String",
    };

    private static string TypeScriptType(OracleColumn column) => JavaType(column) switch
    {
        "Long" or "Double" or "BigDecimal" => "number",
        "byte[]" => "string",
        _ => "string",
    };

    private static string JavaSampleValue(OracleColumn column) => JavaType(column) switch
    {
        "Long" => "1L",
        "Double" => "1.0d",
        "BigDecimal" => "new java.math.BigDecimal(\"1.0\")",
        "LocalDate" => "java.time.LocalDate.of(2024, 1, 1)",
        "LocalDateTime" => "java.time.LocalDateTime.of(2024, 1, 1, 0, 0)",
        "byte[]" => "new byte[] { 1 }",
        _ => "\"verified-value\"",
    };

    private static string TypeScriptSampleValue(OracleColumn column) => TypeScriptType(column) switch
    {
        "number" => "1",
        _ => "\"verified-value\"",
    };

    private static string ClassName(string tableName)
    {
        IEnumerable<string> parts = tableName.Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static string FieldName(string columnName)
    {
        string pascal = ClassName(columnName);
        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    private static string RouteName(string tableName) =>
        tableName.ToLowerInvariant().Replace('_', '-');

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal);
}
