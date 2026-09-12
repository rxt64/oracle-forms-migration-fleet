// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>Whether the plan would create a resource or expects one to already exist.</summary>
public enum ResourceDisposition
{
    Created,
    Required,
}

public sealed record AzureResourceRequirement(
    string ResourceType,
    string Purpose,
    ResourceDisposition Disposition,
    ExecutionMode NeededFrom);

/// <summary>
/// A role assignment the deploying identity would need. <see cref="Scope"/> is the narrowest scope that
/// works, because a role named here is a role someone will be asked to grant.
/// </summary>
public sealed record AzureRoleRequirement(
    string Role,
    string Scope,
    string Why,
    ExecutionMode NeededFrom);

public sealed record AzureFootprint(
    IReadOnlyList<AzureResourceRequirement> Resources,
    IReadOnlyList<AzureRoleRequirement> Roles,
    IReadOnlyList<string> TenantModel,
    IReadOnlyList<string> Disclaimers);

/// <summary>
/// States what a run would need in Azure, and what it would create, before anyone signs in.
///
/// Pure and offline: nothing here contacts Azure, reads a subscription, or provisions anything. It exists
/// so the cost and blast radius of a plan are legible in advance rather than discovered during a deployment.
/// </summary>
public static class AzureFootprintCalculator
{
    public static AzureFootprint Describe(DatabaseTarget database, ExecutionMode authorizedMode)
    {
        List<AzureResourceRequirement> resources =
        [
            new("Microsoft.Resources/resourceGroups",
                "Holds every resource the migration creates, so the whole target estate can be deleted in one action.",
                ResourceDisposition.Required,
                ExecutionMode.SandboxMigration),
            new("Microsoft.ManagedIdentity/userAssignedIdentities",
                "Lets the migrated application reach the database without a stored password.",
                ResourceDisposition.Created,
                ExecutionMode.SandboxMigration),
            new("Microsoft.KeyVault/vaults",
                "Holds the source Oracle credential used to read data. Nothing else needs it.",
                ResourceDisposition.Created,
                ExecutionMode.SandboxMigration),
        ];

        resources.AddRange(DatabaseResources(database));

        resources.Add(new AzureResourceRequirement(
            "Microsoft.Network/virtualNetworks + privateEndpoints",
            "Keeps the target database off the public internet. A sandbox holding production-shaped data should not be publicly reachable.",
            ResourceDisposition.Created,
            ExecutionMode.SandboxMigration));

        resources.Add(new AzureResourceRequirement(
            "Microsoft.OperationalInsights/workspaces",
            "Collects the logs that make a cutover auditable after the fact.",
            ResourceDisposition.Created,
            ExecutionMode.ProductionCutover));

        List<AzureRoleRequirement> roles =
        [
            new("Reader",
                "Subscription",
                "Confirms the subscription exists and the target region is available before anything is created.",
                ExecutionMode.SandboxMigration),
            new("Contributor",
                "Target resource group only, never the subscription",
                "Creates the database, identity, vault, and network. Scoped to one resource group so the blast radius is that group.",
                ExecutionMode.SandboxMigration),
            new("Key Vault Secrets Officer",
                "The created key vault",
                "Writes the source credential once. Separate from the data-plane read the application does.",
                ExecutionMode.SandboxMigration),
            new("Key Vault Secrets User",
                "The created key vault",
                "Lets the migration read the source credential at run time. Read-only on the data plane.",
                ExecutionMode.SandboxMigration),
            new("Cognitive Services OpenAI User",
                "The Foundry account backing the review model",
                "Calls the review model. It grants inference only, not management of the account or its deployments.",
                ExecutionMode.GenerateArtifacts),
        ];

        roles.AddRange(DatabaseRoles(database));

        return new AzureFootprint(
            resources,
            roles,
            [
                "The customer tenant is signed into by a person, not by this service. The operator's own access governs what can be created; the workbench never holds a customer credential and never acts while nobody is present.",
                "The application registration must be multi-tenant and consented to by an administrator in the customer tenant before any sign-in there will succeed.",
                "A customer token is used for the request that acquired it and is not persisted. Nothing is written to a customer tenant without a named approver on the run.",
                "Production and sandbox are separate grants. Approving a sandbox migration never authorizes a production cutover.",
            ],
            [
                "Nothing in this list has been created, and this build cannot create it: no adapter in this service provisions an Azure resource or opens a database connection.",
                "This is the footprint of the plan, not an estimate of cost. Sizing, SKU, and redundancy are decisions for whoever deploys it.",
                "Least privilege is stated at the resource group. Granting Contributor at subscription scope would satisfy these requirements and is not what they ask for.",
            ]);
    }

    private static IEnumerable<AzureResourceRequirement> DatabaseResources(DatabaseTarget database) => database switch
    {
        DatabaseTarget.PostgreSql =>
        [
            new("Microsoft.DBforPostgreSQL/flexibleServers",
                "The converted schema and migrated data land here.",
                ResourceDisposition.Created,
                ExecutionMode.SandboxMigration),
        ],
        DatabaseTarget.AzureSqlDatabase =>
        [
            new("Microsoft.Sql/servers + databases",
                "The converted schema and migrated data land here.",
                ResourceDisposition.Created,
                ExecutionMode.SandboxMigration),
        ],
        DatabaseTarget.AzureSqlManagedInstance =>
        [
            new("Microsoft.Sql/managedInstances",
                "The converted schema and migrated data land here. A managed instance needs a delegated subnet and takes hours to provision.",
                ResourceDisposition.Created,
                ExecutionMode.SandboxMigration),
        ],
        _ =>
        [
            new("Database engine not yet chosen",
                "The target platform recommendation has not resolved, so no database resource can be named.",
                ResourceDisposition.Required,
                ExecutionMode.SandboxMigration),
        ],
    };

    private static IEnumerable<AzureRoleRequirement> DatabaseRoles(DatabaseTarget database) => database switch
    {
        DatabaseTarget.PostgreSql =>
        [
            new("Azure AD administrator on the flexible server",
                "The created PostgreSQL flexible server",
                "Creates the database roles the application uses. This is a server setting, not an Azure RBAC role, and is granted separately.",
                ExecutionMode.SandboxMigration),
        ],
        DatabaseTarget.AzureSqlDatabase or DatabaseTarget.AzureSqlManagedInstance =>
        [
            new("Microsoft Entra admin on the SQL server",
                "The created SQL server or managed instance",
                "Creates contained database users. This is a server setting, not an Azure RBAC role, and is granted separately.",
                ExecutionMode.SandboxMigration),
        ],
        _ => [],
    };
}
