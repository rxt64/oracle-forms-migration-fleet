// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

public class FormsModuleParserTests
{
    /// <summary>Every source-object path under the module element of the master/detail export.</summary>
    private const string Forms = "{http://xmlns.oracle.com/Forms}";

    private static FormsSourceFactSet Facts(string export) =>
        Assert.Single(FormsModuleParser.Parse(export).Modules).SourceFacts!;

    private static FormsSourceFact Fact(FormsSourceFactSet facts, string id) =>
        Assert.Single(facts.Facts, fact => fact.Id == id);

    private static string? Attribute(FormsSourceFact fact, string name) =>
        fact.Attributes.SingleOrDefault(attribute =>
            attribute.Namespace.Length == 0 && attribute.Name == name)?.Value;

    /// <summary>
    /// The retained inventory of the lookup export, written out here rather than derived from the parser,
    /// so an element the parser stops retaining fails this test instead of quietly disappearing.
    /// </summary>
    [Fact]
    public void Every_element_of_an_export_is_retained_in_document_order()
    {
        FormsSourceFactSet facts = Facts(OracleSamples.LookupExport);

        Assert.Equal(
            [
                $"{Forms}FormModule[1]",
                $"{Forms}FormModule[1]/{{urn:contoso:forms-annotations}}Annotation[1]",
                $"{Forms}FormModule[1]/{Forms}Block[1]",
                $"{Forms}FormModule[1]/{Forms}Block[1]/{Forms}Item[1]",
                $"{Forms}FormModule[1]/{Forms}Block[1]/{Forms}Item[2]",
            ],
            facts.Facts.Select(fact => fact.Id));

        Assert.Equal([0, 1, 2, 3, 4], facts.Facts.Select(fact => fact.Order));
        Assert.All(facts.Facts, fact => Assert.Equal(FormsSourceFactKind.Declared, fact.Kind));

        // Repeated siblings are distinguished by index, and each child records where it sits.
        Assert.Equal([0, 1], facts.Facts.Where(fact => fact.LocalName == "Item").Select(fact => fact.ChildIndex));
        Assert.Equal($"{Forms}FormModule[1]/{Forms}Block[1]", facts.Facts[4].ParentId);
        Assert.Null(facts.Facts[0].ParentId);
    }

    [Fact]
    public void A_foreign_element_is_retained_as_declared_and_not_read_as_forms_structure()
    {
        FormsModule module = Assert.Single(FormsModuleParser.Parse(OracleSamples.LookupExport).Modules);
        FormsSourceFact annotation = Fact(
            module.SourceFacts!,
            $"{Forms}FormModule[1]/{{urn:contoso:forms-annotations}}Annotation[1]");

        Assert.Equal("urn:contoso:forms-annotations", annotation.Namespace);
        Assert.Equal("site-standard", Attribute(annotation, "Origin"));
        Assert.Equal(FormsSourceFactKind.Declared, annotation.Kind);

        // Retaining it is not reading it: the interpreted module is still one block of two items.
        Assert.Equal(2, Assert.Single(module.Blocks).Items.Count);
    }

    /// <summary>
    /// The constructs the interpreting parser never reads. Each is retained as a declared attribute, which
    /// is the only reason the loss is recoverable at all.
    /// </summary>
    [Theory]
    [InlineData($"{Forms}FormModule[1]/{Forms}Relation[1]", "JoinCondition", "PICK_HEADER.PICK_ID = PICK_LINE.PICK_ID")]
    [InlineData($"{Forms}FormModule[1]/{Forms}Relation[1]", "DeleteRecordBehavior", "Cascading")]
    [InlineData($"{Forms}FormModule[1]/{Forms}RecordGroup[1]", "RecordGroupQuery", "SELECT BIN_CODE, BIN_LABEL FROM WAREHOUSE.BIN ORDER BY BIN_CODE")]
    [InlineData($"{Forms}FormModule[1]/{Forms}LOV[1]/{Forms}LOVColumnMapping[1]", "ReturnItem", "PICK_LINE.BIN_CODE")]
    [InlineData($"{Forms}FormModule[1]/{Forms}Block[1]/{Forms}Item[3]/{Forms}RadioButton[2]", "RadioButtonValue", "U")]
    [InlineData($"{Forms}FormModule[1]/{Forms}Block[1]/{Forms}Item[4]", "Formula", ":PICK_LINE.QUANTITY * :PICK_LINE.UNIT_COST")]
    [InlineData($"{Forms}FormModule[1]/{Forms}Block[1]/{Forms}Item[1]", "FormatMask", "9999999")]
    [InlineData($"{Forms}FormModule[1]/{Forms}Block[1]/{Forms}Item[1]", "UpdateAllowed", "No")]
    [InlineData($"{Forms}FormModule[1]/{Forms}Block[1]/{Forms}Item[1]", "Required", "Yes")]
    [InlineData($"{Forms}FormModule[1]/{Forms}Block[1]/{Forms}Item[3]", "InitialValue", "N")]
    [InlineData($"{Forms}FormModule[1]/{Forms}AttachedLibrary[1]", "LibrarySource", "File")]
    [InlineData($"{Forms}FormModule[1]", "MenuModule", "WAREHOUSE_MENU")]
    [InlineData($"{Forms}FormModule[1]/{Forms}Trigger[1]", "TriggerText", "BEGIN :GLOBAL.PICK_SESSION := 'OPEN'; END;")]
    [InlineData($"{Forms}FormModule[1]/{Forms}Canvas[1]/{Forms}Graphics[1]/{Forms}CompoundText[1]/{Forms}TextSegment[1]", "TextSegmentString", "Warehouse picking")]
    public void A_property_the_parser_never_reads_is_retained_as_a_declared_fact(string id, string attribute, string expected)
    {
        Assert.Equal(expected, Attribute(Fact(Facts(OracleSamples.MasterDetailExport()), id), attribute));
    }

