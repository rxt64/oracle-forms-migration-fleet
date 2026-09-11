using OracleFormsDemo;

namespace OracleFormsDemo.Tests;

public sealed class BankingValidationTests
{
    [Fact]
    public void AccountRequest_WithValidValues_IsAcceptedAndNormalized()
    {
        var request = ValidAccountRequest() with
        {
            BranchCode = "  sea-01 ",
            AccountKind = " savings ",
            GivenName = "  Ada ",
            EmailAddress = " ada@example.test "
        };

        var error = BankingValidation.ValidateAccountRequest(request, out var normalized);

        Assert.Null(error);
        Assert.Equal("SEA-01", normalized.BranchCode);
        Assert.Equal("SAVINGS", normalized.AccountKind);
        Assert.Equal("Ada", normalized.GivenName);
        Assert.Equal("ada@example.test", normalized.EmailAddress);
    }

    public static TheoryData<AccountRequestSubmission, string> InvalidAccountRequests => new()
    {
        { ValidAccountRequest() with { BranchCode = new string('B', 13) }, "branchCode" },
        { ValidAccountRequest() with { AccountKind = "INVESTMENT" }, "accountKind" },
        { ValidAccountRequest() with { Honorific = new string('H', 9) }, "honorific" },
        { ValidAccountRequest() with { GivenName = new string('G', 41) }, "givenName" },
        { ValidAccountRequest() with { FamilyName = " " }, "familyName" },
        { ValidAccountRequest() with { DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-10)) }, "dateOfBirth" },
        { ValidAccountRequest() with { WorkPhone = new string('1', 21) }, "workPhone" },
        { ValidAccountRequest() with { HomePhone = new string('2', 21) }, "homePhone" },
        { ValidAccountRequest() with { StreetAddress = new string('S', 121) }, "streetAddress" },
        { ValidAccountRequest() with { RegionCode = new string('R', 21) }, "regionCode" },
        { ValidAccountRequest() with { PostalCode = new string('P', 13) }, "postalCode" },
        { ValidAccountRequest() with { EmailAddress = "not-an-email" }, "emailAddress" }
    };

    [Theory]
    [MemberData(nameof(InvalidAccountRequests))]
    public void AccountRequest_WithInvalidValue_ReturnsUsefulError(
        AccountRequestSubmission request,
        string expectedField)
    {
        var error = BankingValidation.ValidateAccountRequest(request, out _);

        Assert.NotNull(error);
        Assert.Contains(expectedField, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9_999_999_999)]
    public void AccountId_WithinBounds_IsAccepted(long accountId) =>
        Assert.Null(BankingValidation.ValidateAccountId(accountId));

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(10_000_000_000L)]
    public void AccountId_OutsideBounds_IsRejected(long? accountId) =>
        Assert.NotEmpty(BankingValidation.ValidateAccountId(accountId)!);

    [Theory]
    [InlineData("person@example.test", true)]
    [InlineData("person@localhost", false)]
    [InlineData("", false)]
    public void Email_RequiresAValidBoundedAddress(string email, bool valid) =>
        Assert.Equal(valid, BankingValidation.ValidateEmail(email) is null);

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void RegistrationPassword_EnforcesLengthBounds(int length, bool valid) =>
        Assert.Equal(valid, BankingValidation.ValidatePassword(new string('p', length)) is null);

    [Fact]
    public void Interest_WithPositiveBoundedValues_IsAccepted() =>
        Assert.Null(BankingValidation.ValidateInterest(new InterestRequest(100_000_000m, 100m, 100m)));

    [Theory]
    [InlineData("principal")]
    [InlineData("annualRate")]
    [InlineData("years")]
    public void Interest_WithNonPositiveOrExcessiveValue_IsRejected(string field)
    {
        var request = field switch
        {
            "principal" => new InterestRequest(0m, 5m, 1m),
            "annualRate" => new InterestRequest(100m, 100.01m, 1m),
            _ => new InterestRequest(100m, 5m, -1m)
        };

        var error = BankingValidation.ValidateInterest(request);

        Assert.NotNull(error);
        Assert.Contains(field, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("0.01", true)]
    [InlineData("1000000", true)]
    [InlineData("1000000.01", false)]
    [InlineData("1.001", false)]
    public void TransactionAmount_EnforcesPositiveMaximumAndScale(string value, bool valid) =>
        Assert.Equal(valid, BankingValidation.ValidateAmount(decimal.Parse(value)) is null);

    [Theory]
    [InlineData(" cr ", "CR", true)]
    [InlineData("DR", "DR", true)]
    [InlineData("XX", "XX", false)]
    [InlineData(null, "", false)]
    public void TransactionDirection_IsValidatedAndNormalized(string? value, string expected, bool valid)
    {
        var error = BankingValidation.ValidateDirectionCode(value, out var normalized);

        Assert.Equal(expected, normalized);
        Assert.Equal(valid, error is null);
    }

    [Theory]
    [InlineData(" invoice/2026-09 ", "invoice/2026-09", true)]
    [InlineData("bad reference", "bad reference", false)]
    [InlineData("", "", false)]
    public void TransactionReference_IsValidatedAndNormalized(string value, string expected, bool valid)
    {
        var error = BankingValidation.ValidateReferenceCode(value, out var normalized);

        Assert.Equal(expected, normalized);
        Assert.Equal(valid, error is null);
    }

    private static AccountRequestSubmission ValidAccountRequest() => new(
        "SEA-01",
        "CHECKING",
        "Dr",
        "Ada",
        "Lovelace",
        DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-30)),
        "+1-555-0100",
        null,
        "1 Local Test Way",
        "WA",
        "98000",
        "ada@example.test");
}