import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { ArrowLeft, DollarSign, FileCheck, Loader2, PackageCheck, Printer, XCircle } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import DocTrail from '../components/DocTrail';
import { dateTime, dayYear, ksh, prettyPhone, toCents } from '../lib/format';
import { useLang } from '../lib/i18n';
import { ErrorNote, Field, Loading, PageHeader, Sheet, SheetFooter, PageError, confirmDialog } from '../components/ui';
import { PurchaseStatusBadge } from './Purchases';
import { DocBox, DocFooter, Letterhead, printAs, Stamp, useLetterhead } from '../components/PrintDoc';

type Line = {
  id: string;
  itemId: string | null;
  accountId: string | null;
  description: string;
  quantity: number;
  unitCost: number;
  tax: number;
  total: number;
  taxType: string;
};

type Payment = {
  id: string;
  method: string;
  amount: number;
  reference: string;
  paidAt: string;
};

type PurchaseDoc = {
  id: string;
  kind: 'Order' | 'Bill';
  number: string;
  supplierRef: string;
  status: string;
  issueDate: string;
  dueDate: string;
  net: number;
  tax: number;
  total: number;
  paid: number;
  balance: number;
  notes: string;
  supplierId: string;
  supplier: { id: string; name: string; number: string; phone: string; kraPin: string; email?: string; address?: string };
  lines: Line[];
};

