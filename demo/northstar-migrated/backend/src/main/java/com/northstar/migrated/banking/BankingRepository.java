package com.northstar.migrated.banking;

import com.northstar.migrated.banking.BankingContracts.AccountRequestCreated;
import com.northstar.migrated.banking.BankingContracts.AccountRequestSubmission;
import com.northstar.migrated.banking.BankingContracts.AccountRequestSummary;
import com.northstar.migrated.banking.BankingContracts.CustomerProfile;
import com.northstar.migrated.banking.BankingContracts.StatementLine;
import com.northstar.migrated.banking.BankingContracts.StatementResponse;
import com.northstar.migrated.banking.BankingContracts.TransactionCreated;
import org.springframework.dao.DataAccessException;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.jdbc.core.RowMapper;
import org.springframework.stereotype.Repository;
import org.springframework.transaction.annotation.Transactional;

import java.math.BigDecimal;
import java.math.RoundingMode;
import java.sql.Date;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Timestamp;
import java.time.LocalDateTime;
import java.util.List;

/**
 * Every workflow's data access, against PostgreSQL only. Statements are parameterised, and each
 * multi-statement mutation is annotated so it commits or rolls back as one unit — approval in
 * particular must not be able to create an account without also deciding the request.
 */
@Repository
public class BankingRepository {

    public enum RegistrationOutcome {
        ENABLED,
        ACCOUNT_NOT_FOUND,
        ALREADY_ENABLED
    }

    public enum ApprovalOutcome {
        APPROVED,
        REQUEST_NOT_FOUND,
        ALREADY_DECIDED
    }

    public record Approval(ApprovalOutcome outcome, Long accountId) {
    }

    private record PendingRequest(String branchCode, String accountKind, String requestStatus) {
    }

    private record Credential(String subject, CustomerProfile profile, byte[] hash) {
    }

    private static final String PROFILE_SQL = """
            SELECT a.account_id,
                   r.given_name || ' ' || r.family_name AS account_holder,
                   a.branch_code,
                   a.account_kind
              FROM bank_account a
              JOIN bank_account_request r ON r.request_id = a.request_id
             WHERE a.account_id = ?
            """;

    private static final String CUSTOMER_CREDENTIAL_SQL = """
            SELECT a.account_id,
                   r.given_name || ' ' || r.family_name AS account_holder,
                   a.branch_code,
                   a.account_kind,
                   a.online_password_hash
              FROM bank_account a
              JOIN bank_account_request r ON r.request_id = a.request_id
             WHERE a.account_id = ?
               AND a.online_enabled = 'Y'
            """;

    private static final String MANAGER_CREDENTIAL_SQL = """
            SELECT username, password_hash
              FROM bank_staff_user
             WHERE username = lower(trim(?))
               AND role_code = 'MANAGER'
               AND active_flag = 'Y'
            """;

    private static final RowMapper<CustomerProfile> PROFILE_MAPPER = (resultSet, row) -> readProfile(resultSet);

    private final JdbcTemplate jdbc;

    public BankingRepository(JdbcTemplate jdbc) {
        this.jdbc = jdbc;
    }

    public boolean databaseAvailable() {
        try {
            return Integer.valueOf(1).equals(jdbc.queryForObject("SELECT 1", Integer.class));
        } catch (DataAccessException cause) {
            return false;
        }
    }

    /** Reads the stored hash and compares it here, so the supplied password never reaches the database. */
    public CustomerProfile authenticateCustomer(long accountId, String password) {
        Credential credential = single(jdbc.query(
                CUSTOMER_CREDENTIAL_SQL,
                (resultSet, row) -> new Credential(
                        null, readProfile(resultSet), resultSet.getBytes("online_password_hash")),
                accountId));

        return credential != null && PasswordHashing.matches(credential.hash(), password)
                ? credential.profile()
                : null;
    }

    public String authenticateManager(String username, String password) {
        Credential credential = single(jdbc.query(
                MANAGER_CREDENTIAL_SQL,
                (resultSet, row) -> new Credential(
                        resultSet.getString("username"), null, resultSet.getBytes("password_hash")),
                username));

        return credential != null && PasswordHashing.matches(credential.hash(), password)
                ? credential.subject()
                : null;
    }

