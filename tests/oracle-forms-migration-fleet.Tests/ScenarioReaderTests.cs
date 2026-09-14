// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

public class ScenarioReaderTests
{
    /// <summary>The shape the workshop test harness uses.</summary>
    private const string EmployeeCrud = """
        name: employee-crud
        description: Employee create, read, update lifecycle
        module: employee
        legacy_package: PKG_EMPLOYEE
        modern_endpoint: /api/employees

        steps:
          - name: Create a new employee
            action: create
            input:
              first_name: "JANE"
              dept_id: 31
            expect:
              employment_status: "ACTIVE"
            save_as: new_employee

          - name: Terminate the employee
            action: terminate
            input:
              emp_id: "{{new_employee.emp_id}}"
            expect:
              employment_status: "TERMINATED"
        """;

    [Fact]
    public void A_scenario_states_the_package_and_endpoint_it_exercises()
    {
        BehaviourScenario scenario = Assert.IsType<BehaviourScenario>(ScenarioReader.Read(EmployeeCrud));

        Assert.Equal("employee-crud", scenario.Name);
        Assert.Equal("PKG_EMPLOYEE", scenario.LegacyPackage);
        Assert.Equal("/api/employees", scenario.ModernEndpoint);
        Assert.Equal(2, scenario.Steps.Count);
        Assert.Equal("create", scenario.Steps[0].Action);
        Assert.Equal("Terminate the employee", scenario.Steps[1].Name);
    }

    [Fact]
    public void A_scenario_is_matched_to_the_program_units_that_did_not_migrate()
    {
        BehaviourScenario scenario = ScenarioReader.Read(EmployeeCrud)!;

        ConversionFinding[] findings =
        [
            new(ConversionSeverity.Unsupported, "Program unit", "PKG_EMPLOYEE.TERMINATE_EMPLOYEE", "Not translated because of RAISE_APPLICATION_ERROR."),
            new(ConversionSeverity.Unsupported, "Program unit", "PKG_PAYROLL.CALCULATE", "Not translated because of PRAGMA."),
            new(ConversionSeverity.ManualReview, "Program unit", "PKG_EMPLOYEE.SOMETHING", "Reviewed, not refused."),
        ];

        ScenarioCoverage coverage = Assert.Single(ScenarioReader.Cover([scenario], findings));

        // Only refusals in this scenario's own package, and only refusals.
        Assert.Equal(["PKG_EMPLOYEE.TERMINATE_EMPLOYEE"], coverage.BlockedBy);
    }

    [Fact]
    public void The_report_never_claims_a_scenario_passed()
    {
        BehaviourScenario scenario = ScenarioReader.Read(EmployeeCrud)!;
        string report = ScenarioReader.Render("HRMS", ScenarioReader.Cover([scenario], []));

        Assert.Contains("Nothing here was executed", report, StringComparison.Ordinal);
        Assert.Contains("is not a passing test", report, StringComparison.Ordinal);
        Assert.DoesNotContain("passed", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_file_that_is_not_a_scenario_yields_nothing()
    {
        Assert.Null(ScenarioReader.Read("version: 2\nservices:\n  db:\n    image: postgres"));
        Assert.Null(ScenarioReader.Read(""));
    }
}
