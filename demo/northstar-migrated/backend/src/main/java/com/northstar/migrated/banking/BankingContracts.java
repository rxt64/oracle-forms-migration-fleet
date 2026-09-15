package com.northstar.migrated.banking;

import java.math.BigDecimal;
import java.time.LocalDate;
import java.time.LocalDateTime;
import java.time.OffsetDateTime;
import java.util.List;

/**
 * The wire shapes the source application used, field for field. Serialised names are already camel
 * case, so the migrated browser client reads the same JSON it read before.
 */
public final class BankingContracts {

    private BankingContracts() {
    }

    public record CustomerLoginRequest(Long accountId, String password) {
    }

    public record ManagerLoginRequest(String username, String password) {
    }

    public record OnlineRegistrationRequest(Long accountId, String emailAddress, String password) {
    }

    public record AccountRequestSubmission(
            String branchCode,
            String accountKind,
            String honorific,
            String givenName,
            String familyName,
            LocalDate dateOfBirth,
            String workPhone,
            String homePhone,
            String streetAddress,
            String regionCode,
            String postalCode,
            String emailAddress) {
    }

    public record InterestRequest(BigDecimal principal, BigDecimal annualRate, BigDecimal years) {
    }

    public record TransactionRequest(BigDecimal amount, String directionCode, String referenceCode) {
    }

    public record ErrorResponse(String error) {
    }

    public record HealthResponse(String status, String database) {
    }

    public record CustomerProfile(long accountId, String accountHolder, String branchCode, String accountKind) {
    }

    public record CustomerLoginResponse(String token, OffsetDateTime expiresAt, CustomerProfile profile) {
    }

    public record ManagerLoginResponse(String token, OffsetDateTime expiresAt, String username) {
    }

    public record OnlineRegistrationResponse(long accountId, boolean onlineEnabled) {
    }

    public record AccountRequestCreated(long requestId, String requestStatus) {
    }

    public record InterestResponse(
            BigDecimal principal,
            BigDecimal annualRate,
            BigDecimal years,
            BigDecimal interest,
            BigDecimal total) {
    }

    public record StatementLine(
            long transactionId,
            LocalDateTime transactionTs,
            String directionCode,
            BigDecimal amount,
            String referenceCode) {
    }

    public record StatementResponse(
            CustomerProfile profile,
            BigDecimal currentBalance,
            List<StatementLine> transactions) {
    }

    public record TransactionCreated(
            long transactionId,
            LocalDateTime transactionTs,
            String directionCode,
            BigDecimal amount,
            String referenceCode,
            BigDecimal currentBalance) {
    }

    public record AccountRequestSummary(
            long requestId,
            String branchCode,
            String accountKind,
            String honorific,
            String givenName,
            String familyName,
            LocalDateTime dateOfBirth,
            String emailAddress,
            String requestStatus,
            LocalDateTime submittedAt,
            LocalDateTime decidedAt) {
    }

    public record ApprovalResponse(long requestId, long accountId, String requestStatus) {
    }
}