    @Transactional
    public RegistrationOutcome enableOnlineAccess(long accountId, String emailAddress, String password) {
        String onlineEnabled = single(jdbc.query("""
                SELECT a.online_enabled
                  FROM bank_account a
                  JOIN bank_account_request r ON r.request_id = a.request_id
                 WHERE a.account_id = ?
                   AND lower(r.email_address) = lower(?)
                   FOR UPDATE OF a
                """, (resultSet, row) -> resultSet.getString(1), accountId, emailAddress));

        if (onlineEnabled == null) {
            return RegistrationOutcome.ACCOUNT_NOT_FOUND;
        }

        if ("Y".equals(onlineEnabled.trim())) {
            return RegistrationOutcome.ALREADY_ENABLED;
        }

        int updated = jdbc.update("""
                UPDATE bank_account
                   SET online_enabled = 'Y',
                       online_password_hash = ?
                 WHERE account_id = ?
                   AND online_enabled = 'N'
                """, PasswordHashing.sha256(password), accountId);

        return updated == 1 ? RegistrationOutcome.ENABLED : RegistrationOutcome.ALREADY_ENABLED;
    }

    @Transactional
    public AccountRequestCreated createAccountRequest(AccountRequestSubmission request) {
        long requestId = nextValue(BankingSequences.REQUEST_SEQUENCE);

        jdbc.update("""
                INSERT INTO bank_account_request
                    (request_id, branch_code, account_kind, honorific, given_name, family_name,
                     date_of_birth, work_phone, home_phone, street_address, region_code, postal_code,
                     email_address, request_status, submitted_at, decided_at)
                VALUES
                    (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'SUBMITTED', localtimestamp, NULL)
                """,
                requestId,
                request.branchCode(),
                request.accountKind(),
                request.honorific(),
                request.givenName(),
                request.familyName(),
                Date.valueOf(request.dateOfBirth()),
                request.workPhone(),
                request.homePhone(),
                request.streetAddress(),
                request.regionCode(),
                request.postalCode(),
                request.emailAddress());

        return new AccountRequestCreated(requestId, "SUBMITTED");
    }

    /**
     * Simple interest, as LEGACY_BANKING_API.CALCULATE_SIMPLE_INTEREST computed it. Oracle's ROUND is
     * half away from zero; the inputs are validated positive, so HALF_UP produces the same value.
     */
    public BigDecimal simpleInterest(BigDecimal principal, BigDecimal annualRate, BigDecimal years) {
        return principal.multiply(annualRate)
                .multiply(years)
                .divide(new BigDecimal("100"), 2, RoundingMode.HALF_UP);
    }

    public StatementResponse statement(long accountId) {
        CustomerProfile profile = single(jdbc.query(PROFILE_SQL, PROFILE_MAPPER, accountId));
        if (profile == null) {
            return null;
        }

        List<StatementLine> lines = jdbc.query("""
                SELECT transaction_id, transaction_ts, direction_code, amount, reference_code
                  FROM bank_transaction
                 WHERE account_id = ?
                 ORDER BY transaction_ts, transaction_id
                """, (resultSet, row) -> new StatementLine(
                resultSet.getLong("transaction_id"),
                localDateTime(resultSet.getTimestamp("transaction_ts")),
                trimmed(resultSet.getString("direction_code")),
                resultSet.getBigDecimal("amount"),
                resultSet.getString("reference_code")), accountId);

        return new StatementResponse(profile, currentBalance(accountId), lines);
    }

    @Transactional
    public TransactionCreated recordTransaction(
            long accountId, BigDecimal amount, String directionCode, String referenceCode) {
        long transactionId = nextValue(BankingSequences.TRANSACTION_SEQUENCE);

        jdbc.update("""
                INSERT INTO bank_transaction
                    (transaction_id, account_id, transaction_ts, amount, reference_code, direction_code)
                VALUES
                    (?, ?, localtimestamp, ?, ?, ?)
                """, transactionId, accountId, amount, referenceCode, directionCode);

        LocalDateTime transactionTs = single(jdbc.query(
                "SELECT transaction_ts FROM bank_transaction WHERE transaction_id = ?",
                (resultSet, row) -> localDateTime(resultSet.getTimestamp(1)),
                transactionId));

        if (transactionTs == null) {
            return null;
        }

        return new TransactionCreated(
                transactionId, transactionTs, directionCode, amount, referenceCode, currentBalance(accountId));
    }

