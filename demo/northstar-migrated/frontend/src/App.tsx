import { useEffect, useState } from "react";
import { listBankAccountRequest } from "./api";
import type { BankAccountRequest } from "./types";

// Generated from Forms block REQUEST_BLOCK over BANK_ACCOUNT_REQUEST.
// Column order and labels follow the form; trigger behaviour does not.
export default function App() {
  const [rows, setRows] = useState<BankAccountRequest[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    listBankAccountRequest().then(setRows).catch((cause: Error) => setError(cause.message));
  }, []);

  if (error) {
    return <p role="alert">{error}</p>;
  }

  return (
    <section>
      <h1>REQUEST_BLOCK</h1>
      <table>
        <thead>
          <tr>
            <th scope="col">Request <abbr title="Required">*</abbr></th>
            <th scope="col">Status <abbr title="Required">*</abbr></th>
            <th scope="col">Surname <abbr title="Required">*</abbr></th>
            <th scope="col">First name <abbr title="Required">*</abbr></th>
            <th scope="col">Title</th>
            <th scope="col">Date of birth <abbr title="Required">*</abbr></th>
            <th scope="col">Branch <abbr title="Required">*</abbr></th>
            <th scope="col">Account type <abbr title="Required">*</abbr></th>
            <th scope="col">Email</th>
            <th scope="col">Work phone</th>
            <th scope="col">Home phone</th>
            <th scope="col">Submitted</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((row, index) => (
            <tr key={index}>
              <td>{String(row.requestId ?? "")}</td>
              <td>{String(row.requestStatus ?? "")}</td>
              <td>{String(row.familyName ?? "")}</td>
              <td>{String(row.givenName ?? "")}</td>
              <td>{String(row.honorific ?? "")}</td>
              <td>{String(row.dateOfBirth ?? "")}</td>
              <td>{String(row.branchCode ?? "")}</td>
              <td>{String(row.accountKind ?? "")}</td>
              <td>{String(row.emailAddress ?? "")}</td>
              <td>{String(row.workPhone ?? "")}</td>
              <td>{String(row.homePhone ?? "")}</td>
              <td>{String(row.submittedAt ?? "")}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
