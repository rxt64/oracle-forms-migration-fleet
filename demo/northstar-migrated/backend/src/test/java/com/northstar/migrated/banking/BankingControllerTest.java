package com.northstar.migrated.banking;

import com.northstar.migrated.banking.BankingSessionStore.Role;
import com.northstar.migrated.banking.BankingSessionStore.Session;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.WebMvcTest;
import org.springframework.boot.test.mock.mockito.MockBean;
import org.springframework.http.MediaType;
import org.springframework.test.web.servlet.MockMvc;

import java.time.OffsetDateTime;

import static org.mockito.ArgumentMatchers.anyLong;
import static org.mockito.ArgumentMatchers.anyString;
import static org.mockito.Mockito.never;
import static org.mockito.Mockito.verify;
import static org.mockito.Mockito.when;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.delete;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.content;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.forwardedUrl;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

/**
 * Exercises the generated HTTP surface through the real Spring dispatcher, with the data and session
 * layers mocked. These are assertions about behaviour, not about the text of the generated source: a
 * route that stopped being mapped, a validation rule that stopped rejecting, or a role check that
 * stopped being enforced fails here even though the file still contains the same words.
 *
 * No controller is named, so every controller the application declares is loaded. A per-table CRUD
 * controller reintroduced beside these routes would be loaded too, and would fail this context for
 * want of its repository.
 */
@WebMvcTest
class BankingControllerTest {

    private static final String CUSTOMER_TOKEN = "customer-token";
    private static final String MANAGER_TOKEN = "manager-token";

    @Autowired
    private MockMvc mvc;

    @MockBean
    private BankingRepository repository;

    @MockBean
    private BankingSessionStore sessions;

    @Test
    void liveness_reports_up_without_touching_the_database() throws Exception {
        mvc.perform(get("/healthz"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.status").value("ok"));

        verify(repository, never()).databaseAvailable();
    }

    @Test
    void readiness_reports_the_database() throws Exception {
        when(repository.databaseAvailable()).thenReturn(true);

        mvc.perform(get("/api/health"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.database").value("available"));
    }

    @Test
    void readiness_is_unavailable_when_the_database_is() throws Exception {
        when(repository.databaseAvailable()).thenReturn(false);

        mvc.perform(get("/api/health"))
                .andExpect(status().isServiceUnavailable())
                .andExpect(jsonPath("$.status").value("degraded"));
    }

    @Test
    void an_unknown_api_path_is_a_json_404() throws Exception {
        mvc.perform(get("/api/no-such-thing"))
                .andExpect(status().isNotFound())
                .andExpect(content().contentTypeCompatibleWith(MediaType.APPLICATION_JSON))
                .andExpect(jsonPath("$.error").value("No such endpoint."));
    }

    @Test
    void an_unknown_api_path_is_a_json_404_for_every_method() throws Exception {
        mvc.perform(post("/api/bank-account").contentType(MediaType.APPLICATION_JSON).content("{}"))
                .andExpect(status().isNotFound())
                .andExpect(jsonPath("$.error").value("No such endpoint."));
    }

    @Test
    void a_browser_route_is_served_by_the_client_shell() throws Exception {
        mvc.perform(get("/statement"))
                .andExpect(status().isOk())
                .andExpect(forwardedUrl("/index.html"));
    }

    @Test
    void the_client_shell_never_intercepts_the_api() throws Exception {
        mvc.perform(get("/api"))
                .andExpect(status().isNotFound())
                .andExpect(jsonPath("$.error").value("No such endpoint."));
    }

    @Test
    void a_rejected_login_never_reaches_the_database() throws Exception {
        mvc.perform(post("/api/customer/login")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"accountId\":500001,\"password\":\"short\"}"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.error").exists());

        verify(repository, never()).authenticateCustomer(anyLong(), anyString());
    }

    @Test
    void wrong_credentials_are_unauthorized_rather_than_not_found() throws Exception {
        when(repository.authenticateCustomer(500001L, "demo1234")).thenReturn(null);

        mvc.perform(post("/api/customer/login")
                        .contentType(MediaType.APPLICATION_JSON)
                        .content("{\"accountId\":500001,\"password\":\"demo1234\"}"))
                .andExpect(status().isUnauthorized());
    }

    @Test
    void a_customer_session_opens_the_statement() throws Exception {
        when(sessions.resolve(CUSTOMER_TOKEN)).thenReturn(customerSession());
        when(repository.statement(500001L)).thenReturn(null);

        mvc.perform(get("/api/customer/statement").header("Authorization", "Bearer " + CUSTOMER_TOKEN))
                .andExpect(status().isNotFound());
    }

    @Test
    void the_statement_is_closed_without_a_session() throws Exception {
        mvc.perform(get("/api/customer/statement"))
                .andExpect(status().isUnauthorized());

        verify(repository, never()).statement(anyLong());
    }

    @Test
    void a_customer_cannot_use_a_manager_route() throws Exception {
        when(sessions.resolve(CUSTOMER_TOKEN)).thenReturn(customerSession());

        mvc.perform(get("/api/manager/requests").header("Authorization", "Bearer " + CUSTOMER_TOKEN))
                .andExpect(status().isUnauthorized());

        verify(repository, never()).listAccountRequests(anyString());
    }

    @Test
    void a_manager_cannot_use_a_customer_route() throws Exception {
        when(sessions.resolve(MANAGER_TOKEN)).thenReturn(managerSession());

        mvc.perform(get("/api/customer/statement").header("Authorization", "Bearer " + MANAGER_TOKEN))
                .andExpect(status().isUnauthorized());
    }

    @Test
    void approval_requires_a_manager_session() throws Exception {
        mvc.perform(post("/api/manager/requests/1006/approve"))
                .andExpect(status().isUnauthorized());

        verify(repository, never()).approve(anyLong());
    }

    @Test
    void signing_out_revokes_the_presented_token() throws Exception {
        mvc.perform(delete("/api/session").header("Authorization", "Bearer " + CUSTOMER_TOKEN))
                .andExpect(status().isNoContent());

        verify(sessions).revoke(CUSTOMER_TOKEN);
    }

    private static Session customerSession() {
        return new Session(Role.CUSTOMER, 500001L, "Ada Lovelace", OffsetDateTime.now().plusHours(1));
    }

    private static Session managerSession() {
        return new Session(Role.MANAGER, null, "branch.manager", OffsetDateTime.now().plusHours(1));
    }
}
