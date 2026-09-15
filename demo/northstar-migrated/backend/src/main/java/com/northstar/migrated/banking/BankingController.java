package com.northstar.migrated.banking;

import com.northstar.migrated.banking.BankingContracts.AccountRequestCreated;
import com.northstar.migrated.banking.BankingContracts.AccountRequestSubmission;
import com.northstar.migrated.banking.BankingContracts.AccountRequestSummary;
import com.northstar.migrated.banking.BankingContracts.ApprovalResponse;
import com.northstar.migrated.banking.BankingContracts.CustomerLoginRequest;
import com.northstar.migrated.banking.BankingContracts.CustomerLoginResponse;
import com.northstar.migrated.banking.BankingContracts.CustomerProfile;
import com.northstar.migrated.banking.BankingContracts.ErrorResponse;
import com.northstar.migrated.banking.BankingContracts.HealthResponse;
import com.northstar.migrated.banking.BankingContracts.InterestRequest;
import com.northstar.migrated.banking.BankingContracts.InterestResponse;
import com.northstar.migrated.banking.BankingContracts.ManagerLoginRequest;
import com.northstar.migrated.banking.BankingContracts.ManagerLoginResponse;
import com.northstar.migrated.banking.BankingContracts.OnlineRegistrationRequest;
import com.northstar.migrated.banking.BankingContracts.OnlineRegistrationResponse;
import com.northstar.migrated.banking.BankingContracts.StatementResponse;
import com.northstar.migrated.banking.BankingContracts.TransactionCreated;
import com.northstar.migrated.banking.BankingContracts.TransactionRequest;
import com.northstar.migrated.banking.BankingRepository.Approval;
import com.northstar.migrated.banking.BankingRepository.ApprovalOutcome;
import com.northstar.migrated.banking.BankingRepository.RegistrationOutcome;
import com.northstar.migrated.banking.BankingSessionStore.Issued;
import com.northstar.migrated.banking.BankingSessionStore.Role;
import com.northstar.migrated.banking.BankingSessionStore.Session;
import com.northstar.migrated.banking.BankingValidation.Normalized;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.dao.DataAccessException;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.DeleteMapping;
import org.springframework.web.bind.annotation.ExceptionHandler;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

import java.math.BigDecimal;
import java.net.URI;
import java.util.List;

/**
 * The source application's HTTP surface, path for path and status code for status code, so the migrated
 * browser client needs no change. A database failure surfaces as a bare 503: no connection string,
 * driver message, or stack frame reaches the caller.
 */
@RestController
public class BankingController {

    private static final Logger LOG = LoggerFactory.getLogger(BankingController.class);
    private static final String BEARER = "Bearer ";

    private final BankingRepository repository;
    private final BankingSessionStore sessions;

    public BankingController(BankingRepository repository, BankingSessionStore sessions) {
        this.repository = repository;
        this.sessions = sessions;
    }

    @GetMapping("/healthz")
    public ResponseEntity<HealthResponse> liveness() {
        // Liveness only: the process is up and serving. It deliberately does not touch PostgreSQL, so
        // a database outage does not make an orchestrator restart a container that is working fine.
        return ResponseEntity.ok(new HealthResponse("ok", "not checked"));
    }

    @GetMapping("/api/health")
    public ResponseEntity<HealthResponse> health() {
        return repository.databaseAvailable()
                ? ResponseEntity.ok(new HealthResponse("ok", "available"))
                : ResponseEntity.status(HttpStatus.SERVICE_UNAVAILABLE)
                        .body(new HealthResponse("degraded", "unavailable"));
    }

    @PostMapping("/api/customer/login")
    public ResponseEntity<?> customerLogin(@RequestBody CustomerLoginRequest request) {
        String error = BankingValidation.validateAccountId(request.accountId());
        if (error == null) {
            error = BankingValidation.validatePassword(request.password());
        }
        if (error != null) {
            return badRequest(error);
        }

        CustomerProfile profile = repository.authenticateCustomer(request.accountId(), request.password());
        if (profile == null) {
            return unauthorized("Invalid account number or password.");
        }

        Issued issued = sessions.create(Role.CUSTOMER, profile.accountId(), profile.accountHolder());
        return ResponseEntity.ok(
                new CustomerLoginResponse(issued.token(), issued.session().expiresAt(), profile));
    }

