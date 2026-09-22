// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The intermediate representation lives in an operator's session workspace beside source that came from a
/// customer repository, so it is untrusted input. Every field the normalization phase writes is read back
/// and checked here, because the fields that were written and never verified — where a module came from and
/// which release it declared — are exactly the ones a generated artifact is attributed to.
/// </summary>
public class FormsIntermediateReaderTests
{
    /// <summary>The source root the valid representation records, which is the run's active source root.</summary>
    private const string ActiveSourceRoot = "legacy/forms";

    private const string Forms = "{http://xmlns.oracle.com/Forms}";
    private const string ModuleId = $"{Forms}FormModule[1]";
    private const string TriggerId = $"{ModuleId}/{Forms}Trigger[1]";
    private const string BlockId = $"{ModuleId}/{Forms}Block[1]";
    private const string ItemId = $"{BlockId}/{Forms}Item[1]";

    /// <summary>An intermediate representation in the exact shape the normalization phase writes.</summary>
    private const string ValidIr = """
        {
          "generator": "oracle-forms-migration-fleet/source-normalization",
          "schemaVersion": "3",
          "normalized": true,
          "sourceRoot": "legacy/forms",
          "formsFamily": "12c",
          "versionAuthority": "declared by the supplied export",
          "modules": [
            {
              "name": "ORDER_ENTRY",
              "title": "Order entry",
              "sourcePath": "legacy/forms/ui/ORDER_ENTRY.xml",
              "declaredVersion": "12.2.1.4",
              "declaredFamily": "12c",
              "blocks": [
                {
                  "name": "ORDER_BLOCK",
                  "baseTable": "BANK_ACCOUNT",
                  "recordsDisplayed": 10,
                  "items": [
                    {
                      "name": "ACCOUNT_ID",
                      "itemType": "Text Item",
                      "dataType": "Number",
                      "columnName": "ACCOUNT_ID",
                      "prompt": "Account",
                      "maxLength": 12,
                      "required": true,
                      "visible": true
                    }
                  ],
                  "triggers": [{"name": "WHEN-VALIDATE-ITEM","scope": "ORDER_BLOCK.ACCOUNT_ID","body": "BEGIN VALIDATE_ITEM; END;","bodyEncoding": "Element"}]
                }
              ],
              "triggers": [{"name": "WHEN-NEW-FORM-INSTANCE","scope": "ORDER_ENTRY","body": "BEGIN EXECUTE_QUERY; END;","bodyEncoding": "Attribute"}],
              "programUnits": [],
              "lovs": [],
              "sourceFacts": {
                "textDigest": "3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b",
                "wrapperDeclaredVersion": "12.2.1.4",
                "facts": [
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]",
                    "order": 0,
                    "childIndex": 0,
                    "localName": "FormModule",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "ORDER_ENTRY",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "ORDER_ENTRY"},
                      {"name": "Title", "namespace": "", "value": "Order entry"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Trigger[1]",
                    "order": 1,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]",
                    "childIndex": 0,
                    "localName": "Trigger",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "WHEN-NEW-FORM-INSTANCE",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "WHEN-NEW-FORM-INSTANCE"},
                      {"name": "TriggerText", "namespace": "", "value": "BEGIN EXECUTE_QUERY; END;"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]",
                    "order": 2,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]",
                    "childIndex": 1,
                    "localName": "Block",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "ORDER_BLOCK",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "ORDER_BLOCK"},
                      {"name": "QueryDataSourceName", "namespace": "", "value": "BANK_ACCOUNT"},
                      {"name": "RecordsDisplayCount", "namespace": "", "value": "10"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]",
                    "order": 3,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]",
                    "childIndex": 0,
                    "localName": "Item",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "ACCOUNT_ID",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "ACCOUNT_ID"},
                      {"name": "ItemType", "namespace": "", "value": "Text Item"},
                      {"name": "DataType", "namespace": "", "value": "Number"},
                      {"name": "Prompt", "namespace": "", "value": "Account:"},
                      {"name": "Required", "namespace": "", "value": "Yes"},
                      {"name": "MaximumLength", "namespace": "", "value": "12"},
                      {"name": "FormatMask", "namespace": "", "value": "999G999"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]/{http://xmlns.oracle.com/Forms}Trigger[1]",
                    "order": 4,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]",
                    "childIndex": 0,
                    "localName": "Trigger",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "WHEN-VALIDATE-ITEM",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "WHEN-VALIDATE-ITEM"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]/{http://xmlns.oracle.com/Forms}Trigger[1]/{http://xmlns.oracle.com/Forms}TriggerText[1]",
                    "order": 5,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]/{http://xmlns.oracle.com/Forms}Trigger[1]",
                    "childIndex": 0,
                    "localName": "TriggerText",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "attributes": [],
                    "text": "BEGIN VALIDATE_ITEM; END;",
                    "kind": "Declared"
                  }
                ]
              }
            }
          ],
          "notes": []
        }
        """;

    private static string Mutate(string find, string replace) => Replace(ValidIr, find, replace);

    /// <summary>
    /// A second edit on the same document, so a case can move the interpreted structure and the retained
    /// facts together. Changing only one of them is now a refusal, which several cases below rely on.
    /// </summary>
    private static string Mutate(string find, string replace, string alsoFind, string alsoReplace) =>
        Replace(Mutate(find, replace), alsoFind, alsoReplace);

    private static string Replace(string json, string find, string replace) =>
        json.Replace(find, replace, StringComparison.Ordinal) is { } mutated && !string.Equals(mutated, json, StringComparison.Ordinal)
            ? mutated
            : throw new InvalidOperationException($"The fragment '{find}' does not appear in the intermediate representation being mutated.");

    /// <summary>
    /// The representation normalization writes for an estate carrying one module name in two directories,
    /// which is two modules rather than a clash.
    /// </summary>
    private const string TwoDirectories = """
        {
          "generator": "oracle-forms-migration-fleet/source-normalization",
          "schemaVersion": "3",
          "normalized": true,
          "sourceRoot": "legacy/forms",
          "formsFamily": "12c",
          "versionAuthority": "declared by the supplied export",
          "modules": [
            {
              "name": "ORDERS",
              "title": "Orders",
              "sourcePath": "legacy/forms/forms-a/ORDERS.xml",
              "declaredVersion": "12.2.1.4",
              "declaredFamily": "12c",
              "blocks": [
                {
                  "name": "ORDER_BLOCK",
                  "baseTable": "BANK_ACCOUNT",
                  "recordsDisplayed": 10,
                  "items": [
                    {
                      "name": "ACCOUNT_ID",
                      "itemType": "Text Item",
                      "columnName": "ACCOUNT_ID",
                      "prompt": "Account",
                      "required": true,
                      "visible": true
                    }
                  ],
                  "triggers": []
                }
              ],
              "triggers": [],
              "programUnits": [],
              "lovs": [],
              "sourceFacts": {
                "textDigest": "1111111111111111111111111111111111111111111111111111111111111111",
                "facts": [
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]",
                    "order": 0,
                    "childIndex": 0,
                    "localName": "FormModule",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "ORDERS",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "ORDERS"},
                      {"name": "Title", "namespace": "", "value": "Orders"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]",
                    "order": 1,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]",
                    "childIndex": 0,
                    "localName": "Block",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "ORDER_BLOCK",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "ORDER_BLOCK"},
                      {"name": "QueryDataSourceName", "namespace": "", "value": "BANK_ACCOUNT"},
                      {"name": "RecordsDisplayCount", "namespace": "", "value": "10"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]",
                    "order": 2,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]",
                    "childIndex": 0,
                    "localName": "Item",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "ACCOUNT_ID",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "ACCOUNT_ID"},
                      {"name": "ItemType", "namespace": "", "value": "Text Item"},
                      {"name": "Prompt", "namespace": "", "value": "Account"},
                      {"name": "Required", "namespace": "", "value": "Yes"}
                    ],
                    "kind": "Declared"
                  }
                ]
              }
            },
            {
              "name": "ORDERS",
              "title": "Orders",
              "sourcePath": "legacy/forms/forms-b/ORDERS.xml",
              "declaredVersion": "12.2.1.4",
              "declaredFamily": "12c",
              "blocks": [
                {
                  "name": "ORDER_BLOCK",
                  "baseTable": "BANK_ACCOUNT",
                  "recordsDisplayed": 10,
                  "items": [
                    {
                      "name": "ACCOUNT_ID",
                      "itemType": "Text Item",
                      "columnName": "ACCOUNT_ID",
                      "prompt": "Account",
                      "required": true,
                      "visible": true
                    }
                  ],
                  "triggers": []
                }
              ],
              "triggers": [],
              "programUnits": [],
              "lovs": [],
              "sourceFacts": {
                "textDigest": "2222222222222222222222222222222222222222222222222222222222222222",
                "facts": [
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]",
                    "order": 0,
                    "childIndex": 0,
                    "localName": "FormModule",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "ORDERS",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "ORDERS"},
                      {"name": "Title", "namespace": "", "value": "Orders"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]",
                    "order": 1,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]",
                    "childIndex": 0,
                    "localName": "Block",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "ORDER_BLOCK",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "ORDER_BLOCK"},
                      {"name": "QueryDataSourceName", "namespace": "", "value": "BANK_ACCOUNT"},
                      {"name": "RecordsDisplayCount", "namespace": "", "value": "10"}
                    ],
                    "kind": "Declared"
                  },
                  {
                    "id": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]",
                    "order": 2,
                    "parentId": "{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]",
                    "childIndex": 0,
                    "localName": "Item",
                    "namespace": "http://xmlns.oracle.com/Forms",
                    "declaredName": "ACCOUNT_ID",
                    "attributes": [
                      {"name": "Name", "namespace": "", "value": "ACCOUNT_ID"},
                      {"name": "ItemType", "namespace": "", "value": "Text Item"},
                      {"name": "Prompt", "namespace": "", "value": "Account"},
                      {"name": "Required", "namespace": "", "value": "Yes"}
                    ],
                    "kind": "Declared"
                  }
                ]
              }
            }
          ],
          "notes": []
        }
        """;

