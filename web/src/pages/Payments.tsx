// Money in. Most M-Pesa payments match themselves; this screen is for the rest. For each one the person
// decides "which invoice is this for?", so each card shows who paid, what they typed, and the best guess.
import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { Check, Loader2, Plus, RefreshCw, Search } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, dateTime, ksh, prettyPhone, toCents } from '../lib/format';
import { useLang } from '../lib/i18n';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, SearchBox, Sheet, Status, SheetFooter } from '../components/ui';

type Payment = { id: string; method: string; amount: number; allocated: number; receivedAt: string; reference: string; payerPhone: string; payerName: string; accountRef: string; status: string; source?: string; fromSafaricom?: boolean; callbackIp?: string };

/** Where a payment came from, in the words a lender would use: only what Safaricom's servers sent counts as proof. */
export function SourceLabel({ p }: { p: Pick<Payment, 'source' | 'fromSafaricom' | 'callbackIp'> }) {
  const mpesa = p.source === 'MpesaConfirmation' || p.source === 'MpesaPrompt';
  if (p.fromSafaricom) return <span className="text-xs font-semibold text-good">Confirmed by Safaricom</span>;
  if (mpesa && !p.callbackIp) return <span className="text-xs text-muted">From M-Pesa (before address checks)</span>;
  if (mpesa) return <span className="text-xs font-semibold text-bad">Not from Safaricom’s servers</span>;
  const words: Record<string, string> = { StatementImport: 'From a statement upload', ConnectedSystem: 'Reported by a connected app', Till: 'Recorded at the till', Manual: 'Entered by hand' };
  return <span className="text-xs text-muted">{words[p.source ?? ''] ?? 'Source not yet known'}</span>;
}
type Suggestion = { reason: string; auto: boolean; allocations: { invoiceId: string; invoiceNumber: string; amount: number }[] } | null;

