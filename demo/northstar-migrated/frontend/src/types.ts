// Generated from the converted Oracle schema. Field names follow the JPA entities.

export interface BankAccountRequest {
  requestId?: number;
  branchCode: string;
  accountKind: string;
  honorific?: string;
  givenName: string;
  familyName: string;
  dateOfBirth: string;
  workPhone?: string;
  homePhone?: string;
  streetAddress: string;
  regionCode: string;
  postalCode: string;
  emailAddress: string;
  requestStatus: string;
  submittedAt: string;
  decidedAt?: string;
}

export interface BankAccount {
  accountId?: number;
  requestId: number;
  branchCode: string;
  accountKind: string;
  openedOn: string;
  onlineEnabled: string;
  onlinePasswordHash?: string;
}

export interface BankStaffUser {
  staffId?: number;
  username: string;
  passwordHash: string;
  roleCode: string;
  activeFlag: string;
}

export interface BankTransaction {
  transactionId?: number;
  accountId: number;
  transactionTs: string;
  amount: number;
  referenceCode: string;
  directionCode: string;
}

