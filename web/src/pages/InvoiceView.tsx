import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { ArrowLeft, Ban, Smartphone, FileCheck2, Loader2, MessageCircle, Pencil, Printer, RefreshCw, Send, Wallet, Sparkles } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, dateTime, dayYear, ksh, money, msisdn, prettyPhone, rateText, toCents } from '../lib/format';
import { DocStatus } from './Invoices';
import { RequestPayment } from '../components/MpesaPrompt';
import { ErrorNote, Field, Loading, Sheet, Status, SheetFooter, PageError, confirmDialog } from '../components/ui';
import { DocBox, DocFooter, Letterhead, printAs, Qr, Stamp, useLetterhead } from '../components/PrintDoc';
import DocTrail from '../components/DocTrail';

const TAX_LABEL: Record<string, string> = { B_Standard16: '16%', E_Reduced8: '8%', C_ZeroRated: '0%', A_Exempt: 'Exempt', D_NonVat: 'Non-VAT' };

/** A withholding agent paid less and gave a certificate: record it so the invoice closes and the tax is claimable. */
function Withheld({ doc, onDone }: { doc: any; onDone: () => void }) {
  const taxable16 = (doc.lines ?? []).filter((l: any) => l.taxType === 'B_Standard16').reduce((s: number, l: any) => s + l.net, 0);
  const [kind, setKind] = useState<'Vat' | 'IncomeTax'>('Vat');
  const suggested = kind === 'Vat' ? Math.round(taxable16 * 0.02) : 0;
  const [amount, setAmount] = useState(suggested ? String(suggested / 100) : '');
  const [certificate, setCertificate] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });
  const balance = doc.total - doc.paid;
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(`/sales/${doc.id}/withheld`, { body: { kind, amount: toCents(amount), certificate } });
      onDone();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <div className="space-y-4 text-sm">
      <p className="text-muted">Use this when the customer is a withholding agent: they pay less and give you a certificate from iTax. The invoice closes by that amount and the tax counts against what you owe KRA.</p>
      <div className="grid grid-cols-2 gap-2">
        {([['Vat', 'Withholding VAT', 'Usually 2% of the taxable value'], ['IncomeTax', 'Withholding tax', 'e.g. 5% on professional fees']] as const).map(([id, label, sub]) => (
          <button key={id} onClick={() => (setKind(id), setAmount(id === 'Vat' && taxable16 ? String(Math.round(taxable16 * 0.02) / 100) : ''))} className={`rounded-md p-3 text-left ring-1 ${kind === id ? 'bg-brand-soft ring-2 ring-brand' : 'bg-white ring-line'}`}>
            <b className="block">{label}</b>
            <span className="text-xs text-muted">{sub}</span>
          </button>
        ))}
      </div>
      <Field label="Amount withheld (KSh)" hint={kind === 'Vat' && suggested ? `2% of ${ksh(taxable16)} is ${ksh(suggested)}` : `Left to pay on this invoice: ${ksh(balance)}`}>
        <input className="field num" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} />
      </Field>
      <Field label="Certificate number">
        <input className="field uppercase" value={certificate} onChange={(e) => setCertificate(e.target.value)} placeholder="From the customer’s iTax certificate" />
      </Field>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button className="btn btn-primary" disabled={state.busy || !amount || certificate.trim().length < 4} onClick={save}>
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Record certificate
      </button>
      </SheetFooter>
    </div>
  );
}

