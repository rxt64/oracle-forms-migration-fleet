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

    /// <summary>The other documented frmf2xml root: FormModule itself, with Yes/No booleans.</summary>
    private const string RealExport = """
        <?xml version="1.0" encoding="UTF-8"?>
        <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="HRMS_EMPLOYEE" ConsoleWindow="CONSOLE" Title="HRMS - Employee Maintenance">
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
    public void Trigger_bodies_are_preserved_from_attribute_and_element_exports()
    {
        FormsModule attributeModule = Assert.Single(FormsModuleParser.Parse(Export).Modules);
        FormsModule elementModule = Assert.Single(FormsModuleParser.Parse(RealExport).Modules);

        Assert.Equal("BEGIN EXECUTE_QUERY; END;", Assert.Single(attributeModule.Triggers).Body);
        Assert.Equal(FormsTriggerBodyEncoding.Attribute, Assert.Single(attributeModule.Triggers).BodyEncoding);
        Assert.Equal(
            "LEGACY_BANKING_API.APPROVE_REQUEST(1, 2);",
            Assert.Single(attributeModule.Blocks[0].Triggers).Body);
        Assert.Equal("BEGIN NULL; END;", Assert.Single(elementModule.Triggers).Body?.Trim());
        Assert.Equal(FormsTriggerBodyEncoding.Element, Assert.Single(elementModule.Triggers).BodyEncoding);
    }

    [Fact]
    public void Identical_trigger_identity_with_changed_body_remains_distinguishable()
    {
        string changed = Export.Replace(
            "BEGIN EXECUTE_QUERY; END;",
            "BEGIN ENTER_QUERY; END;",
            StringComparison.Ordinal);

        FormsTrigger original = Assert.Single(FormsModuleParser.Parse(Export).Modules[0].Triggers);
        FormsTrigger modified = Assert.Single(FormsModuleParser.Parse(changed).Modules[0].Triggers);

        Assert.Equal(original.Name, modified.Name);
        Assert.Equal(original.Scope, modified.Scope);
        Assert.NotEqual(original.Body, modified.Body);
    }

    [Fact]
    public void Item_triggers_retain_the_item_scope()
    {
        FormsModule module = Assert.Single(FormsModuleParser.Parse("""
            <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="ORDER_ENTRY">
              <Block Name="ORDER_BLOCK" QueryDataSourceName="ORDERS">
                <Item Name="QUANTITY">
                  <Trigger Name="WHEN-VALIDATE-ITEM"><TriggerText>BEGIN VALIDATE_QUANTITY; END;</TriggerText></Trigger>
                </Item>
                <Item Name="PRICE">
                  <Trigger Name="WHEN-VALIDATE-ITEM"><TriggerText>BEGIN VALIDATE_PRICE; END;</TriggerText></Trigger>
                </Item>
              </Block>
            </FormModule>
            """).Modules);

        Assert.Equal(
            ["ORDER_BLOCK.QUANTITY", "ORDER_BLOCK.PRICE"],
            module.Blocks[0].Triggers.Select(trigger => trigger.Scope));
        Assert.Equal(2, module.Blocks[0].Triggers.Select(trigger => trigger.Body).Distinct().Count());
    }

    [Theory]
    [InlineData("TriggerText=\"BEGIN ATTRIBUTE_BODY; END;\"><TriggerText>BEGIN ELEMENT_BODY; END;</TriggerText>")]
    [InlineData("><TriggerText>BEGIN FIRST; END;</TriggerText><TriggerText>BEGIN SECOND; END;</TriggerText>")]
    [InlineData("><TriggerText>BEGIN <Nested>NULL;</Nested> END;</TriggerText>")]
    public void Ambiguous_or_nested_trigger_text_is_refused(string bodyShape)
    {
        FormsModuleParse parsed = FormsModuleParser.Parse($"""
            <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="ORDER_ENTRY">
              <Trigger Name="WHEN-NEW-FORM-INSTANCE" {bodyShape}</Trigger>
            </FormModule>
            """);

        Assert.Empty(parsed.Modules);
        Assert.Contains(parsed.Findings, finding => finding.Severity == ConversionSeverity.Unsupported);
    }

      [Theory]
      [InlineData("TriggerText=\"   \"")]
      [InlineData("triggertext=\"BEGIN NULL; END;\"")]
      [InlineData("><TriggerText>   </TriggerText>")]
      [InlineData("><triggertext>BEGIN NULL; END;</triggertext>")]
      public void Explicit_blank_or_mis_cased_trigger_text_is_refused(string bodyShape)
      {
        FormsModuleParse parsed = FormsModuleParser.Parse($"""
          <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="ORDER_ENTRY">
            <Trigger Name="WHEN-NEW-FORM-INSTANCE" {bodyShape}</Trigger>
          </FormModule>
          """);

        Assert.Empty(parsed.Modules);
        Assert.Contains(parsed.Findings, finding => finding.Severity == ConversionSeverity.Unsupported);
      }

      [Fact]
      public void Duplicate_source_trigger_identity_is_an_unsupported_module_finding()
      {
        FormsModuleParse parsed = FormsModuleParser.Parse("""
          <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="ORDER_ENTRY">
            <Trigger Name="WHEN-NEW-FORM-INSTANCE" TriggerText="BEGIN FIRST; END;"/>
            <Trigger Name="WHEN-NEW-FORM-INSTANCE" TriggerText="BEGIN SECOND; END;"/>
          </FormModule>
          """);

        Assert.Single(parsed.Modules);
        Assert.Contains(parsed.Findings, finding =>
          finding.Severity == ConversionSeverity.Unsupported
          && finding.Category == "Forms module"
          && finding.Reason.Contains("more than one trigger", StringComparison.Ordinal));
      }

      [Theory]
      [InlineData("<Block Name=\"DUPLICATE\"/><Block Name=\"DUPLICATE\"/>", "more than one block")]
      [InlineData("<Block Name=\"B\"><Item Name=\"DUPLICATE\"/><Item Name=\"DUPLICATE\"/></Block>", "more than one item")]
      public void Duplicate_source_block_or_item_identity_is_an_unsupported_module_finding(string structure, string expected)
      {
        FormsModuleParse parsed = FormsModuleParser.Parse($"""
          <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="ORDER_ENTRY">
            {structure}
          </FormModule>
          """);

        Assert.Single(parsed.Modules);
        Assert.Contains(parsed.Findings, finding =>
          finding.Severity == ConversionSeverity.Unsupported
          && finding.Category == "Forms module"
          && finding.Reason.Contains(expected, StringComparison.Ordinal));
      }

      [Fact]
      public void Source_module_count_beyond_the_reader_limit_is_refused_before_any_module_is_read()
      {
        string modules = string.Concat(Enumerable.Range(0, FormsIntermediateReader.MaxModules + 1)
          .Select(index => $"<FormModule Name=\"M{index}\"/>"));

        FormsModuleParse parsed = FormsModuleParser.Parse($"""
          <Module xmlns="http://xmlns.oracle.com/Forms">{modules}</Module>
          """);

        Assert.Empty(parsed.Modules);
        Assert.Contains(parsed.Findings, finding =>
          finding.Severity == ConversionSeverity.Unsupported
          && finding.Reason.Contains("5001 module entries", StringComparison.Ordinal));
      }

      [Fact]
      public void Duplicate_modules_in_one_source_export_are_unsupported()
      {
        FormsModuleParse parsed = FormsModuleParser.Parse("""
          <Module xmlns="http://xmlns.oracle.com/Forms">
            <FormModule Name="DUPLICATE"/>
            <FormModule Name="DUPLICATE"/>
          </Module>
          """);

        Assert.Equal(2, parsed.Modules.Count);
        Assert.Contains(parsed.Findings, finding =>
          finding.Severity == ConversionSeverity.Unsupported
          && finding.Category == "Forms module"
          && finding.Reason.Contains("more than one module", StringComparison.Ordinal));
      }

      [Fact]
      public void Source_child_count_beyond_the_reader_limit_is_an_unsupported_module_finding()
      {
        string items = string.Concat(Enumerable.Range(0, FormsIntermediateReader.MaxChildren + 1)
          .Select(index => $"<Item Name=\"I{index}\"/>"));

        FormsModuleParse parsed = FormsModuleParser.Parse($"""
          <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="ORDER_ENTRY">
            <Block Name="B">{items}</Block>
          </FormModule>
          """);

        Assert.Single(parsed.Modules);
        Assert.Contains(parsed.Findings, finding =>
          finding.Severity == ConversionSeverity.Unsupported
          && finding.Reason.Contains("20001 item entries", StringComparison.Ordinal));
      }

    [Fact]
    public void A_document_type_definition_is_refused_rather_than_resolved()
    {
        // An export is untrusted input; resolving an external entity would read files off the host.
        FormsModuleParse parsed = FormsModuleParser.Parse("""
            <?xml version="1.0"?>
            <!DOCTYPE Module [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <Module xmlns="http://xmlns.oracle.com/Forms"><FormModule Name="X"/></Module>
            """);

        Assert.Empty(parsed.Modules);
        Assert.Contains(parsed.Findings, finding => finding.Severity == ConversionSeverity.Unsupported);
    }

    /// <summary>
    /// Only the two documented Oracle roots are read, and both have to be in the Forms namespace. A
    /// namespace-less or foreign document whose elements happen to be named FormModule carries attribute
    /// meanings this fleet has not established, so reading it would invent a module out of arbitrary XML.
    /// </summary>
    [Theory]
    [InlineData("<Module><FormModule Name=\"X\"><Block Name=\"B\"/></FormModule></Module>")]
    [InlineData("<FormModule Name=\"X\"><Block Name=\"B\"/></FormModule>")]
    [InlineData("<Module xmlns=\"urn:example:forms\"><FormModule Name=\"X\"/></Module>")]
    [InlineData("<config xmlns=\"http://xmlns.oracle.com/Forms\"><FormModule Name=\"X\"/></config>")]
    public void A_root_shape_outside_the_oracle_forms_namespace_is_refused(string xml)
    {
        FormsModuleParse parsed = FormsModuleParser.Parse(xml);

        Assert.Empty(parsed.Modules);
        Assert.Contains(
            parsed.Findings,
            finding => finding.Severity == ConversionSeverity.Unsupported
                       && finding.Reason.Contains("http://xmlns.oracle.com/Forms", StringComparison.Ordinal));
    }

    [Fact]
    public void A_mixed_namespace_export_is_refused_rather_than_partly_read()
    {
        FormsModuleParse parsed = FormsModuleParser.Parse("""
            <Module xmlns="http://xmlns.oracle.com/Forms">
              <FormModule Name="READ_ME"><Block Name="B"/></FormModule>
              <other:FormModule xmlns:other="urn:example:forms" Name="HIDDEN"/>
            </Module>
            """);

        Assert.Empty(parsed.Modules);
        Assert.Contains(parsed.Findings, finding => finding.Reason.Contains("mixes namespaces", StringComparison.Ordinal));
    }

    /// <summary>
    /// A valid root is not a licence for everything under it. Forms structure is matched by element name,
    /// so an element in a foreign namespace carrying a Forms name would be read as the block, item,
    /// trigger, program unit, LOV, library, or relation it is not, and a document nobody could change at
    /// the root could still inject structure into the module model.
    /// </summary>
    [Theory]
    [InlineData("<evil:Block xmlns:evil=\"urn:example:evil\" Name=\"INJECTED\" QueryDataSourceName=\"SECRETS\"/>")]
    [InlineData("<evil:Item xmlns:evil=\"urn:example:evil\" Name=\"INJECTED\" ColumnName=\"PASSWORD_HASH\"/>")]
    [InlineData("<evil:Trigger xmlns:evil=\"urn:example:evil\" Name=\"WHEN-INJECTED\"/>")]
    [InlineData("<evil:TriggerText xmlns:evil=\"urn:example:evil\">BEGIN NULL; END;</evil:TriggerText>")]
    [InlineData("<evil:ProgramUnit xmlns:evil=\"urn:example:evil\" Name=\"INJECTED\"/>")]
    [InlineData("<evil:LOV xmlns:evil=\"urn:example:evil\" Name=\"INJECTED\"/>")]
    [InlineData("<evil:AttachedLibrary xmlns:evil=\"urn:example:evil\" Name=\"INJECTED\"/>")]
    [InlineData("<evil:Relation xmlns:evil=\"urn:example:evil\" Name=\"INJECTED\" DetailBlock=\"B\"/>")]
    [InlineData("<Block xmlns=\"\" Name=\"INJECTED\" QueryDataSourceName=\"SECRETS\"/>")]
    [InlineData("<Item xmlns=\"\" Name=\"INJECTED\" ColumnName=\"PASSWORD_HASH\"/>")]
    public void A_foreign_namespace_element_nested_under_a_valid_root_is_refused(string injected)
    {
        FormsModuleParse parsed = FormsModuleParser.Parse($"""
            <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4">
              <FormModule Name="ORDER_ENTRY">
                <Block Name="ORDER_BLOCK" QueryDataSourceName="ORDERS">
                  {injected}
                </Block>
              </FormModule>
            </Module>
            """);

        Assert.Empty(parsed.Modules);
        Assert.Contains(parsed.Findings, finding =>
            finding.Severity == ConversionSeverity.Unsupported
            && finding.Reason.Contains("mixes namespaces", StringComparison.Ordinal));
    }

    [Fact]
    public void A_foreign_namespace_element_under_a_bare_formmodule_root_is_refused()
    {
        // The single-module root shape used to skip the mixed-namespace check entirely.
        FormsModuleParse parsed = FormsModuleParser.Parse("""
            <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="HRMS_EMPLOYEE">
              <evil:Block xmlns:evil="urn:example:evil" Name="INJECTED" QueryDataSourceName="SECRETS"/>
            </FormModule>
            """);

        Assert.Empty(parsed.Modules);
        Assert.Contains(parsed.Findings, finding => finding.Reason.Contains("mixes namespaces", StringComparison.Ordinal));
    }

    /// <summary>
    /// Attributes are read by local name, so a qualified attribute would compete with the real one for the
    /// same meaning and document order would settle it. The document is refused instead.
    /// </summary>
    [Theory]
    [InlineData("<Block evil:Name=\"INJECTED\" xmlns:evil=\"urn:example:evil\" Name=\"ORDER_BLOCK\" QueryDataSourceName=\"ORDERS\"/>")]
    [InlineData("<Block Name=\"ORDER_BLOCK\" evil:QueryDataSourceName=\"SECRETS\" xmlns:evil=\"urn:example:evil\"/>")]
    public void A_namespace_qualified_attribute_on_a_forms_element_is_refused(string block)
    {
        FormsModuleParse parsed = FormsModuleParser.Parse($"""
            <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4">
              <FormModule Name="ORDER_ENTRY">
                {block}
              </FormModule>
            </Module>
            """);

        Assert.Empty(parsed.Modules);
        Assert.Contains(parsed.Findings, finding =>
            finding.Reason.Contains("namespace-qualified attribute", StringComparison.Ordinal));
    }

    /// <summary>The two namespaces a real export routinely carries and that name nothing this fleet reads.</summary>
    [Fact]
    public void Schema_instance_and_xml_attributes_are_still_accepted()
    {
        FormsModuleParse parsed = FormsModuleParser.Parse("""
            <Module xmlns="http://xmlns.oracle.com/Forms"
                    xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                    xsi:schemaLocation="http://xmlns.oracle.com/Forms forms.xsd"
                    version="12.2.1.4">
              <FormModule Name="ORDER_ENTRY" xml:lang="en">
                <Block Name="ORDER_BLOCK" QueryDataSourceName="ORDERS"/>
              </FormModule>
            </Module>
            """);

        FormsModule module = Assert.Single(parsed.Modules);
        Assert.Equal("ORDER_ENTRY", module.Name);
        Assert.Equal("ORDERS", module.Blocks[0].BaseTable);
    }

    /// <summary>
    /// The rejection has to be the same answer on both sides. The version reader and the parser share one
    /// loader precisely so a document cannot be refused structure by one and read for a release by the other.
    /// </summary>
    [Fact]
    public void The_version_reader_refuses_the_same_nested_foreign_namespace_document()
    {
        const string Injected = """
            <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4">
              <FormModule Name="ORDER_ENTRY" FormsVersion="12.2.1.4">
                <evil:Trigger xmlns:evil="urn:example:evil" Name="WHEN-INJECTED"/>
              </FormModule>
            </Module>
            """;

        FormsXmlDeclaration declaration = FormsXmlVersionReader.Read(Injected);

        Assert.False(declaration.HasFormModule);
        Assert.Null(declaration.AnyDeclaredVersion);
        Assert.NotNull(declaration.ShapeRejection);
        Assert.Empty(FormsModuleParser.Parse(Injected).Modules);
    }

    [Fact]
    public void The_version_reader_and_the_parser_agree_on_what_counts_as_an_export()
    {
        // They used to decide independently, so a document could declare a version to one and no module
        // to the other.
        foreach (string xml in (string[])[Export, RealExport, "<FormModule Name=\"X\"/>", "<project/>"])
        {
            Assert.Equal(FormsXmlVersionReader.Read(xml).HasFormModule, FormsModuleParser.Parse(xml).Modules.Count > 0);
        }
    }

    [Fact]
    public void A_file_that_is_not_a_forms_export_yields_no_module()
    {
        FormsModuleParse parsed = FormsModuleParser.Parse("<project><artifactId>demo</artifactId></project>");

        Assert.Empty(parsed.Modules);
        Assert.Contains(parsed.Findings, finding => finding.Reason.Contains("no FormModule element", StringComparison.Ordinal));
    }
}
