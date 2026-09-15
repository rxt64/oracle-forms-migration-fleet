package com.northstar.migrated.banking;

import com.northstar.migrated.banking.BankingContracts.AccountRequestSubmission;
import com.northstar.migrated.banking.BankingContracts.InterestRequest;

import java.math.BigDecimal;
import java.time.LocalDate;
import java.time.Period;
import java.util.List;
import java.util.Locale;
import java.util.regex.Pattern;

/**
 * The source application's field rules, reproduced so a request rejected before is still rejected with
 * the same message. Bounds that also exist as column lengths or check constraints in the converted
 * schema are kept here too, because a 400 is a better answer than a constraint violation.
 */
public final class BankingValidation {

    public static final int MIN_PASSWORD_LENGTH = 8;
    public static final int MAX_PASSWORD_LENGTH = 64;
    public static final BigDecimal MAX_TRANSACTION_AMOUNT = new BigDecimal("1000000");

    private static final List<String> ACCOUNT_KINDS = List.of("SAVINGS", "CHECKING");
    private static final List<String> REQUEST_STATUSES = List.of("SUBMITTED", "APPROVED", "REJECTED");
    private static final Pattern REFERENCE_CODE_PATTERN = Pattern.compile("^[A-Za-z0-9][A-Za-z0-9/_-]*$");
    private static final Pattern EMAIL_PATTERN = Pattern.compile("^[^@\\s]+@[^@\\s.]+(\\.[^@\\s.]+)+$");
    private static final BigDecimal MAX_PRINCIPAL = new BigDecimal("100000000");
    private static final BigDecimal ONE_HUNDRED = new BigDecimal("100");

    private BankingValidation() {
    }

    /** A validated value, or the message explaining why the request was rejected. */
    public record Normalized<T>(String error, T value) {

        public static <T> Normalized<T> rejectedWith(String error) {
            return new Normalized<>(error, null);
        }

        public static <T> Normalized<T> accepted(T value) {
            return new Normalized<>(null, value);
        }

        public boolean isRejected() {
            return error != null;
        }
    }

    public static String validateAccountId(Long accountId) {
        return accountId == null || accountId <= 0L || accountId > 9_999_999_999L
                ? "accountId must be a positive account number."
                : null;
    }

    public static String validatePassword(String password) {
        return password == null
                || password.length() < MIN_PASSWORD_LENGTH
                || password.length() > MAX_PASSWORD_LENGTH
                ? "password must be between " + MIN_PASSWORD_LENGTH + " and " + MAX_PASSWORD_LENGTH + " characters."
                : null;
    }

    public static String validateEmail(String email) {
        return isBlank(email) || email.length() > 120 || !EMAIL_PATTERN.matcher(email.trim()).matches()
                ? "emailAddress must be a valid email address of at most 120 characters."
                : null;
    }

    public static String validateUsername(String username) {
        return isBlank(username) || username.trim().length() > 40
                ? "username is required and must be at most 40 characters."
                : null;
    }

    public static String validateAmount(BigDecimal amount) {
        if (amount == null || amount.signum() <= 0) {
            return "amount must be greater than zero.";
        }

        if (amount.compareTo(MAX_TRANSACTION_AMOUNT) > 0) {
            return "amount must not exceed "
                    + String.format(Locale.ROOT, "%,d", MAX_TRANSACTION_AMOUNT.longValue()) + ".";
        }

        return amount.stripTrailingZeros().scale() > 2 ? "amount must have at most two decimal places." : null;
    }

    public static Normalized<String> directionCode(String directionCode) {
        String normalized = directionCode == null ? "" : directionCode.trim().toUpperCase(Locale.ROOT);
        return "CR".equals(normalized) || "DR".equals(normalized)
                ? Normalized.accepted(normalized)
                : Normalized.rejectedWith("directionCode must be either 'CR' or 'DR'.");
    }

    public static Normalized<String> referenceCode(String referenceCode) {
        String normalized = referenceCode == null ? "" : referenceCode.trim();
        if (normalized.isEmpty() || normalized.length() > 30) {
            return Normalized.rejectedWith("referenceCode is required and must be at most 30 characters.");
        }

        return REFERENCE_CODE_PATTERN.matcher(normalized).matches()
                ? Normalized.accepted(normalized)
                : Normalized.rejectedWith("referenceCode may only contain letters, digits, '-', '_' and '/'.");
    }

