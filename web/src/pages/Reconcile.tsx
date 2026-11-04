// Reconcile: does the M-Pesa (or bank) statement agree with the books? The accountant is deciding "what still
// needs recording?", so the page leads with the difference between the statement and the books, then lists only
// the lines nothing in the books explains, each with the one action that fixes it.
import { useRef, useState } from 'react';
import { ArrowDownLeft, ArrowUpRight, Check, FileUp, Loader2 } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { cn, day, ksh } from '../lib/format';
import { Chips, Empty, ErrorNote, Loading, PageHeader, Sheet, Status, SheetFooter, askDialog } from '../components/ui';

const ACCOUNTS = [
  ['MPESA', 'M-Pesa'],
  ['BANK', 'Bank'],
  ['CASH', 'Cash'],
] as const;

export default function Reconcile() {
  const [account, setAccount] = useState<'MPESA' | 'BANK' | 'CASH'>('MPESA');
  const [show, setShow] = useState<'Unmatched' | 'Matched' | 'Ignored'>('Unmatched');
  const { data, error, reload } = useApi<any>(`/statements/${account}`);
  const [state, setState] = useState({ busy: false, error: '', note: '' });
  const file = useRef<HTMLInputElement>(null);
  const [acting, setActing] = useState<any>(null);

  const upload = async (f: File) => {
    setState({ busy: true, error: '', note: '' });
    try {
      const r = await api<any>('/statements/import', { body: { account, fileName: f.name, csv: await f.text() } });
      setState({ busy: false, error: '', note: `${r.lines} new line${r.lines === 1 ? '' : 's'} from ${day(r.from)} to ${day(r.to)}; ${r.matched} matched by themselves${r.skipped ? `; ${r.skipped} already imported` : ''}.` });
      reload();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message, note: '' });
    }
    if (file.current) file.current.value = '';
  };
  const lines = (data?.lines ?? []).filter((l: any) => l.status === show);

  return (
    <div>
      <PageHeader
        title="Reconcile"
        subtitle="Import a statement; everything the books already have is ticked off, and only what’s missing is left for you."
        actions={
          <>
            <input ref={file} type="file" accept=".csv,text/csv" className="hidden" onChange={(e) => e.target.files?.[0] && upload(e.target.files[0])} />
            <button className="btn btn-primary" disabled={state.busy} onClick={() => file.current?.click()}>
              {state.busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <FileUp className="h-4 w-4" />} Import {ACCOUNTS.find((a) => a[0] === account)![1]} statement (CSV)
            </button>
          </>
        }
      />
      <div className="mb-4">
        <Chips label="Account" value={account} onChange={(v) => setAccount(v)} options={ACCOUNTS.map(([id, label]) => [id, label])} />
      </div>
      {state.note && <p className="mb-4 rounded-md bg-good-soft px-4 py-2.5 text-sm font-medium text-good">{state.note}</p>}
      {state.error && <div className="mb-4"><ErrorNote>{state.error}</ErrorNote></div>}
      {error ? <ErrorNote>{error}</ErrorNote> : !data ? <Loading /> : !data.statement ? (
        <Empty title={`No ${ACCOUNTS.find((a) => a[0] === account)![1]} statement yet`}>
          {account === 'MPESA' ? 'On M-Pesa for Business (org.ke portal), download the statement for a month as CSV, then import it here.' : 'Download the statement from your bank’s online banking as CSV (Excel works too: save it as CSV), then import it here.'}
        </Empty>
      ) : (
        <>
          <div className="grid gap-3 sm:grid-cols-3">
            <div className="card p-4">
              <p className="text-sm text-muted">Statement balance on {day(data.statement.to)}</p>
              <p className="num mt-1 font-display text-2xl font-semibold">{data.statement.closingBalance != null ? ksh(data.statement.closingBalance) : '—'}</p>
            </div>
            <div className="card p-4">
              <p className="text-sm text-muted">Your books on the same day</p>
              <p className="num mt-1 font-display text-2xl font-semibold">{ksh(data.bookBalance)}</p>
            </div>
            <div className={cn('card p-4', data.difference === 0 ? 'bg-good-soft' : data.difference != null ? 'bg-warn-soft' : '')}>
              <p className="text-sm text-muted">Difference</p>
              <p className={cn('num mt-1 font-display text-2xl font-semibold', data.difference === 0 ? 'text-good' : 'text-warn')}>{data.difference == null ? '—' : data.difference === 0 ? 'None' : ksh(data.difference)}</p>
              <p className="text-xs text-muted">{data.counts.unmatched ? `${data.counts.unmatched} line${data.counts.unmatched === 1 ? '' : 's'} to sort out` : 'Every line is accounted for'}</p>
            </div>
          </div>
          {data.difference != null && data.difference !== 0 && data.counts.unmatched === 0 && (
            <p className="mt-3 text-sm text-muted">All lines are matched but the balances differ: the books may start from a different opening balance than the statement. Record the opening balance once and it will agree.</p>
          )}

          <div className="mt-5 mb-3">
            <Chips label="Show" value={show} onChange={setShow} options={[['Unmatched', 'To sort out', data.counts.unmatched], ['Matched', 'Matched', data.counts.matched], ['Ignored', 'Ignored', data.counts.ignored]]} />
          </div>
          {lines.length === 0 ? (
            <Empty title={show === 'Unmatched' ? 'Nothing to sort out' : 'Nothing here'}>{show === 'Unmatched' ? 'Every line on the statement is in the books.' : undefined}</Empty>
          ) : (
            <ul className="divide-y divide-line overflow-hidden rounded-lg bg-white ring-1 ring-line">
              {lines.map((l: any) => (
                <li key={l.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
                  <span className={cn('grid h-8 w-8 shrink-0 place-items-center rounded-full', l.moneyIn ? 'bg-good-soft text-good' : 'bg-bad-soft text-bad')}>
                    {l.moneyIn ? <ArrowDownLeft className="h-4 w-4" /> : <ArrowUpRight className="h-4 w-4" />}
                  </span>
                  <span className="min-w-0 flex-1">
                    <b className="block truncate text-sm">{l.description || l.reference}</b>
                    <span className="text-xs text-muted">{day(l.date)}{l.reference && <> · <span className="font-mono">{l.reference}</span></>}{l.matchKind && ` · ${l.matchKind === 'Payment' ? 'payment received' : l.matchKind === 'SupplierPayment' ? 'supplier paid' : 'in the books'}`}{l.note && ` · ${l.note}`}</span>
                  </span>
                  <span className={cn('num w-28 shrink-0 text-right font-semibold', l.moneyIn ? 'text-good' : 'text-ink')}>{l.moneyIn ? `+${ksh(l.moneyIn)}` : `−${ksh(l.moneyOut)}`}</span>
                  {l.status === 'Unmatched' ? (
                    <span className="flex w-full justify-end gap-2 sm:w-auto">
                      <button className="btn btn-primary py-1.5 text-sm" onClick={() => setActing(l)}>
                        {l.moneyIn ? 'Record payment' : 'Record expense'}
                      </button>
                      <button className="btn btn-ghost py-1.5 text-sm" onClick={async () => { const note = await askDialog({ title: 'Ignore this line?', body: 'It stays on the statement but drops off the list to match.', label: 'Why? (optional)', confirm: 'Ignore line' }); if (note === null) return; await api(`/statements/lines/${l.id}/ignore`, { body: { note } }); reload(); }}>
                        Ignore
                      </button>
                    </span>
                  ) : (
                    <Status tone={l.status === 'Matched' ? 'good' : 'plain'}>{l.status === 'Matched' ? <><Check className="h-3 w-3" /> Matched</> : 'Ignored'}</Status>
                  )}
                </li>
              ))}
            </ul>
          )}
        </>
      )}
      <Sheet open={Boolean(acting)} onClose={() => setActing(null)} title={acting?.moneyIn ? `Record ${ksh(acting?.moneyIn)} received` : `Record ${ksh(acting?.moneyOut ?? 0)} paid out`}>
        {acting && (acting.moneyIn ? <PaymentForm line={acting} onDone={() => (setActing(null), reload())} /> : <ExpenseForm line={acting} onDone={() => (setActing(null), reload())} />)}
      </Sheet>
    </div>
  );
}

