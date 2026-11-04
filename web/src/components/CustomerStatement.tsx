// A customer's statement of account, on the letterhead: what they owe today, how late it is, every unpaid invoice
// with what's been paid on it, their recent payments, and how to pay. Built from the open invoices (not a running
// total of everything ever), so it agrees with the books even when credit notes or write-offs settled an invoice.
import { DocBox, DocFooter, Letterhead, useLetterhead } from './PrintDoc';
import { dayYear, ksh, prettyPhone } from '../lib/format';

type Doc = { id: string; kind: string; number: string; status: string; issueDate: string; dueDate: string; total: number; paid: number };
type Payment = { id: string; method: string; amount: number; allocated: number; reference: string; receivedAt: string };
export type StatementData = {
  customer: { name: string; number: string; phone: string; email: string; address: string; kraPin: string; creditLimit?: number | null };
  documents: Doc[];
  payments: Payment[];
  owes: number;
  overdue: number;
  credit: number;
  payTo?: { paybill?: string };
};

const DAY = 86_400_000;
const BUCKETS = ['Not yet due', '1–30 days', '31–60 days', '61–90 days', 'Over 90 days'] as const;
const bucketOf = (late: number) => (late <= 0 ? 0 : late <= 30 ? 1 : late <= 60 ? 2 : late <= 90 ? 3 : 4);
const METHOD: Record<string, string> = { Mpesa: 'M-Pesa', Bank: 'Bank', Cash: 'Cash', Card: 'Card', Cheque: 'Cheque' };