    /// <summary>Reads against the source root this run is executing on, which is what the adapter passes.</summary>
    private static FormsIntermediateRead Read(string? json) => FormsIntermediateReader.Read(json, ActiveSourceRoot);

    [Fact]
    public void The_shape_the_normalization_phase_writes_is_read_in_full()
    {
        FormsIntermediateRead read = Read(ValidIr);

        Assert.Null(read.Error);
        FormsModule module = Assert.Single(read.Modules!);

        Assert.Equal("ORDER_ENTRY", module.Name);
        Assert.Equal("Order entry", module.Title);

        FormsBlock block = Assert.Single(module.Blocks);
        Assert.Equal("BANK_ACCOUNT", block.BaseTable);
        Assert.Equal(10, block.RecordsDisplayed);

        FormsItem item = Assert.Single(block.Items);
        Assert.Equal("Number", item.DataType);
        Assert.Equal("Account", item.Prompt);
        Assert.Equal(12, item.MaxLength);
        Assert.Equal("BEGIN EXECUTE_QUERY; END;", Assert.Single(module.Triggers).Body);
        Assert.Equal("BEGIN VALIDATE_ITEM; END;", Assert.Single(block.Triggers).Body);
        Assert.Equal("ORDER_ENTRY", Assert.Single(module.Triggers).Scope);
        Assert.Equal("ORDER_BLOCK.ACCOUNT_ID", Assert.Single(block.Triggers).Scope);
        Assert.Equal(FormsTriggerBodyEncoding.Attribute, Assert.Single(module.Triggers).BodyEncoding);
        Assert.Equal(FormsTriggerBodyEncoding.Element, Assert.Single(block.Triggers).BodyEncoding);
    }

