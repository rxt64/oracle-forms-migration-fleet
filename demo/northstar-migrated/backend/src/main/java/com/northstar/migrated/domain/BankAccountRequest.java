package com.northstar.migrated.domain;

import jakarta.persistence.*;
import org.hibernate.annotations.JdbcTypeCode;
import org.hibernate.type.SqlTypes;
import java.math.BigDecimal;
import java.time.LocalDate;
import java.time.LocalDateTime;

@Entity
@Table(name = "bank_account_request")
public class BankAccountRequest {
    @Id
    @Column(name = "request_id")
    private Long requestId;

    @Column(name = "branch_code", nullable = false)
    private String branchCode;

    @Column(name = "account_kind", nullable = false)
    private String accountKind;

    @Column(name = "honorific")
    private String honorific;

    @Column(name = "given_name", nullable = false)
    private String givenName;

    @Column(name = "family_name", nullable = false)
    private String familyName;

    @Column(name = "date_of_birth", nullable = false)
    private LocalDate dateOfBirth;

    @Column(name = "work_phone")
    private String workPhone;

    @Column(name = "home_phone")
    private String homePhone;

    @Column(name = "street_address", nullable = false)
    private String streetAddress;

    @Column(name = "region_code", nullable = false)
    private String regionCode;

    @Column(name = "postal_code", nullable = false)
    private String postalCode;

    @Column(name = "email_address", nullable = false)
    private String emailAddress;

    @Column(name = "request_status", nullable = false)
    private String requestStatus;

    @Column(name = "submitted_at", nullable = false)
    private LocalDateTime submittedAt;

    @Column(name = "decided_at")
    private LocalDateTime decidedAt;

    public Long getRequestId() { return requestId; }
    public void setRequestId(Long value) { this.requestId = value; }

    public String getBranchCode() { return branchCode; }
    public void setBranchCode(String value) { this.branchCode = value; }

    public String getAccountKind() { return accountKind; }
    public void setAccountKind(String value) { this.accountKind = value; }

    public String getHonorific() { return honorific; }
    public void setHonorific(String value) { this.honorific = value; }

    public String getGivenName() { return givenName; }
    public void setGivenName(String value) { this.givenName = value; }

    public String getFamilyName() { return familyName; }
    public void setFamilyName(String value) { this.familyName = value; }

    public LocalDate getDateOfBirth() { return dateOfBirth; }
    public void setDateOfBirth(LocalDate value) { this.dateOfBirth = value; }

    public String getWorkPhone() { return workPhone; }
    public void setWorkPhone(String value) { this.workPhone = value; }

    public String getHomePhone() { return homePhone; }
    public void setHomePhone(String value) { this.homePhone = value; }

    public String getStreetAddress() { return streetAddress; }
    public void setStreetAddress(String value) { this.streetAddress = value; }

    public String getRegionCode() { return regionCode; }
    public void setRegionCode(String value) { this.regionCode = value; }

    public String getPostalCode() { return postalCode; }
    public void setPostalCode(String value) { this.postalCode = value; }

    public String getEmailAddress() { return emailAddress; }
    public void setEmailAddress(String value) { this.emailAddress = value; }

    public String getRequestStatus() { return requestStatus; }
    public void setRequestStatus(String value) { this.requestStatus = value; }

    public LocalDateTime getSubmittedAt() { return submittedAt; }
    public void setSubmittedAt(LocalDateTime value) { this.submittedAt = value; }

    public LocalDateTime getDecidedAt() { return decidedAt; }
    public void setDecidedAt(LocalDateTime value) { this.decidedAt = value; }

}
