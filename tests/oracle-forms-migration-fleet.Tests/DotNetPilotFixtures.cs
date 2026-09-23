using System.Text.Json;
using System.Text.Json.Nodes;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// Two synthetic estates, authored independently of each other and of the generator, plus a variant of
/// the first that binds only what the mapping requires.
///
/// The first two share no identifier, no word stem, no precision, and no column order. That is the point:
/// the generator is supposed to reach the same shape from either, because it reads the roles the manifest
/// binds and never the names. Both are clean-room inventions; neither is derived from any customer,
/// sample, or upstream repository schema.
/// </summary>
internal static class DotNetPilotFixtures
{
    internal const string MeridianLabel = "synthetic-fixture-meridian-orders";

    internal const string KestrelLabel = "synthetic-fixture-kestrel-dispatch";

    internal const string MeridianUnboundHeaderLabel = "synthetic-fixture-meridian-orders-unbound-header";

    /// <summary>
    /// Fixture one. Short cryptic names in the style of a 1990s Forms estate, NUMBER(n) identifiers,
    /// two-place money, single-character domains, and a version column on both the master and the article.
    /// </summary>
    internal const string MeridianSchema = """
        CREATE TABLE MRD_CUSTOMER (
          CUST_NO      NUMBER(8)     NOT NULL,
          CUST_NAME    VARCHAR2(80)  NOT NULL,
          CUST_STATE   CHAR(1)       NOT NULL,
          CUST_MEMO    VARCHAR2(200),
          CONSTRAINT PK_MRD_CUSTOMER PRIMARY KEY (CUST_NO),
          CONSTRAINT CK_MRD_CUST_STATE CHECK (CUST_STATE IN ('A','I'))
        );

        CREATE TABLE MRD_ARTICLE (
          ART_NO       NUMBER(8)     NOT NULL,
          ART_DESC     VARCHAR2(80)  NOT NULL,
          ART_PRICE    NUMBER(11,2)  NOT NULL,
          ART_ON_HAND  NUMBER(9)     NOT NULL,
          ART_STATE    CHAR(1)       NOT NULL,
          ART_REV      NUMBER(12)    NOT NULL,
          CONSTRAINT PK_MRD_ARTICLE PRIMARY KEY (ART_NO),
          CONSTRAINT CK_MRD_ART_STATE CHECK (ART_STATE IN ('A','I'))
        );

        CREATE TABLE MRD_ORDER_HEAD (
          ORD_NO       NUMBER(10)    NOT NULL,
          ORD_CUST     NUMBER(8)     NOT NULL,
          ORD_VALUE    NUMBER(14,2)  NOT NULL,
          ORD_RAISED   DATE          NOT NULL,
          ORD_STATE    VARCHAR2(10)  NOT NULL,
          ORD_REV      NUMBER(12)    NOT NULL,
          CONSTRAINT PK_MRD_ORDER_HEAD PRIMARY KEY (ORD_NO),
          CONSTRAINT FK_MRD_ORDER_CUST FOREIGN KEY (ORD_CUST) REFERENCES MRD_CUSTOMER (CUST_NO),
          CONSTRAINT CK_MRD_ORD_STATE CHECK (ORD_STATE IN ('ENTERED','SHIPPED'))
        );

        CREATE TABLE MRD_ORDER_ITEM (
          ITM_NO       NUMBER(12)    NOT NULL,
          ITM_ORD      NUMBER(10)    NOT NULL,
          ITM_ART      NUMBER(8)     NOT NULL,
          ITM_QTY      NUMBER(9)     NOT NULL,
          ITM_PRICE    NUMBER(11,2)  NOT NULL,
          ITM_VALUE    NUMBER(14,2)  NOT NULL,
          CONSTRAINT PK_MRD_ORDER_ITEM PRIMARY KEY (ITM_NO),
          CONSTRAINT FK_MRD_ITEM_ORD FOREIGN KEY (ITM_ORD) REFERENCES MRD_ORDER_HEAD (ORD_NO),
          CONSTRAINT FK_MRD_ITEM_ART FOREIGN KEY (ITM_ART) REFERENCES MRD_ARTICLE (ART_NO)
        );
        """;

