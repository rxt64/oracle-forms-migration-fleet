namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// Where each generated tier's build descriptor lands, per back-end stack.
///
/// The build phase and the verification phase both have to look in the same place, and they used to agree
/// only because both had <c>pom.xml</c> written into them. One statement of the layout means adding a
/// stack cannot leave one of the two phases looking for a file the generator stopped emitting.
/// </summary>
public static class GeneratedApplicationLayout
{
    /// <summary>The generated front end's build descriptor. Every stack emits the same one.</summary>
    public const string FrontendDescriptor = "frontend/package.json";

    /// <summary>The generated back end's build descriptor for <paramref name="stack"/>.</summary>
    public static string BackendDescriptor(BackEndStack stack) => stack switch
    {
        BackEndStack.AspNetCore => "backend/GeneratedBackend.slnx",
        _ => "backend/pom.xml",
    };

    /// <summary>How the back end is named in operator-facing build and verification output.</summary>
    public static string BackendComponent(BackEndStack stack) => stack switch
    {
        BackEndStack.AspNetCore => "ASP.NET Core",
        _ => "Spring Boot",
    };
}
