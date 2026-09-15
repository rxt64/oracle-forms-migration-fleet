package com.northstar.migrated.domain;

import jakarta.persistence.*;
import org.hibernate.annotations.JdbcTypeCode;
import org.hibernate.type.SqlTypes;
import java.math.BigDecimal;
import java.time.LocalDate;
import java.time.LocalDateTime;

@Entity
@Table(name = "bank_transaction")
public class BankTransaction {
    @Id
    @Column(name = "transaction_id")
    private Long transactionId;

    @Column(name = "account_id", nullable = false)
    private Long accountId;

    @Column(name = "transaction_ts", nullable = false)
    private LocalDateTime transactionTs;

    @Column(name = "amount", nullable = false)
    private BigDecimal amount;

    @Column(name = "reference_code", nullable = false)
    private String referenceCode;

    @JdbcTypeCode(SqlTypes.CHAR)
    @Column(name = "direction_code", nullable = false, columnDefinition = "char(2)")
    private String directionCode;

    public Long getTransactionId() { return transactionId; }
    public void setTransactionId(Long value) { this.transactionId = value; }

    public Long getAccountId() { return accountId; }
    public void setAccountId(Long value) { this.accountId = value; }

    public LocalDateTime getTransactionTs() { return transactionTs; }
    public void setTransactionTs(LocalDateTime value) { this.transactionTs = value; }

    public BigDecimal getAmount() { return amount; }
    public void setAmount(BigDecimal value) { this.amount = value; }

    public String getReferenceCode() { return referenceCode; }
    public void setReferenceCode(String value) { this.referenceCode = value; }

    public String getDirectionCode() { return directionCode; }
    public void setDirectionCode(String value) { this.directionCode = value; }

}