    /// <summary>A site-specific property nothing in this build understands is retained unaltered.</summary>
    [Fact]
    public void An_unknown_property_is_retained_rather_than_dropped()
    {
        Assert.Equal(
            "Y",
            Attribute(Fact(Facts(OracleSamples.MasterDetailExport()), $"{Forms}FormModule[1]"), "ContosoAuditFlag"));
    }

    /// <summary>
    /// A property the export omitted and one it declared as No are different facts. The interpreted model
    /// collapses both to false, which is why the distinction has to survive here.
    /// </summary>
    [Fact]
    public void An_omitted_property_is_distinguishable_from_one_declared_false()
    {
        FormsSourceFactSet facts = Facts(OracleSamples.MasterDetailExport());
        FormsSourceFact declaredNo = Fact(facts, $"{Forms}FormModule[1]/{Forms}Block[1]/{Forms}Item[2]");
        FormsSourceFact omitted = Fact(facts, $"{Forms}FormModule[1]/{Forms}Block[2]/{Forms}Item[3]");

        Assert.Equal("No", Attribute(declaredNo, "Required"));
        Assert.Null(Attribute(omitted, "UpdateAllowed"));
        Assert.Equal("Yes", Attribute(omitted, "Required"));
    }

    /// <summary>
    /// An export that double-escaped its newlines carries the five characters of a character reference, not
    /// a newline. Decoding the XML-normalized value again would rewrite the program unit body.
    /// </summary>
    [Fact]
    public void An_xml_normalized_value_is_retained_without_being_decoded_again()
    {
        string? body = Attribute(
            Fact(Facts(OracleSamples.MasterDetailExport()), $"{Forms}FormModule[1]/{Forms}ProgramUnit[1]"),
            "ProgramUnitText");

        Assert.Equal("PROCEDURE RECALCULATE_TOTALS IS&#10;BEGIN&#10;  NULL;&#10;END;", body);
        Assert.DoesNotContain('\n', body!);
    }

    [Fact]
    public void Direct_element_text_is_retained_and_an_element_without_it_records_none()
    {
        FormsSourceFactSet facts = Facts(OracleSamples.MasterDetailExport());

        Assert.Equal(
            "BEGIN RECALCULATE_TOTALS; END;",
            Fact(facts, $"{Forms}FormModule[1]/{Forms}Block[1]/{Forms}Trigger[1]/{Forms}TriggerText[1]").Text);
        Assert.Null(Fact(facts, $"{Forms}FormModule[1]/{Forms}Block[1]").Text);
    }

