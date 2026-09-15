package com.northstar.migrated.banking;

import org.springframework.stereotype.Component;

import java.security.SecureRandom;
import java.time.Duration;
import java.time.OffsetDateTime;
import java.util.Base64;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * Bearer sessions held in memory, as the source application held them. The token is opaque random
 * material with nothing encoded inside it, so a client cannot read or forge a role out of one, and
 * every session disappears on restart. Expired entries are swept as tokens are issued rather than on a
 * timer, so an idle process holds nothing.
 */
@Component
public class BankingSessionStore {

    public enum Role {
        CUSTOMER,
        MANAGER
    }

    public record Session(Role role, Long accountId, String displayName, OffsetDateTime expiresAt) {
    }

    public record Issued(String token, Session session) {
    }

    private static final Duration LIFETIME = Duration.ofHours(2);
    private static final int SWEEP_INTERVAL = 32;
    private static final int TOKEN_BYTES = 32;

    private final Map<String, Session> sessions = new ConcurrentHashMap<>();
    private final SecureRandom random = new SecureRandom();
    private final AtomicInteger issuedSinceSweep = new AtomicInteger();

    public Issued create(Role role, Long accountId, String displayName) {
        if (issuedSinceSweep.incrementAndGet() >= SWEEP_INTERVAL) {
            issuedSinceSweep.set(0);
            removeExpired();
        }

        byte[] material = new byte[TOKEN_BYTES];
        random.nextBytes(material);

        String token = Base64.getUrlEncoder().withoutPadding().encodeToString(material);
        Session session = new Session(role, accountId, displayName, OffsetDateTime.now().plus(LIFETIME));
        sessions.put(token, session);
        return new Issued(token, session);
    }

    public Session resolve(String token) {
        if (token == null || token.isEmpty()) {
            return null;
        }

        Session session = sessions.get(token);
        if (session == null) {
            return null;
        }

        if (session.expiresAt().isAfter(OffsetDateTime.now())) {
            return session;
        }

        sessions.remove(token);
        return null;
    }

    public void revoke(String token) {
        if (token != null && !token.isEmpty()) {
            sessions.remove(token);
        }
    }

    private void removeExpired() {
        OffsetDateTime now = OffsetDateTime.now();
        sessions.entrySet().removeIf(entry -> !entry.getValue().expiresAt().isAfter(now));
    }
}