    internal const string MeridianManifest = """
        {
          "schemaVersion": "fleet.target-mapping/1",
          "generator": "dotnet-master-detail/1",
          "application": "Meridian Order Entry",
          "fixtureLabel": "synthetic-fixture-meridian-orders",
          "sources": [
            { "id": "sch-cust", "kind": "OracleSchemaObject", "path": "TABLE:MRD_CUSTOMER" },
            { "id": "sch-art",  "kind": "OracleSchemaObject", "path": "TABLE:MRD_ARTICLE" },
            { "id": "sch-head", "kind": "OracleSchemaObject", "path": "TABLE:MRD_ORDER_HEAD" },
            { "id": "sch-item", "kind": "OracleSchemaObject", "path": "TABLE:MRD_ORDER_ITEM" }
          ],
          "objects": [
            {
              "role": "PartyLookup",
              "table": "MRD_CUSTOMER",
              "sourceRefs": ["sch-cust"],
              "constants": { "ActiveFlag": "A" },
              "fields": [
                { "role": "Identifier",  "column": "CUST_NO" },
                { "role": "DisplayName", "column": "CUST_NAME" },
                { "role": "ActiveFlag",  "column": "CUST_STATE" }
              ]
            },
            {
              "role": "ItemLookup",
              "table": "MRD_ARTICLE",
              "sourceRefs": ["sch-art"],
              "constants": { "ActiveFlag": "A" },
              "fields": [
                { "role": "Identifier",         "column": "ART_NO" },
                { "role": "DisplayName",        "column": "ART_DESC" },
                { "role": "UnitPrice",          "column": "ART_PRICE" },
                { "role": "StockOnHand",        "column": "ART_ON_HAND" },
                { "role": "ActiveFlag",         "column": "ART_STATE" },
                { "role": "ConcurrencyVersion", "column": "ART_REV" }
              ]
            },
            {
              "role": "MasterHeader",
              "table": "MRD_ORDER_HEAD",
              "sourceRefs": ["sch-head"],
              "constants": { "Status": "ENTERED" },
              "fields": [
                { "role": "Identifier",         "column": "ORD_NO" },
                { "role": "PartyReference",     "column": "ORD_CUST" },
                { "role": "TotalAmount",        "column": "ORD_VALUE" },
                { "role": "CreatedAt",          "column": "ORD_RAISED" },
                { "role": "Status",             "column": "ORD_STATE" },
                { "role": "ConcurrencyVersion", "column": "ORD_REV" }
              ]
            },
            {
              "role": "DetailLine",
              "table": "MRD_ORDER_ITEM",
              "sourceRefs": ["sch-item"],
              "constants": {},
              "fields": [
                { "role": "Identifier",      "column": "ITM_NO" },
                { "role": "ParentReference", "column": "ITM_ORD" },
                { "role": "ItemReference",   "column": "ITM_ART" },
                { "role": "Quantity",        "column": "ITM_QTY" },
                { "role": "UnitPrice",       "column": "ITM_PRICE" },
                { "role": "LineAmount",      "column": "ITM_VALUE" }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Fixture two. Long descriptive names, a different declaration order, wider identifiers, three-place
    /// money, a multi-character availability domain, and a nullable column no role is bound to. It binds
    /// exactly the same roles as fixture one and shares no identifier with it.
    /// </summary>
    internal const string KestrelSchema = """
        CREATE TABLE DISPATCH_CONSIGNEE_REGISTER (
          CONSIGNEE_IDENTIFIER   NUMBER(12)    NOT NULL,
          CONSIGNEE_LEGAL_NAME   VARCHAR2(160) NOT NULL,
          CONSIGNEE_AVAILABILITY CHAR(1)       NOT NULL,
          CONSIGNEE_NOTE         VARCHAR2(400),
          CONSTRAINT DISPATCH_CONSIGNEE_PK PRIMARY KEY (CONSIGNEE_IDENTIFIER),
          CONSTRAINT DISPATCH_CONSIGNEE_AVAIL_CK CHECK (CONSIGNEE_AVAILABILITY IN ('Y','N'))
        );