    @PostMapping("/api/manager/login")
    public ResponseEntity<?> managerLogin(@RequestBody ManagerLoginRequest request) {
        String error = BankingValidation.validateUsername(request.username());
        if (error == null) {
            error = BankingValidation.validatePassword(request.password());
        }
        if (error != null) {
            return badRequest(error);
        }

        String username = repository.authenticateManager(request.username().trim(), request.password());
        if (username == null) {
            return unauthorized("Invalid username or password.");
        }

        Issued issued = sessions.create(Role.MANAGER, null, username);
        return ResponseEntity.ok(
                new ManagerLoginResponse(issued.token(), issued.session().expiresAt(), username));
    }

    @PostMapping("/api/online-registration")
    public ResponseEntity<?> onlineRegistration(@RequestBody OnlineRegistrationRequest request) {
        String error = BankingValidation.validateAccountId(request.accountId());
        if (error == null) {
            error = BankingValidation.validateEmail(request.emailAddress());
        }
        if (error == null) {
            error = BankingValidation.validatePassword(request.password());
        }
        if (error != null) {
            return badRequest(error);
        }

        RegistrationOutcome outcome = repository.enableOnlineAccess(
                request.accountId(), request.emailAddress().trim(), request.password());

        if (outcome == RegistrationOutcome.ENABLED) {
            return ResponseEntity.ok(new OnlineRegistrationResponse(request.accountId(), true));
        }

        return outcome == RegistrationOutcome.ALREADY_ENABLED
                ? conflict("Online banking is already enabled for this account.")
                : notFound("No account matches that account number and email address.");
    }

    @PostMapping("/api/account-requests")
    public ResponseEntity<?> submitAccountRequest(@RequestBody AccountRequestSubmission request) {
        Normalized<AccountRequestSubmission> normalized = BankingValidation.accountRequest(request);
        if (normalized.isRejected()) {
            return badRequest(normalized.error());
        }

        AccountRequestCreated created = repository.createAccountRequest(normalized.value());
        return ResponseEntity.created(URI.create("/api/account-requests/" + created.requestId())).body(created);
    }

    @PostMapping("/api/interest")
    public ResponseEntity<?> interest(@RequestBody InterestRequest request) {
        String error = BankingValidation.validateInterest(request);
        if (error != null) {
            return badRequest(error);
        }

        BigDecimal value = repository.simpleInterest(request.principal(), request.annualRate(), request.years());
        return ResponseEntity.ok(new InterestResponse(
                request.principal(), request.annualRate(), request.years(), value,
                request.principal().add(value)));
    }

    @GetMapping("/api/customer/statement")
    public ResponseEntity<?> statement(
            @RequestHeader(name = "Authorization", required = false) String authorization) {
        Session session = authorize(authorization, Role.CUSTOMER);
        if (session == null || session.accountId() == null) {
            return unauthorized("A valid customer session is required.");
        }

        StatementResponse statement = repository.statement(session.accountId());
        return statement == null ? notFound("Account not found.") : ResponseEntity.ok(statement);
    }

    @PostMapping("/api/customer/transactions")
    public ResponseEntity<?> postTransaction(
            @RequestBody TransactionRequest request,
            @RequestHeader(name = "Authorization", required = false) String authorization) {
        Session session = authorize(authorization, Role.CUSTOMER);
        if (session == null || session.accountId() == null) {
            return unauthorized("A valid customer session is required.");
        }

        String amountError = BankingValidation.validateAmount(request.amount());
        if (amountError != null) {
            return badRequest(amountError);
        }

        Normalized<String> direction = BankingValidation.directionCode(request.directionCode());
        if (direction.isRejected()) {
            return badRequest(direction.error());
        }

        Normalized<String> reference = BankingValidation.referenceCode(request.referenceCode());
        if (reference.isRejected()) {
            return badRequest(reference.error());
        }

        TransactionCreated created = repository.recordTransaction(
                session.accountId(), request.amount(), direction.value(), reference.value());

        if (created == null) {
            return unavailable();
        }

        return ResponseEntity.created(URI.create("/api/customer/transactions/" + created.transactionId()))
                .body(created);
    }