    public static Normalized<String> requestStatus(String status) {
        String normalized = isBlank(status) ? "SUBMITTED" : status.trim().toUpperCase(Locale.ROOT);
        return REQUEST_STATUSES.contains(normalized)
                ? Normalized.accepted(normalized)
                : Normalized.rejectedWith("status must be one of " + String.join(", ", REQUEST_STATUSES) + ".");
    }

    public static String validateInterest(InterestRequest request) {
        if (request.principal() == null
                || request.principal().signum() <= 0
                || request.principal().compareTo(MAX_PRINCIPAL) > 0) {
            return "principal must be greater than zero and at most 100,000,000.";
        }

        if (request.annualRate() == null
                || request.annualRate().signum() <= 0
                || request.annualRate().compareTo(ONE_HUNDRED) > 0) {
            return "annualRate must be greater than zero and at most 100.";
        }

        if (request.years() == null
                || request.years().signum() <= 0
                || request.years().compareTo(ONE_HUNDRED) > 0) {
            return "years must be greater than zero and at most 100.";
        }

        return null;
    }

    public static Normalized<AccountRequestSubmission> accountRequest(AccountRequestSubmission request) {
        AccountRequestSubmission normalized = new AccountRequestSubmission(
                upper(request.branchCode()),
                upper(request.accountKind()),
                nullIfBlank(request.honorific()),
                trim(request.givenName()),
                trim(request.familyName()),
                request.dateOfBirth(),
                nullIfBlank(request.workPhone()),
                nullIfBlank(request.homePhone()),
                trim(request.streetAddress()),
                upper(request.regionCode()),
                trim(request.postalCode()),
                trim(request.emailAddress()));

        String error = required(normalized.branchCode(), "branchCode", 12);
        if (error == null && !ACCOUNT_KINDS.contains(normalized.accountKind())) {
            error = "accountKind must be one of " + String.join(", ", ACCOUNT_KINDS) + ".";
        }
        if (error == null) {
            error = optional(normalized.honorific(), "honorific", 8);
        }
        if (error == null) {
            error = required(normalized.givenName(), "givenName", 40);
        }
        if (error == null) {
            error = required(normalized.familyName(), "familyName", 40);
        }
        if (error == null) {
            error = validateDateOfBirth(normalized.dateOfBirth());
        }
        if (error == null) {
            error = optional(normalized.workPhone(), "workPhone", 20);
        }
        if (error == null) {
            error = optional(normalized.homePhone(), "homePhone", 20);
        }
        if (error == null) {
            error = required(normalized.streetAddress(), "streetAddress", 120);
        }
        if (error == null) {
            error = required(normalized.regionCode(), "regionCode", 20);
        }
        if (error == null) {
            error = required(normalized.postalCode(), "postalCode", 12);
        }
        if (error == null) {
            error = validateEmail(normalized.emailAddress());
        }

        return error == null ? Normalized.accepted(normalized) : Normalized.rejectedWith(error);
    }

    private static String validateDateOfBirth(LocalDate dateOfBirth) {
        if (dateOfBirth == null) {
            return "dateOfBirth is required.";
        }

        int age = Period.between(dateOfBirth, LocalDate.now()).getYears();
        return age < 18 || age > 120
                ? "dateOfBirth must belong to an applicant aged between 18 and 120."
                : null;
    }

    private static String required(String value, String field, int maxLength) {
        return isBlank(value) || value.length() > maxLength
                ? field + " is required and must be at most " + maxLength + " characters."
                : null;
    }

    private static String optional(String value, String field, int maxLength) {
        return value != null && !value.isEmpty() && value.length() > maxLength
                ? field + " must be at most " + maxLength + " characters."
                : null;
    }

    private static boolean isBlank(String value) {
        return value == null || value.trim().isEmpty();
    }

    private static String trim(String value) {
        return value == null ? null : value.trim();
    }

    private static String upper(String value) {
        return value == null ? null : value.trim().toUpperCase(Locale.ROOT);
    }

    private static String nullIfBlank(String value) {
        return isBlank(value) ? null : value.trim();
    }
}