        CREATE TABLE DISPATCH_STOCK_KEEPING_UNIT (
          STOCK_UNIT_IDENTIFIER  NUMBER(12)    NOT NULL,
          STOCK_UNIT_TITLE       VARCHAR2(160) NOT NULL,
          STOCK_UNIT_TARIFF      NUMBER(15,3)  NOT NULL,
          STOCK_UNIT_AVAILABLE   NUMBER(11)    NOT NULL,
          STOCK_UNIT_AVAILABILITY CHAR(1)      NOT NULL,
          STOCK_UNIT_REVISION    NUMBER(15)    NOT NULL,
          CONSTRAINT DISPATCH_STOCK_UNIT_PK PRIMARY KEY (STOCK_UNIT_IDENTIFIER),
          CONSTRAINT DISPATCH_STOCK_UNIT_AVAIL_CK CHECK (STOCK_UNIT_AVAILABILITY IN ('Y','N'))
        );

        CREATE TABLE DISPATCH_CONSIGNMENT_HEADER (
          CONSIGNMENT_IDENTIFIER   NUMBER(15)    NOT NULL,
          CONSIGNMENT_CONSIGNEE    NUMBER(12)    NOT NULL,
          CONSIGNMENT_GROSS_VALUE  NUMBER(18,3)  NOT NULL,
          CONSIGNMENT_RAISED_AT    TIMESTAMP     NOT NULL,
          CONSIGNMENT_DISPOSITION  VARCHAR2(16)  NOT NULL,
          CONSIGNMENT_REVISION     NUMBER(15)    NOT NULL,
          CONSTRAINT DISPATCH_CONSIGNMENT_PK PRIMARY KEY (CONSIGNMENT_IDENTIFIER),
          CONSTRAINT DISPATCH_CONSIGNMENT_CONSIGNEE_FK FOREIGN KEY (CONSIGNMENT_CONSIGNEE)
            REFERENCES DISPATCH_CONSIGNEE_REGISTER (CONSIGNEE_IDENTIFIER),
          CONSTRAINT DISPATCH_CONSIGNMENT_DISP_CK CHECK (CONSIGNMENT_DISPOSITION IN ('REGISTERED','DESPATCHED'))
        );