function MatchCard({ p, onDone }: { p: Payment; onDone: () => void }) {
  const session = useSession();
  const { data } = useApi<{ suggestion: Suggestion }>(`/payments/${p.id}`);
  const [find, setFind] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });
  const left = p.amount - p.allocated;
  const finance = can(session, 'finance');

  const allocate = async (allocations: { invoiceId: string; amount: number }[]) => {
    setState({ busy: true, error: '' });
    try {
      await api(`/payments/${p.id}/allocate`, { body: { allocations } });
      onDone();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const byNumber = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      const r = await api<{ page: { items: { id: string; number: string; total: number; paid: number; status: string }[] } }>(`/sales?kind=Invoice&status=unpaid&q=${encodeURIComponent(find.trim())}&take=5`);
      const inv = r.page.items.find((i) => i.number.toLowerCase() === find.trim().toLowerCase()) ?? (r.page.items.length === 1 ? r.page.items[0] : null);
      if (!inv) throw new Error(r.page.items.length ? 'More than one unpaid invoice matches. Type the full invoice number.' : 'No unpaid invoice with that number or customer.');
      await allocate([{ invoiceId: inv.id, amount: Math.min(left, inv.total - inv.paid) }]);
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  const s = data?.suggestion;

  return (
    <li className="card p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <b className="block truncate text-sm">{p.payerName || prettyPhone(p.payerPhone) || 'Unknown payer'}</b>
          <p className="text-sm text-muted">
            {p.method === 'Mpesa' ? 'M-Pesa' : p.method}
            {p.reference && ` ${p.reference}`} · {dateTime(p.receivedAt)}
            {p.payerName && p.payerPhone && ` · ${prettyPhone(p.payerPhone)}`}
          </p>
          <SourceLabel p={p} />
          {p.accountRef && (
            <p className="mt-1 text-sm">
              Typed as account: <b className="rounded bg-paper px-1.5 py-0.5 font-mono text-xs">{p.accountRef}</b>
            </p>
          )}
        </div>
        <div className="text-right">
          <b className="num font-display text-xl">{ksh(left)}</b>
          {p.allocated > 0 && <p className="text-xs text-muted">of {ksh(p.amount)}</p>}
        </div>
      </div>
      {finance ? (
        <div className="mt-3 space-y-2">
          {s && s.allocations.length > 0 && (
            <div className="flex flex-wrap items-center justify-between gap-2 rounded-md bg-brand-soft px-3 py-2.5">
              <p className="text-sm">
                <b>Looks like {s.allocations.map((a) => a.invoiceNumber).join(' + ')}</b>
                <span className="block text-xs text-muted">{s.reason}</span>
              </p>
              <button className="btn btn-primary py-2" disabled={state.busy} onClick={() => allocate(s.allocations.map((a) => ({ invoiceId: a.invoiceId, amount: a.amount })))}>
                {state.busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />} Apply {ksh(s.allocations.reduce((x, a) => x + a.amount, 0))}
              </button>
            </div>
          )}
          <form onSubmit={byNumber} className="flex gap-2">
            <label className="relative flex-1">
              <Search className="pointer-events-none absolute top-1/2 left-3 h-4 w-4 -translate-y-1/2 text-faint" />
              <input className="field py-2 pl-9" value={find} onChange={(e) => setFind(e.target.value)} placeholder={s ? 'Or another invoice no. / customer' : 'Which invoice? Type its number or the customer'} aria-label="Invoice number or customer" />
            </label>
            <button className="btn btn-ghost py-2" disabled={state.busy || !find.trim()}>
              Apply
            </button>
          </form>
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
        </div>
      ) : (
        <p className="mt-3 text-sm text-muted">An accountant or the owner will match this one.</p>
      )}
    </li>
  );
}

function NewPayment({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const [f, setF] = useState({ method: 'Mpesa', amount: '', reference: '', payerName: '', payerPhone: '', accountRef: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      await api('/payments', { body: { ...f, amount: toCents(f.amount) } });
      onDone();
      onClose();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  const set = (k: keyof typeof f) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value });
  return (
    <Sheet open onClose={onClose} title="Record a payment" description="Money received by bank, cash or M-Pesa. Match it to an invoice now or later.">
      <form onSubmit={submit} className="space-y-4">
        <div className="grid grid-cols-3 gap-2">
          {[['Mpesa', 'M-Pesa'], ['Cash', 'Cash'], ['Bank', 'Bank']].map(([v, l]) => (
            <button key={v} type="button" aria-pressed={f.method === v} onClick={() => setF({ ...f, method: v })} className={f.method === v ? 'btn bg-brand-soft text-brand ring-1 ring-brand/40' : 'btn btn-ghost'}>
              {l}
            </button>
          ))}
        </div>
        <Field label="Amount (KSh)">
          <input className="field num" inputMode="decimal" required value={f.amount} onChange={set('amount')} />
        </Field>
        <Field label={f.method === 'Mpesa' ? 'M-Pesa code' : 'Reference'}>
          <input className="field uppercase" value={f.reference} onChange={set('reference')} required={f.method === 'Mpesa'} />
        </Field>
        <Field label="Who paid">
          <input className="field" value={f.payerName} onChange={set('payerName')} />
        </Field>
        <Field label="Their phone" hint="Helps match it to the right customer.">
          <input className="field" inputMode="tel" value={f.payerPhone} onChange={set('payerPhone')} />
        </Field>
        <Field label="Invoice or account they quoted">
          <input className="field" value={f.accountRef} onChange={set('accountRef')} placeholder="e.g. INV-0012" />
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

export default function Payments() {
  const session = useSession();
  const { t } = useLang();
  const [status, setStatus] = useState('to-match');
  const [q, setQ] = useState('');
  const [query, setQuery] = useState('');
  const [adding, setAdding] = useState(false);
  const [retry, setRetry] = useState<{ busy: boolean; note: string }>({ busy: false, note: '' });
  useEffect(() => {
    const id = setTimeout(() => setQuery(q), 250);
    return () => clearTimeout(id);
  }, [q]);
  const { data, error, reload } = useApi<{ counts: { all: number; toMatch: number; matched: number; confirmed?: number }; page: { items: Payment[]; total: number } }>(`/payments?status=${status}&q=${encodeURIComponent(query)}&take=50`);
  const rows = data?.page.items ?? [];
  const toMatchTotal = status === 'to-match' ? rows.reduce((s, p) => s + p.amount - p.allocated, 0) : 0;

  const retryAll = async () => {
    setRetry({ busy: true, note: '' });
    try {
      const r = await api<{ matched: number }>('/payments/retry-matching', { body: {} });
      setRetry({ busy: false, note: r.matched ? `${r.matched} matched.` : 'Nothing new could be matched automatically.' });
      reload();
    } catch (e) {
      setRetry({ busy: false, note: (e as Error).message });
    }
  };

  return (
    <>
      <PageHeader print
        title={t('Money in')}
        subtitle={status === 'to-match' && data ? (rows.length ? `${ksh(toMatchTotal)} waiting to be matched to invoices` : 'Everything is matched') : 'Every payment received: M-Pesa, cash and bank'}
        actions={
          <>
            {can(session, 'finance') && status === 'to-match' && rows.length > 0 && (
              <button className="btn btn-ghost" onClick={retryAll} disabled={retry.busy}>
                {retry.busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />} Try matching again
              </button>
            )}
            {can(session, 'sell') && (
              <button className="btn btn-primary" onClick={() => setAdding(true)}>
                <Plus className="h-4 w-4" /> {t('Record payment')}
              </button>
            )}
          </>
        }
      />
      {retry.note && <p className="mb-3 text-sm font-medium text-muted">{retry.note}</p>}
      <div className="mb-4 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <Chips label="Filter payments" value={status} onChange={setStatus} options={[['to-match', t('To match'), data?.counts.toMatch], ['matched', t('Matched'), data?.counts.matched], ['confirmed', 'Confirmed by Safaricom', data?.counts.confirmed], ['all', t('All'), data?.counts.all]]} />
        <SearchBox value={q} onChange={setQ} placeholder="Search M-Pesa code, name, phone or account" />
      </div>
      {error && <ErrorNote>{error}</ErrorNote>}
      {!data ? (
        <Loading />
      ) : rows.length === 0 ? (
        <Empty title={status === 'to-match' ? 'Nothing to match' : 'No payments yet'}>
          {status === 'to-match' ? 'Payments that can’t be matched by themselves will wait here.' : (
            <>
              Connect your Paybill or Till in <Link to="/settings" className="font-semibold text-brand">Settings</Link> and M-Pesa payments will arrive here by themselves.
            </>
          )}
        </Empty>
      ) : status === 'to-match' ? (
        <ul className="space-y-3">
          {rows.map((p) => (
            <MatchCard key={p.id} p={p} onDone={reload} />
          ))}
        </ul>
      ) : (
        <ul className="card divide-y divide-line">
          {rows.map((p) => (
            <li key={p.id} className="flex items-center gap-3 px-4 py-3">
              <span className="min-w-0 flex-1">
                <b className="block truncate">{p.payerName || prettyPhone(p.payerPhone) || (p.method === 'Mpesa' ? 'M-Pesa' : p.method)}</b>
                <span className="text-sm text-muted">
                  {p.reference && `${p.reference} · `}
                  {dateTime(p.receivedAt)}
                </span>
                <span className="block">
                  <SourceLabel p={p} />
                </span>
              </span>
              <span className="text-right">
                <b className="num block">{ksh(p.amount)}</b>
                <span className={cn('text-xs')}>{p.status === 'Matched' ? <Status tone="good">Matched</Status> : <Status tone="warn">{ksh(p.amount - p.allocated)} to match</Status>}</span>
              </span>
            </li>
          ))}
        </ul>
      )}
      {adding && <NewPayment onClose={() => setAdding(false)} onDone={reload} />}
    </>
  );
}