function PaymentForm({ line, onDone }: { line: any; onDone: () => void }) {
  const [q, setQ] = useState('');
  const customers = useApi<any>(`/customers?q=${encodeURIComponent(q)}&take=8`);
  const [customer, setCustomer] = useState<any>(null);
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(`/statements/lines/${line.id}/payment`, { body: { customerId: customer?.id ?? null } });
      onDone();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <div className="space-y-4 text-sm">
      <p className="text-muted">“{line.description}” on {day(line.date)}. It’s recorded as a payment received; if the details name an invoice number or a customer’s phone, it’s applied to their invoice by itself.</p>
      <label className="block">
        <span className="mb-1.5 block font-semibold">Who paid? (optional)</span>
        <input className="field" value={q} onChange={(e) => (setQ(e.target.value), setCustomer(null))} placeholder="Search customers" />
      </label>
      <ul className="divide-y divide-line rounded-md ring-1 ring-line">
        {(customers.data?.page.items ?? []).map((c: any) => (
          <li key={c.id}>
            <button onClick={() => setCustomer(c)} className={cn('flex w-full justify-between px-3 py-2 text-left', customer?.id === c.id ? 'bg-brand-soft' : 'hover:bg-paper')}>
              <span>{c.name}</span>
              {c.owes > 0 && <span className="num text-muted">owes {ksh(c.owes)}</span>}
            </button>
          </li>
        ))}
      </ul>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button className="btn btn-primary" disabled={state.busy} onClick={save}>
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Record payment{customer ? ` from ${customer.name}` : ''}
      </button>
      </SheetFooter>
    </div>
  );
}

function ExpenseForm({ line, onDone }: { line: any; onDone: () => void }) {
  const accounts = useApi<any[]>('/reports/accounts');
  const expenses = (accounts.data ?? []).filter((a) => a.type === 'Expense');
  const [accountId, setAccountId] = useState('');
  const [memo, setMemo] = useState(line.description);
  const [state, setState] = useState({ busy: false, error: '' });
  const chosen = accountId || (line.suggestCharges ? expenses.find((a) => a.systemKey === 'BANK_CHARGES')?.id : '') || '';
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(`/statements/lines/${line.id}/expense`, { body: { accountId: chosen, memo } });
      onDone();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <div className="space-y-4 text-sm">
      <p className="text-muted">“{line.description}” on {day(line.date)}. If this paid a supplier’s bill, record it on the bill instead and it will match here by itself.</p>
      <label className="block">
        <span className="mb-1.5 block font-semibold">What was it for?</span>
        <select className="field" value={chosen} onChange={(e) => setAccountId(e.target.value)}>
          <option value="">Choose…</option>
          {expenses.map((a) => (
            <option key={a.id} value={a.id}>{a.name}</option>
          ))}
        </select>
      </label>
      <label className="block">
        <span className="mb-1.5 block font-semibold">Description</span>
        <input className="field" value={memo} onChange={(e) => setMemo(e.target.value)} />
      </label>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button className="btn btn-primary" disabled={state.busy || !chosen} onClick={save}>
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Record expense
      </button>
      </SheetFooter>
    </div>
  );
}