    public List<AccountRequestSummary> listAccountRequests(String requestStatus) {
        return jdbc.query("""
                SELECT request_id, branch_code, account_kind, honorific, given_name, family_name,
                       date_of_birth, email_address, request_status, submitted_at, decided_at
                  FROM bank_account_request
                 WHERE request_status = ?
                 ORDER BY submitted_at, request_id
                """, (resultSet, row) -> new AccountRequestSummary(
                resultSet.getLong("request_id"),
                resultSet.getString("branch_code"),
                resultSet.getString("account_kind"),
                resultSet.getString("honorific"),
                resultSet.getString("given_name"),
                resultSet.getString("family_name"),
                startOfDay(resultSet, "date_of_birth"),
                resultSet.getString("email_address"),
                trimmed(resultSet.getString("request_status")),
                localDateTime(resultSet.getTimestamp("submitted_at")),
                localDateTime(resultSet.getTimestamp("decided_at"))), requestStatus);
    }

    /**
     * LEGACY_BANKING_API.APPROVE_REQUEST, reimplemented. The row is locked before it is read, so two
     * managers cannot both see SUBMITTED, and the account insert and the status update share one
     * transaction, so an approved request can never exist without its account.
     */
    @Transactional
    public Approval approve(long requestId) {
        PendingRequest request = single(jdbc.query("""
                SELECT branch_code, account_kind, request_status
                  FROM bank_account_request
                 WHERE request_id = ?
                   FOR UPDATE
                """, (resultSet, row) -> new PendingRequest(
                resultSet.getString("branch_code"),
                resultSet.getString("account_kind"),
                trimmed(resultSet.getString("request_status"))), requestId));

        if (request == null) {
            return new Approval(ApprovalOutcome.REQUEST_NOT_FOUND, null);
        }

        if (!"SUBMITTED".equals(request.requestStatus())) {
            return new Approval(ApprovalOutcome.ALREADY_DECIDED, null);
        }

        long accountId = nextValue(BankingSequences.ACCOUNT_SEQUENCE);

        jdbc.update("""
                INSERT INTO bank_account
                    (account_id, request_id, branch_code, account_kind, opened_on,
                     online_enabled, online_password_hash)
                VALUES
                    (?, ?, ?, ?, current_date, 'N', NULL)
                """, accountId, requestId, request.branchCode(), request.accountKind());

        jdbc.update("""
                UPDATE bank_account_request
                   SET request_status = 'APPROVED',
                       decided_at = localtimestamp
                 WHERE request_id = ?
                """, requestId);

        return new Approval(ApprovalOutcome.APPROVED, accountId);
    }

    /** LEGACY_BANKING_API.CURRENT_BALANCE: credits add, debits subtract, no rows means zero. */
    private BigDecimal currentBalance(long accountId) {
        BigDecimal balance = jdbc.queryForObject("""
                SELECT COALESCE(SUM(CASE direction_code WHEN 'CR' THEN amount WHEN 'DR' THEN -amount END), 0)
                  FROM bank_transaction
                 WHERE account_id = ?
                """, BigDecimal.class, accountId);

        return balance == null ? BigDecimal.ZERO : balance;
    }

    private long nextValue(String sequence) {
        Long value = jdbc.queryForObject("SELECT nextval('" + sequence + "')", Long.class);
        if (value == null) {
            throw new IllegalStateException("Sequence " + sequence + " did not return a value.");
        }

        return value;
    }

    private static <T> T single(List<T> rows) {
        return rows.isEmpty() ? null : rows.get(0);
    }

    private static CustomerProfile readProfile(ResultSet resultSet) throws SQLException {
        return new CustomerProfile(
                resultSet.getLong("account_id"),
                resultSet.getString("account_holder"),
                resultSet.getString("branch_code"),
                resultSet.getString("account_kind"));
    }

    private static LocalDateTime startOfDay(ResultSet resultSet, String column) throws SQLException {
        Date value = resultSet.getDate(column);
        return value == null ? null : value.toLocalDate().atStartOfDay();
    }

    private static LocalDateTime localDateTime(Timestamp value) {
        return value == null ? null : value.toLocalDateTime();
    }

    /** Fixed-length character columns arrive space padded; the source compared unpadded codes. */
    private static String trimmed(String value) {
        return value == null ? null : value.trim();
    }
}
