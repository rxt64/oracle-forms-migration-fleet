import type { BankAccountRequest, BankAccount, BankStaffUser, BankTransaction } from "./types";

const base = import.meta.env.VITE_API_BASE ?? "";

async function get<T>(path: string): Promise<T> {
  const response = await fetch(`${base}${path}`, { headers: { accept: "application/json" } });
  if (!response.ok) {
    throw new Error(`${path} failed: ${response.status}`);
  }
  return (await response.json()) as T;
}

export const listBankAccountRequest = () => get<BankAccountRequest[]>("/api/bank-account-request");
export const listBankAccount = () => get<BankAccount[]>("/api/bank-account");
export const listBankStaffUser = () => get<BankStaffUser[]>("/api/bank-staff-user");
export const listBankTransaction = () => get<BankTransaction[]>("/api/bank-transaction");
