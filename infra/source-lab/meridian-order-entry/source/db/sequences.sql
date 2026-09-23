-- Surrogate key sources for the Meridian Order Entry source lab.
--
-- They are kept out of schema.sql on purpose: schema.sql is the table-only DDL the repository's
-- mapping fixture already declares, and it stays byte-identical to that fixture so the lab and the
-- test fixture can never drift into two different estates. Sequences are lab installation material.

CREATE SEQUENCE MRD_ORDER_SEQ START WITH 9003 INCREMENT BY 1 NOCACHE ORDER NOCYCLE;

CREATE SEQUENCE MRD_ORDER_ITEM_SEQ START WITH 7004 INCREMENT BY 1 NOCACHE ORDER NOCYCLE;

CREATE INDEX IX_MRD_ORDER_HEAD_CUST ON MRD_ORDER_HEAD (ORD_CUST);

CREATE INDEX IX_MRD_ORDER_ITEM_ORD ON MRD_ORDER_ITEM (ITM_ORD);

CREATE INDEX IX_MRD_ORDER_ITEM_ART ON MRD_ORDER_ITEM (ITM_ART);
