-- Meridian Order Entry source lab: object types and the order-entry package.
--
-- The block between the markers is reproduced verbatim from ../source/db/package.sql, which is the
-- canonical source text a migration run reads. tests/.../MeridianSourceLabFixtureTests.cs fails if
-- the two ever differ.

WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK

ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = MERIDIAN;

SET SERVEROUTPUT ON
SET DEFINE OFF

-- BEGIN source/db/package.sql
-- Meridian Order Entry source PL/SQL. Independently authored for this repository's synthetic
-- source lab. It is not a transcription of any customer routine, and it is not derived from the
-- PostgreSQL routine this repository's generator emits: the generated routine is target output and
-- is never used as the oracle for what this source is supposed to do.
--
-- Transaction ownership: no routine here commits. Each public routine opens its own savepoint and,
-- on any error, rolls back only its own work before re-raising. A caller may therefore compose
-- several calls in one transaction and still roll the whole thing back, and a failed call never
-- leaves a partial order behind.
--
-- Locking: CREATE_ORDER takes the MRD_CUSTOMER row FOR UPDATE first, then MRD_ARTICLE rows FOR
-- UPDATE in ascending ART_NO order. Every session therefore takes the same locks in the same
-- order and two concurrent order sessions queue rather than deadlock. The customer's eligibility
-- cannot be revoked between the check and the insert, and the stock check and the stock decrement
-- happen under the same lock. ART_REV is a monotonic revision a caller may use for optimistic checks.
--
-- Money: unit prices are NUMBER(11,2), extended and order amounts NUMBER(14,2). Every extended
-- amount is ROUND(qty * price, 2); no binary floating point is used anywhere.

-- Both attributes are unconstrained NUMBER on purpose. Declaring ITM_QTY as NUMBER(9) would give it
-- scale 0, so Oracle would silently round a caller's 1.5 to 2 at construction and the fractional
-- quantity would be accepted instead of rejected. Validation has to see what the caller actually sent.
CREATE OR REPLACE TYPE MRD_ORDER_LINE_T AS OBJECT
(
    ART_NO   NUMBER,
    ITM_QTY  NUMBER
);
/

CREATE OR REPLACE TYPE MRD_ORDER_LINE_TAB AS TABLE OF MRD_ORDER_LINE_T;
/

CREATE OR REPLACE PACKAGE MRD_ORDER_ENTRY_API AS
    -- Error numbers are part of the supported contract. A caller may branch on them.
    c_err_no_lines         CONSTANT PLS_INTEGER := -20101;
    c_err_customer_unusable CONSTANT PLS_INTEGER := -20102;
    c_err_article_unusable CONSTANT PLS_INTEGER := -20103;
    c_err_quantity_invalid CONSTANT PLS_INTEGER := -20104;
    c_err_stock_short      CONSTANT PLS_INTEGER := -20105;
    c_err_line_duplicated  CONSTANT PLS_INTEGER := -20106;
    c_err_revision_stale   CONSTANT PLS_INTEGER := -20107;

    no_lines          EXCEPTION;
    customer_unusable EXCEPTION;
    article_unusable  EXCEPTION;
    quantity_invalid  EXCEPTION;
    stock_short       EXCEPTION;
    line_duplicated   EXCEPTION;
    revision_stale    EXCEPTION;

    PRAGMA EXCEPTION_INIT(no_lines, -20101);
    PRAGMA EXCEPTION_INIT(customer_unusable, -20102);
    PRAGMA EXCEPTION_INIT(article_unusable, -20103);
    PRAGMA EXCEPTION_INIT(quantity_invalid, -20104);
    PRAGMA EXCEPTION_INIT(stock_short, -20105);
    PRAGMA EXCEPTION_INIT(line_duplicated, -20106);
    PRAGMA EXCEPTION_INIT(revision_stale, -20107);

    -- Extended amount for one line. Declared rather than inlined so the rounding rule has one owner.
    FUNCTION LINE_AMOUNT(
        p_qty IN NUMBER,
        p_unit_price IN NUMBER
    ) RETURN NUMBER DETERMINISTIC;

    -- Sum of the persisted line amounts. Independent of the value cached on the header.
    FUNCTION ORDER_TOTAL(p_ord_no IN NUMBER) RETURN NUMBER;

    -- Raises one order atomically. Does not commit. p_ord_no is NULL when the call failed.
    PROCEDURE CREATE_ORDER(
        p_cust_no IN NUMBER,
        p_lines IN MRD_ORDER_LINE_TAB,
        p_ord_no OUT NUMBER
    );

    -- Optimistic stock correction. Fails with -20107 when the article moved since p_expected_rev.
    PROCEDURE ADJUST_STOCK(
        p_art_no IN NUMBER,
        p_delta IN NUMBER,
        p_expected_rev IN NUMBER
    );
