import type { ReactElement } from "react";

/**
 * Service marks for the components this workbench talks about.
 *
 * These are original geometric glyphs drawn in each service's brand colour. Microsoft's official
 * Azure architecture icon set is trademarked and ships under its own terms, so it is deliberately
 * not embedded here; swapping these for the official SVGs is a drop-in change if the terms are
 * accepted for this product.
 */
export type GlyphId =
  | "foundry-hosted-agent"
  | "managed-identity"
  | "app-insights"
  | "entra-id"
  | "key-vault"
  | "blob-storage"
  | "database-target"
  | "execution-adapters"
  | "container-apps"
  | "static-web-apps"
  | "sql-database"
  | "postgresql"
  | "github"
  | "azure-devops"
  | "gitlab"
  | "bitbucket"
  | "archive"
  | "oracle-forms";

const PALETTE: Record<GlyphId, string> = {
  "foundry-hosted-agent": "#9b6cf2",
  "managed-identity": "#f0a30a",
  "app-insights": "#e86c3a",
  "entra-id": "#2f7ad4",
  "key-vault": "#f2c94c",
  "blob-storage": "#3ba7d8",
  "database-target": "#4b8ef0",
  "execution-adapters": "#8d95a5",
  "container-apps": "#4f7ff0",
  "static-web-apps": "#39a0d8",
  "sql-database": "#4b8ef0",
  postgresql: "#3d7fbf",
  github: "#5b6472",
  "azure-devops": "#2f7ad4",
  gitlab: "#e8683a",
  bitbucket: "#2f6fd0",
  archive: "#7a8393",
  "oracle-forms": "#c74634",
};

