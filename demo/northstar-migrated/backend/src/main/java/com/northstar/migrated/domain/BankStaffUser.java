package com.northstar.migrated.domain;

import jakarta.persistence.*;
import org.hibernate.annotations.JdbcTypeCode;
import org.hibernate.type.SqlTypes;
import java.math.BigDecimal;
import java.time.LocalDate;
import java.time.LocalDateTime;

@Entity
@Table(name = "bank_staff_user")
public class BankStaffUser {
    @Id
    @Column(name = "staff_id")
    private Long staffId;

    @Column(name = "username", nullable = false)
    private String username;

    @Column(name = "password_hash", nullable = false)
    private byte[] passwordHash;

    @Column(name = "role_code", nullable = false)
    private String roleCode;

    @JdbcTypeCode(SqlTypes.CHAR)
    @Column(name = "active_flag", nullable = false, columnDefinition = "char(1)")
    private String activeFlag;

    public Long getStaffId() { return staffId; }
    public void setStaffId(Long value) { this.staffId = value; }

    public String getUsername() { return username; }
    public void setUsername(String value) { this.username = value; }

    public byte[] getPasswordHash() { return passwordHash; }
    public void setPasswordHash(byte[] value) { this.passwordHash = value; }

    public String getRoleCode() { return roleCode; }
    public void setRoleCode(String value) { this.roleCode = value; }

    public String getActiveFlag() { return activeFlag; }
    public void setActiveFlag(String value) { this.activeFlag = value; }

}
