package com.northstar.migrated.domain;

import jakarta.persistence.*;
import java.math.BigDecimal;
import java.time.LocalDate;
import java.time.LocalDateTime;

@Entity
@Table(name = "bank_account")
public class BankAccount {
    @Id
    @Column(name = "account_id")
    private Long accountId;

    @Column(name = "request_id", nullable = false)
    private Long requestId;

    @Column(name = "branch_code", nullable = false)
    private String branchCode;

    @Column(name = "account_kind", nullable = false)
    private String accountKind;

    @Column(name = "opened_on", nullable = false)
    private LocalDate openedOn;

    @Column(name = "online_enabled", nullable = false, columnDefinition = "char(1)")
    private String onlineEnabled;

    @Column(name = "online_password_hash")
    private byte[] onlinePasswordHash;

    public Long getAccountId() { return accountId; }
    public void setAccountId(Long value) { this.accountId = value; }

    public Long getRequestId() { return requestId; }
    public void setRequestId(Long value) { this.requestId = value; }

    public String getBranchCode() { return branchCode; }
    public void setBranchCode(String value) { this.branchCode = value; }

    public String getAccountKind() { return accountKind; }
    public void setAccountKind(String value) { this.accountKind = value; }

    public LocalDate getOpenedOn() { return openedOn; }
    public void setOpenedOn(LocalDate value) { this.openedOn = value; }

    public String getOnlineEnabled() { return onlineEnabled; }
    public void setOnlineEnabled(String value) { this.onlineEnabled = value; }

    public byte[] getOnlinePasswordHash() { return onlinePasswordHash; }
    public void setOnlinePasswordHash(byte[] value) { this.onlinePasswordHash = value; }

}