        CREATE TABLE DISPATCH_CONSIGNMENT_ALLOCATION (
          ALLOCATION_IDENTIFIER    NUMBER(17)    NOT NULL,
          ALLOCATION_CONSIGNMENT   NUMBER(15)    NOT NULL,
          ALLOCATION_STOCK_UNIT    NUMBER(12)    NOT NULL,
          ALLOCATION_UNIT_COUNT    NUMBER(11)    NOT NULL,
          ALLOCATION_AGREED_TARIFF NUMBER(15,3)  NOT NULL,
          ALLOCATION_EXTENDED      NUMBER(18,3)  NOT NULL,
          CONSTRAINT DISPATCH_ALLOCATION_PK PRIMARY KEY (ALLOCATION_IDENTIFIER),
          CONSTRAINT DISPATCH_ALLOCATION_HEADER_FK FOREIGN KEY (ALLOCATION_CONSIGNMENT)
            REFERENCES DISPATCH_CONSIGNMENT_HEADER (CONSIGNMENT_IDENTIFIER),
          CONSTRAINT DISPATCH_ALLOCATION_UNIT_FK FOREIGN KEY (ALLOCATION_STOCK_UNIT)
            REFERENCES DISPATCH_STOCK_KEEPING_UNIT (STOCK_UNIT_IDENTIFIER)
        );
        """;

    internal const string KestrelManifest = """
        {
          "schemaVersion": "fleet.target-mapping/1",
          "generator": "dotnet-master-detail/1",
          "application": "Kestrel Dispatch",
          "fixtureLabel": "synthetic-fixture-kestrel-dispatch",
          "sources": [
            { "id": "consignee-table",   "kind": "OracleSchemaObject", "path": "TABLE:DISPATCH_CONSIGNEE_REGISTER" },
            { "id": "stock-unit-table",  "kind": "OracleSchemaObject", "path": "TABLE:DISPATCH_STOCK_KEEPING_UNIT" },
            { "id": "consignment-table", "kind": "OracleSchemaObject", "path": "TABLE:DISPATCH_CONSIGNMENT_HEADER" },
            { "id": "allocation-table",  "kind": "OracleSchemaObject", "path": "TABLE:DISPATCH_CONSIGNMENT_ALLOCATION" }
          ],
          "objects": [
            {
              "role": "MasterHeader",
              "table": "DISPATCH_CONSIGNMENT_HEADER",
              "sourceRefs": ["consignment-table"],
              "constants": { "Status": "REGISTERED" },
              "fields": [
                { "role": "Identifier",         "column": "CONSIGNMENT_IDENTIFIER" },
                { "role": "PartyReference",     "column": "CONSIGNMENT_CONSIGNEE" },
                { "role": "TotalAmount",        "column": "CONSIGNMENT_GROSS_VALUE" },
                { "role": "CreatedAt",          "column": "CONSIGNMENT_RAISED_AT" },
                { "role": "Status",             "column": "CONSIGNMENT_DISPOSITION" },
                { "role": "ConcurrencyVersion", "column": "CONSIGNMENT_REVISION" }
              ]
            },
            {
              "role": "DetailLine",
              "table": "DISPATCH_CONSIGNMENT_ALLOCATION",
              "sourceRefs": ["allocation-table"],
              "constants": {},
              "fields": [
                { "role": "Identifier",      "column": "ALLOCATION_IDENTIFIER" },
                { "role": "ParentReference", "column": "ALLOCATION_CONSIGNMENT" },
                { "role": "ItemReference",   "column": "ALLOCATION_STOCK_UNIT" },
                { "role": "Quantity",        "column": "ALLOCATION_UNIT_COUNT" },
                { "role": "UnitPrice",       "column": "ALLOCATION_AGREED_TARIFF" },
                { "role": "LineAmount",      "column": "ALLOCATION_EXTENDED" }
              ]
            },
            {
              "role": "PartyLookup",
              "table": "DISPATCH_CONSIGNEE_REGISTER",
              "sourceRefs": ["consignee-table"],
              "constants": { "ActiveFlag": "Y" },
              "fields": [
                { "role": "Identifier",  "column": "CONSIGNEE_IDENTIFIER" },
                { "role": "DisplayName", "column": "CONSIGNEE_LEGAL_NAME" },
                { "role": "ActiveFlag",  "column": "CONSIGNEE_AVAILABILITY" }
              ]
            },
            {
              "role": "ItemLookup",
              "table": "DISPATCH_STOCK_KEEPING_UNIT",
              "sourceRefs": ["stock-unit-table"],
              "constants": { "ActiveFlag": "Y" },
              "fields": [
                { "role": "Identifier",         "column": "STOCK_UNIT_IDENTIFIER" },
                { "role": "DisplayName",        "column": "STOCK_UNIT_TITLE" },
                { "role": "UnitPrice",          "column": "STOCK_UNIT_TARIFF" },
                { "role": "StockOnHand",        "column": "STOCK_UNIT_AVAILABLE" },
                { "role": "ActiveFlag",         "column": "STOCK_UNIT_AVAILABILITY" },
                { "role": "ConcurrencyVersion", "column": "STOCK_UNIT_REVISION" }
              ]
            }
          ]
        }
        """;

    /// <summary>Every identifier fixture one declares, for asserting that fixture two never leaks into it.</summary>
    internal static IReadOnlyList<string> MeridianIdentifiers { get; } =
    [
        "MRD_CUSTOMER", "MRD_ARTICLE", "MRD_ORDER_HEAD", "MRD_ORDER_ITEM",
        "CUST_NO", "ART_ON_HAND", "ORD_VALUE", "ITM_QTY",
    ];

    /// <summary>Every identifier fixture two declares.</summary>
    internal static IReadOnlyList<string> KestrelIdentifiers { get; } =
    [
        "DISPATCH_CONSIGNEE_REGISTER", "DISPATCH_STOCK_KEEPING_UNIT",
        "DISPATCH_CONSIGNMENT_HEADER", "DISPATCH_CONSIGNMENT_ALLOCATION",
        "CONSIGNEE_IDENTIFIER", "STOCK_UNIT_AVAILABLE", "CONSIGNMENT_GROSS_VALUE", "ALLOCATION_UNIT_COUNT",
    ];

    /// <summary>The optional header roles fixture three leaves unbound. Both of the two the reader admits.</summary>
    internal static IReadOnlyList<string> UnboundHeaderRoles { get; } = ["Status", "ConcurrencyVersion"];

    /// <summary>
    /// Fixture three. Fixture one's estate, with the two columns its manifest bound to the optional header
    /// roles left unbound and carrying an Oracle literal default instead.
    ///
    /// It is a variant rather than a third invention so that the only difference under test is the
    /// binding: the master header binds nothing to Status or ConcurrencyVersion — the only two optional
    /// header roles the reader admits — and the estate still declares both columns NOT NULL, so the
    /// generated inserts, which omit them, depend on the default reaching the target.
    /// </summary>
    internal static string MeridianUnboundHeaderSchema { get; } = VaryOnce(
        VaryOnce(
            MeridianSchema,
            "ORD_STATE    VARCHAR2(10)  NOT NULL,",
            "ORD_STATE    VARCHAR2(10)  DEFAULT 'ENTERED' NOT NULL,"),
        "ORD_REV      NUMBER(12)    NOT NULL,",
        "ORD_REV      NUMBER(12)    DEFAULT 0 NOT NULL,");

    internal static string MeridianUnboundHeaderManifest { get; } = BuildUnboundHeaderManifest();

    /// <summary>
    /// Replaces text that has to appear exactly once. A variant built by a replacement that silently
    /// matched nothing would be the original fixture wearing a different label.
    /// </summary>
    private static string VaryOnce(string source, string find, string replacement)
    {
        int first = source.IndexOf(find, StringComparison.Ordinal);
        if (first < 0 || source.IndexOf(find, first + find.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"'{find}' does not appear exactly once in the fixture this variant is derived from.");
        }

        return source.Replace(find, replacement, StringComparison.Ordinal);
    }

    private static string BuildUnboundHeaderManifest()
    {
        JsonNode root = JsonNode.Parse(MeridianManifest)!;
        root["fixtureLabel"] = MeridianUnboundHeaderLabel;
        root["application"] = "Meridian Order Entry (unbound header)";

        JsonNode header = root["objects"]!.AsArray()
            .First(entry => entry!["role"]!.GetValue<string>() == "MasterHeader")!;
        JsonArray fields = header["fields"]!.AsArray();

        foreach (string role in UnboundHeaderRoles)
        {
            JsonNode bound = fields.FirstOrDefault(entry => entry!["role"]!.GetValue<string>() == role)
                ?? throw new InvalidOperationException(
                    $"The fixture this variant is derived from binds nothing to {role}, so unbinding it proves nothing.");
            fields.Remove(bound);
        }

        // Status was the only role on this object that required a declared constant.
        header["constants"] = new JsonObject();

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
