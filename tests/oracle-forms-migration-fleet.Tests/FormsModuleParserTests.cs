// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

public class FormsModuleParserTests
{
    private const string Export = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4">
          <FormModule Name="BANK_ACCOUNT_REQUEST_FORM" Title="Account Opening Requests">
            <Trigger Name="WHEN-NEW-FORM-INSTANCE" TriggerText="BEGIN EXECUTE_QUERY; END;"/>
            <Block Name="REQUEST_BLOCK" QueryDataSourceName="BANK_ACCOUNT_REQUEST" RecordsDisplayCount="10">
              <Item Name="REQUEST_ID" ItemType="Text Item" DataType="Number" ColumnName="REQUEST_ID"
                    Prompt="Request" Required="true" Visible="true"/>
              <Item Name="FAMILY_NAME" ItemType="Text Item" DataType="Char" ColumnName="FAMILY_NAME"
                    Prompt="Surname" Required="true" Visible="true"/>
              <Item Name="STREET_ADDRESS" ItemType="Text Item" ColumnName="STREET_ADDRESS"
                    Prompt="Address" Visible="false"/>
              <Item Name="APPROVE_BUTTON" ItemType="Push Button" Prompt="Approve" Visible="true"/>
              <Trigger Name="WHEN-BUTTON-PRESSED" TriggerText="LEGACY_BANKING_API.APPROVE_REQUEST(1, 2);"/>
            </Block>
            <Block Name="SUMMARY_BLOCK" RecordsDisplayCount="1">
              <Item Name="TOTAL_PENDING" ItemType="Display Item" Prompt="Pending" Visible="true"/>
            </Block>
            <LOV Name="BRANCH_LOV" RecordGroupName="BRANCH_RG"/>
            <ProgramUnit Name="REFRESH_SUMMARY" ProgramUnitType="Procedure"/>
          </FormModule>
        </Module>
        """;

    /// <summary>The shape a real frmf2xml export has: FormModule at the root and Yes/No booleans.</summary>
    private const string RealExport = """
        <?xml version="1.0" encoding="UTF-8"?>
        <FormModule Name="HRMS_EMPLOYEE" ConsoleWindow="CONSOLE" Title="HRMS - Employee Maintenance">
          <AttachedLibrary Name="HRMS_COMMON_LIB" LibrarySource="File"/>
          <Trigger Name="WHEN-NEW-FORM-INSTANCE" TriggerStyle="PL/SQL">
            <TriggerText>BEGIN NULL; END;</TriggerText>
          </Trigger>
          <Block Name="EMPLOYEE" QueryDataSourceType="Table" QueryDataSourceName="HRMS.EMPLOYEES"
                 DMLDataTargetName="HRMS.EMPLOYEES" RecordsDisplayed="1">
            <Item Name="EMP_ID" ItemType="Text Field" DataType="Number" Required="Yes"
                  Visible="No" DatabaseItem="Yes" PrimaryKey="Yes"/>
            <Item Name="LAST_NAME" ItemType="Text Field" DataType="Char" Required="Yes"
                  Visible="Yes" Prompt="Last Name:" DatabaseItem="Yes"/>
            <Item Name="FULL_NAME" ItemType="Display Item" DataType="Char"
                  Visible="Yes" Prompt="Full Name:" DatabaseItem="No"/>
          </Block>
          <Relation Name="EMP_SALARY_REL" DetailBlock="SALARY"/>
        </FormModule>
        """;

    [Fact]
    public void A_real_export_with_formmodule_at_the_root_is_read()
    {
        FormsModule module = Assert.Single(FormsModuleParser.Parse(RealExport).Modules);

        Assert.Equal("HRMS_EMPLOYEE", module.Name);
        Assert.Equal("EMPLOYEES", module.Blocks[0].BaseTable);
    }

    [Fact]
    public void Yes_and_no_are_read_as_booleans()
    {
        FormsBlock block = FormsModuleParser.Parse(RealExport).Modules[0].Blocks[0];

        // Reading these as true/false only would invert every flag in a real export.
        Assert.True(block.Items[0].Required);
        Assert.False(block.Items[0].Visible);
        Assert.True(block.Items[1].Visible);
    }

    [Fact]
    public void A_non_database_item_is_not_mapped_to_a_column()
    {
        FormsBlock block = FormsModuleParser.Parse(RealExport).Modules[0].Blocks[0];

        Assert.Equal("LAST_NAME", block.Items[1].ColumnName);
        Assert.Null(block.Items[2].ColumnName);
        Assert.Equal("Last Name", block.Items[1].Prompt);
    }

    [Fact]
    public void An_attached_library_and_a_master_detail_relation_are_reported()
    {
        FormsModuleParse parsed = FormsModuleParser.Parse(RealExport);

        // The .pll is not in the export, so its contents were never seen.
        Assert.Contains(parsed.Findings, finding => finding.Construct.EndsWith("HRMS_COMMON_LIB", StringComparison.Ordinal));
        Assert.Contains(parsed.Findings, finding => finding.Construct.EndsWith("EMP_SALARY_REL", StringComparison.Ordinal));
    }

    [Fact]
    public void Blocks_items_and_prompts_are_recovered_in_form_order()
    {
        FormsModule module = Assert.Single(FormsModuleParser.Parse(Export).Modules);

        Assert.Equal("BANK_ACCOUNT_REQUEST_FORM", module.Name);
        Assert.Equal("Account Opening Requests", module.Title);

        FormsBlock block = module.Blocks[0];
        Assert.Equal("BANK_ACCOUNT_REQUEST", block.BaseTable);
        Assert.Equal(10, block.RecordsDisplayed);

        // Order is the form's, not the table's, which is the whole reason for reading the export.
        Assert.Equal(["REQUEST_ID", "FAMILY_NAME", "STREET_ADDRESS", "APPROVE_BUTTON"], block.Items.Select(item => item.Name));
        Assert.Equal("Surname", block.Items[1].Prompt);
        Assert.True(block.Items[1].Required);
        Assert.False(block.Items[2].Visible);
    }

    [Fact]
    public void A_control_block_is_reported_because_nothing_populates_it()
    {
        FormsModuleParse parsed = FormsModuleParser.Parse(Export);

        Assert.Contains(
            parsed.Findings,
            finding => finding.Construct.Contains("SUMMARY_BLOCK", StringComparison.Ordinal)
                       && finding.Reason.Contains("control block", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_trigger_is_reported_as_untranslated_behaviour()
    {
        FormsModuleParse parsed = FormsModuleParser.Parse(Export);

        // Structure is recovered; behaviour is not. Saying so is what keeps the screen from looking finished.
        Assert.Contains(parsed.Findings, finding => finding.Construct.EndsWith("WHEN-NEW-FORM-INSTANCE", StringComparison.Ordinal));
        Assert.Contains(parsed.Findings, finding => finding.Construct.EndsWith("WHEN-BUTTON-PRESSED", StringComparison.Ordinal));
        Assert.Contains(parsed.Findings, finding => finding.Construct.EndsWith("REFRESH_SUMMARY", StringComparison.Ordinal));
        Assert.Contains(parsed.Findings, finding => finding.Construct.EndsWith("BRANCH_LOV", StringComparison.Ordinal));

        Assert.All(
            parsed.Findings.Where(finding => finding.Category == "Forms behaviour" && finding.Construct.Contains("WHEN-", StringComparison.Ordinal)),
            finding => Assert.Equal(ConversionSeverity.Unsupported, finding.Severity));
    }

    [Fact]
    public void A_document_type_definition_is_refused_rather_than_resolved()
    {
        // An export is untrusted input; resolving an external entity would read files off the host.
        FormsModuleParse parsed = FormsModuleParser.Parse("""
            <?xml version="1.0"?>
            <!DOCTYPE Module [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <Module><FormModule Name="X"/></Module>
            """);

        Assert.Empty(parsed.Modules);
        Assert.Contains(parsed.Findings, finding => finding.Severity == ConversionSeverity.Unsupported);
    }

    [Fact]
    public void A_file_that_is_not_a_forms_export_yields_no_module()
    {
        FormsModuleParse parsed = FormsModuleParser.Parse("<project><artifactId>demo</artifactId></project>");

        Assert.Empty(parsed.Modules);
        Assert.Contains(parsed.Findings, finding => finding.Reason.Contains("no FormModule element", StringComparison.Ordinal));
    }
}