    /// <summary>
    /// Where a module was read from and which release it declared were written by the producer and never
    /// checked, so a document could attribute a generated screen to a file outside the workspace, to no
    /// file at all, or to a release the document as a whole contradicts.
    /// </summary>
    [Theory]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"\"", "no non-empty 'sourcePath'")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"   \"", "no non-empty 'sourcePath'")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\",", "", "no non-empty 'sourcePath'")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": 7", "no non-empty 'sourcePath'")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"../../etc/passwd\"", "must not traverse outside the workspace")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/../../secrets.xml\"", "must not traverse outside the workspace")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"C:/windows/ORDER_ENTRY.xml\"", "must be workspace-relative")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"/etc/ORDER_ENTRY.xml\"", "must be workspace-relative")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"https://contoso/ORDER_ENTRY.xml\"", "must be workspace-relative")]
    public void A_module_that_cannot_be_attributed_to_a_source_file_is_refused(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"declaredFamily\": \"12c\",", "", "no non-empty 'declaredFamily'")]
    [InlineData("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"\"", "no non-empty 'declaredFamily'")]
    [InlineData("\"declaredFamily\": \"12c\"", "\"declaredFamily\": 12", "no non-empty 'declaredFamily'")]
    [InlineData("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"banana\"", "is not a family name this catalog recognizes")]
    [InlineData("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"12.2.1.4\"", "is not a family name this catalog recognizes")]
    [InlineData("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"6i\"", "while the normalized Forms representation records '12c'")]
    [InlineData("\"formsFamily\": \"12c\"", "\"formsFamily\": \"banana\"", "is not a family name this catalog recognizes")]
    [InlineData("\"formsFamily\": \"12c\"", "\"formsFamily\": \"\"", "no non-empty 'formsFamily'")]
    [InlineData("\"formsFamily\": \"12c\"", "\"formsFamily\": 12", "no non-empty 'formsFamily'")]
    public void A_module_whose_declared_family_is_absent_unrecognized_or_contradictory_is_refused(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"declaredVersion\": \"12.2.1.4\"", "\"declaredVersion\": 12", "is present and is not a string")]
    [InlineData("\"declaredVersion\": \"12.2.1.4\"", "\"declaredVersion\": [\"12.2.1.4\"]", "is present and is not a string")]
    [InlineData("\"declaredVersion\": \"12.2.1.4\"", "\"declaredVersion\": \"\"", "declares an empty 'declaredVersion'")]
    [InlineData("\"declaredVersion\": \"12.2.1.4\"", "\"declaredVersion\": \"banana\"", "matches no release this catalog knows")]
    [InlineData("\"declaredVersion\": \"12.2.1.4\"", "\"declaredVersion\": \"6.0.8.28\"", "which this catalog reads as family '6i'")]
    public void A_declared_version_that_is_mistyped_or_uninterpretable_is_refused(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// An export that declared no version is written with no version and the unknown family, which is the
    /// one case where a module's family may differ from the release the run settled on. It carries no
    /// wrapper version either: the wrapper's attribute is one of the declarations that would have given the
    /// module a family.
    /// </summary>
    [Fact]
    public void An_export_that_declared_no_version_reads_as_the_unknown_family()
    {
        string json = Mutate("\"declaredVersion\": \"12.2.1.4\",", string.Empty, "\"wrapperDeclaredVersion\": \"12.2.1.4\",", string.Empty)
            .Replace("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"unknown\"", StringComparison.Ordinal);

        FormsIntermediateRead read = Read(json);

        Assert.Null(read.Error);
        Assert.Single(read.Modules!);
    }

    [Fact]
    public void A_null_declared_version_reads_as_no_version_rather_than_a_refusal()
    {
        FormsIntermediateRead read = Read(
            Mutate("\"declaredVersion\": \"12.2.1.4\"", "\"declaredVersion\": null", "\"wrapperDeclaredVersion\": \"12.2.1.4\",", string.Empty)
                .Replace("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"unknown\"", StringComparison.Ordinal));

        Assert.Null(read.Error);
        Assert.Single(read.Modules!);
    }

    /// <summary>
    /// A maximum length supplied as text, as a fraction, or beyond the 32-bit range used to read back as no
    /// limit at all, so the generated field silently accepted input the source module bounded.
    /// </summary>
    [Theory]
    [InlineData("\"maxLength\": \"12\"")]
    [InlineData("\"maxLength\": 12.5")]
    [InlineData("\"maxLength\": -1")]
    [InlineData("\"maxLength\": 99999999999")]
    [InlineData("\"maxLength\": true")]
    [InlineData("\"maxLength\": [12]")]
    public void A_malformed_maximum_length_is_refused_rather_than_read_as_no_limit(string replacement)
    {
        FormsIntermediateRead read = Read(Mutate("\"maxLength\": 12", replacement));

        Assert.Null(read.Modules);
        Assert.Contains("'maxLength'", read.Error!, StringComparison.Ordinal);
        Assert.Contains("non-negative whole number", read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"maxLength\": 12,", "", "{\"name\": \"MaximumLength\", \"namespace\": \"\", \"value\": \"12\"},", "")]
    [InlineData("\"maxLength\": 12", "\"maxLength\": null", "{\"name\": \"MaximumLength\", \"namespace\": \"\", \"value\": \"12\"},", "")]
    [InlineData("\"maxLength\": 12", "\"maxLength\": 0", "\"value\": \"12\"", "\"value\": \"0\"")]
    public void An_absent_null_or_zero_maximum_length_is_read_rather_than_refused(string find, string replace, string factFind, string factReplace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace, factFind, factReplace));

        Assert.Null(read.Error);
        Assert.Single(read.Modules!);
    }

    /// <summary>
    /// A wrongly typed optional string used to read back as null, so a prompt, a column binding, or a base
    /// table supplied as a number disappeared from the generated artifact without a word.
    /// </summary>
    [Theory]
    [InlineData("\"title\": \"Order entry\"", "\"title\": true")]
    [InlineData("\"title\": \"Order entry\"", "\"title\": 7")]
    [InlineData("\"baseTable\": \"BANK_ACCOUNT\"", "\"baseTable\": []")]
    [InlineData("\"baseTable\": \"BANK_ACCOUNT\"", "\"baseTable\": 7")]
    [InlineData("\"dataType\": \"Number\"", "\"dataType\": 1")]
    [InlineData("\"columnName\": \"ACCOUNT_ID\"", "\"columnName\": {}")]
    [InlineData("\"prompt\": \"Account\"", "\"prompt\": 7")]
    public void A_wrongly_typed_optional_string_is_refused_rather_than_dropped(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains("is present and is not a string", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A null optional string reads as absent, which is only a valid document when the retained element
    /// declared no such attribute either. Each case drops the attribute rather than renaming the field it
    /// would otherwise contradict.
    /// </summary>
    [Theory]
    [InlineData("\"title\": \"Order entry\"", "\"title\": null", "{\"name\": \"Title\", \"namespace\": \"\", \"value\": \"Order entry\"}", "{\"name\": \"WindowStyle\", \"namespace\": \"\", \"value\": \"Document\"}")]
    [InlineData("\"prompt\": \"Account\"", "\"prompt\": null", "{\"name\": \"Prompt\", \"namespace\": \"\", \"value\": \"Account:\"},", "")]
    public void A_null_optional_string_is_read_as_absent(string find, string replace, string factFind, string factReplace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace, factFind, factReplace));

        Assert.Null(read.Error);
        Assert.Single(read.Modules!);
    }

    [Theory]
    [InlineData("\"required\": true", "\"required\": \"true\"", "is not a boolean")]
    [InlineData("\"required\": true", "\"required\": 1", "is not a boolean")]
    [InlineData("\"visible\": true", "\"visible\": null", "is not a boolean")]
    [InlineData("\"recordsDisplayed\": 10", "\"recordsDisplayed\": \"10\"", "is not a non-negative whole number")]
    [InlineData("\"recordsDisplayed\": 10", "\"recordsDisplayed\": 10.5", "is not a non-negative whole number")]
    [InlineData("\"recordsDisplayed\": 10", "\"recordsDisplayed\": -1", "is not a non-negative whole number")]
    [InlineData("\"recordsDisplayed\": 10", "\"recordsDisplayed\": null", "is not a non-negative whole number")]
    public void A_wrongly_typed_boolean_or_count_is_refused(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"triggers\": [{\"name\": \"WHEN-NEW-FORM-INSTANCE\",\"scope\": \"ORDER_ENTRY\",\"body\": \"BEGIN EXECUTE_QUERY; END;\",\"bodyEncoding\": \"Attribute\"}]", "\"triggers\": \"WHEN-NEW-FORM-INSTANCE\"", "is not an array")]
    [InlineData("\"triggers\": [{\"name\": \"WHEN-NEW-FORM-INSTANCE\",\"scope\": \"ORDER_ENTRY\",\"body\": \"BEGIN EXECUTE_QUERY; END;\",\"bodyEncoding\": \"Attribute\"}]", "\"triggers\": [7]", "not an object with a non-empty 'name'")]
    [InlineData("\"triggers\": [{\"name\": \"WHEN-NEW-FORM-INSTANCE\",\"scope\": \"ORDER_ENTRY\",\"body\": \"BEGIN EXECUTE_QUERY; END;\",\"bodyEncoding\": \"Attribute\"}]", "\"triggers\": [{\"name\": \"\",\"scope\": \"ORDER_ENTRY\",\"body\": null}]", "not an object with a non-empty 'name'")]
    [InlineData("\"triggers\": [{\"name\": \"WHEN-VALIDATE-ITEM\",\"scope\": \"ORDER_BLOCK.ACCOUNT_ID\",\"body\": \"BEGIN VALIDATE_ITEM; END;\",\"bodyEncoding\": \"Element\"}]", "\"triggers\": [[\"WHEN-VALIDATE-ITEM\"]]", "not an object with a non-empty 'name'")]
    [InlineData("\"body\": \"BEGIN EXECUTE_QUERY; END;\"", "\"body\": 7", "is present and is not a string")]
    [InlineData("\"body\": \"BEGIN EXECUTE_QUERY; END;\"", "\"body\": \"   \"", "is blank")]
    [InlineData("\"scope\": \"ORDER_ENTRY\"", "\"scope\": \"OTHER_MODULE\"", "outside the containing module or block")]
    [InlineData("\"scope\": \"ORDER_BLOCK.ACCOUNT_ID\"", "\"scope\": \"ORDER_BLOCK.NOT_AN_ITEM\"", "outside the containing module or block")]
    [InlineData("\"bodyEncoding\": \"Attribute\"", "\"bodyEncoding\": \"Unknown\"", "no recognized 'bodyEncoding'")]
    [InlineData("\"bodyEncoding\": \"Attribute\"", "\"bodyEncoding\": \"0\"", "no recognized 'bodyEncoding'")]
    [InlineData("\"bodyEncoding\": \"Attribute\"", "\"bodyEncoding\": \"1\"", "no recognized 'bodyEncoding'")]
    [InlineData("\"bodyEncoding\": \"Attribute\"", "\"bodyEncoding\": \"999\"", "no recognized 'bodyEncoding'")]
    [InlineData("\"bodyEncoding\": \"Attribute\"", "\"bodyEncoding\": \"-1\"", "no recognized 'bodyEncoding'")]
    [InlineData("\"bodyEncoding\": \"Attribute\"", "\"bodyEncoding\": \"attribute\"", "no recognized 'bodyEncoding'")]
    [InlineData("\"bodyEncoding\": \"Attribute\"", "\"bodyEncoding\": \"ELEMENT\"", "no recognized 'bodyEncoding'")]
    [InlineData("\"programUnits\": []", "\"programUnits\": {}", "is not an array")]
    [InlineData("\"lovs\": []", "\"lovs\": [{ \"name\": \"LOV\" }]", "not a non-empty string")]
    public void A_malformed_string_array_is_refused_rather_than_partly_read(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_oversized_trigger_body_is_refused_rather_than_truncated()
    {
        string oversized = new('X', 200_001);
        FormsIntermediateRead read = Read(Mutate(
            "\"body\": \"BEGIN EXECUTE_QUERY; END;\"",
            $"\"body\": \"{oversized}\""));

        Assert.Null(read.Modules);
        Assert.Contains("200001 characters", read.Error!, StringComparison.Ordinal);
        Assert.Contains("reads at most 200000", read.Error!, StringComparison.Ordinal);
        Assert.Contains("refused rather than truncated", read.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_retained_source_facts_are_read_back_in_full()
    {
        FormsModule module = Assert.Single(Read(ValidIr).Modules!);
        FormsSourceFactSet facts = module.SourceFacts!;

        Assert.Equal("12.2.1.4", facts.WrapperDeclaredVersion);
        Assert.Equal(6, facts.Facts.Count);
        Assert.Equal([0, 1, 2, 3, 4, 5], facts.Facts.Select(fact => fact.Order));
        Assert.All(facts.Facts, fact => Assert.Equal(FormsSourceFactKind.Declared, fact.Kind));

        Assert.Equal("BEGIN VALIDATE_ITEM; END;", facts.Facts[5].Text);

        FormsSourceFact item = facts.Facts[3];
        Assert.Equal("Item", item.LocalName);
        Assert.Equal("ACCOUNT_ID", item.DeclaredName);
        Assert.Equal("999G999", Assert.Single(item.Attributes, attribute => attribute.Name == "FormatMask").Value);
        Assert.Equal("{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]", item.ParentId);
    }

    /// <summary>
    /// The wrapper version is retained verbatim and this reader adjudicates nothing from it. What it checks
    /// is that the string agrees with the release decision the same document records: normalization reads
    /// every version attribute a supplied export declares, the Module wrapper's among them, and refuses the
    /// estate when one is uninterpretable, names a second family, or names a second release. The Oracle
    /// internal build number below is the case that mattered — normalization refuses an estate declaring
    /// it, while this reader accepted it beside a module attributed to Forms 12c.
    /// </summary>
    [Theory]
    [InlineData("\"wrapperDeclaredVersion\": \"122010400\"", "matches no Oracle Forms release this catalog knows")]
    [InlineData("\"wrapperDeclaredVersion\": \"banana\"", "matches no Oracle Forms release this catalog knows")]
    [InlineData("\"wrapperDeclaredVersion\": \"\"", "empty 'wrapperDeclaredVersion'")]
    [InlineData("\"wrapperDeclaredVersion\": \"   \"", "empty 'wrapperDeclaredVersion'")]
    [InlineData("\"wrapperDeclaredVersion\": 122010400", "present and is not a string")]
    [InlineData("\"wrapperDeclaredVersion\": \"6i\"", "reads as Oracle Forms family '6i'")]
    [InlineData("\"wrapperDeclaredVersion\": \"12.2.1.5\"", "rather than one release stated at different precision")]
    public void A_wrapper_version_the_normalization_phase_would_have_refused_is_refused(string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate("\"wrapperDeclaredVersion\": \"12.2.1.4\"", replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The accepted cases, so the check above cannot pass by refusing everything: the same release the
    /// export declared, stated less precisely on the wrapper than in the module beside it.
    /// </summary>
    [Theory]
    [InlineData("12.2")]
    [InlineData("12c")]
    public void A_wrapper_version_the_normalization_phase_would_have_written_is_read_back_verbatim(string retained)
    {
        FormsIntermediateRead read = Read(Mutate("\"wrapperDeclaredVersion\": \"12.2.1.4\"", $"\"wrapperDeclaredVersion\": \"{retained}\""));

        Assert.Null(read.Error);
        Assert.Equal(retained, Assert.Single(read.Modules!).SourceFacts!.WrapperDeclaredVersion);
    }

    /// <summary>
    /// An export whose root is the FormModule itself carries no wrapper, so it declares no wrapper version
    /// and the producer retains none. That absence is read back as an absence, not as a gap in the record.
    /// </summary>
    [Fact]
    public void A_module_retaining_no_wrapper_version_is_read_as_having_declared_none()
    {
        FormsIntermediateRead read = Read(Mutate("\"wrapperDeclaredVersion\": \"12.2.1.4\",", string.Empty));

        Assert.Null(read.Error);
        Assert.Null(Assert.Single(read.Modules!).SourceFacts!.WrapperDeclaredVersion);
    }

    /// <summary>
    /// The wrapper's version is one of the declarations that decides the release, so a module recording
    /// that its export declared none cannot sit beside a wrapper that named one.
    /// </summary>
    [Fact]
    public void A_wrapper_version_beside_a_module_declaring_no_release_is_refused()
    {
        FormsIntermediateRead read = Read(Mutate(
            "\"declaredVersion\": \"12.2.1.4\",",
            string.Empty,
            "\"declaredFamily\": \"12c\"",
            "\"declaredFamily\": \"unknown\""));

        Assert.Null(read.Modules);
        Assert.Contains("declared no release at all", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A retained element's direct text is rebuilt before its children, so a set declaring both describes a
    /// tree in an order the retaining side refuses to write. Indentation an export preserves is not that:
    /// moving whitespace changes no declared content, and refusing it would reject exports this fleet reads.
    /// </summary>
    [Theory]
    [InlineData("\"text\": \"A\",", false)]
    [InlineData("\"text\": \"   \",", true)]
    public void Text_retained_beside_child_elements_is_refused_and_preserved_indentation_is_not(string inserted, bool accepted)
    {
        FormsIntermediateRead read = Read(Mutate("\"declaredName\": \"ORDER_BLOCK\",", $"\"declaredName\": \"ORDER_BLOCK\", {inserted}"));

        if (accepted)
        {
            Assert.Null(read.Error);
            Assert.Equal("   ", Assert.Single(read.Modules!).SourceFacts!.Facts[2].Text);
            return;
        }

        Assert.Null(read.Modules);
        Assert.Contains("retains text of its own beside it", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The retained facts are the only record of what the export declared beyond the structure this build
    /// interprets, so a set that is absent, damaged, reordered, or reattributed is refused rather than read
    /// short: a shorter inventory reads back as an export that genuinely declared less.
    /// </summary>
    [Theory]
    [InlineData("\"sourceFacts\": {", "\"otherFacts\": {", "has no 'sourceFacts' object")]
    [InlineData("\"kind\": \"Declared\"", "\"kind\": \"Inferred\"", "fact kind other than 'Declared'")]
    [InlineData("\"kind\": \"Declared\"", "\"kind\": \"declared\"", "fact kind other than 'Declared'")]
    [InlineData("\"order\": 2,", "\"order\": 5,", "declares order 5 at position 2")]
    [InlineData("\"textDigest\": \"3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b\"", "\"textDigest\": \"3A7BD3E2360A3D29EEA436FCFB7E44C735D117C42D1C1835420B6B9942DD4F1B\"", "lowercase hexadecimal SHA-256 form")]
    [InlineData("\"textDigest\": \"3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b\"", "\"textDigest\": \"3a7bd3e2\"", "lowercase hexadecimal SHA-256 form")]
    [InlineData("\"parentId\": \"{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]\",", "", "declares no 'parentId'")]
    [InlineData("\"parentId\": \"{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]\"", "\"parentId\": \"{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[9]\"", "is not a fact declared before it")]
    [InlineData("\"parentId\": \"{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]\"", "\"parentId\": \"{http://xmlns.oracle.com/Forms}FormModule[1]\"", "A source-object path is its parent's plus one step")]
    [InlineData("\"localName\": \"FormModule\"", "\"localName\": \"Module\"", "ends its path with")]
    [InlineData("\"declaredName\": \"ORDER_ENTRY\"", "\"declaredName\": \"OTHER_MODULE\"", "retained attributes declare 'ORDER_ENTRY'")]
    [InlineData("{\"name\": \"Title\", \"namespace\": \"\", \"value\": \"Order entry\"}", "{\"name\": \"Title\", \"namespace\": \"\"}", "has no 'value' string")]
    [InlineData("{\"name\": \"Title\", \"namespace\": \"\", \"value\": \"Order entry\"}", "{\"name\": \"Title\", \"value\": \"Order entry\"}", "has no 'namespace' string")]
    [InlineData("{\"name\": \"Title\", \"namespace\": \"\", \"value\": \"Order entry\"}", "{\"name\": \"Name\", \"namespace\": \"\", \"value\": \"Order entry\"}", "more than one attribute")]
    [InlineData("\"namespace\": \"http://xmlns.oracle.com/Forms\",", "", "has no 'namespace' string")]
    [InlineData("\"facts\": [", "\"facts\": [], \"unusedFacts\": [", "declares no facts")]
    public void A_tampered_source_fact_set_is_refused(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path that agrees with nothing lets a retained element be cited under a name, a namespace, or a
    /// sibling position it never had, which is the one thing every later citation depends on.
    /// </summary>
    [Theory]
    [InlineData("\"localName\": \"Item\"", "\"localName\": \"Widget\"", "ends its path with")]
    [InlineData(ItemId, $"{BlockId}/{{urn:evil}}Item[1]", "ends its path with")]
    [InlineData(ItemId, $"{BlockId}/{Forms}Item[0]", "ends its path with")]
    [InlineData(ItemId, $"{BlockId}/{Forms}Item[01]", "ends its path with")]
    [InlineData("FormModule[1]", "FormModule[0]", "ends its path with")]
    [InlineData("\"childIndex\": 1,", "\"childIndex\": 2,", "while it is child 1")]
    [InlineData("\"childIndex\": 0,", "\"childIndex\": 1,", "while it is child 0")]
    [InlineData("\"declaredName\": \"ACCOUNT_ID\"", "\"declaredName\": \"SOMETHING_ELSE\"", "retained attributes declare 'ACCOUNT_ID'")]
    public void A_source_fact_whose_path_or_position_disagrees_with_itself_is_refused(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fact is not an identity until it has been checked. Recording it first let an entry name itself as
    /// its own parent and satisfy the lookup it was supposed to fail.
    /// </summary>
    [Fact]
    public void A_source_fact_that_names_itself_as_its_own_parent_is_refused()
    {
        FormsIntermediateRead read = Read(Mutate($"\"parentId\": \"{BlockId}\"", $"\"parentId\": \"{ItemId}\""));

        Assert.Null(read.Modules);
        Assert.Contains("is not a fact declared before it", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The item moves from the block that is still open above it to the trigger the producer had already
    /// finished. Every other relationship still checks out, so only the depth-first order catches it.
    /// </summary>
    [Fact]
    public void A_source_fact_reattached_to_a_closed_branch_is_refused()
    {
        string reattached = Mutate($"\"id\": \"{ItemId}\"", $"\"id\": \"{TriggerId}/{Forms}Item[1]\"")
            .Replace($"\"parentId\": \"{BlockId}\"", $"\"parentId\": \"{TriggerId}\"", StringComparison.Ordinal);

        FormsIntermediateRead read = Read(reattached);

        Assert.Null(read.Modules);
        Assert.Contains("had already closed", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The module element and the module it is filed under have to name the same module. The tamper is
    /// self-consistent — declared name and Name attribute agree — so only this check refuses it.
    /// </summary>
    [Fact]
    public void A_module_element_retained_for_another_module_is_refused()
    {
        string reattributed = Mutate("\"declaredName\": \"ORDER_ENTRY\"", "\"declaredName\": \"OTHER_MODULE\"")
            .Replace(
                "{\"name\": \"Name\", \"namespace\": \"\", \"value\": \"ORDER_ENTRY\"}",
                "{\"name\": \"Name\", \"namespace\": \"\", \"value\": \"OTHER_MODULE\"}",
                StringComparison.Ordinal);

        FormsIntermediateRead read = Read(reattributed);

        Assert.Null(read.Modules);
        Assert.Contains("disagree about which module was read", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The interpreted structure and the retained facts were read independently and compared only by
    /// module name, so a document could declare a base table, a prompt, a flag, or a trigger body that no
    /// retained element supports and the converter generated from the declaration. Each case here moves the
    /// interpreted structure alone; the facts stay exactly as the retaining side wrote them.
    /// </summary>
    [Theory]
    [InlineData("\"title\": \"Order entry\"", "\"title\": \"Something else\"", "title")]
    [InlineData("\"title\": \"Order entry\"", "\"title\": null", "title")]
    [InlineData("\"baseTable\": \"BANK_ACCOUNT\"", "\"baseTable\": \"SALARY\"", "base table")]
    [InlineData("\"baseTable\": \"BANK_ACCOUNT\"", "\"baseTable\": null", "base table")]
    [InlineData("\"recordsDisplayed\": 10", "\"recordsDisplayed\": 1", "displayed record count")]
    [InlineData("\"prompt\": \"Account\"", "\"prompt\": \"Sort code\"", "declared properties")]
    [InlineData("\"prompt\": \"Account\"", "\"prompt\": null", "declared properties")]
    [InlineData("\"dataType\": \"Number\"", "\"dataType\": \"Char\"", "declared properties")]
    [InlineData("\"columnName\": \"ACCOUNT_ID\"", "\"columnName\": \"SORT_CODE\"", "declared properties")]
    [InlineData("\"itemType\": \"Text Item\"", "\"itemType\": \"Display Item\"", "declared properties")]
    [InlineData("\"required\": true", "\"required\": false", "declared properties")]
    [InlineData("\"visible\": true", "\"visible\": false", "declared properties")]
    [InlineData("\"maxLength\": 12", "\"maxLength\": 4", "declared properties")]
    [InlineData("\"body\": \"BEGIN EXECUTE_QUERY; END;\"", "\"body\": \"BEGIN DELETE_RECORD; END;\"", "identity, scope, body, or body encoding")]
    [InlineData("\"body\": \"BEGIN VALIDATE_ITEM; END;\"", "\"body\": \"BEGIN RAISE FORM_TRIGGER_FAILURE; END;\"", "identity, scope, body, or body encoding")]
    [InlineData("\"bodyEncoding\": \"Attribute\"", "\"bodyEncoding\": \"Element\"", "identity, scope, body, or body encoding")]
    [InlineData("\"programUnits\": []", "\"programUnits\": [\"RECALCULATE_TOTALS\"]", "program unit count")]
    [InlineData("\"lovs\": []", "\"lovs\": [\"BIN_LOV\"]", "LOV count")]
    public void An_interpreted_structure_the_retained_facts_do_not_support_is_refused(string find, string replace, string field)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains($"The interpreted {field}", read.Error!, StringComparison.Ordinal);
        Assert.Contains("the source facts retained beside it describe", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same reconciliation from the other side. Each case moves a retained element and leaves the
    /// interpreted structure exactly as the producer wrote it, so a tampered inventory cannot describe an
    /// export the projection beside it never came from. The text digest names which export text the facts
    /// were read from; it is never evidence that anything beside them agrees with it.
    /// </summary>
    [Theory]
    [InlineData("{\"name\": \"Title\", \"namespace\": \"\", \"value\": \"Order entry\"}", "{\"name\": \"Title\", \"namespace\": \"\", \"value\": \"Something else\"}", "title")]
    [InlineData("{\"name\": \"QueryDataSourceName\", \"namespace\": \"\", \"value\": \"BANK_ACCOUNT\"}", "{\"name\": \"QueryDataSourceName\", \"namespace\": \"\", \"value\": \"SALARY\"}", "base table")]
    [InlineData("{\"name\": \"RecordsDisplayCount\", \"namespace\": \"\", \"value\": \"10\"}", "{\"name\": \"RecordsDisplayCount\", \"namespace\": \"\", \"value\": \"1\"}", "displayed record count")]
    [InlineData("{\"name\": \"Prompt\", \"namespace\": \"\", \"value\": \"Account:\"}", "{\"name\": \"Prompt\", \"namespace\": \"\", \"value\": \"Sort code:\"}", "declared properties")]
    [InlineData("{\"name\": \"Required\", \"namespace\": \"\", \"value\": \"Yes\"}", "{\"name\": \"Required\", \"namespace\": \"\", \"value\": \"No\"}", "declared properties")]
    [InlineData("{\"name\": \"ItemType\", \"namespace\": \"\", \"value\": \"Text Item\"}", "{\"name\": \"ItemType\", \"namespace\": \"\", \"value\": \"Display Item\"}", "declared properties")]
    [InlineData("{\"name\": \"MaximumLength\", \"namespace\": \"\", \"value\": \"12\"}", "{\"name\": \"MaximumLength\", \"namespace\": \"\", \"value\": \"4\"}", "declared properties")]
    [InlineData("{\"name\": \"TriggerText\", \"namespace\": \"\", \"value\": \"BEGIN EXECUTE_QUERY; END;\"}", "{\"name\": \"TriggerText\", \"namespace\": \"\", \"value\": \"BEGIN DELETE_RECORD; END;\"}", "identity, scope, body, or body encoding")]
    [InlineData("\"text\": \"BEGIN VALIDATE_ITEM; END;\"", "\"text\": \"BEGIN RAISE FORM_TRIGGER_FAILURE; END;\"", "identity, scope, body, or body encoding")]
    public void Retained_facts_that_do_not_describe_the_interpreted_structure_are_refused(string find, string replace, string field)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains($"The interpreted {field}", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A trigger renamed in the retained facts keeps its declared name and its Name attribute in step, so
    /// every check inside the inventory still passes and only the projection beside it disagrees.
    /// </summary>
    [Fact]
    public void A_retained_trigger_renamed_consistently_within_the_facts_is_still_refused()
    {
        FormsIntermediateRead read = Read(Mutate(
            "\"declaredName\": \"WHEN-NEW-FORM-INSTANCE\"",
            "\"declaredName\": \"WHEN-NEW-BLOCK-INSTANCE\"",
            "{\"name\": \"Name\", \"namespace\": \"\", \"value\": \"WHEN-NEW-FORM-INSTANCE\"}",
            "{\"name\": \"Name\", \"namespace\": \"\", \"value\": \"WHEN-NEW-BLOCK-INSTANCE\"}"));

        Assert.Null(read.Modules);
        Assert.Contains("identity, scope, body, or body encoding", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The retaining side refuses an export nested deeper than it keeps, so a fact set declaring a deeper
    /// element was never written by it. The reader read one regardless, which accepted a tree the retaining
    /// side would have thrown out.
    /// </summary>
    [Fact]
    public void A_fact_set_nested_deeper_than_the_retaining_side_keeps_is_refused()
    {
        FormsIntermediateRead read = Read(DeepIr(FormsSourceFactReader.MaxDepth + 1));

        Assert.Null(read.Modules);
        Assert.Contains($"reads at most {FormsSourceFactReader.MaxDepth}", read.Error!, StringComparison.Ordinal);
        Assert.Contains("refused rather than read in part", read.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fact_set_nested_to_the_retained_depth_is_read()
    {
        FormsIntermediateRead read = Read(DeepIr(FormsSourceFactReader.MaxDepth));

        Assert.Null(read.Error);
        Assert.Equal(FormsSourceFactReader.MaxDepth, Assert.Single(read.Modules!).SourceFacts!.Facts.Count);
    }

    /// <summary>
    /// A representation whose only retained structure is a chain of elements this build does not interpret,
    /// nested <paramref name="depth"/> levels counting the module itself. Its projection is empty, which is
    /// what the module below declares, so only the depth decides whether it is read.
    /// </summary>
    private static string DeepIr(int depth)
    {
        string id = $"{Forms}FormModule[1]";
        string facts =
            $$"""
            {"id": "{{id}}","order": 0,"childIndex": 0,"localName": "FormModule","namespace": "http://xmlns.oracle.com/Forms","declaredName": "ORDER_ENTRY","attributes": [{"name": "Name", "namespace": "", "value": "ORDER_ENTRY"}],"kind": "Declared"}
            """;

        for (int level = 2; level <= depth; level++)
        {
            string parent = id;
            id = $"{parent}/{{}}a[1]";
            facts +=
                $$"""
                ,{"id": "{{id}}","order": {{level - 1}},"parentId": "{{parent}}","childIndex": 0,"localName": "a","namespace": "","attributes": [],"kind": "Declared"}
                """;
        }

        return $$"""
            {
              "generator": "oracle-forms-migration-fleet/source-normalization",
              "schemaVersion": "3",
              "normalized": true,
              "sourceRoot": "legacy/forms",
              "formsFamily": "12c",
              "versionAuthority": "declared by the supplied export",
              "modules": [
                {
                  "name": "ORDER_ENTRY",
                  "sourcePath": "legacy/forms/ui/ORDER_ENTRY.xml",
                  "declaredVersion": "12.2.1.4",
                  "declaredFamily": "12c",
                  "blocks": [],
                  "triggers": [],
                  "programUnits": [],
                  "lovs": [],
                  "sourceFacts": {
                    "textDigest": "0000000000000000000000000000000000000000000000000000000000000000",
                    "facts": [{{facts}}]
                  }
                }
              ],
              "notes": []
            }
            """;
    }

    /// <summary>
    /// The projection reports a retained tree it could not carry instead of throwing, and the reader used
    /// to discard that report. A tree carrying more blocks than this build retains projects no blocks at
    /// all, so a document declaring an empty structure beside it compared equal and was read as a module
    /// with nothing in it — the estate silently shrank to zero screens rather than being refused.
    /// </summary>
    [Fact]
    public void A_retained_tree_over_the_block_limit_is_refused_rather_than_read_as_an_empty_module()
    {
        FormsIntermediateRead read = Read(BlocksIr(FormsIntermediateReader.MaxChildren + 1));

        Assert.Null(read.Modules);
        Assert.Contains("would have refused to normalize", read.Error!, StringComparison.Ordinal);
        Assert.Contains("declares 20001 block entries", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same gap one level down, where the projection keeps the block and empties only its items, so the
    /// document that pairs with it is a whole screen carrying no fields.
    /// </summary>
    [Fact]
    public void A_retained_block_over_the_item_limit_is_refused_rather_than_read_as_an_empty_block()
    {
        FormsIntermediateRead read = Read(ItemsIr(FormsIntermediateReader.MaxChildren + 1));

        Assert.Null(read.Modules);
        Assert.Contains("would have refused to normalize", read.Error!, StringComparison.Ordinal);
        Assert.Contains("ORDER_ENTRY.ORDER_BLOCK — The export declares 20001 item entries", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The projection reports every untranslated trigger as unsupported too, and the representation retains
    /// those triggers on purpose. Only findings about the module's own structure may refuse a read, so the
    /// document that carries behaviour this fleet never translates is still read in full.
    /// </summary>
    [Fact]
    public void A_representation_retaining_untranslated_trigger_behaviour_is_still_read()
    {
        FormsIntermediateRead read = Read(ValidIr);

        Assert.Null(read.Error);
        FormsModule module = Assert.Single(read.Modules!);
        Assert.Equal("BEGIN EXECUTE_QUERY; END;", Assert.Single(module.Triggers).Body);
        Assert.Equal("BEGIN VALIDATE_ITEM; END;", Assert.Single(Assert.Single(module.Blocks).Triggers).Body);
    }

    /// <summary>
    /// A representation declaring no blocks whose retained facts carry <paramref name="blocks"/> of them,
    /// each one an element the projection would have to drop as a whole once the count passes what this
    /// build retains.
    /// </summary>
    private static string BlocksIr(int blocks)
    {
        string module =
            $$"""
            {"id": "{{ModuleId}}","order": 0,"childIndex": 0,"localName": "FormModule","namespace": "http://xmlns.oracle.com/Forms","declaredName": "ORDER_ENTRY","attributes": [{"name": "Name", "namespace": "", "value": "ORDER_ENTRY"}],"kind": "Declared"}
            """;

        string retained = string.Join(",", Enumerable.Range(1, blocks).Select(position =>
            $$"""
            {"id": "{{ModuleId}}/{{Forms}}Block[{{position}}]","order": {{position}},"parentId": "{{ModuleId}}","childIndex": {{position - 1}},"localName": "Block","namespace": "http://xmlns.oracle.com/Forms","attributes": [],"kind": "Declared"}
            """));

        return $$"""
            {
              "generator": "oracle-forms-migration-fleet/source-normalization",
              "schemaVersion": "3",
              "normalized": true,
              "sourceRoot": "legacy/forms",
              "formsFamily": "12c",
              "versionAuthority": "declared by the supplied export",
              "modules": [
                {
                  "name": "ORDER_ENTRY",
                  "sourcePath": "legacy/forms/ui/ORDER_ENTRY.xml",
                  "declaredVersion": "12.2.1.4",
                  "declaredFamily": "12c",
                  "blocks": [],
                  "triggers": [],
                  "programUnits": [],
                  "lovs": [],
                  "sourceFacts": {
                    "textDigest": "0000000000000000000000000000000000000000000000000000000000000000",
                    "facts": [{{module}},{{retained}}]
                  }
                }
              ],
              "notes": []
            }
            """;
    }

    /// <summary>
    /// A representation declaring one block with no items whose retained facts give that same block
    /// <paramref name="items"/> of them. Everything the block itself declares agrees with the projection,
    /// so only the emptied item list is left to catch.
    /// </summary>
    private static string ItemsIr(int items)
    {
        string module =
            $$"""
            {"id": "{{ModuleId}}","order": 0,"childIndex": 0,"localName": "FormModule","namespace": "http://xmlns.oracle.com/Forms","declaredName": "ORDER_ENTRY","attributes": [{"name": "Name", "namespace": "", "value": "ORDER_ENTRY"}],"kind": "Declared"}
            """;

        string block =
            $$"""
            {"id": "{{BlockId}}","order": 1,"parentId": "{{ModuleId}}","childIndex": 0,"localName": "Block","namespace": "http://xmlns.oracle.com/Forms","declaredName": "ORDER_BLOCK","attributes": [{"name": "Name", "namespace": "", "value": "ORDER_BLOCK"},{"name": "QueryDataSourceName", "namespace": "", "value": "BANK_ACCOUNT"}],"kind": "Declared"}
            """;

        string retained = string.Join(",", Enumerable.Range(1, items).Select(position =>
            $$"""
            {"id": "{{BlockId}}/{{Forms}}Item[{{position}}]","order": {{position + 1}},"parentId": "{{BlockId}}","childIndex": {{position - 1}},"localName": "Item","namespace": "http://xmlns.oracle.com/Forms","attributes": [],"kind": "Declared"}
            """));

        return $$"""
            {
              "generator": "oracle-forms-migration-fleet/source-normalization",
              "schemaVersion": "3",
              "normalized": true,
              "sourceRoot": "legacy/forms",
              "formsFamily": "12c",
              "versionAuthority": "declared by the supplied export",
              "modules": [
                {
                  "name": "ORDER_ENTRY",
                  "sourcePath": "legacy/forms/ui/ORDER_ENTRY.xml",
                  "declaredVersion": "12.2.1.4",
                  "declaredFamily": "12c",
                  "blocks": [
                    {
                      "name": "ORDER_BLOCK",
                      "baseTable": "BANK_ACCOUNT",
                      "recordsDisplayed": 1,
                      "items": [],
                      "triggers": []
                    }
                  ],
                  "triggers": [],
                  "programUnits": [],
                  "lovs": [],
                  "sourceFacts": {
                    "textDigest": "0000000000000000000000000000000000000000000000000000000000000000",
                    "facts": [{{module}},{{block}},{{retained}}]
                  }
                }
              ],
              "notes": []
            }
            """;
    }

    /// <summary>
    /// The module element roots its own id space, so the producer always writes it as the first sibling of
    /// its qualified name. A path rooted anywhere else used to be accepted on the grounds that it was still
    /// resolvable, which let a document carry an ordinal no retained tree contains.
    /// </summary>
    [Fact]
    public void A_module_element_rooted_at_another_sibling_index_is_refused()
    {
        FormsIntermediateRead read = Read(Mutate("FormModule[1]", "FormModule[2]"));

        Assert.Null(read.Modules);
        Assert.Contains("element 1 of that qualified name under the module root", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sibling ordinal is counted per parent and per qualified name by the producer. Every parent
    /// reference in these documents still resolves and every path is still its parent's plus one step, so
    /// only counting the siblings actually retained refuses them.
    /// </summary>
    [Theory]
    [InlineData("Block[1]", "Block[2]")]
    [InlineData("Item[1]", "Item[9]")]
    [InlineData("TriggerText[1]", "TriggerText[2]")]
    public void A_forged_sibling_ordinal_is_refused_even_where_every_reference_is_coherent(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains("element 1 of that qualified name under", read.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicated_source_fact_identity_is_refused()
    {
        FormsIntermediateRead read = Read(Mutate(
            "\"id\": \"{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]/{http://xmlns.oracle.com/Forms}Item[1]\"",
            "\"id\": \"{http://xmlns.oracle.com/Forms}FormModule[1]/{http://xmlns.oracle.com/Forms}Block[1]\""));

        Assert.Null(read.Modules);
        Assert.Contains("more than one source fact", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A duplicate key shows one value to a reviewer reading the document and is read as another, so a
    /// tampered copy could carry both the fact it claims and the fact it is read with.
    /// </summary>
    [Fact]
    public void A_duplicate_json_key_is_refused()
    {
        FormsIntermediateRead read = Read(Mutate(
            "\"declaredName\": \"ACCOUNT_ID\"",
            "\"declaredName\": \"ACCOUNT_ID\", \"declaredName\": \"SOMETHING_ELSE\""));

        Assert.Null(read.Modules);
        Assert.Contains("more than once in one object", read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    public void A_superseded_schema_version_is_refused_with_reimport_guidance(string superseded)
    {
        FormsIntermediateRead read = Read(Mutate("\"schemaVersion\": \"3\"", $"\"schemaVersion\": \"{superseded}\""));

        Assert.Null(read.Modules);
        Assert.Contains($"schema version '{superseded}'", read.Error!, StringComparison.Ordinal);
        Assert.Contains("Re-import source to regenerate this format.", read.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_trigger_identity_in_one_scope_is_refused()
    {
        const string Trigger = "{\"name\": \"WHEN-NEW-FORM-INSTANCE\",\"scope\": \"ORDER_ENTRY\",\"body\": \"BEGIN EXECUTE_QUERY; END;\",\"bodyEncoding\": \"Attribute\"}";
        FormsIntermediateRead read = Read(Mutate(
            $"\"triggers\": [{Trigger}]",
            $"\"triggers\": [{Trigger},{Trigger}]"));

        Assert.Null(read.Modules);
        Assert.Contains("more than one trigger", read.Error!, StringComparison.Ordinal);
        Assert.Contains("ORDER_ENTRY.WHEN-NEW-FORM-INSTANCE", read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"\"")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": 7")]
    [InlineData("\"versionAuthority\": \"declared by the supplied export\"", "\"versionAuthority\": \"\"")]
    [InlineData("\"versionAuthority\": \"declared by the supplied export\"", "\"versionAuthority\": false")]
    public void A_blank_or_mistyped_root_field_is_refused(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains("source root or version authority", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The source root was written by the producer and compared to nothing, so a representation left in a
    /// session workspace by an earlier run — or edited by hand — was read against whatever estate happened
    /// to be executing, and every screen generated from it was attributed to modules this run never
    /// normalized. It has to be the active root exactly.
    /// </summary>
    [Theory]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"legacy/other\"")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"legacy\"")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"legacy/forms/ui\"")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"LEGACY/FORMS\"")]
    public void A_representation_recording_another_source_root_is_refused(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains("while this run is executing against 'legacy/forms'", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>A source root that is not a workspace path at all is refused before any module is read.</summary>
    [Theory]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"../../etc\"", "must not traverse outside the workspace")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"C:/legacy/forms\"", "must be workspace-relative")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"/legacy/forms\"", "must be workspace-relative")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"https://contoso/forms\"", "must be workspace-relative")]
    public void A_source_root_that_is_not_a_workspace_path_is_refused(string find, string replace, string expected)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains("The 'sourceRoot' of the normalized Forms representation", read.Error!, StringComparison.Ordinal);
        Assert.Contains(expected, read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A module path inside the workspace but outside the root this run normalized belongs to a different
    /// estate. The containment check is on whole segments, so a sibling directory whose name merely begins
    /// with the root's does not pass as one beneath it.
    /// </summary>
    [Theory]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/other/ORDER_ENTRY.xml\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/forms-b/ORDER_ENTRY.xml\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/formsX/ORDER_ENTRY.xml\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"ORDER_ENTRY.xml\"")]
    public void A_module_read_from_outside_the_source_root_is_refused(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains("is not under the source root 'legacy/forms'", read.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/forms/ui/OTHER.xml\"")]
    [InlineData("\"name\": \"ORDER_ENTRY\"", "\"name\": \"OTHER\"")]
    public void A_module_whose_file_and_embedded_identity_disagree_is_refused(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Modules);
        Assert.Contains("file name does not identify that module", read.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_load_order_prefix_uses_the_same_normalized_identity_as_the_producer()
    {
        FormsIntermediateRead read = Read(Mutate(
            "\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"",
            "\"sourcePath\": \"legacy/forms/ui/005-order.entry.xml\""));

        Assert.Null(read.Error);
        Assert.Single(read.Modules!);
    }

    /// <summary>The root itself, and any path beneath it, are the paths a module may be attributed to.</summary>
    [Theory]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/forms/ORDER_ENTRY.xml\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/forms/a/b/c/ORDER_ENTRY.xml\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy\\\\forms\\\\ui\\\\ORDER_ENTRY.xml\"")]
    public void A_module_read_from_under_the_source_root_is_accepted(string find, string replace)
    {
        FormsIntermediateRead read = Read(Mutate(find, replace));

        Assert.Null(read.Error);
        Assert.Single(read.Modules!);
    }

    /// <summary>
    /// Two directories carrying one module name carry two modules. Normalization accepts that estate when
    /// each directory supplies its own export, so refusing it here on the bare name would reject a
    /// representation this fleet had just written.
    /// </summary>
    [Fact]
    public void Two_modules_of_one_name_from_separate_directories_are_both_read()
    {
        FormsIntermediateRead read = Read(TwoDirectories);

        Assert.Null(read.Error);
        Assert.Equal(2, read.Modules!.Count);
        Assert.All(read.Modules!, module => Assert.Equal("ORDERS", module.Name));
        Assert.Equal(
            ["legacy/forms/forms-a/ORDERS", "legacy/forms/forms-b/ORDERS"],
            read.Modules!.Select(module => module.QualifiedName).Order(StringComparer.Ordinal));
    }

    /// <summary>Directory scoping must not loosen identity inside one directory.</summary>
    [Fact]
    public void Two_modules_of_one_name_from_one_directory_are_refused()
    {
        FormsIntermediateRead read = Read(TwoDirectories.Replace(
            "\"sourcePath\": \"legacy/forms/forms-b/ORDERS.xml\"",
            "\"sourcePath\": \"legacy/forms/forms-a/ORDERS_COPY.xml\"",
            StringComparison.Ordinal));

        Assert.Null(read.Modules);
        Assert.Contains("legacy/forms/forms-a/ORDERS", read.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal has to reach the caller: a damaged representation that still generated an application
    /// would present CRUD over the converted tables as a migration of modules nothing read.
    /// </summary>
    [Theory]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"../../etc/passwd\"")]
    [InlineData("\"declaredFamily\": \"12c\",", "")]
    [InlineData("\"declaredFamily\": \"12c\"", "\"declaredFamily\": \"6i\"")]
    [InlineData("\"maxLength\": 12", "\"maxLength\": \"12\"")]
    [InlineData("\"prompt\": \"Account\"", "\"prompt\": 7")]
    [InlineData("\"triggers\": [{\"name\": \"WHEN-NEW-FORM-INSTANCE\",\"scope\": \"ORDER_ENTRY\",\"body\": \"BEGIN EXECUTE_QUERY; END;\",\"bodyEncoding\": \"Attribute\"}]", "\"triggers\": [7]")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"legacy/other\"")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"legacy\"")]
    [InlineData("\"sourceRoot\": \"legacy/forms\"", "\"sourceRoot\": \"/legacy/forms\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/forms-b/ORDER_ENTRY.xml\"")]
    [InlineData("\"sourcePath\": \"legacy/forms/ui/ORDER_ENTRY.xml\"", "\"sourcePath\": \"legacy/other/ORDER_ENTRY.xml\"")]
    public async Task Application_conversion_refuses_a_damaged_representation_and_writes_no_application_file(string find, string replace)
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", OracleSamples.FormsXml("12.2.1.4"));
        workspace.WriteFile("out/orders/intermediate/forms-ir.json", Mutate(find, replace));

        PhaseExecutionResult result = await ConvertAsync(workspace);

        Assert.False(result.Succeeded);
        Assert.Contains("was refused", result.FailureReason!, StringComparison.Ordinal);
        Assert.Empty(ApplicationFiles(workspace));
    }

    [Fact]
    public async Task Application_conversion_reads_the_representation_this_fleet_writes()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile("legacy/forms/ui/ORDER_ENTRY.xml", OracleSamples.FormsXml("12.2.1.4"));
        workspace.WriteFile("out/orders/intermediate/forms-ir.json", ValidIr);

        PhaseExecutionResult result = await ConvertAsync(workspace);

        Assert.True(result.Succeeded);
        Assert.NotEmpty(ApplicationFiles(workspace));
    }

    private static IReadOnlyList<string> ApplicationFiles(TemporaryWorkspace workspace)
    {
        string directory = workspace.Absolute("out/orders/application");

        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            : [];
    }

    private static Task<PhaseExecutionResult> ConvertAsync(TemporaryWorkspace workspace)
    {
        MigrationRunRequest request = new()
        {
            EngagementId = "ENG-IR",
            ApplicationName = "ORDERS",
            RequestedMode = ExecutionMode.GenerateArtifacts,
            Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
            OracleFormsVersion = "12c",
            SourceRoot = "legacy/forms",
            OutputRoot = "out/orders",
            Evidence =
            [
                Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
                Requests.Evidence("EV-SRC", EvidenceKind.FormsModuleSource),
                Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
                Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
                Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
            ],
            PlanApproval = Requests.Approved("plan-owner@contoso.com"),
        };

        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases
            .Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);

        return new ApplicationCodeConversionAdapter().ExecuteAsync(
            new PhaseExecutionContext(workspace.Root, request.SourceRoot, request.OutputRoot, plan, request, (_, _) => { })
            {
                CompletedPhases =
                [
                    new PhaseOutcome(MigrationPhase.SourceNormalization, PhaseStatus.Planned, PhaseExecutionState.Executed, [], [], null),
                ],
            },
            CancellationToken.None);
    }
}
