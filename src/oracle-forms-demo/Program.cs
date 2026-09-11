using System.Text.Json;
using Oracle.ManagedDataAccess.Client;
using OracleFormsDemo;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
});

builder.Services.AddSingleton<DemoSessionStore>();
builder.Services.AddSingleton<OracleBankingRepository>();

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BankingApi");

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new ErrorResponse("An unexpected error occurred."));
}));

if (Directory.Exists(app.Environment.WebRootPath ?? string.Empty))
{
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = context =>
            context.Context.Response.Headers.CacheControl = "no-cache"
    });
}

app.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok", "not-checked")));

app.MapGet("/api/health", async (OracleBankingRepository repository, CancellationToken cancellationToken) =>
{
    var available = await repository.IsDatabaseAvailableAsync(cancellationToken);
    return available
        ? Results.Ok(new HealthResponse("ok", "available"))
        : Results.Json(
            new HealthResponse("degraded", "unavailable"),
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapPost("/api/customer/login", (
    CustomerLoginRequest request,
    OracleBankingRepository repository,
    DemoSessionStore sessions,
    CancellationToken cancellationToken) =>
    Guarded(async () =>
    {
        var error = BankingValidation.ValidateAccountId(request.AccountId)
            ?? BankingValidation.ValidatePassword(request.Password);
        if (error is not null)
        {
            return BadRequest(error);
        }

        var profile = await repository.AuthenticateCustomerAsync(
            request.AccountId!.Value, request.Password!, cancellationToken);
        if (profile is null)
        {
            return Unauthorized("Invalid account number or password.");
        }

        var (token, session) = sessions.Create(SessionRole.Customer, profile.AccountId, profile.AccountHolder);
        return Results.Ok(new CustomerLoginResponse(token, session.ExpiresAt, profile));
    }));

app.MapPost("/api/manager/login", (
    ManagerLoginRequest request,
    OracleBankingRepository repository,
    DemoSessionStore sessions,
    CancellationToken cancellationToken) =>
    Guarded(async () =>
    {
        var error = BankingValidation.ValidateUsername(request.Username)
            ?? BankingValidation.ValidatePassword(request.Password);
        if (error is not null)
        {
            return BadRequest(error);
        }

        var username = await repository.AuthenticateManagerAsync(
            request.Username!.Trim(), request.Password!, cancellationToken);
        if (username is null)
        {
            return Unauthorized("Invalid username or password.");
        }

        var (token, session) = sessions.Create(SessionRole.Manager, null, username);
        return Results.Ok(new ManagerLoginResponse(token, session.ExpiresAt, username));
    }));

app.MapPost("/api/online-registration", (
    OnlineRegistrationRequest request,
    OracleBankingRepository repository,
    CancellationToken cancellationToken) =>
    Guarded(async () =>
    {
        var error = BankingValidation.ValidateAccountId(request.AccountId)
            ?? BankingValidation.ValidateEmail(request.EmailAddress)
            ?? BankingValidation.ValidatePassword(request.Password);
        if (error is not null)
        {
            return BadRequest(error);
        }

        var outcome = await repository.EnableOnlineAccessAsync(
            request.AccountId!.Value, request.EmailAddress!.Trim(), request.Password!, cancellationToken);

        return outcome switch
        {
            RegistrationOutcome.Enabled => Results.Ok(new OnlineRegistrationResponse(request.AccountId.Value, true)),
            RegistrationOutcome.AlreadyEnabled => Conflict("Online banking is already enabled for this account."),
            _ => NotFound("No account matches that account number and email address.")
        };
    }));

app.MapPost("/api/account-requests", (
    AccountRequestSubmission request,
    OracleBankingRepository repository,
    CancellationToken cancellationToken) =>
    Guarded(async () =>
    {
        var error = BankingValidation.ValidateAccountRequest(request, out var normalized);
        if (error is not null)
        {
            return BadRequest(error);
        }

        var created = await repository.CreateAccountRequestAsync(normalized, cancellationToken);
        return Results.Created($"/api/account-requests/{created.RequestId}", created);
    }));

app.MapPost("/api/interest", (
    InterestRequest request,
    OracleBankingRepository repository,
    CancellationToken cancellationToken) =>
    Guarded(async () =>
    {
        var error = BankingValidation.ValidateInterest(request);
        if (error is not null)
        {
            return BadRequest(error);
        }

        var interest = await repository.CalculateSimpleInterestAsync(
            request.Principal!.Value, request.AnnualRate!.Value, request.Years!.Value, cancellationToken);

        return interest is { } value
            ? Results.Ok(new InterestResponse(
                request.Principal.Value,
                request.AnnualRate.Value,
                request.Years.Value,
                value,
                request.Principal.Value + value))
            : Unavailable();
    }));

app.MapGet("/api/customer/statement", (
    HttpContext http,
    OracleBankingRepository repository,
    DemoSessionStore sessions,
    CancellationToken cancellationToken) =>
    Guarded(async () =>
    {
        if (Authorize(http, sessions, SessionRole.Customer) is not { AccountId: { } accountId })
        {
            return Unauthorized("A valid customer session is required.");
        }

        var statement = await repository.GetStatementAsync(accountId, cancellationToken);
        return statement is null ? NotFound("Account not found.") : Results.Ok(statement);
    }));

app.MapPost("/api/customer/transactions", (
    TransactionRequest request,
    HttpContext http,
    OracleBankingRepository repository,
    DemoSessionStore sessions,
    CancellationToken cancellationToken) =>
    Guarded(async () =>
    {
        if (Authorize(http, sessions, SessionRole.Customer) is not { AccountId: { } accountId })
        {
            return Unauthorized("A valid customer session is required.");
        }

        var amountError = BankingValidation.ValidateAmount(request.Amount);
        if (amountError is not null)
        {
            return BadRequest(amountError);
        }

        var directionError = BankingValidation.ValidateDirectionCode(request.DirectionCode, out var directionCode);
        if (directionError is not null)
        {
            return BadRequest(directionError);
        }

        var referenceError = BankingValidation.ValidateReferenceCode(request.ReferenceCode, out var referenceCode);
        if (referenceError is not null)
        {
            return BadRequest(referenceError);
        }

        var created = await repository.RecordTransactionAsync(
            accountId, request.Amount!.Value, directionCode, referenceCode, cancellationToken);

        return created is null
            ? Unavailable()
            : Results.Created($"/api/customer/transactions/{created.TransactionId}", created);
    }));

app.MapGet("/api/manager/requests", (
    string? status,
    HttpContext http,
    OracleBankingRepository repository,
    DemoSessionStore sessions,
    CancellationToken cancellationToken) =>
    Guarded(async () =>
    {
        if (Authorize(http, sessions, SessionRole.Manager) is null)
        {
            return Unauthorized("A valid manager session is required.");
        }

        var error = BankingValidation.ValidateRequestStatus(status, out var requestStatus);
        if (error is not null)
        {
            return BadRequest(error);
        }

        var requests = await repository.ListAccountRequestsAsync(requestStatus, cancellationToken);
        return Results.Ok(requests);
    }));

app.MapPost("/api/manager/requests/{requestId:long}/approve", (
    long requestId,
    HttpContext http,
    OracleBankingRepository repository,
    DemoSessionStore sessions,
    CancellationToken cancellationToken) =>
    Guarded(async () =>
    {
        if (Authorize(http, sessions, SessionRole.Manager) is null)
        {
            return Unauthorized("A valid manager session is required.");
        }

        if (requestId <= 0)
        {
            return BadRequest("requestId must be a positive request number.");
        }

        var (outcome, accountId) = await repository.ApproveRequestAsync(requestId, cancellationToken);
        return outcome switch
        {
            ApprovalOutcome.Approved when accountId is { } id =>
                Results.Ok(new ApprovalResponse(requestId, id, "APPROVED")),
            ApprovalOutcome.AlreadyDecided => Conflict("This request has already been decided."),
            ApprovalOutcome.RequestNotFound => NotFound("Account request not found."),
            _ => Unavailable()
        };
    }));

app.MapDelete("/api/session", (HttpContext http, DemoSessionStore sessions) =>
{
    sessions.Revoke(ReadBearerToken(http));
    return Results.NoContent();
});

app.Map("/api/{**path}", () => Results.NotFound(new ErrorResponse("API endpoint not found.")));

if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html")))
{
    app.MapFallbackToFile("index.html");
}

app.Run();

// Database outages surface as a generic 503 so no connection or exception detail reaches the client.
async Task<IResult> Guarded(Func<Task<IResult>> operation)
{
    try
    {
        return await operation();
    }
    catch (OracleException exception)
    {
        logger.LogError(exception, "Oracle operation failed with error number {ErrorNumber}.", exception.Number);
        return Unavailable();
    }
    catch (InvalidOperationException exception)
    {
        logger.LogError(exception, "Banking backend is not configured correctly.");
        return Unavailable();
    }
}

static DemoSession? Authorize(HttpContext http, DemoSessionStore sessions, SessionRole role)
{
    var session = sessions.Resolve(ReadBearerToken(http));
    return session is not null && session.Role == role ? session : null;
}

static string? ReadBearerToken(HttpContext http)
{
    var header = http.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? header["Bearer ".Length..].Trim()
        : null;
}

static IResult BadRequest(string message) =>
    Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status400BadRequest);

static IResult Unauthorized(string message) =>
    Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status401Unauthorized);

static IResult NotFound(string message) =>
    Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status404NotFound);

static IResult Conflict(string message) =>
    Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status409Conflict);

static IResult Unavailable() =>
    Results.Json(
        new ErrorResponse("The banking service is temporarily unavailable."),
        statusCode: StatusCodes.Status503ServiceUnavailable);

public partial class Program;
