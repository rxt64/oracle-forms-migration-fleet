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
/// bound to Azure Database for PostgreSQL with Entra authentication, and a React front end over it.
///
/// What this is derived from matters. Every entity, field, endpoint, and screen here comes from DDL the
/// parser actually read. Nothing is inferred from a `.fmb`, because their contents are a proprietary
/// binary this build cannot open; the screens produced are CRUD over the real tables, not a reproduction
/// of the original forms. Anything that lived only in Forms triggers or PL/SQL bodies is reported as
/// outstanding work rather than invented.
/// </summary>
public static class ApplicationCodeEmitter
{
    private const string BasePackage = "com.northstar.migrated";

    public static ApplicationConversion Convert(OracleSchema schema, string applicationName, DatabaseTarget target)
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

        foreach (OracleTable table in tables)
        {
            files.Add(BuildEntity(table, findings));
            files.Add(BuildRepository(table));
            files.Add(BuildController(table));
        }

        files.Add(BuildReactTypes(tables));
        files.Add(BuildReactClient(tables));
        files.Add(BuildReactApp(tables));
        files.Add(BuildReadme(applicationName, tables));

        foreach (string unparsed in schema.Unparsed)
        {
            string head = unparsed.Trim().Split('\n')[0].Trim();
            if (head.Length > 90)
            {
                head = head[..90];
            }

            if (LooksLikeProgramUnit(unparsed))
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported,
                    "Server-side logic",
                    head,
                    "Business logic in a PL/SQL program unit was not translated. The generated back end exposes CRUD " +
                    "over the tables only; this rule has no equivalent in it yet and must be reimplemented in Java or PL/pgSQL."));
            }
        }

        findings.Add(new ConversionFinding(
            ConversionSeverity.ManualReview,
            "User interface",
            "Screen layout and navigation",
            "The React screens are generated from table structure, not from the original Forms modules, whose binary " +
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
              <groupId>com.azure.spring</groupId>
              <artifactId>spring-cloud-azure-starter-jdbc-postgresql</artifactId>
              <version>5.18.0</version>
            </dependency>
          </dependencies>
        </project>
        """,
        "Spring Boot build for the migrated back end, with the Azure PostgreSQL Entra JDBC starter.");

    private static GeneratedFile BuildApplicationYaml() => new(
        "backend/src/main/resources/application.yml",
        """
        # Passwordless by design: the JDBC starter exchanges the app's managed identity for a token.
        # No credential appears in this file, in configuration, or in a container image.
        spring:
          datasource:
            url: jdbc:postgresql://${PGHOST}:5432/${PGDATABASE}?sslmode=require
            username: ${PGUSER}
            azure:
              passwordless-enabled: true
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

            builder.Append("    @Column(name = \"").Append(column.Name.ToLowerInvariant()).Append('"');
            if (column.NotNull)
            {
                builder.Append(", nullable = false");
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

    private static GeneratedFile BuildReadme(string applicationName, IReadOnlyList<OracleTable> tables)
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
        builder.AppendLine("cd backend && ./mvnw spring-boot:run");
        builder.AppendLine("```");

        return new GeneratedFile("README.md", builder.ToString(), "What was generated, and what still has to be built by hand.");
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
