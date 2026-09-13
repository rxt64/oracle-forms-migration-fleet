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
