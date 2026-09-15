package com.northstar.migrated.banking;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;

/**
 * The source stored STANDARD_HASH(password, 'SHA256') in a RAW(32) column, which the schema conversion
 * carried across to bytea unchanged. Hashing the same UTF-8 bytes here keeps every migrated credential
 * valid without a reset, and the comparison is constant time, so a wrong password takes as long as a
 * right one however many leading bytes matched.
 */
public final class PasswordHashing {

    private PasswordHashing() {
    }

    public static byte[] sha256(String password) {
        try {
            return MessageDigest.getInstance("SHA-256").digest(password.getBytes(StandardCharsets.UTF_8));
        } catch (NoSuchAlgorithmException cause) {
            throw new IllegalStateException("SHA-256 is required of every Java platform.", cause);
        }
    }

    public static boolean matches(byte[] stored, String password) {
        if (stored == null || password == null) {
            return false;
        }

        return MessageDigest.isEqual(stored, sha256(password));
    }
}