const SHAPES: Record<GlyphId, ReactElement> = {
  // Sparked node: a model endpoint radiating to callers.
  "foundry-hosted-agent": <>
    <circle cx="12" cy="12" r="3.4" />
    <path d="M12 2.6v3.6M12 17.8v3.6M2.6 12h3.6M17.8 12h3.6M5.4 5.4l2.5 2.5M16.1 16.1l2.5 2.5M18.6 5.4l-2.5 2.5M7.9 16.1l-2.5 2.5" />
  </>,
  // Badge with a key bit: an identity that carries no secret of its own.
  "managed-identity": <>
    <path d="M12 2.6 20 6v6.2c0 4.2-3.2 7.7-8 9.2-4.8-1.5-8-5-8-9.2V6z" />
    <circle cx="12" cy="10.4" r="2.2" />
    <path d="M12 12.6v4.2M10.4 15.2h3.2" />
  </>,
  // Rising trace over a baseline.
  "app-insights": <>
    <path d="M3.2 20.4h17.6" />
    <path d="M4.6 16.4 9.4 10l3.9 3.6 5.9-8.2" />
    <circle cx="9.4" cy="10" r="1.5" />
    <circle cx="13.3" cy="13.6" r="1.5" />
  </>,
  // Interlocking directory rings.
  "entra-id": <>
    <path d="M12 2.8 21 19.4H3z" />
    <path d="M12 8.6 16.6 17H7.4z" />
  </>,
  // Vault door.
  "key-vault": <>
    <rect x="3.2" y="4.2" width="17.6" height="15.6" rx="2.4" />
    <circle cx="12" cy="12" r="4" />
    <path d="M12 8v2.2M12 16v-2.2M8 12h2.2M16 12h-2.2" />
  </>,
  // Stacked object containers.
  "blob-storage": <>
    <ellipse cx="12" cy="5.6" rx="8.2" ry="3" />
    <path d="M3.8 5.6v12.8c0 1.7 3.7 3 8.2 3s8.2-1.3 8.2-3V5.6" />
    <path d="M3.8 12c0 1.7 3.7 3 8.2 3s8.2-1.3 8.2-3" />
  </>,
  // Relational cylinder with a linked key.
  "database-target": <>
    <ellipse cx="12" cy="5.4" rx="7.6" ry="2.8" />
    <path d="M4.4 5.4v13.2c0 1.6 3.4 2.8 7.6 2.8s7.6-1.2 7.6-2.8V5.4" />
    <path d="M4.4 11.8c0 1.6 3.4 2.8 7.6 2.8s7.6-1.2 7.6-2.8" />
  </>,
  // Plug awaiting a socket.
  "execution-adapters": <>
    <path d="M9 3.4v5M15 3.4v5" />
    <path d="M5.6 8.4h12.8v3a6.4 6.4 0 0 1-6.4 6.4 6.4 6.4 0 0 1-6.4-6.4z" />
    <path d="M12 17.8v3" />
  </>,
  // Container in motion.
  "container-apps": <>
    <path d="M12 2.8 20.6 7v10L12 21.2 3.4 17V7z" />
    <path d="m3.4 7 8.6 4.4L20.6 7M12 11.4v9.8" />
  </>,
  // Global edge with content.
  "static-web-apps": <>
    <circle cx="12" cy="12" r="9" />
    <path d="M3 12h18M12 3a14 14 0 0 1 0 18 14 14 0 0 1 0-18z" />
  </>,
  "sql-database": <>
    <ellipse cx="12" cy="5.4" rx="7.6" ry="2.8" />
    <path d="M4.4 5.4v13.2c0 1.6 3.4 2.8 7.6 2.8s7.6-1.2 7.6-2.8V5.4" />
    <path d="M4.4 11.8c0 1.6 3.4 2.8 7.6 2.8s7.6-1.2 7.6-2.8" />
  </>,
  postgresql: <>
    <ellipse cx="12" cy="5.4" rx="7.6" ry="2.8" />
    <path d="M4.4 5.4v13.2c0 1.6 3.4 2.8 7.6 2.8s7.6-1.2 7.6-2.8V5.4" />
    <path d="M8.6 9.6v8M15.4 9.6v8" />
  </>,
  github: <>
    <path d="M9.2 20.4c-4.4 1.4-4.4-2.4-6.2-2.9m12.4 5.1v-3.6a3.1 3.1 0 0 0-.9-2.4c2.9-.3 6-1.4 6-6.4a5 5 0 0 0-1.4-3.5 4.6 4.6 0 0 0-.1-3.5s-1.1-.3-3.6 1.4a12.4 12.4 0 0 0-6.4 0C6.5 2.9 5.4 3.2 5.4 3.2a4.6 4.6 0 0 0-.1 3.5A5 5 0 0 0 3.9 10.2c0 5 3.1 6.1 6 6.4a3.1 3.1 0 0 0-.9 2.4v3.6" />
  </>,
  "azure-devops": <>
    <path d="M3.2 8.4 8.2 2.8l6.6 2.6 5.9-2.2v15.4l-5.9 3.6-6.6-3.1-5-2.8z" />
    <path d="m8.2 2.8.1 15.7M14.8 5.4v16.8" />
  </>,
  gitlab: <>
    <path d="m12 21.4-3.4-10.5H3.2z" />
    <path d="m12 21.4 3.4-10.5h5.4z" />
    <path d="M3.2 10.9 5 4.6l3.6 6.3zM20.8 10.9 19 4.6l-3.6 6.3z" />
  </>,
  bitbucket: <>
    <path d="M3 4.6h18l-2.8 15.4a1 1 0 0 1-1 .8H6.8a1 1 0 0 1-1-.8z" />
    <path d="M9 9.4h6l-1 5.6h-4z" />
  </>,
  archive: <>
    <rect x="3.4" y="4" width="17.2" height="16" rx="2.2" />
    <path d="M10.4 4v4.4M13.6 4v4.4M10.4 8.4h3.2M12 8.4v3.2M10.6 11.6h2.8v3.4h-2.8z" />
  </>,
  "oracle-forms": <>
    <rect x="2.8" y="4.4" width="18.4" height="15.2" rx="2.2" />
    <path d="M2.8 9h18.4M6.6 12.6h7M6.6 16h4.6" />
  </>,
};

export function ServiceGlyph({ id, size = 24 }: { id: GlyphId; size?: number }) {
  return (
    <svg
      className="mf-glyph"
      viewBox="0 0 24 24"
      width={size}
      height={size}
      fill="none"
      stroke={PALETTE[id]}
      strokeWidth={1.6}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {SHAPES[id]}
    </svg>
  );
}

export function glyphForHost(host: string): GlyphId {
  const value = host.toLowerCase();
  if (value.includes("github")) return "github";
  if (value.includes("dev.azure.com") || value.includes("visualstudio.com")) return "azure-devops";
  if (value.includes("gitlab")) return "gitlab";
  if (value.includes("bitbucket")) return "bitbucket";
  return "oracle-forms";
}

export function glyphForDatabase(target: string): GlyphId {
  // Matches the server's DatabaseTarget enum.
  if (target === "PostgreSql") return "postgresql";
  return "sql-database";
}

/** Falls back to a neutral mark if the catalog ever grows a component this file has not drawn. */
export function glyphForComponent(id: string): GlyphId {
  return id in SHAPES ? (id as GlyphId) : "execution-adapters";
}
