using System.Globalization;
using System.Text.RegularExpressions;

namespace OracleFormsDemo;

// Requests

public sealed record CustomerLoginRequest(long? AccountId, string? Password);

public sealed record ManagerLoginRequest(string? Username, string? Password);

public sealed record OnlineRegistrationRequest(long? AccountId, string? EmailAddress, string? Password);

public sealed record AccountRequestSubmission(
    string? BranchCode,
    string? AccountKind,
    string? Honorific,
    string? GivenName,
    string? FamilyName,
    DateOnly? DateOfBirth,
    string? WorkPhone,
    string? HomePhone,
    string? StreetAddress,
    string? RegionCode,
    string? PostalCode,
    string? EmailAddress);

public sealed record InterestRequest(decimal? Principal, decimal? AnnualRate, decimal? Years);

public sealed record TransactionRequest(decimal? Amount, string? DirectionCode, string? ReferenceCode);

// Responses

public sealed record ErrorResponse(string Error);

public sealed record HealthResponse(string Status, string Database);

public sealed record CustomerProfile(long AccountId, string AccountHolder, string BranchCode, string AccountKind);

public sealed record CustomerLoginResponse(string Token, DateTimeOffset ExpiresAt, CustomerProfile Profile);

public sealed record ManagerLoginResponse(string Token, DateTimeOffset ExpiresAt, string Username);

public sealed record OnlineRegistrationResponse(long AccountId, bool OnlineEnabled);

public sealed record AccountRequestCreated(long RequestId, string RequestStatus);

public sealed record InterestResponse(decimal Principal, decimal AnnualRate, decimal Years, decimal Interest, decimal Total);

public sealed record StatementLine(
    long TransactionId,
    DateTime TransactionTs,
    string DirectionCode,
    decimal Amount,
    string ReferenceCode);

public sealed record StatementResponse(
    CustomerProfile Profile,
    decimal CurrentBalance,
    IReadOnlyList<StatementLine> Transactions);

public sealed record TransactionCreated(
    long TransactionId,
    DateTime TransactionTs,
    string DirectionCode,
    decimal Amount,
    string ReferenceCode,
    decimal CurrentBalance);

public sealed record AccountRequestSummary(
    long RequestId,
    string BranchCode,
    string AccountKind,
    string? Honorific,
    string GivenName,
    string FamilyName,
    DateTime DateOfBirth,
    string EmailAddress,
    string RequestStatus,
    DateTime SubmittedAt,
    DateTime? DecidedAt);

public sealed record ApprovalResponse(long RequestId, long AccountId, string RequestStatus);

/// <summary>Outcome of a mutation whose failure modes are domain states rather than exceptions.</summary>
public enum RegistrationOutcome
{
    Enabled,
    AccountNotFound,
    AlreadyEnabled
}

public enum ApprovalOutcome
{
    Approved,
    RequestNotFound,
    AlreadyDecided
}