    /// <summary>
    /// The loader drops insignificant inter-element whitespace, so whatever text reaches retention is text
    /// the export meant to carry. Whitespace under <c>xml:space="preserve"</c> is therefore kept as
    /// declared: blanking it to null would make a preserved run of spaces indistinguishable from an
    /// element that declared no text at all, which is the one distinction retention exists to hold.
    /// </summary>
    [Fact]
    public void Preserved_whitespace_is_retained_and_only_a_missing_text_node_records_none()
    {
        const string Export = """
            <?xml version="1.0" encoding="UTF-8"?>
            <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="WHITESPACE" Title="Whitespace">
              <Comment xml:space="preserve">   </Comment>
              <Comment>  spaced  </Comment>
              <Comment/>
              <Block Name="B" QueryDataSourceName="T" RecordsDisplayed="1">
                <Item Name="I" ItemType="Text Item"/>
              </Block>
            </FormModule>
            """;

        FormsSourceFactSet facts = Facts(Export);

        Assert.Equal("   ", Fact(facts, $"{Forms}FormModule[1]/{Forms}Comment[1]").Text);
        Assert.Equal("  spaced  ", Fact(facts, $"{Forms}FormModule[1]/{Forms}Comment[2]").Text);
        Assert.Null(Fact(facts, $"{Forms}FormModule[1]/{Forms}Comment[3]").Text);

        // Whitespace the loader discarded never reaches retention, so a container still records none.
        Assert.Null(Fact(facts, $"{Forms}FormModule[1]/{Forms}Block[1]").Text);
    }

    /// <summary>
    /// A module's paths are its own. The wrapper's second FormModule used to be retained as
    /// <c>FormModule[2]</c> because the step counted siblings in the document, so the same element of two
    /// otherwise identical modules carried different ids and neither agreed with the parent it recorded.
    /// </summary>
    [Fact]
    public void Each_module_of_a_multi_module_wrapper_roots_its_paths_at_index_one()
    {
        const string Export = """
            <?xml version="1.0" encoding="UTF-8"?>
            <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4">
              <FormModule Name="FIRST" Title="First">
                <Block Name="A" QueryDataSourceName="T1" RecordsDisplayed="1">
                  <Item Name="X" ItemType="Text Item"/>
                </Block>
              </FormModule>
              <FormModule Name="SECOND" Title="Second">
                <Block Name="B" QueryDataSourceName="T2" RecordsDisplayed="1">
                  <Item Name="Y" ItemType="Text Item"/>
                </Block>
              </FormModule>
            </Module>
            """;

        IReadOnlyList<FormsModule> modules = FormsModuleParser.Parse(Export).Modules;

        Assert.Equal(["FIRST", "SECOND"], modules.Select(module => module.Name));

        foreach (FormsModule module in modules)
        {
            Assert.Equal(
                [
                    $"{Forms}FormModule[1]",
                    $"{Forms}FormModule[1]/{Forms}Block[1]",
                    $"{Forms}FormModule[1]/{Forms}Block[1]/{Forms}Item[1]",
                ],
                module.SourceFacts!.Facts.Select(fact => fact.Id));

            Assert.Equal([null, $"{Forms}FormModule[1]", $"{Forms}FormModule[1]/{Forms}Block[1]"],
                module.SourceFacts!.Facts.Select(fact => fact.ParentId));
            Assert.Equal("12.2.1.4", module.SourceFacts!.WrapperDeclaredVersion);
        }

        // Identical paths, different modules: the facts under them are what tell the two apart.
        Assert.Equal(["FIRST", "SECOND"], modules.Select(module => module.SourceFacts!.Facts[0].DeclaredName));
        Assert.Equal(["A", "B"], modules.Select(module => module.SourceFacts!.Facts[1].DeclaredName));
    }

    /// <summary>
    /// Nesting is bounded explicitly rather than by whatever depth the runtime stack survives, because the
    /// export is untrusted input and short unqualified names keep every path well inside the id limit.
    /// </summary>
    [Theory]
    [InlineData(FormsSourceFactReader.MaxDepth - 1, true)]
    [InlineData(600, false)]
    public void Nesting_is_retained_to_the_declared_depth_and_refused_beyond_it(int nested, bool retained)
    {
        string export =
            "<FormModule xmlns=\"http://xmlns.oracle.com/Forms\" Name=\"DEEP\">" +
            "<n xmlns=\"\">" + string.Concat(Enumerable.Repeat("<n>", nested - 1)) +
            string.Concat(Enumerable.Repeat("</n>", nested - 1)) + "</n>" +
            "</FormModule>";

        FormsModuleParse parse = FormsModuleParser.Parse(export);

        if (retained)
        {
            Assert.Equal(nested + 1, Assert.Single(parse.Modules).SourceFacts!.Facts.Count);
            return;
        }

        Assert.Empty(parse.Modules);
        Assert.Contains(
            parse.Findings,
            finding => finding.Reason.Contains($"nests elements more than {FormsSourceFactReader.MaxDepth} deep", StringComparison.Ordinal));
    }