export default function CustomerStatement({ data }: { data: StatementData }) {
  const lh = useLetterhead();
  const c = data.customer;
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const open = data.documents
    .filter((d) => d.kind === 'Invoice' && (d.status === 'Sent' || d.status === 'PartPaid') && d.total - d.paid > 0)
    .map((d) => ({ ...d, balance: d.total - d.paid, late: Math.floor((today.getTime() - new Date(`${d.dueDate}T00:00:00`).getTime()) / DAY) }))
    .sort((a, b) => a.issueDate.localeCompare(b.issueDate) || a.number.localeCompare(b.number));
  const aging = [0, 0, 0, 0, 0];
  for (const d of open) aging[bucketOf(d.late)] += d.balance;
  const due = Math.max(data.owes - data.credit, 0);
  const recent = data.payments.slice(0, 8);

  return (
    <article className="doc relative">
      <Letterhead
        title="Statement"
        number={c.number}
        facts={[
          ['Statement date', dayYear(today.toISOString())],
          ['Unpaid invoices', String(open.length)],
          ...(c.creditLimit ? [['Credit limit', ksh(c.creditLimit)] as [string, string]] : []),
        ]}
      />

      <div className="mt-6 grid gap-4 sm:grid-cols-[1fr_15rem]">
        <DocBox label="Statement for">
          <p className="font-semibold text-ink">{c.name}</p>
          <div className="mt-0.5 text-xs leading-relaxed text-ink-2">
            {c.address && <p className="whitespace-pre-line">{c.address}</p>}
            {[prettyPhone(c.phone), c.email].filter(Boolean).length > 0 && <p>{[prettyPhone(c.phone), c.email].filter(Boolean).join('  ·  ')}</p>}
            {c.kraPin && <p>KRA PIN {c.kraPin}</p>}
          </div>
        </DocBox>
        <section className={due > 0 ? 'break-inside-avoid rounded-md bg-brand p-4 text-white' : 'break-inside-avoid rounded-md bg-good-soft p-4 text-good'}>
          <p className="text-2xs font-semibold tracking-wide uppercase opacity-80">{due > 0 ? 'Amount due' : 'Nothing to pay'}</p>
          <p className="num mt-1 font-display text-2xl font-semibold">{ksh(due)}</p>
          <p className="mt-0.5 text-xs opacity-90">{data.overdue > 0 ? `${ksh(data.overdue)} of it is overdue` : due > 0 ? 'Nothing overdue. Thank you.' : 'Your account is up to date. Thank you.'}</p>
        </section>
      </div>

      {/* How late the balance is, oldest last. */}
      <div className="mt-6 grid break-inside-avoid grid-cols-5 overflow-hidden rounded-md ring-1 ring-line">
        {BUCKETS.map((label, i) => (
          <div key={label} className={i ? 'border-l border-line p-3' : 'p-3'}>
            <p className="text-2xs font-semibold tracking-wide text-muted uppercase">{label}</p>
            <p className={`num mt-1 text-sm font-semibold ${i >= 2 && aging[i] > 0 ? 'text-bad' : 'text-ink'}`}>{ksh(aging[i])}</p>
          </div>
        ))}
      </div>

      <h2 className="mt-7 mb-2 text-sm font-semibold text-ink">Unpaid invoices</h2>
      {open.length ? (
        <table className="doc-table">
          <thead>
            <tr>
              <th>Date</th>
              <th>Invoice</th>
              <th>Due</th>
              <th className="text-right">Days late</th>
              <th className="text-right">Amount</th>
              <th className="text-right">Paid</th>
              <th className="text-right">Balance</th>
            </tr>
          </thead>
          <tbody>
            {open.map((d) => (
              <tr key={d.id}>
                <td className="num whitespace-nowrap">{dayYear(d.issueDate)}</td>
                <td className="num font-medium text-ink">{d.number}</td>
                <td className="num whitespace-nowrap">{dayYear(d.dueDate)}</td>
                <td className={`num text-right ${d.late > 30 ? 'font-semibold text-bad' : d.late > 0 ? 'text-warn' : 'text-muted'}`}>{d.late > 0 ? d.late : '–'}</td>
                <td className="num text-right">{ksh(d.total)}</td>
                <td className="num text-right text-muted">{d.paid ? ksh(d.paid) : '–'}</td>
                <td className="num text-right font-semibold">{ksh(d.balance)}</td>
              </tr>
            ))}
            {data.credit > 0 && (
              <tr>
                <td colSpan={6} className="text-ink-2">Payments received but not yet matched to an invoice</td>
                <td className="num text-right font-semibold text-good">−{ksh(data.credit)}</td>
              </tr>
            )}
            <tr>
              <td colSpan={6} className="font-bold text-ink">Total due</td>
              <td className="num text-right font-bold text-ink">{ksh(due)}</td>
            </tr>
          </tbody>
        </table>
      ) : (
        <p className="rounded-md bg-paper p-4 text-sm text-ink-2 ring-1 ring-line">No unpaid invoices. Thank you for keeping your account up to date.</p>
      )}

      {recent.length > 0 && (
        <>
          <h2 className="mt-7 mb-2 text-sm font-semibold text-ink">Recent payments</h2>
          <table className="doc-table">
            <thead>
              <tr>
                <th>Date</th>
                <th>Paid by</th>
                <th>Reference</th>
                <th className="text-right">Amount</th>
              </tr>
            </thead>
            <tbody>
              {recent.map((p) => (
                <tr key={p.id}>
                  <td className="num whitespace-nowrap">{dayYear(p.receivedAt)}</td>
                  <td>{METHOD[p.method] ?? p.method}</td>
                  <td className="num text-muted">{p.reference || '–'}</td>
                  <td className="num text-right font-semibold">{ksh(p.amount)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}

      {due > 0 && (
        <DocBox label="How to pay" className="mt-6">
          {data.payTo?.paybill ? (
            <p className="text-ink-2">
              M-Pesa Paybill <b className="num text-ink">{data.payTo.paybill}</b>, with the <b className="text-ink">invoice number</b> as the account number, so each payment is matched to its invoice.
            </p>
          ) : (
            <p className="text-ink-2">Pay by M-Pesa, bank transfer or cash, quoting the invoice number, so each payment is matched to its invoice.</p>
          )}
        </DocBox>
      )}

      <DocFooter>
        If anything on this statement doesn’t match your records, please let us know within 14 days.
        {lh.phone && ` Call ${prettyPhone(lh.phone)}.`}
      </DocFooter>
    </article>
  );
}