END MRD_ORDER_ENTRY_API;
/

CREATE OR REPLACE PACKAGE BODY MRD_ORDER_ENTRY_API AS

    FUNCTION LINE_AMOUNT(
        p_qty IN NUMBER,
        p_unit_price IN NUMBER
    ) RETURN NUMBER DETERMINISTIC IS
    BEGIN
        IF p_qty IS NULL OR p_unit_price IS NULL THEN
            RETURN NULL;
        END IF;

        RETURN ROUND(p_qty * p_unit_price, 2);
    END LINE_AMOUNT;

    FUNCTION ORDER_TOTAL(p_ord_no IN NUMBER) RETURN NUMBER IS
        v_total NUMBER(14, 2);
    BEGIN
        SELECT NVL(SUM(ITM_VALUE), 0)
          INTO v_total
          FROM MRD_ORDER_ITEM
         WHERE ITM_ORD = p_ord_no;

        RETURN v_total;
    END ORDER_TOTAL;

    -- Whole positive counts only. A fractional quantity is a data-entry fault, not a rounding chance.
    PROCEDURE ASSERT_QUANTITY(p_qty IN NUMBER, p_art_no IN NUMBER) IS
    BEGIN
        IF p_qty IS NULL OR p_qty <= 0 OR p_qty <> TRUNC(p_qty) THEN
            RAISE_APPLICATION_ERROR(
                c_err_quantity_invalid,
                'Quantity ' || NVL(TO_CHAR(p_qty), 'NULL') || ' for article '
                || NVL(TO_CHAR(p_art_no), 'NULL') || ' must be a whole number greater than zero.');
        END IF;
    END ASSERT_QUANTITY;

    -- FOR UPDATE, not a plain read. An unlocked check decides eligibility for an instant only: the
    -- row could be deactivated before the order rows land. The lock is held to the caller's COMMIT
    -- or ROLLBACK, so the answer given here is still true when CREATE_ORDER inserts.
    PROCEDURE ASSERT_CUSTOMER_USABLE(p_cust_no IN NUMBER) IS
        v_state MRD_CUSTOMER.CUST_STATE%TYPE;
    BEGIN
        BEGIN
            SELECT CUST_STATE
              INTO v_state
              FROM MRD_CUSTOMER
             WHERE CUST_NO = p_cust_no
               FOR UPDATE;
        EXCEPTION
            WHEN NO_DATA_FOUND THEN
                RAISE_APPLICATION_ERROR(
                    c_err_customer_unusable,
                    'Customer ' || NVL(TO_CHAR(p_cust_no), 'NULL') || ' does not exist.');
        END;

        IF v_state <> 'A' THEN
            RAISE_APPLICATION_ERROR(
                c_err_customer_unusable,
                'Customer ' || p_cust_no || ' is not active.');
        END IF;
    END ASSERT_CUSTOMER_USABLE;

    PROCEDURE CREATE_ORDER(
        p_cust_no IN NUMBER,
        p_lines IN MRD_ORDER_LINE_TAB,
        p_ord_no OUT NUMBER
    ) IS
        v_price   MRD_ARTICLE.ART_PRICE%TYPE;
        v_on_hand MRD_ARTICLE.ART_ON_HAND%TYPE;
        v_state   MRD_ARTICLE.ART_STATE%TYPE;
        v_amount  NUMBER(14, 2);
        v_total   NUMBER(14, 2) := 0;
    BEGIN
        SAVEPOINT mrd_create_order;
        p_ord_no := NULL;

        IF p_lines IS NULL OR p_lines.COUNT = 0 THEN
            RAISE_APPLICATION_ERROR(c_err_no_lines, 'An order must carry at least one line.');
        END IF;

        -- First lock of the transaction, and always this one: customer before articles.
        ASSERT_CUSTOMER_USABLE(p_cust_no);

        -- Line shape is judged before any MRD_ARTICLE row is touched.
        FOR i IN 1 .. p_lines.COUNT LOOP
            IF p_lines(i).ART_NO IS NULL THEN
                RAISE_APPLICATION_ERROR(c_err_article_unusable, 'A line carries no article number.');
            END IF;

            ASSERT_QUANTITY(p_lines(i).ITM_QTY, p_lines(i).ART_NO);

            FOR j IN 1 .. i - 1 LOOP
                IF p_lines(j).ART_NO = p_lines(i).ART_NO THEN
                    RAISE_APPLICATION_ERROR(
                        c_err_line_duplicated,
                        'Article ' || p_lines(i).ART_NO || ' appears on more than one line. '
                        || 'Combine the quantities instead.');
                END IF;
            END LOOP;
        END LOOP;

        p_ord_no := MRD_ORDER_SEQ.NEXTVAL;

        INSERT INTO MRD_ORDER_HEAD (ORD_NO, ORD_CUST, ORD_VALUE, ORD_RAISED, ORD_STATE, ORD_REV)
        VALUES (p_ord_no, p_cust_no, 0, TRUNC(SYSDATE), 'ENTERED', 0);

        -- Customer already locked; ascending ART_NO next, so every session takes the same locks in
        -- the same order and two overlapping orders queue instead of deadlocking.
        FOR line IN (SELECT ART_NO, ITM_QTY FROM TABLE(p_lines) ORDER BY ART_NO) LOOP
            BEGIN
                SELECT ART_PRICE, ART_ON_HAND, ART_STATE
                  INTO v_price, v_on_hand, v_state
                  FROM MRD_ARTICLE
                 WHERE ART_NO = line.ART_NO
                   FOR UPDATE;
            EXCEPTION
                WHEN NO_DATA_FOUND THEN
                    RAISE_APPLICATION_ERROR(
                        c_err_article_unusable,
                        'Article ' || line.ART_NO || ' does not exist.');
            END;

            IF v_state <> 'A' THEN
                RAISE_APPLICATION_ERROR(
                    c_err_article_unusable,
                    'Article ' || line.ART_NO || ' is not active.');
            END IF;

            IF v_on_hand < line.ITM_QTY THEN
                RAISE_APPLICATION_ERROR(
                    c_err_stock_short,
                    'Article ' || line.ART_NO || ' has ' || v_on_hand || ' on hand and '
                    || line.ITM_QTY || ' were requested.');
            END IF;

            v_amount := LINE_AMOUNT(line.ITM_QTY, v_price);
            v_total := v_total + v_amount;

            INSERT INTO MRD_ORDER_ITEM (ITM_NO, ITM_ORD, ITM_ART, ITM_QTY, ITM_PRICE, ITM_VALUE)
            VALUES (MRD_ORDER_ITEM_SEQ.NEXTVAL, p_ord_no, line.ART_NO, line.ITM_QTY, v_price, v_amount);

            UPDATE MRD_ARTICLE
               SET ART_ON_HAND = ART_ON_HAND - line.ITM_QTY,
                   ART_REV = ART_REV + 1
             WHERE ART_NO = line.ART_NO;
        END LOOP;

        UPDATE MRD_ORDER_HEAD
           SET ORD_VALUE = v_total,
               ORD_REV = ORD_REV + 1
         WHERE ORD_NO = p_ord_no;
    EXCEPTION
        WHEN OTHERS THEN
            ROLLBACK TO SAVEPOINT mrd_create_order;
            p_ord_no := NULL;
            RAISE;
    END CREATE_ORDER;

    PROCEDURE ADJUST_STOCK(
        p_art_no IN NUMBER,
        p_delta IN NUMBER,
        p_expected_rev IN NUMBER
    ) IS
        v_on_hand MRD_ARTICLE.ART_ON_HAND%TYPE;
        v_rev     MRD_ARTICLE.ART_REV%TYPE;
    BEGIN
        SAVEPOINT mrd_adjust_stock;

        IF p_delta IS NULL OR p_delta = 0 OR p_delta <> TRUNC(p_delta) THEN
            RAISE_APPLICATION_ERROR(
                c_err_quantity_invalid,
                'A stock adjustment must be a non-zero whole number.');
        END IF;

        BEGIN
            SELECT ART_ON_HAND, ART_REV
              INTO v_on_hand, v_rev
              FROM MRD_ARTICLE
             WHERE ART_NO = p_art_no
               FOR UPDATE;
        EXCEPTION
            WHEN NO_DATA_FOUND THEN
                RAISE_APPLICATION_ERROR(
                    c_err_article_unusable,
                    'Article ' || NVL(TO_CHAR(p_art_no), 'NULL') || ' does not exist.');
        END;

        IF p_expected_rev IS NULL OR v_rev <> p_expected_rev THEN
            RAISE_APPLICATION_ERROR(
                c_err_revision_stale,
                'Article ' || p_art_no || ' is at revision ' || v_rev || ' and the caller expected '
                || NVL(TO_CHAR(p_expected_rev), 'NULL') || '. Re-read the article and retry.');
        END IF;

        IF v_on_hand + p_delta < 0 THEN
            RAISE_APPLICATION_ERROR(
                c_err_stock_short,
                'Article ' || p_art_no || ' has ' || v_on_hand
                || ' on hand and cannot absorb an adjustment of ' || p_delta || '.');
        END IF;

        UPDATE MRD_ARTICLE
           SET ART_ON_HAND = ART_ON_HAND + p_delta,
               ART_REV = ART_REV + 1
         WHERE ART_NO = p_art_no;
    EXCEPTION
        WHEN OTHERS THEN
            ROLLBACK TO SAVEPOINT mrd_adjust_stock;
            RAISE;
    END ADJUST_STOCK;

END MRD_ORDER_ENTRY_API;
/
-- END source/db/package.sql

DECLARE
    v_app_user VARCHAR2(128) := 'BANKING';
    v_exists   PLS_INTEGER;
BEGIN
    SELECT COUNT(*) INTO v_exists FROM DBA_USERS WHERE USERNAME = v_app_user;

    IF v_exists = 1 THEN
        EXECUTE IMMEDIATE 'GRANT EXECUTE ON MERIDIAN.MRD_ORDER_LINE_T TO ' || v_app_user;
        EXECUTE IMMEDIATE 'GRANT EXECUTE ON MERIDIAN.MRD_ORDER_LINE_TAB TO ' || v_app_user;
        EXECUTE IMMEDIATE 'GRANT EXECUTE ON MERIDIAN.MRD_ORDER_ENTRY_API TO ' || v_app_user;
        DBMS_OUTPUT.PUT_LINE('MERIDIAN ORDER LAB: granted EXECUTE on the order API to ' || v_app_user);
    ELSE
        DBMS_OUTPUT.PUT_LINE('MERIDIAN ORDER LAB: no ' || v_app_user || ' account present; no grant issued');
    END IF;
END;
/