    /// <summary>
    /// An element's direct text is retained as one value and rebuilt before its children, so text declared
    /// on both sides of a child came back as one run before it: <c>&lt;Note&gt;A&lt;Span&gt;B&lt;/Span&gt;C&lt;/Note&gt;</c>
    /// retained "AC" and put it ahead of Span. Nothing said so, and the retained inventory is the only
    /// record of what the export declared, so the document is refused instead.
    /// </summary>
    [Fact]
    public void Text_declared_beside_child_elements_is_refused_rather_than_retained_out_of_order()
    {
        const string Mixed = """
            <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="MIXED">
              <Note>A<Span>B</Span>C</Note>
              <Block Name="B1" QueryDataSourceName="T" RecordsDisplayed="1"/>
            </FormModule>
            """;

        FormsModuleParse parse = FormsModuleParser.Parse(Mixed);

        Assert.Empty(parse.Modules);

        ConversionFinding finding = Assert.Single(
            parse.Findings,
            item => item.Severity == ConversionSeverity.Unsupported && item.Category == "Forms module");

        Assert.Contains("declares text directly beside child elements", finding.Reason, StringComparison.Ordinal);
        Assert.Contains("{http://xmlns.oracle.com/Forms}Note[1]", finding.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal above is about text that shares an element with child elements, and nothing else. A leaf
    /// carrying a body, indentation an export preserves around its children, and an ordinary export all
    /// stay readable, or the fix would refuse the estates it exists to keep honest.
    /// </summary>
    [Fact]
    public void A_leaf_body_preserved_indentation_and_an_ordinary_export_are_still_retained()
    {
        const string Neighbours = """
            <FormModule xmlns="http://xmlns.oracle.com/Forms" Name="NEIGHBOURS">
              <Block Name="B1" QueryDataSourceName="T" RecordsDisplayed="1">
                <Trigger Name="WHEN-VALIDATE-RECORD">
                  <TriggerText>BEGIN RECALCULATE; END;</TriggerText>
                </Trigger>
              </Block>
              <Wrapper xml:space="preserve">
                <Leaf>body</Leaf>
              </Wrapper>
            </FormModule>
            """;

        FormsSourceFactSet facts = Facts(Neighbours);

        Assert.Equal("BEGIN RECALCULATE; END;", Fact(facts, $"{Forms}FormModule[1]/{Forms}Block[1]/{Forms}Trigger[1]/{Forms}TriggerText[1]").Text);
        Assert.Equal("body", Fact(facts, $"{Forms}FormModule[1]/{Forms}Wrapper[1]/{Forms}Leaf[1]").Text);

        // Whitespace an export preserves around its children is indentation, not declared content.
        Assert.True(string.IsNullOrWhiteSpace(Fact(facts, $"{Forms}FormModule[1]/{Forms}Wrapper[1]").Text));

        Assert.Equal(5, Facts(Export).Facts.Count(fact => fact.LocalName == "Item"));
        Assert.DoesNotContain(
            FormsModuleParser.Parse(Export).Findings,
            finding => finding.Reason.Contains("beside child elements", StringComparison.Ordinal));
    }

    /// <summary>
    /// The wrapper's version attribute is a string the file carried. It is kept verbatim and apart from any
    /// release this fleet adjudicates, and an export with no wrapper records none.
    /// </summary>
    [Fact]
    public void The_wrapper_declared_version_is_retained_verbatim_and_separately()
    {
        Assert.Equal("122010400", Facts(OracleSamples.MasterDetailExport()).WrapperDeclaredVersion);
        Assert.Null(Facts(OracleSamples.LookupExport).WrapperDeclaredVersion);
    }

    [Fact]
    public void The_text_digest_changes_when_a_single_property_changes()
    {
        string edited = OracleSamples.MasterDetailExport().Replace(
            "FormatMask=\"999G999D99\"", "FormatMask=\"999G999D999\"", StringComparison.Ordinal);

        FormsSourceFactSet original = Facts(OracleSamples.MasterDetailExport());
        FormsSourceFactSet changed = Facts(edited);

        Assert.NotEqual(original.TextDigest, changed.TextDigest);
        Assert.Equal(64, original.TextDigest.Length);
        Assert.Equal(original.TextDigest, Facts(OracleSamples.MasterDetailExport()).TextDigest);

        // Two different exports are two different digests, so facts cannot be attributed to the wrong one.
        Assert.NotEqual(original.TextDigest, Facts(OracleSamples.LookupExport).TextDigest);
    }

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
