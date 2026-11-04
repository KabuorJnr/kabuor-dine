// The books themselves, for the finance team: the chart of accounts with what each holds, and the general ledger
// (every entry, newest first, with its lines). An accountant adds their own accounts and posts manual journals for
// adjustments the app doesn't make by itself; the journal form shows debits against credits as they type, and won't
// post until they agree. Clicking an account opens its ledger.
import { Fragment, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { ArrowLeft, ChevronDown, ChevronRight, FileSpreadsheet, Loader2, PenLine, Plus, Trash2, Undo2 } from 'lucide-react';
import { api, can, download, useApi, useSession } from '../lib/api';
import { cn, day, ksh, todayIso } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, SearchBox, Sheet, Status, SheetFooter, confirmDialog } from '../components/ui';

type Account = { id: string; code: string; name: string; type: 'Asset' | 'Liability' | 'Equity' | 'Income' | 'Expense'; system: boolean; balance: number };
type Entry = { id: string; date: string; memo: string; source: string; manual: boolean; reversed: boolean; amount: number; lines: { accountId: string; code: string; name: string; debit: number; credit: number }[] };
type Journal = { total: number; items: Entry[] };

// Replies are checked before they're drawn: anything else (an older server, a proxy's error page) shows a message
// instead of breaking the page.
const isAccounts = (d: unknown): d is Account[] => Array.isArray(d);
const isJournal = (d: unknown): d is Journal => typeof d === 'object' && d !== null && Array.isArray((d as Journal).items) && typeof (d as Journal).total === 'number';
const BAD_REPLY = 'The books didn’t load properly. Reload the page; if it keeps happening, the server may be mid-update.';

const TYPES: [Account['type'], string, string][] = [
  ['Asset', 'Assets', 'What the business owns: cash, bank, stock, money owed to it.'],
  ['Liability', 'Liabilities', 'What it owes: suppliers, KRA, loans, staff deductions.'],
  ['Equity', 'Equity', 'The owner’s money in the business.'],
  ['Income', 'Income', 'Money earned: sales and other income.'],
  ['Expense', 'Expenses', 'Money spent running the business.'],
];

// Where an entry came from, in words.
const SOURCE: Record<string, string> = {
  Invoice: 'Invoice', InvoiceVoid: 'Void invoice', Payment: 'Payment', Allocation: 'Payment matched', Bill: 'Bill', BillVoid: 'Void bill',
  SupplierPayment: 'Supplier paid', Payroll: 'Payroll', PayrollDeductions: 'Payroll deductions', PayrollPaid: 'Salaries paid', Remittance: 'Statutory paid',
  StatementExpense: 'Expense', CostOfSales: 'Cost of sales', CostOfSalesVoid: 'Cost of sales reversed', OpeningStock: 'Opening stock', OpeningBalance: 'Opening balance',
  StockAdjustment: 'Stock adjustment', Production: 'Production', Withholding: 'Tax withheld', Manual: 'Manual journal', ManualReversal: 'Reversal',
};

