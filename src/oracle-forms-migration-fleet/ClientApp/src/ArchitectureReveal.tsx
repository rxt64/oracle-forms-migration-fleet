import { useEffect, useState } from "react";
import { ArrowRight, LockKeyhole } from "lucide-react";
import { ServiceGlyph, glyphForDatabase, type GlyphId } from "./ServiceGlyph";
import { proposedResourceNames } from "./sourceClient";
import { InfoTip } from "./InfoTip";

interface Node {
  id: string;
  glyph: GlyphId;
  service: string;
  resource: string;
  secondary?: string;
  role: string;
  tier: "source" | "presentation" | "application" | "data" | "platform";
}

/**
 * Animated reveal of the target architecture.
 *
 * The resource names are proposals derived from the application name using the Azure Cloud
 * Adoption Framework abbreviations. Nothing here is deployed: this is what the plan recommends
 * building, drawn so the shape of it is obvious at a glance.
 */
export function ArchitectureReveal({
  applicationName,
  database,
  databaseName,
}: {
  applicationName: string;
  database: string;
  databaseName: string;
}) {
  const names = proposedResourceNames(applicationName, database);
  const [revealed, setRevealed] = useState(false);

  useEffect(() => {
    const frame = requestAnimationFrame(() => setRevealed(true));
    return () => cancelAnimationFrame(frame);
  }, []);

  const nodes: Node[] = [
    { id: "forms", glyph: "oracle-forms", service: "Oracle Forms today", resource: applicationName || "Your application", role: "Forms modules, menus, libraries and PL/SQL running on Oracle.", tier: "source" },
    { id: "web", glyph: "static-web-apps", service: "Azure Static Web Apps", resource: names.frontEnd, role: "Serves the converted React front end to browsers.", tier: "presentation" },
    { id: "api", glyph: "container-apps", service: "Azure Container Apps", resource: names.api, secondary: names.apiEnvironment, role: "Runs the converted Java Spring Boot back end.", tier: "application" },
    { id: "db", glyph: glyphForDatabase(database), service: databaseName, resource: names.databaseHost, secondary: names.database === names.databaseHost ? undefined : names.database, role: "Holds the converted schema, the migrated data and the translated PL/SQL.", tier: "data" },
    { id: "identity", glyph: "managed-identity", service: "Managed identity", resource: names.identity, role: "Lets the API reach the database and the vault without a stored password.", tier: "platform" },
    { id: "vault", glyph: "key-vault", service: "Azure Key Vault", resource: names.vault, role: "Holds any connection secrets the migration tooling still needs.", tier: "platform" },
    { id: "storage", glyph: "blob-storage", service: "Azure Blob Storage", resource: names.storage, role: "Keeps generated code, conversion reports and reconciliation output.", tier: "platform" },
    { id: "insights", glyph: "app-insights", service: "Application Insights", resource: names.insights, role: "Collects traces and metrics from the running application.", tier: "platform" },
  ];

  const flow = nodes.filter((node) => node.tier !== "platform");
  const platform = nodes.filter((node) => node.tier === "platform");

  return (
    <div className={revealed ? "mf-arch revealed" : "mf-arch"}>
      <header className="mf-arch-head">
        <div>
          <p className="mf-kicker">Target architecture</p>
          <h2>
            What this plan would build
            <InfoTip label="the target architecture">
              These resource names follow the Azure Cloud Adoption Framework naming rules and are derived from your application name. They are proposals in the plan. Nothing shown here has been created in Azure.
            </InfoTip>
          </h2>
        </div>
        <p className="mf-arch-rg">
          Resource group <code>{names.resourceGroup}</code>
        </p>
      </header>

      <ol className="mf-arch-flow">
        {flow.map((node, index) => (
          <li key={node.id} style={{ "--mf-delay": `${index * 220}ms` } as React.CSSProperties}>
            <article className={`mf-arch-node ${node.tier}`}>
              <span className="mf-arch-icon"><ServiceGlyph id={node.glyph} size={28} /></span>
              <h3>{node.service}</h3>
              <code>{node.resource}</code>
              {node.secondary && <code>{node.secondary}</code>}
              <p>{node.role}</p>
            </article>
            {index < flow.length - 1 && (
              <span className="mf-arch-link" aria-hidden="true"><ArrowRight /></span>
            )}
          </li>
        ))}
      </ol>

      <p className="mf-arch-caption">Supporting services</p>
      <ul className="mf-arch-platform">
        {platform.map((node, index) => (
          <li key={node.id} style={{ "--mf-delay": `${(flow.length + index) * 180}ms` } as React.CSSProperties}>
            <span className="mf-arch-icon"><ServiceGlyph id={node.glyph} size={22} /></span>
            <div>
              <strong>{node.service}</strong>
              <code>{node.resource}</code>
              <small>{node.role}</small>
            </div>
          </li>
        ))}
      </ul>

      <p className="mf-arch-note">
        <LockKeyhole aria-hidden="true" />
        This diagram is part of the written plan. No Azure resource has been created, and no code has been converted.
      </p>
    </div>
  );
}