function RecordPayment({ doc, open, onClose, onDone }: { doc: any; open: boolean; onClose: () => void; onDone: () => void }) {
  const cur: string = doc.currency ?? '';
  const fxBalance = doc.fxTotal - doc.fxPaid;
  const [f, setF] = useState({ method: cur ? 'Bank' : 'Mpesa', amount: cur ? '' : String((doc.total - doc.paid) / 100), fx: cur ? String(fxBalance / 100) : '', reference: '', receivedAt: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  // Abroad: the part of the invoice this settles was booked at the invoice's rate; the shillings that landed may differ.
  const fxCents = toCents(f.fx);
  const booked = !cur ? 0 : fxCents >= fxBalance ? doc.total - doc.paid : Math.min(doc.total - doc.paid, Math.round(fxCents * doc.rate));
  const landed = toCents(f.amount);
  const diff = landed - booked;
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      await api('/payments', { body: { method: f.method, amount: toCents(f.amount), fxAmount: cur ? fxCents : null, reference: f.reference, receivedAt: f.receivedAt ? new Date(f.receivedAt).toISOString() : null, customerId: doc.customerId, invoiceId: doc.id } });
      onDone();
      onClose();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  return (
    <Sheet open={open} onClose={onClose} title={`Payment for ${doc.number}`} description="Record money the customer has paid against this invoice.">
      <form onSubmit={submit} className="space-y-4">
        <p className="text-sm text-muted">M-Pesa payments to your Paybill/Till arrive and match by themselves. Record payments here that came another way.</p>
        <div>
          <p className="mb-1.5 text-sm font-semibold">How they paid</p>
          <div className="grid grid-cols-3 gap-2">
            {[['Mpesa', 'M-Pesa'], ['Cash', 'Cash'], ['Bank', 'Bank']].map(([v, l]) => (
              <button key={v} type="button" aria-pressed={f.method === v} onClick={() => setF({ ...f, method: v })} className={f.method === v ? 'btn bg-brand-soft text-brand ring-1 ring-brand/40' : 'btn btn-ghost'}>
                {l}
              </button>
            ))}
          </div>
        </div>
        {cur ? (
          <>
            <Field label={`${cur} this pays`} hint={`Still to pay: ${money(fxBalance, cur)}`}>
              <input className="field num" inputMode="decimal" value={f.fx} onChange={(e) => setF({ ...f, fx: e.target.value })} required />
            </Field>
            <Field label="Shillings that landed (KSh)" hint="What your bank credited after converting, from the statement.">
              <input className="field num" inputMode="decimal" value={f.amount} onChange={(e) => setF({ ...f, amount: e.target.value })} required placeholder={`About ${ksh(booked)} at the invoice's rate`} />
            </Field>
            {fxCents > 0 && landed > 0 && (
              <p className="rounded-md bg-paper px-3 py-2 text-xs text-muted">
                That's {rateText(Math.round((landed / fxCents) * 100) / 100)} a {cur}, against {rateText(doc.rate)} on the invoice:{' '}
                {diff === 0 ? 'no exchange difference.' : <b className={diff > 0 ? 'text-good' : 'text-bad'}>{diff > 0 ? 'a gain' : 'a loss'} of {ksh(Math.abs(diff))}</b>}
                {diff !== 0 && ', posted to exchange gains and losses.'}
              </p>
            )}
          </>
        ) : (
          <Field label="Amount (KSh)" hint={`Still to pay: ${ksh(doc.total - doc.paid)}`}>
            <input className="field num" inputMode="decimal" value={f.amount} onChange={(e) => setF({ ...f, amount: e.target.value })} required />
          </Field>
        )}
        <Field label={f.method === 'Mpesa' ? 'M-Pesa code' : f.method === 'Bank' ? 'Bank reference' : 'Receipt number'} hint={f.method === 'Mpesa' ? 'From the SMS, e.g. SJK4H7L2PQ' : 'Optional'}>
          <input className="field uppercase" value={f.reference} onChange={(e) => setF({ ...f, reference: e.target.value })} required={f.method === 'Mpesa'} />
        </Field>
        <Field label="Date paid" hint="Leave empty for now.">
          <input className="field" type="datetime-local" value={f.receivedAt} onChange={(e) => setF({ ...f, receivedAt: e.target.value })} />
        </Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter>
        <button className="btn btn-primary" disabled={state.busy}>
          {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Record {cur ? money(fxCents, cur) : ksh(toCents(f.amount))}
        </button>
        </SheetFooter>
      </form>
    </Sheet>
  );
}


function QuoteAuditCard({ invoiceId }: { invoiceId: string }) {
  const { data: variance, loading } = useApi<any>(`/sales/${invoiceId}/variance`);
  if (loading || !variance) return null;

  const isIdentical = variance.status === 'Identical';
  const isDiscount = variance.varianceAmount < 0;

  return (
    <section className="mb-6 rounded-xl border border-line bg-gradient-to-br from-white to-paper p-5 shadow-sm print:hidden">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-line pb-3">
        <div className="flex items-center gap-2">
          <div className="flex h-8 w-8 items-center justify-center rounded-lg bg-brand-soft text-brand">
            <Sparkles className="h-4 w-4" />
          </div>
          <div>
            <h3 className="font-semibold text-ink">Quote vs Invoice Audit</h3>
            <p className="text-xs text-muted">
              Quoted in <Link className="font-medium text-brand hover:underline" to={`/sales/${variance.quoteId}`}>{variance.quoteNumber}</Link> · Converted to invoice {variance.invoiceNumber}
            </p>
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xs text-muted">Brought in by:</span>
          <span className="rounded-full bg-brand-soft px-2.5 py-0.5 text-xs font-semibold text-brand">
            {variance.broughtBy}
          </span>
          <span className={cn('rounded-full px-2.5 py-0.5 text-xs font-bold',
            isIdentical ? 'bg-good-soft text-good' : isDiscount ? 'bg-warn-soft text-warn' : 'bg-indigo-soft text-indigo'
          )}>
            {isIdentical ? 'Identical (0.0%)' : `${variance.varianceAmount > 0 ? '+' : ''}${ksh(variance.varianceAmount)} (${variance.variancePercent > 0 ? '+' : ''}${variance.variancePercent}%)`}
          </span>
        </div>
      </div>

      {/* AI Agent Verdict & Explanation */}
      <div className="mt-3.5 rounded-lg bg-paper/80 p-3.5 text-sm">
        <div className="flex items-start gap-2.5">
          <span className="mt-0.5 inline-flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-ink text-2xs font-bold text-white">AI</span>
          <div className="space-y-1.5 text-ink-2">
            <p className="font-medium text-ink">{variance.agentSummary}</p>
            {variance.agentReasons?.length > 0 && (
              <ul className="list-inside list-disc space-y-1 text-xs text-muted">
                {variance.agentReasons.map((r: string, idx: number) => (
                  <li key={idx}>{r}</li>
                ))}
              </ul>
            )}
          </div>
        </div>
      </div>

      {/* Line-by-line diff table if there are changes */}
      {variance.lines?.some((l: any) => l.changeKind !== 'Unchanged') && (
        <div className="mt-4 overflow-x-auto">
          <p className="mb-2 text-xs font-semibold uppercase tracking-wider text-muted">Line Variance Audit</p>
          <table className="w-full min-w-[32rem] text-xs">
            <thead className="border-b border-line text-left text-muted">
              <tr>
                <th className="py-1.5">Item / Brand</th>
                <th className="py-1.5 text-right">Quoted</th>
                <th className="py-1.5 text-right">Invoiced</th>
                <th className="py-1.5 text-right">Diff</th>
                <th className="py-1.5 pl-4">Reason / Audit Finding</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line/60">
              {variance.lines.map((l: any, i: number) => {
                const diff = l.invoicedTotal - l.quotedTotal;
                return (
                  <tr key={i} className={l.changeKind === 'Unchanged' ? 'opacity-60' : ''}>
                    <td className="py-2 pr-2">
                      <b className="text-ink">{l.description}</b>
                      {l.brand && <span className="ml-1 text-2xs text-muted">({l.brand})</span>}
                    </td>
                    <td className="num py-2 text-right text-muted">
                      {l.quotedQty} × {ksh(l.quotedUnitPrice)}
                      <span className="block font-semibold text-ink">{ksh(l.quotedTotal)}</span>
                    </td>
                    <td className="num py-2 text-right">
                      {l.invoicedQty} × {ksh(l.invoicedUnitPrice)}
                      <span className="block font-semibold text-ink">{ksh(l.invoicedTotal)}</span>
                    </td>
                    <td className={cn('num py-2 text-right font-bold', diff < 0 ? 'text-warn' : diff > 0 ? 'text-indigo' : 'text-good')}>
                      {diff === 0 ? '0' : `${diff > 0 ? '+' : ''}${ksh(diff)}`}
                    </td>
                    <td className="py-2 pl-4 text-muted">
                      <span className={cn('inline-block rounded px-1.5 py-0.5 text-2xs font-semibold mr-1.5',
                        l.changeKind === 'Discounted' ? 'bg-warn-soft text-warn' :
                        l.changeKind === 'PriceIncreased' ? 'bg-indigo-soft text-indigo' :
                        l.changeKind === 'QtyReduced' ? 'bg-warn-soft text-warn' :
                        l.changeKind === 'QtyIncreased' ? 'bg-good-soft text-good' :
                        l.changeKind === 'ItemAdded' ? 'bg-brand-soft text-brand' :
                        l.changeKind === 'ItemDropped' ? 'bg-bad-soft text-bad' : 'bg-paper text-muted'
                      )}>
                        {l.changeKind}
                      </span>
                      {l.reason}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

export default function InvoiceView() {
  const { id } = useParams();
  const navigate = useNavigate();
  const session = useSession();
  const { data, error, reload } = useApi<{ document: any; payments: any[]; payTo: { paybill: string; account: string; prompt?: boolean } }>(`/sales/${id}`);
  const [withholding, setWithholding] = useState(false);
  const [paying, setPaying] = useState(false);
  const [prompting, setPrompting] = useState(false);
  const [state, setState] = useState({ busy: '', error: '' });
  const lh = useLetterhead();
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const d = data.document;
  const invoice = d.kind === 'Invoice';
  const balance = d.total - d.paid;
  // A foreign-currency document reads in its currency; the shillings it's booked at are shown under the totals.
  const cur: string = d.currency ?? '';
  const m = (cents: number) => money(cents, cur);
  const docTotal = cur ? d.fxTotal : d.total;
  const docPaid = cur ? d.fxPaid : d.paid;
  const docBalance = docTotal - docPaid;
  const issued = d.status !== 'Draft' && d.status !== 'Void';
  // VAT shown per rate (16%, 8%) the way KRA reads an invoice; rates that charge nothing aren't listed.
  const vatByRate = [...(d.lines as any[]).reduce((acc: Map<string, number>, l: any) => { const t = cur ? l.fxTax : l.tax; return t ? acc.set(TAX_LABEL[l.taxType], (acc.get(TAX_LABEL[l.taxType]) ?? 0) + t) : acc; }, new Map<string, number>())];
  if (!vatByRate.length && (cur ? d.fxTax : d.tax)) vatByRate.push(['', cur ? d.fxTax : d.tax]);

  const act = async (name: string, path: string, after?: (r: any) => void) => {
    setState({ busy: name, error: '' });
    try {
      const r = await api(path, { body: {} });
      after ? after(r) : reload();
      setState({ busy: '', error: '' });
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };

  const phone = msisdn(d.customer?.phone);
  const payLine = data.payTo.paybill && !cur ? `Pay by M-Pesa: Paybill ${data.payTo.paybill}, account ${d.number}.` : `Please quote ${d.number} when you pay.`;
  const waText = invoice
    ? `Hello ${d.customer?.name}, here is invoice ${d.number} from ${session?.company.name} for ${m(docBalance)}${d.dueDate ? `, due ${dayYear(d.dueDate)}` : ''}. ${payLine} Thank you.`
    : `Hello ${d.customer?.name}, here is our quote ${d.number} for ${m(docTotal)}, valid until ${dayYear(d.dueDate)}. Reply to confirm and we'll get started. Thank you.`;

  return (
    <>
      <Link to={invoice ? '/invoices' : '/quotes'} className="mb-3 inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink print:hidden">
        <ArrowLeft className="h-4 w-4" /> {invoice ? 'Invoices' : 'Quotes'}
      </Link>
      <header className="mb-5 flex flex-wrap items-start justify-between gap-3 print:hidden">
        <div>
          <div className="flex items-center gap-2">
            <h1 className="font-display text-[26px] font-semibold tracking-tight sm:text-3xl">{d.number}</h1>
            <DocStatus d={d} />
          </div>
          <p className="mt-1 text-sm text-muted">
            <Link to={`/customers/${d.customerId}`} className="font-semibold text-ink hover:text-brand">
              {d.customer?.name}
            </Link>
            {invoice && issued && <> · {balance > 0 ? <>still to pay <b className="num text-ink">{m(docBalance)}</b> of {m(docTotal)}</> : <>paid in full</>}</>}
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          {d.status === 'Draft' && can(session, 'sell') && (
            <>
              <Link to={`/sales/${d.id}/edit`} className="btn btn-ghost">
                <Pencil className="h-4 w-4" /> Edit
              </Link>
              <button className="btn btn-primary" disabled={Boolean(state.busy)} onClick={() => act('issue', `/sales/${d.id}/issue`)}>
                {state.busy === 'issue' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />} {invoice ? 'Issue invoice' : 'Mark as sent'}
              </button>
            </>
          )}
          {!invoice && d.status === 'Sent' && can(session, 'sell') && (
            <button className="btn btn-primary" disabled={Boolean(state.busy)} onClick={() => act('convert', `/sales/${d.id}/to-invoice`, (r) => navigate(`/sales/${r.id}`))}>
              <FileCheck2 className="h-4 w-4" /> Turn into invoice
            </button>
          )}
          {issued && phone && (balance > 0 || !invoice) && (
            <a className="btn btn-mpesa" href={`https://wa.me/${phone}?text=${encodeURIComponent(waText)}`} target="_blank" rel="noreferrer">
              <MessageCircle className="h-4 w-4" /> Send on WhatsApp
            </a>
          )}
          {invoice && issued && balance > 0 && !cur && can(session, 'sell') && data.payTo.prompt && (
            <button className="btn btn-mpesa" onClick={() => setPrompting(true)}>
              <Smartphone className="h-4 w-4" /> Ask for M-Pesa
            </button>
          )}
          {invoice && issued && balance > 0 && can(session, 'sell') && (
            <button className="btn btn-ghost" onClick={() => setPaying(true)}>
              <Wallet className="h-4 w-4" /> Record payment
            </button>
          )}
          <button className="btn btn-ghost" onClick={() => printAs(`${invoice ? 'Invoice' : 'Quote'} ${d.number} · ${d.customer?.name ?? ''}`)}>
            <Printer className="h-4 w-4" /> Print / PDF
          </button>
        </div>
      </header>
      {invoice && issued && <RequestPayment open={prompting} onClose={() => setPrompting(false)} invoiceId={d.id} number={d.number} balance={balance} phone={prettyPhone(d.customer?.phone) || ''} onPaid={reload} />}

      {state.error && (
        <div className="mb-4 space-y-2 print:hidden">
          <ErrorNote>{state.error}</ErrorNote>
          {state.error.includes('credit limit') && d.status === 'Draft' && can(session, 'finance') && (
            <button className="btn btn-ghost" disabled={Boolean(state.busy)} onClick={() => act('issue', `/sales/${d.id}/issue?overLimit=true`)}>Issue anyway, over the limit</button>
          )}
        </div>
      )}
      {invoice && issued && d.etimsStatus !== 'Signed' && d.etimsStatus !== 'NotRequired' && (
        <div className="mb-4 flex flex-wrap items-center justify-between gap-3 rounded-md bg-warn-soft px-4 py-3 text-sm text-warn print:hidden">
          <span>
            <b>Not yet signed with KRA eTIMS.</b> {d.etimsError}
          </span>
          <button className="btn btn-ghost py-1.5" disabled={Boolean(state.busy)} onClick={() => act('etims', `/sales/${d.id}/etims`)}>
            {state.busy === 'etims' ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />} Try again
          </button>
        </div>
      )}

      {invoice && <QuoteAuditCard invoiceId={d.id} />}
      <DocTrail kind={invoice ? 'invoice' : 'quote'} id={d.id} className="mb-5" />

      <div className="grid gap-5 lg:grid-cols-[1fr_18rem]">
        {/* The document as the customer sees it (and as it prints). */}
        <article className="card doc relative min-w-0 p-5 sm:p-10 print:p-0 print:shadow-none print:ring-0">
          {d.status === 'Draft' ? <Stamp word="Draft" tone="plain" /> : d.status === 'Void' ? <Stamp word="Void" tone="bad" /> : invoice && d.status === 'Paid' ? <Stamp word="Paid" tone="good" /> : null}
          <Letterhead
            title={invoice ? (session?.company.vatRegistered ? 'Tax invoice' : 'Invoice') : 'Quotation'}
            number={d.number}
            facts={[
              [invoice ? 'Invoice date' : 'Date', dayYear(d.issueDate)],
              [invoice ? 'Due' : 'Valid until', dayYear(d.dueDate)],
              ...(d.customer?.number ? [['Customer no.', d.customer.number] as [string, string]] : []),
            ]}
          />

          <div className="mt-6 grid gap-4 sm:grid-cols-[1fr_15rem]">
            <DocBox label={invoice ? 'Bill to' : 'Prepared for'}>
              <p className="font-semibold text-ink">{d.customer?.name}</p>
              <div className="mt-0.5 text-xs leading-relaxed text-ink-2">
                {d.customer?.address && <p className="whitespace-pre-line">{d.customer.address}</p>}
                {[prettyPhone(d.customer?.phone), d.customer?.email].filter(Boolean).length > 0 && <p>{[prettyPhone(d.customer?.phone), d.customer?.email].filter(Boolean).join('  ·  ')}</p>}
                {d.customer?.kraPin && <p>KRA PIN {d.customer.kraPin}</p>}
              </div>
            </DocBox>
            {/* What the reader came for, in the brand colour: what to pay and by when. */}
            <section className={cn('break-inside-avoid rounded-md p-4', invoice && issued && balance <= 0 ? 'bg-good-soft text-good' : 'bg-brand text-white')}>
              <p className="text-2xs font-semibold tracking-wide uppercase opacity-80">{!invoice ? 'Quote total' : issued && balance <= 0 ? 'Paid in full' : d.paid > 0 ? 'Balance due' : 'Amount due'}</p>
              <p className="num mt-1 font-display text-2xl font-semibold">{m(invoice ? (issued ? Math.max(docBalance, 0) : docTotal) : docTotal)}</p>
              <p className="mt-0.5 text-xs opacity-90">{!invoice ? `Valid until ${dayYear(d.dueDate)}` : issued && balance <= 0 ? `Thank you. Total ${m(docTotal)}` : `By ${dayYear(d.dueDate)}`}</p>
            </section>
          </div>

          <div className="mt-6 overflow-x-auto print:overflow-visible">
            <table className="doc-table min-w-[30rem]">
              <thead>
                <tr>
                  <th className="w-8">#</th>
                  <th>Description</th>
                  <th className="text-right">Qty</th>
                  <th className="text-right">Unit price</th>
                  <th className="text-right">VAT</th>
                  <th className="text-right">Amount</th>
                </tr>
              </thead>
              <tbody>
                {d.lines.map((l: any, i: number) => (
                  <tr key={l.id}>
                    <td className="num text-muted">{i + 1}</td>
                    <td className="font-medium text-ink">{l.description}</td>
                    <td className="num text-right whitespace-nowrap">
                      {l.quantity}
                      {l.unit && <span className="text-muted"> {l.unit}</span>}
                    </td>
                    <td className="num text-right">{m(cur ? l.fxUnitPrice : l.unitPrice)}</td>
                    <td className="text-right text-muted">{TAX_LABEL[l.taxType]}</td>
                    <td className="num text-right font-semibold">{m(cur ? l.fxTotal : l.total)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="mt-5 flex flex-wrap items-start justify-between gap-6">
            <div className="max-w-sm min-w-0 flex-1 text-sm">
              {d.notes && (
                <>
                  <p className="text-2xs font-semibold tracking-wide text-muted uppercase">Notes</p>
                  <p className="mt-1 whitespace-pre-line text-ink-2">{d.notes}</p>
                </>
              )}
            </div>
            <dl className="w-full max-w-72 space-y-1.5 text-sm break-inside-avoid">
              <div className="flex justify-between gap-4">
                <dt className="text-muted">Subtotal (before VAT)</dt>
                <dd className="num">{m(cur ? d.fxNet : d.net)}</dd>
              </div>
              {vatByRate.map(([rate, tax]) => (
                <div key={rate} className="flex justify-between gap-4">
                  <dt className="text-muted">VAT {rate}</dt>
                  <dd className="num">{m(tax)}</dd>
                </div>
              ))}
              <div className="flex justify-between gap-4 border-t-2 border-ink pt-2 text-base font-bold">
                <dt>Total</dt>
                <dd className="num">{m(docTotal)}</dd>
              </div>
              {d.paid > 0 && (
                <>
                  <div className="flex justify-between gap-4 text-good">
                    <dt>Paid</dt>
                    <dd className="num">−{m(docPaid)}</dd>
                  </div>
                  <div className="flex justify-between gap-4 rounded-sm bg-brand-soft px-2 py-1.5 font-bold">
                    <dt>Balance due</dt>
                    <dd className="num">{m(docBalance)}</dd>
                  </div>
                </>
              )}
              {/* KRA reads a tax invoice in shillings: the amounts it's signed and booked at. */}
              {cur && (
                <div className="mt-2 space-y-0.5 rounded-sm bg-paper px-2 py-1.5 text-xs text-muted">
                  <p className="font-semibold text-ink-2">In Kenya shillings at {rateText(d.rate)} per {cur}</p>
                  <div className="flex justify-between gap-4"><span>Total</span><span className="num">{ksh(d.total)}</span></div>
                  {d.tax > 0 && <div className="flex justify-between gap-4"><span>of which VAT</span><span className="num">{ksh(d.tax)}</span></div>}
                </div>
              )}
            </dl>
          </div>

          {(invoice && issued) || d.etimsStatus === 'Signed' ? (
            <div className="mt-6 grid gap-4 sm:grid-cols-2">
              {invoice && issued && balance > 0 && (
                <DocBox label="How to pay">
                  {data.payTo.paybill ? (
                    <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-0.5">
                      <dt className="text-muted">M-Pesa Paybill</dt>
                      <dd className="num font-bold text-ink">{data.payTo.paybill}</dd>
                      <dt className="text-muted">Account no.</dt>
                      <dd className="num font-bold text-ink">{d.number}</dd>
                    </dl>
                  ) : (
                    <p className="text-ink-2">{payLine}</p>
                  )}
                </DocBox>
              )}
              {d.etimsStatus === 'Signed' && (
                <section className="flex break-inside-avoid items-start justify-between gap-4 rounded-md bg-paper p-4 text-sm ring-1 ring-line">
                  <div className="min-w-0">
                    <p className="mb-1.5 text-2xs font-semibold tracking-wide text-muted uppercase">KRA eTIMS</p>
                    <dl className="text-xs leading-relaxed">
                      <dt className="text-muted">CU invoice no.</dt>
                      <dd className="num font-semibold break-all text-ink">{d.etimsInvoiceNo}</dd>
                      <dt className="mt-1 text-muted">Control code</dt>
                      <dd className="num font-semibold break-all text-ink">{d.etimsControlCode}</dd>
                    </dl>
                  </div>
                  {d.etimsQrUrl && <Qr value={d.etimsQrUrl} size={80} label="Scan to check this invoice with KRA" />}
                </section>
              )}
            </div>
          ) : null}

          <DocFooter>
            {invoice ? 'Thank you for your business.' : 'Thank you for the opportunity. Reply to confirm and we’ll get started.'}
            {lh.phone && ` Questions? Call ${prettyPhone(lh.phone)}.`}
          </DocFooter>
        </article>

        <aside className="space-y-4 print:hidden">
          {invoice && issued && (
            <section className="card p-4">
              <h2 className="text-sm font-semibold text-muted">Payments</h2>
              {data.payments.length ? (
                <ul className="mt-2 space-y-2.5">
                  {data.payments.map((p, i) => (
                    <li key={i} className="text-sm">
                      <div className="flex justify-between gap-2">
                        <b>{p.method === 'Mpesa' ? 'M-Pesa' : p.method}</b>
                        <span className="num font-semibold">{cur ? m(p.fxAmount) : ksh(p.amount)}</span>
                      </div>
                      {cur && <p className="num text-xs text-muted">{ksh(p.amount)} landed</p>}
                      <p className="text-xs text-muted">
                        {p.reference && `${p.reference} · `}
                        {dateTime(p.receivedAt)}
                      </p>
                      <p className="text-xs text-faint">Matched {p.matchedBy.startsWith('auto') ? 'automatically' : `by ${p.matchedBy.replace(/^user: /, '')}`}</p>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="mt-2 text-sm text-muted">{cur ? `None yet. When the ${cur} land, record what your bank credited.` : `None yet. M-Pesa payments with account ${d.number} will appear here by themselves.`}</p>
              )}
            </section>
          )}
          {invoice && d.etimsStatus === 'Signed' && (
            <section className="card p-4 text-sm">
              <Status tone="good">Signed with KRA eTIMS</Status>
              {d.etimsControlCode.startsWith('TEST-') && <p className="mt-2 text-xs text-muted">Test signature (eTIMS sandbox). Live signing starts after KRA certifies the integration.</p>}
            </section>
          )}
          {invoice && !cur && (d.status === 'Sent' || d.status === 'PartPaid') && can(session, 'finance') && (
            <button className="btn btn-ghost w-full" onClick={() => setWithholding(true)}>
              <FileCheck2 className="h-4 w-4" /> Customer withheld tax
            </button>
          )}
          {d.status !== 'Void' && d.paid === 0 && can(session, 'finance') && (
            <button className="btn btn-ghost w-full text-bad" disabled={Boolean(state.busy)} onClick={async () => (await confirmDialog({ title: `Void ${d.number}?`, body: 'This can’t be undone.', confirm: 'Void invoice', danger: true })) && act('void', `/sales/${d.id}/void`)}>
              <Ban className="h-4 w-4" /> Void {invoice ? 'invoice' : 'quote'}
            </button>
          )}
        </aside>
      </div>
      {paying && <RecordPayment doc={d} open={paying} onClose={() => setPaying(false)} onDone={reload} />}
      <Sheet open={withholding} onClose={() => setWithholding(false)} title={`Tax withheld on ${d.number}`}>
        {withholding && <Withheld doc={d} onDone={() => (setWithholding(false), reload())} />}
      </Sheet>
    </>
  );
}
