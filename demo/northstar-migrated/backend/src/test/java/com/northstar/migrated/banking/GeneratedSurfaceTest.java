package com.northstar.migrated.banking;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.WebMvcTest;
import org.springframework.boot.test.mock.mockito.MockBean;
import org.springframework.web.servlet.mvc.method.RequestMappingInfo;
import org.springframework.web.servlet.mvc.method.annotation.RequestMappingHandlerMapping;

import java.util.List;
import java.util.Set;
import java.util.stream.Collectors;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * Holds the generated HTTP surface to what the workflow service authorises.
 *
 * The generic emitter would have published a REST controller per table: every column of every row
 * readable without a session, and unvalidated writes accepted on the same paths. For a schema whose
 * tables hold password hashes that is not a rough edge, it is a disclosure. None of those controllers
 * is generated, and this asserts it twice — the classes are not on the classpath, and no path under
 * their routes is mapped in a live application context.
 */
@WebMvcTest
class GeneratedSurfaceTest {

    private static final List<String> ABSENT_CONTROLLERS = List.of(
    "com.northstar.migrated.api.BankAccountController",
            "com.northstar.migrated.api.BankAccountRequestController",
            "com.northstar.migrated.api.BankStaffUserController",
            "com.northstar.migrated.api.BankTransactionController");

    private static final List<String> ABSENT_ROUTES = List.of(
    "/api/bank-account",
            "/api/bank-account-request",
            "/api/bank-staff-user",
            "/api/bank-transaction");

    @Autowired
    private RequestMappingHandlerMapping mappings;

    @MockBean
    private BankingRepository repository;

    @MockBean
    private BankingSessionStore sessions;

    @Test
    void no_per_table_crud_controller_was_generated() {
        for (String className : ABSENT_CONTROLLERS) {
            assertThrows(
                    ClassNotFoundException.class,
                    () -> Class.forName(className),
                    className + " must not be generated: it would expose every column of its table without "
                            + "a session or role check.");
        }
    }

    @Test
    void no_per_table_route_is_mapped() {
        Set<String> patterns = mappings.getHandlerMethods().keySet().stream()
                .map(RequestMappingInfo::getPathPatternsCondition)
                .filter(condition -> condition != null)
                .flatMap(condition -> condition.getPatternValues().stream())
                .collect(Collectors.toSet());

        for (String route : ABSENT_ROUTES) {
            assertFalse(
                    patterns.contains(route),
                    route + " must not be mapped: the workflow routes are the only authorised surface.");
        }
    }

    @Test
    void the_workflow_routes_are_mapped() {
        Set<String> patterns = mappings.getHandlerMethods().keySet().stream()
                .map(RequestMappingInfo::getPathPatternsCondition)
                .filter(condition -> condition != null)
                .flatMap(condition -> condition.getPatternValues().stream())
                .collect(Collectors.toSet());

        assertTrue(patterns.contains("/healthz"), "The liveness route must be mapped.");
        assertTrue(patterns.contains("/api/health"), "The readiness route must be mapped.");
        assertTrue(patterns.contains("/api/**"), "The JSON 404 fallback must be mapped.");
    }
}