function Accounts({ accounts, onOpen }: { accounts: Account[]; onOpen: (a: Account) => void }) {
  const [q, setQ] = useState('');
  const safeAccounts = Array.isArray(accounts) ? accounts : [];
  const shown = safeAccounts.filter((a) => !q || `${a.code ?? ''} ${a.name ?? ''}`.toLowerCase().includes(q.toLowerCase()));
  return (
    <>
      <div className="mb-3"><SearchBox value={q} onChange={setQ} placeholder="Search account code or name" /></div>
      {shown.length === 0 ? (
        <Empty title="No accounts found">{q ? 'Try another search term.' : 'No accounts exist yet. Click "+ Add account" to create one.'}</Empty>
      ) : (
        <div className="card overflow-hidden">
          <table className="w-full text-sm">
            <thead className="border-b border-line bg-paper text-left text-xs text-muted">
              <tr><th className="px-4 py-2.5">Code</th><th className="px-4 py-2.5">Account</th><th className="px-4 py-2.5 text-right">Balance</th></tr>
            </thead>
            <tbody>
              {TYPES.map(([type, label]) => {
                const list = shown.filter((a) => a.type === type);
                if (list.length === 0) return null;
                return (
                  <Fragment key={type}>
                    <tr className="bg-paper/60"><td colSpan={2} className="px-4 pt-3 pb-1.5 text-xs font-semibold tracking-wide text-muted uppercase">{label}</td><td className="num px-4 pt-3 pb-1.5 text-right text-xs font-semibold text-muted">{ksh(list.reduce((s, a) => s + (a.balance || 0), 0))}</td></tr>
                    {list.map((a) => (
                      <tr key={a.id} onClick={() => onOpen(a)} className="cursor-pointer border-t border-line/70 hover:bg-paper">
                        <td className="num px-4 py-2.5 text-muted">{a.code}</td>
                        <td className="px-4 py-2.5"><span className="font-medium">{a.name}</span>{!a.system && <span className="ml-2 text-xs text-faint">yours</span>}</td>
                        <td className={cn('num px-4 py-2.5 text-right font-semibold whitespace-nowrap', (a.balance || 0) === 0 && 'font-normal text-faint', (a.balance || 0) < 0 && 'text-bad')}>{(a.balance || 0) === 0 ? '—' : ksh(a.balance)}</td>
                      </tr>
                    ))}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

function Ledger({ accounts, accountId, setAccountId, canPost, onChanged }: { accounts: Account[]; accountId: string; setAccountId: (id: string) => void; canPost: boolean; onChanged: () => void }) {
  const [q, setQ] = useState('');
  const [query, setQuery] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [take, setTake] = useState(50);
  const [open, setOpen] = useState<string | null>(null);
  const [state, setState] = useState({ busy: '', error: '' });
  useEffect(() => {
    const t = setTimeout(() => setQuery(q), 250);
    return () => clearTimeout(t);
  }, [q]);
  const params = new URLSearchParams({ take: String(take), ...(accountId && { accountId }), ...(query && { q: query }), ...(from && { from }), ...(to && { to }) });
  const { data: reply, error, loading, reload } = useApi<Journal>(`/finance/journal?${params}`);
  const data = isJournal(reply) ? reply : null;
  const safeAccounts = Array.isArray(accounts) ? accounts : [];
  const account = safeAccounts.find((a) => a.id === accountId);
  const items = Array.isArray(data?.items) ? data.items : [];
  const total = typeof data?.total === 'number' ? data.total : 0;

  const reverse = async (e: Entry) => {
    if (!(await confirmDialog({ title: `Reverse “${e.memo}”?`, body: 'A mirror entry is posted today; the original stays in the books.', confirm: 'Reverse entry' }))) return;
    setState({ busy: e.id, error: '' });
    try {
      await api(`/finance/journal/${e.id}/reverse`, { method: 'POST' });
      reload();
      onChanged();
      setState({ busy: '', error: '' });
    } catch (err) {
      setState({ busy: '', error: (err as Error).message });
    }
  };
  const exportExcel = async () => {
    setState({ busy: 'export', error: '' });
    try {
      const f = from || `${todayIso().slice(0, 7)}-01`;
      await download(`/reports/export/general-ledger?from=${f}&to=${to || todayIso()}`, 'General ledger.xlsx');
      setState({ busy: '', error: '' });
    } catch (err) {
      setState({ busy: '', error: (err as Error).message });
    }
  };

  return (
    <>
      <div className="mb-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-[1fr_16rem_auto]">
        <SearchBox value={q} onChange={setQ} placeholder="Search what entries say: invoice no., supplier, memo" />
        <select className="field" value={accountId} onChange={(e) => setAccountId(e.target.value)} aria-label="Account">
          <option value="">All accounts</option>
          {TYPES.map(([type, label]) => (
            <optgroup key={type} label={label}>
              {safeAccounts.filter((a) => a.type === type).map((a) => <option key={a.id} value={a.id}>{a.code} · {a.name}</option>)}
            </optgroup>
          ))}
        </select>
        <div className="flex min-w-0 items-center gap-2 sm:col-span-2 lg:col-span-1">
          <input type="date" className="field min-w-0 flex-1 lg:w-auto lg:flex-none" value={from} onChange={(e) => setFrom(e.target.value)} aria-label="From" />
          <span className="text-muted">to</span>
          <input type="date" className="field min-w-0 flex-1 lg:w-auto lg:flex-none" value={to} onChange={(e) => setTo(e.target.value)} aria-label="To" />
        </div>
      </div>
      <div className="mb-3 flex flex-wrap items-center justify-between gap-2 text-sm text-muted">
        <span>
          {data ? `${total.toLocaleString('en-KE')} entr${total === 1 ? 'y' : 'ies'}` : ' '}
          {account && <> in <b className="text-ink">{account.code} {account.name}</b> · balance <b className="num text-ink">{ksh(account.balance)}</b></>}
        </span>
        <button className="btn btn-ghost py-1.5" onClick={exportExcel} disabled={state.busy === 'export'} title="The general ledger for these dates as an Excel workbook (this month if no dates are set)">
          {state.busy === 'export' ? <Loader2 className="h-4 w-4 animate-spin" /> : <FileSpreadsheet className="h-4 w-4" />} Excel
        </button>
      </div>
      {(error || state.error) && <ErrorNote>{error || state.error}</ErrorNote>}
      {reply && !data ? <ErrorNote>{BAD_REPLY}</ErrorNote> : loading && !data ? <Loading /> : items.length === 0 ? (
        <Empty title="No entries match">{query || accountId || from || to ? 'Try another account, word or date.' : 'Entries appear here as invoices, payments and bills are posted.'}</Empty>
      ) : (
        <div className="card overflow-hidden">
          <ul className="divide-y divide-line">
            {items.map((e) => (
              <li key={e.id}>
                <button className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-paper" onClick={() => setOpen(open === e.id ? null : e.id)} aria-expanded={open === e.id}>
                  {open === e.id ? <ChevronDown className="h-4 w-4 shrink-0 text-muted" /> : <ChevronRight className="h-4 w-4 shrink-0 text-muted" />}
                  <span className="num w-24 shrink-0 text-sm text-muted">{day(e.date)}</span>
                  <span className="min-w-0 flex-1">
                    <span className="line-clamp-1 text-sm font-medium">{e.memo}</span>
                    <span className="mt-0.5 flex flex-wrap gap-1.5">
                      <Status tone={e.manual ? 'info' : 'plain'}>{SOURCE[e.source] ?? e.source}</Status>
                      {e.reversed && <Status tone="warn">Reversed</Status>}
                    </span>
                  </span>
                  <b className="num shrink-0 text-sm">{ksh(e.amount)}</b>
                </button>
                {open === e.id && (
                  <div className="bg-paper/60 px-4 pt-1 pb-3 sm:pl-[8.25rem]">
                    <table className="w-full text-sm">
                      <thead className="text-left text-xs text-muted"><tr><th className="py-1.5">Account</th><th className="py-1.5 text-right">Debit</th><th className="py-1.5 text-right">Credit</th></tr></thead>
                      <tbody>
                        {(e.lines ?? []).map((l, i) => (
                          <tr key={i} className="border-t border-line/70">
                            <td className="py-1.5"><button className="text-left hover:text-brand" onClick={() => setAccountId(l.accountId)}>{l.code} · {l.name}</button></td>
                            <td className="num py-1.5 text-right">{l.debit ? ksh(l.debit) : ''}</td>
                            <td className="num py-1.5 text-right">{l.credit ? ksh(l.credit) : ''}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                    {canPost && e.manual && !e.reversed && (
                      <button className="btn btn-ghost mt-2 py-1.5 text-sm" onClick={() => reverse(e)} disabled={state.busy === e.id}>
                        {state.busy === e.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <Undo2 className="h-4 w-4" />} Reverse this journal
                      </button>
                    )}
                  </div>
                )}
              </li>
            ))}
          </ul>
          {items.length < total && (
            <button className="w-full border-t border-line py-3 text-sm font-semibold text-brand hover:bg-paper" onClick={() => setTake((t) => t + 50)}>
              Show more ({(total - items.length).toLocaleString('en-KE')} older)
            </button>
          )}
        </div>
      )}
    </>
  );
}

type Line = { key: number; accountId: string; debit: string; credit: string };
const cents = (v: string) => Math.round((parseFloat(v.replace(/,/g, '')) || 0) * 100);

function NewJournal({ accounts, onDone }: { accounts: Account[]; onDone: () => void }) {
  const [date, setDate] = useState(todayIso());
  const [memo, setMemo] = useState('');
  const [lines, setLines] = useState<Line[]>([{ key: 1, accountId: '', debit: '', credit: '' }, { key: 2, accountId: '', debit: '', credit: '' }]);
  const [state, setState] = useState({ busy: false, error: '' });
  const safeAccounts = Array.isArray(accounts) ? accounts : [];
  const dr = lines.reduce((s, l) => s + cents(l.debit), 0);
  const cr = lines.reduce((s, l) => s + cents(l.credit), 0);
  const used = lines.filter((l) => l.accountId && (cents(l.debit) || cents(l.credit)));
  const ready = memo.trim().length >= 3 && used.length >= 2 && dr === cr && dr > 0 && lines.every((l) => !(cents(l.debit) && cents(l.credit)));
  const set = (key: number, patch: Partial<Line>) => setLines((ls) => ls.map((l) => (l.key === key ? { ...l, ...patch } : l)));

  const post = async () => {
    setState({ busy: true, error: '' });
    try {
      await api('/finance/journal', { body: { date, memo, lines: used.map((l) => ({ accountId: l.accountId, debit: cents(l.debit), credit: cents(l.credit) })) } });
      onDone();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-[10rem_1fr]">
        <Field label="Date"><input type="date" className="field" value={date} max={todayIso()} onChange={(e) => setDate(e.target.value)} /></Field>
        <Field label="What it’s for" hint="Whoever reads the books next year should understand it."><input className="field" value={memo} onChange={(e) => setMemo(e.target.value)} placeholder="e.g. October depreciation of the Isuzu lorry" /></Field>
      </div>
      <div>
        <div className="mb-1.5 hidden grid-cols-[1fr_8rem_8rem_2.25rem] gap-2 text-xs font-semibold text-muted sm:grid"><span>Account</span><span className="text-right">Debit (KSh)</span><span className="text-right">Credit (KSh)</span><span /></div>
        <ul className="space-y-2">
          {lines.map((l, i) => (
            <li key={l.key} className="grid grid-cols-2 gap-2 rounded-md bg-paper p-2 sm:grid-cols-[1fr_8rem_8rem_2.25rem] sm:bg-transparent sm:p-0">
              <select className="field col-span-2 sm:col-span-1" value={l.accountId} onChange={(e) => set(l.key, { accountId: e.target.value })} aria-label={`Line ${i + 1} account`}>
                <option value="">Choose an account…</option>
                {TYPES.map(([type, label]) => (
                  <optgroup key={type} label={label}>
                    {safeAccounts.filter((a) => a.type === type).map((a) => <option key={a.id} value={a.id}>{a.code} · {a.name}</option>)}
                  </optgroup>
                ))}
              </select>
              <input className="field num text-right" inputMode="decimal" placeholder="Debit" value={l.debit} onChange={(e) => set(l.key, { debit: e.target.value, credit: e.target.value ? '' : l.credit })} aria-label={`Line ${i + 1} debit`} />
              <input className="field num text-right" inputMode="decimal" placeholder="Credit" value={l.credit} onChange={(e) => set(l.key, { credit: e.target.value, debit: e.target.value ? '' : l.debit })} aria-label={`Line ${i + 1} credit`} />
              <button type="button" className="col-span-2 grid h-9 w-9 place-items-center justify-self-end rounded-full text-muted hover:bg-bad-soft hover:text-bad sm:col-span-1" onClick={() => setLines((ls) => (ls.length > 2 ? ls.filter((x) => x.key !== l.key) : ls))} aria-label={`Remove line ${i + 1}`} disabled={lines.length <= 2}>
                <Trash2 className="h-4 w-4" />
              </button>
            </li>
          ))}
        </ul>
        <button type="button" className="mt-2 inline-flex items-center gap-1.5 text-sm font-semibold text-brand" onClick={() => setLines((ls) => [...ls, { key: Math.max(...ls.map((x) => x.key)) + 1, accountId: '', debit: '', credit: '' }])}>
          <Plus className="h-4 w-4" /> Add a line
        </button>
      </div>
      {/* The two sides as they're typed: posting waits until they agree. */}
      <div className={cn('flex flex-wrap items-center justify-between gap-2 rounded-md px-3 py-2.5 text-sm', dr === cr && dr > 0 ? 'bg-good-soft text-good' : 'bg-paper text-ink-2 ring-1 ring-line')}>
        <span className="num">Debits <b>{ksh(dr)}</b> · Credits <b>{ksh(cr)}</b></span>
        <b>{dr === 0 && cr === 0 ? 'Enter the amounts' : dr === cr ? 'Balanced' : `Off by ${ksh(Math.abs(dr - cr))}`}</b>
      </div>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button className="btn btn-primary" disabled={!ready || state.busy} onClick={post}>
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Post journal{dr > 0 && dr === cr ? ` · ${ksh(dr)}` : ''}
      </button>
      </SheetFooter>
    </div>
  );
}

function NewAccount({ accounts, onDone }: { accounts: Account[]; onDone: (a: Account) => void }) {
  const [f, setF] = useState({ code: '', name: '', type: 'Expense' as Account['type'] });
  const [state, setState] = useState({ busy: false, error: '' });
  const safeAccounts = Array.isArray(accounts) ? accounts : [];
  const taken = safeAccounts.some((a) => a.code.toLowerCase() === f.code.trim().toLowerCase());
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      onDone(await api<Account>('/finance/accounts', { body: f }));
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <div className="space-y-4">
      <fieldset>
        <legend className="mb-1.5 text-sm font-semibold">Kind of account</legend>
        <div className="grid gap-2">
          {TYPES.map(([type, label, help]) => (
            <label key={type} className={cn('flex cursor-pointer items-start gap-3 rounded-md p-3 ring-1', f.type === type ? 'bg-brand-soft ring-2 ring-brand' : 'bg-white ring-line')}>
              <input type="radio" name="type" className="mt-1 accent-[var(--color-brand)]" checked={f.type === type} onChange={() => setF({ ...f, type })} />
              <span><b className="block text-sm">{label}</b><span className="text-xs text-muted">{help}</span></span>
            </label>
          ))}
        </div>
      </fieldset>
      <div className="grid gap-3 sm:grid-cols-[8rem_1fr]">
        <Field label="Code" hint={taken ? <span className="text-bad">Already used</span> : 'Numbers like 6150'}><input className="field num" value={f.code} onChange={(e) => setF({ ...f, code: e.target.value })} /></Field>
        <Field label="Name"><input className="field" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. Fuel and transport" /></Field>
      </div>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button className="btn btn-primary" disabled={state.busy || taken || f.code.trim().length < 2 || f.name.trim().length < 2} onClick={save}>
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Add account
      </button>
      </SheetFooter>
    </div>
  );
}

export default function LedgerPage() {
  const session = useSession();
  const canPost = can(session, 'finance');
  const [params, setParams] = useSearchParams();
  const view = params.get('view') === 'ledger' || params.get('account') ? 'ledger' : 'accounts';
  const accountId = params.get('account') ?? '';
  const sheet = params.get('new');
  const accounts = useApi<Account[]>('/finance/accounts');
  const list = useMemo(() => (isAccounts(accounts.data) ? accounts.data : []), [accounts.data]);
  const go = (next: Record<string, string>) => setParams(Object.fromEntries(Object.entries(next).filter(([, v]) => v)), { replace: true });

  return (
    <>
      <PageHeader print
        title="Accounts & ledger"
        subtitle="Every account and what it holds, and every entry in the books, newest first."
        actions={canPost && (
          <>
            <button className="btn btn-primary" onClick={() => go({ view, account: accountId, new: 'journal' })}><PenLine className="h-4 w-4" /> New journal</button>
            <button className="btn btn-ghost" onClick={() => go({ view, account: accountId, new: 'account' })}><Plus className="h-4 w-4" /> Add account</button>
          </>
        )}
      />
      <div className="mb-4 flex flex-wrap items-center gap-3">
        <Chips label="Show" value={view} onChange={(v) => go({ view: v })} options={[['accounts', 'Chart of accounts', list.length || undefined], ['ledger', 'General ledger']]} />
        {accountId && (
          <button className="inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink" onClick={() => go({ view: 'ledger' })}><ArrowLeft className="h-4 w-4" /> All accounts</button>
        )}
      </div>
      {accounts.error ? <ErrorNote>{accounts.error}</ErrorNote> : !accounts.data ? <Loading /> : !isAccounts(accounts.data) ? <ErrorNote>{BAD_REPLY}</ErrorNote> : view === 'accounts' ? (
        <Accounts accounts={list} onOpen={(a) => go({ view: 'ledger', account: a.id })} />
      ) : (
        <Ledger accounts={list} accountId={accountId} setAccountId={(id) => go({ view: 'ledger', account: id })} canPost={canPost} onChanged={accounts.reload} />
      )}
      <Sheet open={sheet === 'journal' && canPost} onClose={() => go({ view, account: accountId })} title="New journal" wide>
        {sheet === 'journal' && <NewJournal accounts={list} onDone={() => (accounts.reload(), go({ view: 'ledger' }))} />}
      </Sheet>
      <Sheet open={sheet === 'account' && canPost} onClose={() => go({ view, account: accountId })} title="Add an account">
        {sheet === 'account' && <NewAccount accounts={list} onDone={() => (accounts.reload(), go({ view: 'accounts' }))} />}
      </Sheet>
    </>
  );
}
