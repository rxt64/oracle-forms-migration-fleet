-- Seed rows for the Meridian Order Entry source lab.
--
-- Every customer, article, and order below is invented for this repository. No name, address,
-- price, or order corresponds to a real person, company, or transaction. On-hand quantities are
-- stated as the position *after* the two seeded orders, so the estate reads as a going concern.

INSERT INTO MRD_CUSTOMER (CUST_NO, CUST_NAME, CUST_STATE, CUST_MEMO) VALUES
    (1001, 'Ardenne Bakehouse Ltd', 'A', 'Weekly delivery slot, Tuesdays.');
INSERT INTO MRD_CUSTOMER (CUST_NO, CUST_NAME, CUST_STATE, CUST_MEMO) VALUES
    (1002, 'Halewood Marine Services', 'A', NULL);
INSERT INTO MRD_CUSTOMER (CUST_NO, CUST_NAME, CUST_STATE, CUST_MEMO) VALUES
    (1003, 'Trellis Gardens Cooperative', 'A', 'Purchase orders required on every line.');
INSERT INTO MRD_CUSTOMER (CUST_NO, CUST_NAME, CUST_STATE, CUST_MEMO) VALUES
    (1004, 'Pinfold Stationers', 'A', NULL);
INSERT INTO MRD_CUSTOMER (CUST_NO, CUST_NAME, CUST_STATE, CUST_MEMO) VALUES
    (1005, 'Quarry Bank Foundry', 'I', 'Account closed; retained for order history only.');

INSERT INTO MRD_ARTICLE (ART_NO, ART_DESC, ART_PRICE, ART_ON_HAND, ART_STATE, ART_REV) VALUES
    (2001, 'Kiln-dried oak dowel, 18mm', 19.99, 40, 'A', 0);
INSERT INTO MRD_ARTICLE (ART_NO, ART_DESC, ART_PRICE, ART_ON_HAND, ART_STATE, ART_REV) VALUES
    (2002, 'Brass hinge, 60mm, pair', 4.25, 150, 'A', 0);
INSERT INTO MRD_ARTICLE (ART_NO, ART_DESC, ART_PRICE, ART_ON_HAND, ART_STATE, ART_REV) VALUES
    (2003, 'Bench vice, 125mm jaw', 125.00, 12, 'A', 0);
INSERT INTO MRD_ARTICLE (ART_NO, ART_DESC, ART_PRICE, ART_ON_HAND, ART_STATE, ART_REV) VALUES
    (2004, 'Linseed oil, 5 litre', 7.05, 60, 'A', 0);
INSERT INTO MRD_ARTICLE (ART_NO, ART_DESC, ART_PRICE, ART_ON_HAND, ART_STATE, ART_REV) VALUES
    (2005, 'Cabinet scraper set', 249.95, 3, 'A', 0);
INSERT INTO MRD_ARTICLE (ART_NO, ART_DESC, ART_PRICE, ART_ON_HAND, ART_STATE, ART_REV) VALUES
    (2006, 'Shellac flakes, 500g', 3.33, 25, 'I', 0);

INSERT INTO MRD_ORDER_HEAD (ORD_NO, ORD_CUST, ORD_VALUE, ORD_RAISED, ORD_STATE, ORD_REV) VALUES
    (9001, 1001, 68.47, DATE '2026-03-04', 'ENTERED', 1);
INSERT INTO MRD_ORDER_HEAD (ORD_NO, ORD_CUST, ORD_VALUE, ORD_RAISED, ORD_STATE, ORD_REV) VALUES
    (9002, 1002, 125.00, DATE '2026-03-11', 'SHIPPED', 2);

INSERT INTO MRD_ORDER_ITEM (ITM_NO, ITM_ORD, ITM_ART, ITM_QTY, ITM_PRICE, ITM_VALUE) VALUES
    (7001, 9001, 2001, 3, 19.99, 59.97);
INSERT INTO MRD_ORDER_ITEM (ITM_NO, ITM_ORD, ITM_ART, ITM_QTY, ITM_PRICE, ITM_VALUE) VALUES
    (7002, 9001, 2002, 2, 4.25, 8.50);
INSERT INTO MRD_ORDER_ITEM (ITM_NO, ITM_ORD, ITM_ART, ITM_QTY, ITM_PRICE, ITM_VALUE) VALUES
    (7003, 9002, 2003, 1, 125.00, 125.00);

COMMIT;
