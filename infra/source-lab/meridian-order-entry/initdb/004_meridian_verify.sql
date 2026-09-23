-- Meridian Order Entry source lab: independent verifier.
--
-- Fails container initialization unless the order estate behaves the way this file, on its own,
-- says it must. Every expected value below is a literal declared here. Nothing in this verifier
-- reads the repository's generated PostgreSQL routine, and the generated target is never treated
-- as the oracle for what the Oracle source is supposed to do.
--
-- The verifier leaves the estate exactly as the seed script committed it: all of its own work is
-- rolled back before the counts are re-asserted.
--
-- NOT PROVED HERE: the two-session stock race. A single session cannot observe its own lock
-- contention, so no assertion in this file may be read as evidence that concurrent oversell is
-- prevented. The two-session protocol is in ../README.md and has to be run by an operator.

WHENEVER OSERROR EXIT FAILURE ROLLBACK
WHENEVER SQLERROR EXIT SQL.SQLCODE ROLLBACK

ALTER SESSION SET CONTAINER = FREEPDB1;
ALTER SESSION SET CURRENT_SCHEMA = MERIDIAN;

SET SERVEROUTPUT ON
SET FEEDBACK OFF
SET DEFINE OFF

DECLARE
    v_failures  PLS_INTEGER := 0;
    v_customers PLS_INTEGER;
    v_articles  PLS_INTEGER;
    v_invalid   PLS_INTEGER;
    v_ord       NUMBER;
    v_total     NUMBER;
    v_stock_a   NUMBER;
    v_rev_a     NUMBER;
    v_stock_b   NUMBER;
    v_rev_b     NUMBER;

    PROCEDURE note(p_label IN VARCHAR2, p_ok IN BOOLEAN, p_detail IN VARCHAR2 DEFAULT NULL) IS
    BEGIN
        IF p_ok THEN
            DBMS_OUTPUT.PUT_LINE('MERIDIAN ORDER LAB: PASS  ' || p_label);
        ELSE
            v_failures := v_failures + 1;
            DBMS_OUTPUT.PUT_LINE(
                'MERIDIAN ORDER LAB: FAIL  ' || p_label
                || CASE WHEN p_detail IS NULL THEN NULL ELSE ' -- ' || p_detail END);
        END IF;
    END note;

    FUNCTION head_rows RETURN PLS_INTEGER IS
        v PLS_INTEGER;
    BEGIN
        SELECT COUNT(*) INTO v FROM MRD_ORDER_HEAD;
        RETURN v;
    END head_rows;

    FUNCTION line_rows RETURN PLS_INTEGER IS
        v PLS_INTEGER;
    BEGIN
        SELECT COUNT(*) INTO v FROM MRD_ORDER_ITEM;
        RETURN v;
    END line_rows;

    FUNCTION on_hand(p_art IN NUMBER) RETURN NUMBER IS
        v NUMBER;
    BEGIN
        SELECT ART_ON_HAND INTO v FROM MRD_ARTICLE WHERE ART_NO = p_art;
        RETURN v;
    END on_hand;

    FUNCTION revision(p_art IN NUMBER) RETURN NUMBER IS
        v NUMBER;
    BEGIN
        SELECT ART_REV INTO v FROM MRD_ARTICLE WHERE ART_NO = p_art;
        RETURN v;
    END revision;

    -- Runs one order and reports the Oracle error number instead of letting it escape, so a
    -- rejection can be asserted by code rather than by the absence of a crash.
    FUNCTION create_code(p_cust IN NUMBER, p_lines IN MRD_ORDER_LINE_TAB) RETURN PLS_INTEGER IS
        v_id NUMBER;
    BEGIN
        MRD_ORDER_ENTRY_API.CREATE_ORDER(p_cust, p_lines, v_id);
        RETURN 0;
    EXCEPTION
        WHEN OTHERS THEN
            RETURN SQLCODE;
    END create_code;

    FUNCTION adjust_code(p_art IN NUMBER, p_delta IN NUMBER, p_rev IN NUMBER) RETURN PLS_INTEGER IS
    BEGIN
        MRD_ORDER_ENTRY_API.ADJUST_STOCK(p_art, p_delta, p_rev);
        RETURN 0;
    EXCEPTION
        WHEN OTHERS THEN
            RETURN SQLCODE;
    END adjust_code;
