// Cash advances (imprest): money handed to staff before they spend it for the business, signed off against the
// department's budget, paid out, then accounted for with receipts. Finance sees what's waiting, what to pay, what's
// out with staff (and overdue), and every decision in each advance's trail.
import { useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, day, dateTime, ksh, toCents, todayIso } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageError, PageHeader, Sheet, SheetFooter, Stat, Status, askDialog } from '../components/ui';
import { Trail } from './Requisitions';
import DocTrail from '../components/DocTrail';

export type AdvanceRow = {
  id: string; number: string; employeeId: string; employee: string; departmentId: string; department: string; purpose: string; amount: number; accountBy: string;
  status: 'WithHead' | 'WithFinance' | 'SignedOff' | 'PaidOut' | 'Accounted' | 'Rejected' | 'Cancelled'; overBudget: boolean; signedOffBy: string; financeNote: string;
  paidBy: string | null; paidReference: string; paidAt: string | null; spent: number; returned: number; recoveredFromPay: number; reimbursed: number; accountedAt: string | null; rejectReason: string; createdAt: string; overdue: boolean;
};
export const ADV_STATUS: Record<AdvanceRow['status'], [string, 'good' | 'warn' | 'bad' | 'info' | 'plain']> = {
  WithHead: ['With the head of department', 'warn'], WithFinance: ['With finance', 'warn'], SignedOff: ['Signed off, to pay', 'info'], PaidOut: ['Out with staff', 'info'],
  Accounted: ['Accounted for', 'good'], Rejected: ['Turned down', 'bad'], Cancelled: ['Cancelled', 'plain'],
};
type Detail = {
  advance: AdvanceRow; expenses: { description: string; account: string; amount: number; receipt: string; date: string }[];
  budget: { budget: number | null; spent: number; committed: number; available: number; hasBudget: boolean; over: boolean } | null;
  trail: Parameters<typeof Trail>[0]['rows'];
};