function PayBillModal({
  open,
  onClose,
  doc,
  onPaid,
}: {
  open: boolean;
  onClose: () => void;
  doc: PurchaseDoc;
  onPaid: () => void;
}) {
  const [method, setMethod] = useState<'Mpesa' | 'Bank' | 'Cash'>('Mpesa');
  const [amountStr, setAmountStr] = useState(((doc.total - doc.paid) / 100).toFixed(2));
  const [reference, setReference] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    const amount = toCents(amountStr);
    if (amount <= 0 || amount > doc.total - doc.paid) {
      setState({ busy: false, error: 'Enter a valid payment amount.' });
      return;
    }
    try {
      await api(`/purchases/${doc.id}/pay`, {
        method: 'POST',
        body: {
          method,
          amount,
          reference: reference.trim() || undefined,
        },
      });
      onPaid();
      onClose();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };

  return (
    <Sheet open={open} onClose={onClose} title={`Pay ${doc.number}`} description={`Record money paid to ${doc.supplier.name}.`}>
      <form onSubmit={submit} className="space-y-4">
        <div className="rounded-lg bg-paper p-3 ring-1 ring-line">
          <span className="text-xs text-muted block">Remaining balance</span>
          <b className="num text-xl font-bold text-ink">{ksh(doc.total - doc.paid)}</b>
        </div>

        <Field label="Payment method">
          <div className="grid grid-cols-3 gap-2">
            {(['Mpesa', 'Bank', 'Cash'] as const).map((m) => (
              <button
                key={m}
                type="button"
                onClick={() => setMethod(m)}
                className={`py-2 text-xs font-semibold rounded-md border ${
                  method === m ? 'border-brand bg-brand-soft text-brand font-bold' : 'border-line text-muted'
                }`}
              >
                {m === 'Mpesa' ? 'M-Pesa' : m}
              </button>
            ))}
          </div>
        </Field>

        <Field label="Amount to pay (KSh)">
          <input
            className="field"
            type="number"
            step="any"
            value={amountStr}
            onChange={(e) => setAmountStr(e.target.value)}
            required
          />
        </Field>

        <Field
          label="Transaction reference"
          hint={method === 'Mpesa' ? 'e.g. SKP1Q2W3E4' : 'Bank slip or receipt number'}
        >
          <input
            className="field uppercase"
            value={reference}
            onChange={(e) => setReference(e.target.value)}
            placeholder={method === 'Mpesa' ? 'M-Pesa code' : 'Ref #'}
          />
        </Field>

        {state.error && <ErrorNote>{state.error}</ErrorNote>}

        <SheetFooter>
        <button className="btn btn-primary" disabled={state.busy}>
          {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Record payment
        </button>
        </SheetFooter>
      </form>
    </Sheet>
  );
}

export default function PurchaseView() {
  const { id } = useParams();
  const navigate = useNavigate();
  const session = useSession();
  const { t } = useLang();

  const [paying, setPaying] = useState(false);
  const [state, setState] = useState({ busy: false, error: '' });
  const lh = useLetterhead();

  const { data, error, reload } = useApi<{
    document: PurchaseDoc;
    payments: Payment[];
    location: string | null;
    waitingForFinance?: boolean;
  }>(id ? `/purchases/${id}` : null);

  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;

  const { document: doc, payments, location } = data;
  const isBill = doc.kind === 'Bill';
  const isDraft = doc.status === 'Draft';
  const isOpen = ['Open', 'PartPaid'].includes(doc.status);

  const postDoc = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(`/purchases/${doc.id}/post`, { method: 'POST' });
      reload();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };

  // A department's or a job's order goes to the supplier only once finance has released the money.
  const signOff = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(`/purchases/${doc.id}/sign-off`, { body: {} });
      reload();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };

  const receiveOrder = async () => {
    setState({ busy: true, error: '' });
    try {
      const bill = await api(`/purchases/${doc.id}/receive`, { method: 'POST' });
      navigate(`/purchases/${bill.id}`);
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };

  const voidDoc = async () => {
    if (!(await confirmDialog({ title: 'Void this bill?', body: 'Any stock it received and its entries in the books are reversed.', confirm: 'Void bill', danger: true }))) return;
    setState({ busy: true, error: '' });
    try {
      await api(`/purchases/${doc.id}/void`, { method: 'POST' });
      reload();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };

  return (
    <div className="space-y-6">
      <div className="print:hidden">
        <Link
          to={`/purchases?kind=${doc.kind}`}
          className="inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink"
        >
          <ArrowLeft className="h-4 w-4" /> {isBill ? t('Bills') : t('Purchase orders')}
        </Link>
        <PageHeader
          title={`${doc.number}`}
          subtitle={`${isBill ? 'Supplier Bill' : 'Purchase Order'} · ${doc.supplier.name}${
            doc.supplierRef ? ` · Invoice ${doc.supplierRef}` : ''
          }`}
          actions={
            <div className="flex flex-wrap gap-2">
              <button type="button" className="btn btn-ghost" onClick={() => printAs(`${isBill ? 'Bill' : 'Purchase order'} ${doc.number} · ${doc.supplier.name}`)}>
                <Printer className="h-4 w-4" /> Print / PDF
              </button>

              {!isBill && isDraft && data.waitingForFinance && can(session, 'finance') && (
                <button type="button" className="btn btn-primary" disabled={state.busy} onClick={signOff}>
                  <FileCheck className="h-4 w-4" /> Release the money
                </button>
              )}

              {isDraft && !(data.waitingForFinance && !isBill) && can(session, isBill ? 'finance' : 'stock') && (
                <button type="button" className="btn btn-primary" disabled={state.busy} onClick={postDoc}>
                  {state.busy && <Loader2 className="h-4 w-4 animate-spin" />}
                  <FileCheck className="h-4 w-4" /> {isBill ? 'Post bill' : 'Send order'}
                </button>
              )}

              {!isBill && doc.status === 'Sent' && can(session, 'stock') && (
                <button
                  type="button"
                  className="btn btn-primary"
                  disabled={state.busy}
                  onClick={receiveOrder}
                >
                  {state.busy && <Loader2 className="h-4 w-4 animate-spin" />}
                  <PackageCheck className="h-4 w-4" /> Receive goods & create bill
                </button>
              )}

              {isBill && isOpen && can(session, 'finance') && (
                <button type="button" className="btn btn-primary" onClick={() => setPaying(true)}>
                  <DollarSign className="h-4 w-4" /> Pay bill
                </button>
              )}

              {isBill && (isDraft || isOpen) && payments.length === 0 && can(session, 'finance') && (
                <button
                  type="button"
                  className="btn btn-ghost text-bad hover:bg-bad-soft"
                  disabled={state.busy}
                  onClick={voidDoc}
                >
                  <XCircle className="h-4 w-4" /> Void
                </button>
              )}
            </div>
          }
        />
      </div>

      {!isBill && isDraft && data.waitingForFinance && (
        <p className="rounded-md bg-warn-soft px-3 py-2 text-sm text-warn print:hidden">This order spends a department’s money: finance releases it before it can go to the supplier. The release is written to the approvals register.</p>
      )}
      {state.error && <ErrorNote>{state.error}</ErrorNote>}

      <DocTrail kind="purchase" id={doc.id} />
      {/* The document as the supplier sees it (and as it prints). */}
      <article className="card doc relative min-w-0 p-5 sm:p-10 print:p-0 print:ring-0">
        {isDraft ? <Stamp word="Draft" tone="plain" /> : doc.status === 'Void' ? <Stamp word="Void" tone="bad" /> : isBill && doc.status === 'Paid' ? <Stamp word="Paid" tone="good" /> : null}
        <Letterhead
          title={isBill ? 'Supplier bill' : 'Purchase order'}
          number={doc.number}
          facts={[
            [isBill ? 'Bill date' : 'Order date', dayYear(doc.issueDate)],
            [isBill ? 'Due' : 'Deliver by', dayYear(doc.dueDate)],
            ...(doc.supplierRef ? [[isBill ? 'Their invoice' : 'Their ref.', doc.supplierRef] as [string, string]] : []),
          ]}
        />
        <div className="mt-6 grid gap-4 sm:grid-cols-2">
          <DocBox label={isBill ? 'From supplier' : 'Supplier'}>
            <Link to={`/suppliers/${doc.supplier.id}`} className="font-semibold text-ink hover:text-brand print:no-underline">
              {doc.supplier.name}
            </Link>
            <div className="mt-0.5 text-xs leading-relaxed text-ink-2">
              {doc.supplier.address && <p className="whitespace-pre-line">{doc.supplier.address}</p>}
              {[prettyPhone(doc.supplier.phone), doc.supplier.email].filter(Boolean).length > 0 && <p>{[prettyPhone(doc.supplier.phone), doc.supplier.email].filter(Boolean).join('  ·  ')}</p>}
              {doc.supplier.kraPin && <p>KRA PIN {doc.supplier.kraPin}</p>}
            </div>
          </DocBox>
          <DocBox label={isBill ? 'Received into' : 'Deliver to'}>
            <p className="font-semibold text-ink">{lh.name}</p>
            <div className="mt-0.5 text-xs leading-relaxed text-ink-2">
              <p>{location || 'Main store'}</p>
              {(lh.address || lh.county) && <p>{[lh.address, lh.county].filter(Boolean).join(', ')}</p>}
            </div>
            <div className="mt-2 print:hidden">
              <PurchaseStatusBadge d={doc} />
            </div>
          </DocBox>
        </div>

        <div className="mt-6 overflow-x-auto print:overflow-visible">
          <table className="doc-table min-w-[30rem]">
            <thead>
              <tr>
                <th className="w-8">#</th>
                <th>Item / description</th>
                <th className="text-right">Qty</th>
                <th className="text-right">Unit cost (incl. VAT)</th>
                <th className="text-right">VAT</th>
                <th className="text-right">Amount</th>
              </tr>
            </thead>
            <tbody>
              {doc.lines.map((l, i) => (
                <tr key={l.id}>
                  <td className="num text-muted">{i + 1}</td>
                  <td className="font-medium text-ink">{l.description}</td>
                  <td className="num text-right">{l.quantity}</td>
                  <td className="num text-right">{ksh(l.unitCost)}</td>
                  <td className="num text-right text-muted">{ksh(l.tax)}</td>
                  <td className="num text-right font-semibold">{ksh(l.total)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <div className="mt-5 flex flex-wrap items-start justify-between gap-6">
          <div className="max-w-sm min-w-0 flex-1 text-sm">
            {doc.notes && (
              <>
                <p className="text-2xs font-semibold tracking-wide text-muted uppercase">Notes</p>
                <p className="mt-1 whitespace-pre-line text-ink-2">{doc.notes}</p>
              </>
            )}
          </div>
          <dl className="w-full max-w-72 space-y-1.5 text-sm break-inside-avoid">
            <div className="flex justify-between gap-4">
              <dt className="text-muted">Subtotal (before VAT)</dt>
              <dd className="num">{ksh(doc.net)}</dd>
            </div>
            <div className="flex justify-between gap-4">
              <dt className="text-muted">VAT</dt>
              <dd className="num">{ksh(doc.tax)}</dd>
            </div>
            <div className="flex justify-between gap-4 border-t-2 border-ink pt-2 text-base font-bold">
              <dt>Total</dt>
              <dd className="num">{ksh(doc.total)}</dd>
            </div>
            {isBill && (
              <>
                <div className="flex justify-between gap-4 text-good">
                  <dt>Paid</dt>
                  <dd className="num">−{ksh(doc.paid)}</dd>
                </div>
                <div className="flex justify-between gap-4 rounded-sm bg-brand-soft px-2 py-1.5 font-bold">
                  <dt>Balance due</dt>
                  <dd className="num">{ksh(doc.total - doc.paid)}</dd>
                </div>
              </>
            )}
          </dl>
        </div>

        {!isBill && (
          <div className="mt-10 grid grid-cols-3 gap-5 text-2xs text-muted break-inside-avoid">
            {['Authorised by', 'Signature', 'Date'].map((s) => (
              <p key={s} className="border-t border-ink pt-1">{s}</p>
            ))}
          </div>
        )}

        <DocFooter>
          {isBill ? 'Our record of this supplier’s bill.' : `Please quote ${doc.number} on your delivery note and invoice.`}
          {lh.phone && ` Questions? Call ${prettyPhone(lh.phone)}.`}
        </DocFooter>
      </article>

      {payments.length > 0 && (
        <section className="card p-4 sm:p-5 print:hidden">
          <h3 className="text-sm font-semibold text-ink mb-3">Payments on this bill</h3>
          <ul className="divide-y divide-line text-sm">
            {payments.map((p) => (
              <li key={p.id} className="flex items-center justify-between py-2">
                <div>
                  <b className="text-ink">{p.method === "Mpesa" ? "M-Pesa" : p.method}</b>
                  {p.reference && <span className="text-muted text-xs"> ({p.reference})</span>}
                  <span className="text-xs text-muted block">{dateTime(p.paidAt)}</span>
                </div>
                <b className="num text-good font-semibold">{ksh(p.amount)}</b>
              </li>
            ))}
          </ul>
        </section>
      )}

      <PayBillModal
        open={paying}
        onClose={() => setPaying(false)}
        doc={doc}
        onPaid={() => reload()}
      />
    </div>
  );
}