BEGIN
    ------------------------------------------------------------------ seeded estate
    SELECT COUNT(*) INTO v_customers FROM MRD_CUSTOMER;
    SELECT COUNT(*) INTO v_articles FROM MRD_ARTICLE;
    SELECT COUNT(*) INTO v_invalid FROM DBA_OBJECTS WHERE OWNER = 'MERIDIAN' AND STATUS <> 'VALID';

    note('seeded customers = 5', v_customers = 5, 'actual ' || v_customers);
    note('seeded articles = 6', v_articles = 6, 'actual ' || v_articles);
    note('seeded order headers = 2', head_rows = 2, 'actual ' || head_rows);
    note('seeded order lines = 3', line_rows = 3, 'actual ' || line_rows);
    note('no invalid MERIDIAN objects', v_invalid = 0, 'actual ' || v_invalid);

    ------------------------------------------------------------------ decimal money
    note('LINE_AMOUNT(3, 19.99) = 59.97',
        MRD_ORDER_ENTRY_API.LINE_AMOUNT(3, 19.99) = 59.97,
        'actual ' || MRD_ORDER_ENTRY_API.LINE_AMOUNT(3, 19.99));
    note('LINE_AMOUNT(7, 0.05) = 0.35',
        MRD_ORDER_ENTRY_API.LINE_AMOUNT(7, 0.05) = 0.35,
        'actual ' || MRD_ORDER_ENTRY_API.LINE_AMOUNT(7, 0.05));
    note('LINE_AMOUNT(3, 4.255) rounds to 12.77',
        MRD_ORDER_ENTRY_API.LINE_AMOUNT(3, 4.255) = 12.77,
        'actual ' || MRD_ORDER_ENTRY_API.LINE_AMOUNT(3, 4.255));
    note('ORDER_TOTAL(9001) = 68.47',
        MRD_ORDER_ENTRY_API.ORDER_TOTAL(9001) = 68.47,
        'actual ' || MRD_ORDER_ENTRY_API.ORDER_TOTAL(9001));

    ------------------------------------------------------------------ a valid order
    v_stock_a := on_hand(2001);
    v_rev_a := revision(2001);
    v_stock_b := on_hand(2002);
    v_rev_b := revision(2002);

    BEGIN
        MRD_ORDER_ENTRY_API.CREATE_ORDER(
            1001,
            MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(2001, 2), MRD_ORDER_LINE_T(2002, 4)),
            v_ord);
    EXCEPTION
        WHEN OTHERS THEN
            v_ord := NULL;
            note('a valid two-line order is accepted', FALSE, SQLERRM);
    END;

    IF v_ord IS NOT NULL THEN
        SELECT ORD_VALUE INTO v_total FROM MRD_ORDER_HEAD WHERE ORD_NO = v_ord;

        note('a valid two-line order is accepted', TRUE);
        note('header total = 56.98', v_total = 56.98, 'actual ' || v_total);
        note('line totals sum to 56.98',
            MRD_ORDER_ENTRY_API.ORDER_TOTAL(v_ord) = 56.98,
            'actual ' || MRD_ORDER_ENTRY_API.ORDER_TOTAL(v_ord));
        note('article 2001 stock fell by 2', on_hand(2001) = v_stock_a - 2,
            'actual ' || on_hand(2001));
        note('article 2001 revision rose by 1', revision(2001) = v_rev_a + 1);
        note('article 2002 stock fell by 4', on_hand(2002) = v_stock_b - 4,
            'actual ' || on_hand(2002));
        note('article 2002 revision rose by 1', revision(2002) = v_rev_b + 1);
        note('one header and two lines were added', head_rows = 3 AND line_rows = 5,
            head_rows || ' headers, ' || line_rows || ' lines');
    END IF;

    ------------------------------------------------------------------ rejected input
    note('an order with no lines is rejected with -20101',
        create_code(1001, MRD_ORDER_LINE_TAB()) = -20101);
    note('quantity 0 is rejected with -20104',
        create_code(1001, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(2001, 0))) = -20104);
    note('a negative quantity is rejected with -20104',
        create_code(1001, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(2001, -3))) = -20104);
    note('a fractional quantity is rejected with -20104',
        create_code(1001, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(2001, 1.5))) = -20104);
    note('an unknown customer is rejected with -20102',
        create_code(9999, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(2001, 1))) = -20102);
    note('an inactive customer is rejected with -20102',
        create_code(1005, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(2001, 1))) = -20102);
    note('an unknown article is rejected with -20103',
        create_code(1001, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(8888, 1))) = -20103);
    note('an inactive article is rejected with -20103',
        create_code(1001, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(2006, 1))) = -20103);
    note('a repeated article is rejected with -20106',
        create_code(1001, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(2001, 1), MRD_ORDER_LINE_T(2001, 1))) = -20106);
    note('ordering more than the stock on hand is rejected with -20105',
        create_code(1001, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(2005, 4))) = -20105);

    note('no rejected order changed the row counts', head_rows = 3 AND line_rows = 5,
        head_rows || ' headers, ' || line_rows || ' lines');

    ------------------------------------------------------------------ atomic rollback
    -- Article 2004 is orderable and is locked first; 2005 is short. The whole order must vanish.
    v_stock_a := on_hand(2004);
    v_rev_a := revision(2004);

    note('a part-valid order is rejected with -20105',
        create_code(1001, MRD_ORDER_LINE_TAB(MRD_ORDER_LINE_T(2004, 5), MRD_ORDER_LINE_T(2005, 99))) = -20105);
    note('the accepted line of the rejected order moved no stock',
        on_hand(2004) = v_stock_a AND revision(2004) = v_rev_a,
        'on hand ' || on_hand(2004) || ', revision ' || revision(2004));
    note('the rejected order left no header or line rows', head_rows = 3 AND line_rows = 5,
        head_rows || ' headers, ' || line_rows || ' lines');
    note('the rejected order did not discard the caller''s earlier committed-in-transaction work',
        v_ord IS NOT NULL AND MRD_ORDER_ENTRY_API.ORDER_TOTAL(v_ord) = 56.98);

    ------------------------------------------------------------------ optimistic revision
    v_stock_a := on_hand(2003);
    v_rev_a := revision(2003);

    note('an in-date optimistic adjustment is accepted', adjust_code(2003, 5, v_rev_a) = 0);
    note('the adjustment moved stock and revision together',
        on_hand(2003) = v_stock_a + 5 AND revision(2003) = v_rev_a + 1,
        'on hand ' || on_hand(2003) || ', revision ' || revision(2003));
    note('a stale optimistic adjustment is rejected with -20107',
        adjust_code(2003, 5, v_rev_a) = -20107);
    note('the rejected adjustment moved nothing',
        on_hand(2003) = v_stock_a + 5 AND revision(2003) = v_rev_a + 1,
        'on hand ' || on_hand(2003) || ', revision ' || revision(2003));

    ------------------------------------------------------------------ restore the seed
    ROLLBACK;

    SELECT COUNT(*) INTO v_customers FROM MRD_CUSTOMER;
    SELECT COUNT(*) INTO v_articles FROM MRD_ARTICLE;

    note('rollback restored the seeded estate',
        v_customers = 5
        AND v_articles = 6
        AND head_rows = 2
        AND line_rows = 3
        AND on_hand(2001) = 40 AND revision(2001) = 0
        AND on_hand(2002) = 150 AND revision(2002) = 0
        AND on_hand(2003) = 12 AND revision(2003) = 0
        AND on_hand(2004) = 60 AND revision(2004) = 0,
        head_rows || ' headers, ' || line_rows || ' lines');

    DBMS_OUTPUT.PUT_LINE(
        'MERIDIAN ORDER LAB: NOT VERIFIED  two-session stock race -- one session cannot prove it; '
        || 'run the protocol in infra/source-lab/meridian-order-entry/README.md');

    IF v_failures = 0 THEN
        DBMS_OUTPUT.PUT_LINE('MERIDIAN ORDER LAB: SEED OK');
    ELSE
        DBMS_OUTPUT.PUT_LINE('MERIDIAN ORDER LAB: SEED INCOMPLETE (' || v_failures || ' checks failed)');
        RAISE_APPLICATION_ERROR(
            -20001,
            'Meridian order-entry source lab verification failed: ' || v_failures || ' checks.');
    END IF;
END;
/