public static class BankingValidation
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 64;
    public const decimal MaxTransactionAmount = 1_000_000m;

    private static readonly string[] AccountKinds = ["SAVINGS", "CHECKING"];
    private static readonly string[] RequestStatuses = ["SUBMITTED", "APPROVED", "REJECTED"];
    private static readonly Regex ReferenceCodePattern = new("^[A-Za-z0-9][A-Za-z0-9/_-]*$", RegexOptions.Compiled);
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$", RegexOptions.Compiled);

    public static string? ValidateAccountId(long? accountId) =>
        accountId is null or <= 0 or > 9_999_999_999 ? "accountId must be a positive account number." : null;

    public static string? ValidatePassword(string? password) =>
        password is null || password.Length < MinPasswordLength || password.Length > MaxPasswordLength
            ? $"password must be between {MinPasswordLength} and {MaxPasswordLength} characters."
            : null;

    public static string? ValidateEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) || email.Length > 120 || !EmailPattern.IsMatch(email.Trim())
            ? "emailAddress must be a valid email address of at most 120 characters."
            : null;

    public static string? ValidateUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) || username.Trim().Length > 40
            ? "username is required and must be at most 40 characters."
            : null;

    public static string? ValidateAmount(decimal? amount)
    {
        if (amount is not { } value || value <= 0m)
        {
            return "amount must be greater than zero.";
        }

        if (value > MaxTransactionAmount)
        {
            return $"amount must not exceed {MaxTransactionAmount.ToString("N0", CultureInfo.InvariantCulture)}.";
        }

        return decimal.Round(value, 2) != value ? "amount must have at most two decimal places." : null;
    }

    public static string? ValidateDirectionCode(string? directionCode, out string normalized)
    {
        normalized = directionCode?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized is "CR" or "DR" ? null : "directionCode must be either 'CR' or 'DR'.";
    }

    public static string? ValidateReferenceCode(string? referenceCode, out string normalized)
    {
        normalized = referenceCode?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 30)
        {
            return "referenceCode is required and must be at most 30 characters.";
        }

        return ReferenceCodePattern.IsMatch(normalized)
            ? null
            : "referenceCode may only contain letters, digits, '-', '_' and '/'.";
    }

    public static string? ValidateRequestStatus(string? status, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(status) ? "SUBMITTED" : status.Trim().ToUpperInvariant();
        return RequestStatuses.Contains(normalized)
            ? null
            : $"status must be one of {string.Join(", ", RequestStatuses)}.";
    }

    public static string? ValidateInterest(InterestRequest request)
    {
        if (request.Principal is not { } principal || principal <= 0m || principal > 100_000_000m)
        {
            return "principal must be greater than zero and at most 100,000,000.";
        }

        if (request.AnnualRate is not { } rate || rate <= 0m || rate > 100m)
        {
            return "annualRate must be greater than zero and at most 100.";
        }

        if (request.Years is not { } years || years <= 0m || years > 100m)
        {
            return "years must be greater than zero and at most 100.";
        }

        return null;
    }

    public static string? ValidateAccountRequest(AccountRequestSubmission request, out AccountRequestSubmission normalized)
    {
        normalized = new AccountRequestSubmission(
            request.BranchCode?.Trim().ToUpperInvariant(),
            request.AccountKind?.Trim().ToUpperInvariant(),
            NullIfBlank(request.Honorific),
            request.GivenName?.Trim(),
            request.FamilyName?.Trim(),
            request.DateOfBirth,
            NullIfBlank(request.WorkPhone),
            NullIfBlank(request.HomePhone),
            request.StreetAddress?.Trim(),
            request.RegionCode?.Trim().ToUpperInvariant(),
            request.PostalCode?.Trim(),
            request.EmailAddress?.Trim());

        return RequiredText(normalized.BranchCode, "branchCode", 12)
            ?? (AccountKinds.Contains(normalized.AccountKind)
                ? null
                : $"accountKind must be one of {string.Join(", ", AccountKinds)}.")
            ?? OptionalText(normalized.Honorific, "honorific", 8)
            ?? RequiredText(normalized.GivenName, "givenName", 40)
            ?? RequiredText(normalized.FamilyName, "familyName", 40)
            ?? ValidateDateOfBirth(normalized.DateOfBirth)
            ?? OptionalText(normalized.WorkPhone, "workPhone", 20)
            ?? OptionalText(normalized.HomePhone, "homePhone", 20)
            ?? RequiredText(normalized.StreetAddress, "streetAddress", 120)
            ?? RequiredText(normalized.RegionCode, "regionCode", 20)
            ?? RequiredText(normalized.PostalCode, "postalCode", 12)
            ?? ValidateEmail(normalized.EmailAddress);
    }

    private static string? ValidateDateOfBirth(DateOnly? dateOfBirth)
    {
        if (dateOfBirth is not { } dob)
        {
            return "dateOfBirth is required.";
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - dob.Year - (dob > today.AddYears(-(today.Year - dob.Year)) ? 1 : 0);
        return age is < 18 or > 120 ? "dateOfBirth must belong to an applicant aged between 18 and 120." : null;
    }

    private static string? RequiredText(string? value, string field, int maxLength) =>
        string.IsNullOrWhiteSpace(value) || value.Length > maxLength
            ? $"{field} is required and must be at most {maxLength} characters."
            : null;

    private static string? OptionalText(string? value, string field, int maxLength) =>
        value is { Length: > 0 } && value.Length > maxLength
            ? $"{field} must be at most {maxLength} characters."
            : null;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