    @GetMapping("/api/manager/requests")
    public ResponseEntity<?> managerRequests(
            @RequestParam(name = "status", required = false) String status,
            @RequestHeader(name = "Authorization", required = false) String authorization) {
        if (authorize(authorization, Role.MANAGER) == null) {
            return unauthorized("A valid manager session is required.");
        }

        Normalized<String> requestStatus = BankingValidation.requestStatus(status);
        if (requestStatus.isRejected()) {
            return badRequest(requestStatus.error());
        }

        List<AccountRequestSummary> requests = repository.listAccountRequests(requestStatus.value());
        return ResponseEntity.ok(requests);
    }

    @PostMapping("/api/manager/requests/{requestId}/approve")
    public ResponseEntity<?> approve(
            @PathVariable long requestId,
            @RequestHeader(name = "Authorization", required = false) String authorization) {
        if (authorize(authorization, Role.MANAGER) == null) {
            return unauthorized("A valid manager session is required.");
        }

        if (requestId <= 0L) {
            return badRequest("requestId must be a positive request number.");
        }

        Approval approval = repository.approve(requestId);
        if (approval.outcome() == ApprovalOutcome.APPROVED && approval.accountId() != null) {
            return ResponseEntity.ok(new ApprovalResponse(requestId, approval.accountId(), "APPROVED"));
        }

        return approval.outcome() == ApprovalOutcome.ALREADY_DECIDED
                ? conflict("This request has already been decided.")
                : notFound("Account request not found.");
    }

    @DeleteMapping("/api/session")
    public ResponseEntity<Void> signOut(
            @RequestHeader(name = "Authorization", required = false) String authorization) {
        sessions.revoke(bearerToken(authorization));
        return ResponseEntity.noContent().build();
    }

    @ExceptionHandler(DataAccessException.class)
    public ResponseEntity<ErrorResponse> databaseFailure(DataAccessException cause) {
        LOG.error("A banking operation failed against PostgreSQL.", cause);
        return unavailable();
    }

    @ExceptionHandler(IllegalStateException.class)
    public ResponseEntity<ErrorResponse> misconfigured(IllegalStateException cause) {
        LOG.error("The banking backend is not configured correctly.", cause);
        return unavailable();
    }

    private Session authorize(String authorization, Role role) {
        Session session = sessions.resolve(bearerToken(authorization));
        return session != null && session.role() == role ? session : null;
    }

    private static String bearerToken(String authorization) {
        if (authorization == null || !authorization.regionMatches(true, 0, BEARER, 0, BEARER.length())) {
            return null;
        }

        return authorization.substring(BEARER.length()).trim();
    }

    private static ResponseEntity<ErrorResponse> badRequest(String message) {
        return ResponseEntity.badRequest().body(new ErrorResponse(message));
    }

    private static ResponseEntity<ErrorResponse> unauthorized(String message) {
        return ResponseEntity.status(HttpStatus.UNAUTHORIZED).body(new ErrorResponse(message));
    }

    private static ResponseEntity<ErrorResponse> notFound(String message) {
        return ResponseEntity.status(HttpStatus.NOT_FOUND).body(new ErrorResponse(message));
    }

    private static ResponseEntity<ErrorResponse> conflict(String message) {
        return ResponseEntity.status(HttpStatus.CONFLICT).body(new ErrorResponse(message));
    }

    private static ResponseEntity<ErrorResponse> unavailable() {
        return ResponseEntity.status(HttpStatus.SERVICE_UNAVAILABLE)
                .body(new ErrorResponse("The banking service is temporarily unavailable."));
    }
}
