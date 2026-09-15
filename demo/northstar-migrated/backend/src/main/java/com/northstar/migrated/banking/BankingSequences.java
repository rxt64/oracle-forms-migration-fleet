package com.northstar.migrated.banking;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.boot.context.event.ApplicationReadyEvent;
import org.springframework.context.event.EventListener;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.stereotype.Component;
import org.springframework.transaction.annotation.Transactional;

/**
 * Migrated rows keep the identifiers they had in Oracle, but a sequence restored from DDL starts where
 * the DDL said it started. Left alone it would hand out values that already exist, and the first insert
 * after a data load would fail on the primary key.
 *
 * Alignment therefore runs once at startup, inside a single transaction, and it only ever moves a
 * sequence forward. Three properties matter and each is enforced here rather than assumed:
 *
 * <ul>
 *   <li><b>It never lowers a sequence.</b> Each setval takes GREATEST of the sequence's own last value,
 *       the largest identifier in the table, and the floor the converted DDL declared. A replica that
 *       starts while another replica is already issuing identifiers cannot claw one back.</li>
 *   <li><b>It is safe across rolling replicas.</b> Every replica takes the same advisory transaction
 *       lock first, so alignment is serialised across the whole cluster and released when the
 *       transaction ends, including when it ends by failing.</li>
 *   <li><b>It fails startup rather than continuing.</b> Nothing is caught. A sequence left behind the
 *       migrated rows would fail every insert with a duplicate key, so a process that could not align
 *       one must not go on to serve traffic and be reported healthy.</li>
 * </ul>
 *
 * The sequence and table names below are compile-time constants taken from the converted schema, never
 * from a request, so the statements they are concatenated into cannot be influenced by a caller.
 */
@Component
public class BankingSequences {

    public static final String REQUEST_SEQUENCE = "bank_request_seq";
    public static final String ACCOUNT_SEQUENCE = "bank_account_seq";
    public static final String TRANSACTION_SEQUENCE = "bank_transaction_seq";

    /**
     * A constant chosen by this generator and shared by every replica of this application. Advisory
     * locks live in one cluster-wide space, so the value only has to be stable and unlikely to collide.
     */
    public static final long ALIGNMENT_LOCK_KEY = 8774246031052843022L;

    private static final Logger LOG = LoggerFactory.getLogger(BankingSequences.class);

    private final JdbcTemplate jdbc;

    public BankingSequences(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    @EventListener(ApplicationReadyEvent.class)
    @Transactional
    public void align() {
        // Held until this transaction ends, so the three setvals below cannot interleave with another
        // replica's. pg_advisory_xact_lock has no unlock call and none is needed.
        jdbc.queryForList("SELECT pg_advisory_xact_lock(?)", ALIGNMENT_LOCK_KEY);

        align(REQUEST_SEQUENCE, "bank_account_request", "request_id", 1000L);
        align(ACCOUNT_SEQUENCE, "bank_account", "account_id", 500000L);
        align(TRANSACTION_SEQUENCE, "bank_transaction", "transaction_id", 9000L);
    }

    /**
     * Moves one sequence to the highest of its own last value, the largest identifier in its table, and
     * the floor, then marks it as called so the next value is one past that.
     *
     * pg_sequences.last_value reads null for a sequence that has never been handed out, which is
     * exactly the case where the floor from the converted DDL has to win. The join resolves the
     * sequence through regclass rather than by comparing names, so the current search_path decides
     * which schema is meant and a same-named sequence in another schema cannot be picked up.
     */
    private void align(String sequence, String table, String column, long floor) {
        Long aligned = jdbc.queryForObject("""
                SELECT setval(
                           '%s',
                           GREATEST(
                               COALESCE((SELECT s.last_value
                                           FROM pg_sequences s
                                           JOIN pg_class c ON c.relname = s.sequencename
                                           JOIN pg_namespace n ON n.oid = c.relnamespace
                                                             AND n.nspname = s.schemaname
                                          WHERE c.oid = '%s'::regclass), 0),
                               COALESCE((SELECT MAX(%s) FROM %s), 0),
                               %d)::bigint,
                           true)
                """.formatted(sequence, sequence, column, table, floor),
                Long.class);

        if (aligned == null) {
            throw new IllegalStateException(
                    "Sequence " + sequence + " could not be aligned past the migrated rows.");
        }

        LOG.info("Sequence {} now continues from {}.", sequence, aligned);
    }
}