export default function Advances() {
  const session = useSession();
  const [view, setView] = useState('waiting');
  const { data, error, reload } = useApi<{ counts: Record<string, number>; outstandingValue: number; items: AdvanceRow[] }>(`/advances?view=${view}`);
  const [open, setOpen] = useState<string | null>(null);
  const [asking, setAsking] = useState(false);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const c = data.counts;
  return (
    <div className="space-y-5">
      <PageHeader print
        title="Cash advances"
        subtitle="Money given to staff before they spend it for the business. Signed off against the department’s budget, paid out, then accounted for with receipts. Nobody gets a new one while an old one is overdue."
        actions={(can(session, 'finance') || can(session, 'people')) && <button className="btn btn-primary" onClick={() => setAsking(true)}><Plus className="h-4 w-4" /> New advance</button>}
      />
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Waiting for a decision" value={c.waiting} />
        <Stat label="Signed off, to pay" value={c.toPay} />
        <Stat label="Out with staff" value={ksh(data.outstandingValue)}>{c.outstanding} advance(s)</Stat>
        <Stat label="Overdue receipts" value={c.overdue} tone={c.overdue > 0 ? 'bad' : undefined} />
      </div>
      <Chips label="Show" value={view} onChange={setView} options={[['waiting', 'Waiting', c.waiting], ['pay', 'To pay', c.toPay], ['outstanding', 'Out with staff', c.outstanding], ['done', 'Done', c.done], ['all', 'All']]} />
      {data.items.length === 0 ? <Empty title="Nothing here">Staff ask for advances from their portal; finance and HR can raise one here.</Empty> : (
        <ul className="card divide-y divide-line">
          {data.items.map((a) => (
            <li key={a.id}>
              <button className="flex w-full items-start gap-3 px-4 py-3 text-left hover:bg-paper" onClick={() => setOpen(a.id)}>
                <span className="min-w-0 flex-1">
                  <span className="flex flex-wrap items-baseline gap-x-2"><code className="whitespace-nowrap text-xs font-semibold text-muted">{a.number}</code><b className="text-sm">{a.employee}</b></span>
                  <span className="mt-0.5 block text-sm text-muted">{a.purpose} · {a.department}</span>
                  <span className="mt-1.5 flex flex-wrap gap-1.5">{a.overdue && <Status tone="bad">Receipts overdue</Status>}<Status tone={ADV_STATUS[a.status][1]}>{ADV_STATUS[a.status][0]}</Status></span>
                </span>
                <span className="num shrink-0 text-sm font-semibold">{ksh(a.amount)}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
      {open && <AdvanceSheet id={open} onClose={() => setOpen(null)} onChange={reload} />}
      {asking && <AdvanceForm onClose={() => setAsking(false)} onSaved={(id) => (setAsking(false), reload(), setOpen(id))} />}
    </div>
  );
}

/** Asking for an advance: from the app (finance or HR, for an employee) or the portal (for yourself). */
export function AdvanceForm({ portal, onClose, onSaved }: { portal?: boolean; onClose: () => void; onSaved: (id: string) => void }) {
  const employees = useApi<{ id: string; name: string; department: string }[]>(portal ? null : '/directory/employees');
  const departments = useApi<{ id: string; name: string; active?: boolean }[]>(portal ? null : '/departments');
  const inWeek = new Date(Date.now() + 7 * 864e5).toISOString().slice(0, 10);
  const [f, setF] = useState({ employeeId: '', departmentId: '', purpose: '', amount: '', accountBy: inWeek });
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      const r = await api<{ advance: { id: string } }>(portal ? '/me/advances' : '/advances', { body: { employeeId: f.employeeId || null, departmentId: f.departmentId || null, purpose: f.purpose, amount: toCents(f.amount || '0'), accountBy: f.accountBy } });
      onSaved(r.advance.id);
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title="Ask for a cash advance" description="It goes to the head of department (if there is one), then finance signs it off against the department’s budget.">
      <div className="space-y-4">
        {!portal && (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="For"><select className="field" value={f.employeeId} onChange={(e) => setF({ ...f, employeeId: e.target.value })}><option value="">Choose…</option>{(employees.data ?? []).map((e) => <option key={e.id} value={e.id}>{e.name}{e.department ? ` · ${e.department}` : ''}</option>)}</select></Field>
            <Field label="Charged to" hint="Empty: their own department."><select className="field" value={f.departmentId} onChange={(e) => setF({ ...f, departmentId: e.target.value })}><option value="">Their department</option>{(departments.data ?? []).filter((d) => d.active !== false).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}</select></Field>
          </div>
        )}
        <Field label="What it’s for"><input className="field" value={f.purpose} onChange={(e) => setF({ ...f, purpose: e.target.value })} placeholder="Fuel and lodging, Kisumu site visit, 3 days" /></Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Amount (KSh)"><input className="field num" inputMode="decimal" value={f.amount} onChange={(e) => setF({ ...f, amount: e.target.value })} /></Field>
          <Field label="Receipts in by"><input className="field" type="date" min={todayIso()} value={f.accountBy} onChange={(e) => setF({ ...f, accountBy: e.target.value })} /></Field>
        </div>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy || !f.amount || f.purpose.trim().length < 5 || (!portal && !f.employeeId)} onClick={save}>Send the request</button></SheetFooter>
      </div>
    </Sheet>
  );
}

function AdvanceSheet({ id, onClose, onChange }: { id: string; onClose: () => void; onChange: () => void }) {
  const session = useSession();
  const finance = can(session, 'finance');
  const { data, error, reload } = useApi<Detail>(`/advances/${id}`);
  const [state, setState] = useState({ busy: false, error: '' });
  const [accounting, setAccounting] = useState(false);
  const [payMethod, setPayMethod] = useState('Cash');
  const act = async (path: string, body: object = {}) => {
    setState({ busy: true, error: '' });
    try { await api(`/advances/${id}/${path}`, { body }); reload(); onChange(); setState({ busy: false, error: '' }); } catch (e) { setState({ busy: false, error: (e as Error).message }); }
  };
  if (error) return <Sheet open onClose={onClose} title="Advance"><ErrorNote>{error}</ErrorNote></Sheet>;
  if (!data) return <Sheet open onClose={onClose} title="Advance"><Loading /></Sheet>;
  const a = data.advance;
  const b = data.budget;
  return (
    <Sheet open onClose={onClose} title={`${a.number} · ${a.employee}`} description={`${a.department} · asked ${dateTime(a.createdAt)} · receipts by ${day(a.accountBy)}`} size="lg">
      <div className="space-y-5 text-sm">
        <div className="flex flex-wrap items-center gap-2">
          <Status tone={ADV_STATUS[a.status][1]}>{ADV_STATUS[a.status][0]}</Status>
          {a.overdue && <Status tone="bad">Receipts overdue</Status>}
          <span className="num text-base font-semibold">{ksh(a.amount)}</span>
        </div>
        <p className="rounded-md bg-paper p-3">{a.purpose}</p>
        {a.rejectReason && <p className="rounded-md bg-bad-soft p-3 text-bad">Turned down: {a.rejectReason}</p>}
        {a.financeNote && <p className="rounded-md bg-warn-soft p-3 text-warn">Finance: {a.financeNote}</p>}
        {b && (
          <div className={cn('rounded-md p-3 ring-1', b.over || !b.hasBudget ? 'bg-bad-soft ring-bad/30' : 'bg-paper ring-line')}>
            {b.hasBudget ? <>
              <p className={cn('font-semibold', b.over && 'text-bad')}>{b.over ? `Over what’s left of the budget by ${ksh(a.amount - Math.max(0, b.available))}` : `Within budget: ${ksh(b.available - a.amount)} left after this`}</p>
              <p className="mt-1 text-muted">This month: approved {ksh(b.budget ?? 0)} · spent {ksh(b.spent)} · committed {ksh(b.committed)}.</p>
            </> : <p className="font-semibold text-bad">{a.department} has no approved budget this month, so it can’t be signed off.</p>}
          </div>
        )}
        {a.status === 'PaidOut' || a.status === 'Accounted' ? (
          <dl className="grid grid-cols-2 gap-x-4 gap-y-1">
            <dt className="text-muted">Paid out</dt><dd>{a.paidBy} {a.paidReference} {a.paidAt && `· ${dateTime(a.paidAt)}`}</dd>
            {a.status === 'Accounted' && <>
              <dt className="text-muted">Receipts</dt><dd className="num">{ksh(a.spent)}</dd>
              {a.returned > 0 && <><dt className="text-muted">Handed back</dt><dd className="num">{ksh(a.returned)}</dd></>}
              {a.recoveredFromPay > 0 && <><dt className="text-muted">From next pay</dt><dd className="num">{ksh(a.recoveredFromPay)}</dd></>}
              {a.reimbursed > 0 && <><dt className="text-muted">Paid back to them</dt><dd className="num">{ksh(a.reimbursed)}</dd></>}
            </>}
          </dl>
        ) : null}
        {data.expenses.length > 0 && (
          <div className="overflow-x-auto rounded-md ring-1 ring-line">
            <table className="w-full min-w-[28rem]">
              <thead className="bg-paper text-left text-xs text-muted"><tr><th className="px-3 py-2">Receipt</th><th className="px-3 py-2">For</th><th className="px-3 py-2">Account</th><th className="px-3 py-2 text-right">Amount</th></tr></thead>
              <tbody className="divide-y divide-line">{data.expenses.map((x, i) => <tr key={i}><td className="px-3 py-2 font-mono text-xs">{x.receipt || '—'}</td><td className="px-3 py-2">{x.description}</td><td className="px-3 py-2 text-muted">{x.account}</td><td className="num px-3 py-2 text-right">{ksh(x.amount)}</td></tr>)}</tbody>
            </table>
          </div>
        )}
        {finance && (
          <div className="flex flex-wrap gap-2">
            {a.status === 'WithFinance' && <button className="btn btn-primary" disabled={state.busy} onClick={async () => {
              let reason = '';
              if (b?.over) { reason = (await askDialog({ title: `Sign off ${a.number} over budget?`, body: 'It stays in the approvals register for good.', label: 'Why', required: true, confirm: 'Sign off' })) ?? ''; if (!reason) return; }
              act('sign-off', { reason });
            }}>Sign off the money</button>}
            {a.status === 'SignedOff' && <select className="field w-auto" value={payMethod} onChange={(e) => setPayMethod(e.target.value)} aria-label="Paid by"><option value="Cash">Cash</option><option value="Mpesa">M-Pesa</option><option value="Bank">Bank</option></select>}
            {a.status === 'SignedOff' && <button className="btn btn-primary" disabled={state.busy} onClick={async () => {
              const ref = await askDialog({ title: `Pay out ${ksh(a.amount)} to ${a.employee}?`, body: 'Record how it was handed over (this doesn’t send money). They then owe it until they account for it.', label: 'Reference (voucher or M-Pesa code)', confirm: 'Paid out' });
              if (ref !== null) act('pay', { method: payMethod, reference: ref });
            }}>Record pay-out</button>}
            {a.status === 'PaidOut' && <button className="btn btn-primary" onClick={() => setAccounting(true)}>Account for it</button>}
            {(a.status === 'WithFinance' || a.status === 'SignedOff' || a.status === 'WithHead') && <button className="btn btn-ghost text-bad" disabled={state.busy} onClick={async () => { const reason = await askDialog({ title: `Turn down ${a.number}?`, label: 'Why (they see this)', required: true, confirm: 'Turn down', danger: true }); if (reason) act('reject', { reason }); }}>Turn down</button>}
          </div>
        )}
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <DocTrail kind="advance" id={id} />
        {data.trail.length > 0 && <Trail rows={data.trail} />}
      </div>
      {accounting && <AccountForm advance={a} path={`/advances/${id}/account`} onClose={() => setAccounting(false)} onDone={() => (setAccounting(false), reload(), onChange())} />}
    </Sheet>
  );
}

/** Receipts against an advance, and what happens to what's left. Shared with the portal. */
export function AccountForm({ advance, path, accountsPath = '/expense-accounts', onClose, onDone }: { advance: AdvanceRow; path: string; accountsPath?: string; onClose: () => void; onDone: () => void }) {
  const accounts = useApi<{ id: string; name: string }[]>(accountsPath);
  const [rows, setRows] = useState([{ description: '', amount: '', receipt: '', accountId: '' }]);
  const [leftover, setLeftover] = useState<'returned' | 'payroll'>('returned');
  const [method, setMethod] = useState('Cash');
  const [state, setState] = useState({ busy: false, error: '' });
  const spent = rows.reduce((s, r) => s + toCents(r.amount || '0'), 0);
  const left = advance.amount - spent;
  const set = (i: number, patch: Partial<(typeof rows)[number]>) => setRows(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)));
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(path, { body: { expenses: rows.filter((r) => r.description.trim() && r.amount).map((r) => ({ description: r.description, amount: toCents(r.amount), receipt: r.receipt, accountId: r.accountId || null })), leftover, method } });
      onDone();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title={`Account for ${advance.number}`} description={`${ksh(advance.amount)} was paid out. Add each receipt.`} size="md">
      <div className="space-y-4">
        {rows.map((r, i) => (
          <div key={i} className="grid gap-2 rounded-md bg-paper p-2 ring-1 ring-line sm:grid-cols-[1fr_7rem_auto]">
            <div className="space-y-2">
              <input className="field" value={r.description} onChange={(e) => set(i, { description: e.target.value })} placeholder="Fuel, lodging, matatu fare…" aria-label="What for" />
              <div className="grid grid-cols-2 gap-2">
                <input className="field" value={r.receipt} onChange={(e) => set(i, { receipt: e.target.value })} placeholder="Receipt or M-Pesa code" aria-label="Receipt" />
                <select className="field" value={r.accountId} onChange={(e) => set(i, { accountId: e.target.value })} aria-label="Account"><option value="">Other expenses</option>{(accounts.data ?? []).map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}</select>
              </div>
            </div>
            <input className="field num" inputMode="decimal" value={r.amount} onChange={(e) => set(i, { amount: e.target.value })} placeholder="KSh" aria-label="Amount" />
            <button className="btn btn-ghost px-2" aria-label="Remove receipt" disabled={rows.length === 1} onClick={() => setRows(rows.filter((_, j) => j !== i))}><Trash2 className="h-4 w-4" /></button>
          </div>
        ))}
        <button className="btn btn-ghost" onClick={() => setRows([...rows, { description: '', amount: '', receipt: '', accountId: '' }])}><Plus className="h-4 w-4" /> Another receipt</button>
        <div className="rounded-md p-3 ring-1 ring-line">
          <p className="text-sm">Receipts <b className="num">{ksh(spent)}</b> of {ksh(advance.amount)}: {left > 0 ? <b>{ksh(left)} left over</b> : left < 0 ? <b className="text-warn">{ksh(-left)} of their own money spent</b> : 'spent in full'}</p>
          {left > 0 && <div className="mt-2"><Chips label="Leftover" value={leftover} onChange={(v) => setLeftover(v)} options={[['returned', 'Handed back'], ['payroll', 'Take it from next pay']]} /></div>}
          {(left < 0 || (left > 0 && leftover === 'returned')) && (
            <div className="mt-2"><Chips label="How" value={method} onChange={setMethod} options={[['Cash', 'Cash'], ['Mpesa', 'M-Pesa'], ['Bank', 'Bank']]} /></div>
          )}
        </div>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy} onClick={save}>Account for it</button></SheetFooter>
      </div>
    </Sheet>
  );
}
