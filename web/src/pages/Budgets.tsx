// Budgets: each department's year, month by month. Departments (through finance, procurement, HR or maintenance) prepare
// them; finance approves or turns them down, and the one who prepared a budget can't approve it. Without an approved
// budget a department can't have requests approved. A revision replaces the budget in force only once it's approved.
import { useState } from 'react';
import { Link } from 'react-router';
import { Plus } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { cn, fromCents, ksh, toCents } from '../lib/format';
import { Empty, ErrorNote, Field, Loading, PageError, PageHeader, Sheet, SheetFooter, Stat, Status, askDialog } from '../components/ui';
import { Trail } from './Requisitions';

type BudgetRow = { id: string; number: string; departmentId: string; department: string; year: number; total: number; status: 'Draft' | 'Submitted' | 'Approved' | 'Rejected' | 'Replaced'; justification: string; preparedBy: string; decidedBy: string; decisionNote: string; isRevision: boolean };
type YearView = {
  year: number; waiting: number; withoutBudget: number; annual: number; budgetToDate: number; spentToDate: number; canDecide: boolean; canPrepare: boolean;
  rows: { departmentId: string; department: string; approved: BudgetRow | null; pending: BudgetRow | null; annual: number; budgetToDate: number; spentToDate: number; committed: number; thisMonth: number | null }[];
};
type Detail = { budget: BudgetRow; months: { month: string; budget: number; actual: number; current: number | null }[]; revises: { number: string; total: number } | null; trail: Parameters<typeof Trail>[0]['rows']; canDecide: boolean };

const TONE: Record<BudgetRow['status'], ['good' | 'warn' | 'bad' | 'info' | 'plain', string]> = {
  Draft: ['plain', 'Draft'], Submitted: ['info', 'With finance'], Approved: ['good', 'Approved'], Rejected: ['bad', 'Turned down'], Replaced: ['plain', 'Replaced'],
};
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

export default function Budgets() {
  const thisYear = new Date().getFullYear();
  const [year, setYear] = useState(thisYear);
  const { data, error, reload } = useApi<YearView>(`/budgets?year=${year}`);
  const [open, setOpen] = useState<string | null>(null);
  const [preparing, setPreparing] = useState<{ departmentId: string; base?: BudgetRow | null; edit?: BudgetRow } | null>(null);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  return (
    <div className="space-y-5">
      <PageHeader print
        title="Budgets"
        subtitle="No department spends without a budget approved by finance. Requests are signed off against what's left of the month's budget, and every decision goes into the approvals register."
        actions={
          <select className="field w-auto" value={year} onChange={(e) => setYear(Number(e.target.value))} aria-label="Year">
            {[thisYear - 1, thisYear, thisYear + 1].map((y) => <option key={y} value={y}>{y}</option>)}
          </select>
        }
      />
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label={`Approved for ${year}`} value={ksh(data.annual)} />
        <Stat label="Budget to date" value={ksh(data.budgetToDate)} />
        <Stat label="Spent to date" value={ksh(data.spentToDate)} tone={data.spentToDate > data.budgetToDate && data.budgetToDate > 0 ? 'bad' : undefined} />
        <Stat label="Waiting for finance" value={data.waiting} tone={data.withoutBudget > 0 ? 'bad' : undefined}>{data.withoutBudget > 0 && <span className="text-xs text-bad">{data.withoutBudget} department(s) without a budget</span>}</Stat>
      </div>
      {data.rows.length === 0 ? (
        <Empty title="No departments yet" action={<Link className="btn btn-primary" to="/departments">Add departments</Link>}>Budgets are per department. Add them first.</Empty>
      ) : (
        <div className="card overflow-x-auto">
          <table className="w-full min-w-[46rem] text-sm">
            <thead className="bg-paper text-left text-xs text-muted">
              <tr><th className="px-4 py-2.5">Department</th><th className="px-4 py-2.5">Budget in force</th><th className="px-4 py-2.5 text-right">This month</th><th className="px-4 py-2.5 text-right">Year</th><th className="px-4 py-2.5">Spent to date</th><th className="px-4 py-2.5">Being prepared</th><th className="px-4 py-2.5" /></tr>
            </thead>
            <tbody className="divide-y divide-line">
              {data.rows.map((r) => {
                const used = r.budgetToDate > 0 ? Math.min(100, (100 * r.spentToDate) / r.budgetToDate) : 0;
                return (
                  <tr key={r.departmentId}>
                    <td className="px-4 py-3 font-semibold">{r.department}</td>
                    <td className="px-4 py-3">{r.approved ? <button className="font-semibold text-brand hover:underline" onClick={() => setOpen(r.approved!.id)}>{r.approved.number}</button> : <Status tone="bad">None: can’t spend</Status>}</td>
                    <td className="num px-4 py-3 text-right">{r.thisMonth !== null ? ksh(r.thisMonth) : '—'}</td>
                    <td className="num px-4 py-3 text-right">{r.annual ? ksh(r.annual) : '—'}</td>
                    <td className="px-4 py-3">
                      <span className={cn('num', r.spentToDate > r.budgetToDate && r.budgetToDate > 0 && 'font-semibold text-bad')}>{ksh(r.spentToDate)}</span>
                      {r.budgetToDate > 0 && <div className="mt-1 h-1.5 w-28 rounded-full bg-paper ring-1 ring-line"><div className={cn('h-1.5 rounded-full', used >= 100 ? 'bg-bad' : used >= 85 ? 'bg-warn' : 'bg-good')} style={{ width: `${used}%` }} /></div>}
                    </td>
                    <td className="px-4 py-3">{r.pending ? <button className="flex items-center gap-2" onClick={() => setOpen(r.pending!.id)}><span className="font-semibold text-brand hover:underline">{r.pending.number}</span><Status tone={TONE[r.pending.status][0]}>{TONE[r.pending.status][1]}</Status></button> : <span className="text-muted">—</span>}</td>
                    <td className="px-4 py-3 text-right">
                      {data.canPrepare && !r.pending && <button className="btn btn-ghost whitespace-nowrap" onClick={() => setPreparing({ departmentId: r.departmentId, base: r.approved })}><Plus className="h-4 w-4" /> {r.approved ? 'Revise' : 'Prepare'}</button>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      <Transfers rows={data.rows.filter((r) => r.approved).map((r) => ({ id: r.departmentId, name: r.department }))} year={year} canPrepare={data.canPrepare} onChange={reload} />
      {open && <BudgetSheet id={open} onClose={() => setOpen(null)} onChange={reload} onEdit={(b) => (setOpen(null), setPreparing({ departmentId: b.departmentId, edit: b }))} />}
      {preparing && <BudgetForm year={year} departmentId={preparing.departmentId} department={data.rows.find((r) => r.departmentId === preparing.departmentId)?.department ?? ''} base={preparing.base ?? null} edit={preparing.edit} onClose={() => setPreparing(null)} onSaved={() => (setPreparing(null), reload())} />}
    </div>
  );
}

function BudgetForm({ year, departmentId, department, base, edit, onClose, onSaved }: { year: number; departmentId: string; department: string; base: BudgetRow | null; edit?: BudgetRow; onClose: () => void; onSaved: () => void }) {
  const source = useApi<Detail>(edit ? `/budgets/${edit.id}` : base ? `/budgets/${base.id}` : null);
  const [months, setMonths] = useState<string[] | null>(edit || base ? null : Array(12).fill(''));
  const [why, setWhy] = useState(edit?.justification ?? '');
  const [state, setState] = useState({ busy: false, error: '' });
  const values = months ?? (source.data ? source.data.months.map((m) => (m.budget ? fromCents(m.budget) : '')) : null);
  if (!values) return <Sheet open onClose={onClose} title={`Budget for ${department}`}><Loading /></Sheet>;
  const set = (i: number, v: string) => setMonths(values.map((x, j) => (j === i ? v : x)));
  const total = values.reduce((s, v) => s + toCents(v || '0'), 0);
  const save = async (submit: boolean) => {
    setState({ busy: true, error: '' });
    try {
      const body = { departmentId, year, months: values.map((v) => toCents(v || '0')), justification: why, submit };
      await api(edit ? `/budgets/${edit.id}` : '/budgets', { method: edit ? 'PUT' : 'POST', body });
      onSaved();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title={`${base && !edit ? 'Revise' : 'Prepare'} ${department}’s ${year} budget`} description={base && !edit ? `The budget in force (${base.number}, ${ksh(base.total)}) stays until finance approves this revision.` : 'Finance approves it before the department can spend.'} size="md">
      <div className="space-y-4">
        <div className="flex flex-wrap gap-2">
          <button className="btn btn-ghost" onClick={() => { const first = values.find((v) => v) ?? ''; setMonths(values.map(() => first)); }}>Same every month</button>
          <span className="ml-auto self-center text-sm text-muted">Year: <b className="num text-ink">{ksh(total)}</b></span>
        </div>
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
          {values.map((v, i) => (
            <label key={i} className="text-xs text-muted">{MONTHS[i]}
              <input className="field num mt-1" inputMode="decimal" value={v} onChange={(e) => set(i, e.target.value)} placeholder="0" />
            </label>
          ))}
        </div>
        <Field label="What it will pay for" hint="Finance reads this to judge it."><textarea className="field min-h-20" value={why} onChange={(e) => setWhy(e.target.value)} placeholder="Spares and contractors for the fleet; two service kits a month; tyres in March and September…" /></Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter>
          <button className="btn btn-primary" disabled={state.busy || total <= 0} onClick={() => save(true)}>Send to finance</button>
          <button className="btn btn-ghost" disabled={state.busy || total <= 0} onClick={() => save(false)}>Save as draft</button>
        </SheetFooter>
      </div>
    </Sheet>
  );
}

function BudgetSheet({ id, onClose, onChange, onEdit }: { id: string; onClose: () => void; onChange: () => void; onEdit: (b: BudgetRow) => void }) {
  const { data, error, reload } = useApi<Detail>(`/budgets/${id}`);
  const [state, setState] = useState({ busy: false, error: '' });
  const act = async (path: string, body: object = {}) => {
    setState({ busy: true, error: '' });
    try { await api(`/budgets/${id}/${path}`, { body }); reload(); onChange(); setState({ busy: false, error: '' }); } catch (e) { setState({ busy: false, error: (e as Error).message }); }
  };
  if (error) return <Sheet open onClose={onClose} title="Budget"><ErrorNote>{error}</ErrorNote></Sheet>;
  if (!data) return <Sheet open onClose={onClose} title="Budget"><Loading /></Sheet>;
  const b = data.budget;
  const max = Math.max(1, ...data.months.flatMap((m) => [m.budget, m.actual]));
  return (
    <Sheet open onClose={onClose} title={`${b.number} · ${b.department} ${b.year}`} description={`Prepared by ${b.preparedBy}${b.isRevision && data.revises ? ` · revises ${data.revises.number} (${ksh(data.revises.total)})` : ''}`} size="lg">
      <div className="space-y-5 text-sm">
        <div className="flex flex-wrap items-center gap-2">
          <Status tone={TONE[b.status][0]}>{TONE[b.status][1]}</Status>
          <span className="num font-semibold">{ksh(b.total)} for the year</span>
          {b.decidedBy && <span className="text-muted">· {b.status === 'Rejected' ? 'turned down' : 'decided'} by {b.decidedBy}</span>}
        </div>
        <p className="rounded-md bg-paper p-3">{b.justification}</p>
        {b.decisionNote && <p className={cn('rounded-md p-3', b.status === 'Rejected' ? 'bg-bad-soft text-bad' : 'bg-paper')}>Finance: {b.decisionNote}</p>}
        <div className="overflow-x-auto">
          <table className="w-full min-w-[30rem]">
            <thead className="text-left text-xs text-muted"><tr><th className="py-1.5 pr-3">Month</th>{data.revises && <th className="py-1.5 pr-3 text-right">In force</th>}<th className="py-1.5 pr-3 text-right">Budget</th><th className="py-1.5 pr-3 text-right">Spent</th><th className="w-1/3 py-1.5" /></tr></thead>
            <tbody className="divide-y divide-line">
              {data.months.map((m) => (
                <tr key={m.month}>
                  <td className="py-1.5 pr-3">{m.month}</td>
                  {data.revises && <td className="num py-1.5 pr-3 text-right text-muted">{ksh(m.current ?? 0)}</td>}
                  <td className="num py-1.5 pr-3 text-right">{ksh(m.budget)}</td>
                  <td className={cn('num py-1.5 pr-3 text-right', m.actual > m.budget && 'font-semibold text-bad')}>{m.actual ? ksh(m.actual) : '—'}</td>
                  <td className="py-1.5" title={`Budget ${ksh(m.budget)} · spent ${ksh(m.actual)}`}>
                    <div className="relative h-2 rounded-full bg-paper ring-1 ring-line">
                      <div className="absolute inset-y-0 left-0 rounded-full bg-line" style={{ width: `${(m.budget / max) * 100}%` }} />
                      <div className={cn('absolute inset-y-0 left-0 rounded-full', m.actual > m.budget ? 'bg-bad' : 'bg-good')} style={{ width: `${(m.actual / max) * 100}%` }} />
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="flex flex-wrap gap-2">
          {data.canDecide && <button className="btn btn-primary" disabled={state.busy} onClick={() => act('approve')}>Approve</button>}
          {data.canDecide && <button className="btn btn-ghost text-bad" disabled={state.busy} onClick={async () => { const reason = await askDialog({ title: `Turn down ${b.number}?`, label: 'Why (so the department can fix it)', required: true, confirm: 'Turn down', danger: true }); if (reason) act('reject', { reason }); }}>Turn down</button>}
          {(b.status === 'Draft' || b.status === 'Rejected') && <button className="btn btn-ghost" onClick={() => onEdit(b)}>Change it</button>}
          {b.status === 'Draft' && <button className="btn btn-primary" disabled={state.busy} onClick={() => act('submit')}>Send to finance</button>}
        </div>
        {b.status === 'Submitted' && !data.canDecide && <p className="text-muted">Waiting for finance. Whoever prepared it can’t approve it.</p>}
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        {data.trail.length > 0 && <Trail rows={data.trail} />}
      </div>
    </Sheet>
  );
}

type Transfer = { id: string; number: string; from: string; to: string; year: number; month: number; monthName: string; amount: number; reason: string; status: 'Submitted' | 'Approved' | 'Rejected'; requestedBy: string; decidedBy: string; decisionNote: string; canDecide: boolean };

/** Moving part of one department's approved budget for a month to another's: asked by one person, approved by finance. */
function Transfers({ rows, year, canPrepare, onChange }: { rows: { id: string; name: string }[]; year: number; canPrepare: boolean; onChange: () => void }) {
  const list = useApi<Transfer[]>('/budgets/transfers');
  const [asking, setAsking] = useState(false);
  const now = new Date();
  const [f, setF] = useState({ from: '', to: '', month: String(now.getMonth() + 1), amount: '', reason: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const run = async (fn: () => Promise<unknown>) => {
    setState({ busy: true, error: '' });
    try { await fn(); list.reload(); onChange(); setState({ busy: false, error: '' }); setAsking(false); } catch (e) { setState({ busy: false, error: (e as Error).message }); }
  };
  const items = list.data ?? [];
  if (rows.length < 2 && items.length === 0) return null;
  return (
    <section className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="font-semibold">Moving money between departments</h2>
        {canPrepare && rows.length >= 2 && <button className="btn btn-ghost" onClick={() => setAsking(!asking)}>Ask to move budget</button>}
      </div>
      {asking && (
        <div className="card grid gap-3 p-4 sm:grid-cols-2 lg:grid-cols-5">
          <Field label="From"><select className="field" value={f.from} onChange={(e) => setF({ ...f, from: e.target.value })}><option value="">Choose…</option>{rows.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}</select></Field>
          <Field label="To"><select className="field" value={f.to} onChange={(e) => setF({ ...f, to: e.target.value })}><option value="">Choose…</option>{rows.filter((r) => r.id !== f.from).map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}</select></Field>
          <Field label="Month"><select className="field" value={f.month} onChange={(e) => setF({ ...f, month: e.target.value })}>{MONTHS.map((m, i) => <option key={m} value={i + 1}>{m} {year}</option>)}</select></Field>
          <Field label="Amount (KSh)"><input className="field num" inputMode="decimal" value={f.amount} onChange={(e) => setF({ ...f, amount: e.target.value })} /></Field>
          <Field label="Why"><input className="field" value={f.reason} onChange={(e) => setF({ ...f, reason: e.target.value })} placeholder="Tug overhaul brought forward" /></Field>
          <div className="sm:col-span-2 lg:col-span-5"><button className="btn btn-primary" disabled={state.busy || !f.from || !f.to || !f.amount} onClick={() => run(() => api('/budgets/transfers', { body: { fromDepartmentId: f.from, toDepartmentId: f.to, year, month: Number(f.month), amount: toCents(f.amount), reason: f.reason } }))}>Send to finance</button></div>
        </div>
      )}
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      {items.length > 0 && (
        <ul className="card divide-y divide-line text-sm">
          {items.map((t) => (
            <li key={t.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
              <span className="min-w-0 flex-1"><code className="text-xs font-semibold text-muted">{t.number}</code> <b>{t.from} → {t.to}</b>, {t.monthName} {t.year}<span className="block text-muted">{t.reason} · asked by {t.requestedBy}{t.decidedBy && ` · ${t.status === 'Approved' ? 'approved' : 'turned down'} by ${t.decidedBy}`}</span></span>
              <span className="num font-semibold">{ksh(t.amount)}</span>
              <Status tone={t.status === 'Approved' ? 'good' : t.status === 'Rejected' ? 'bad' : 'info'}>{t.status === 'Submitted' ? 'With finance' : t.status === 'Approved' ? 'Moved' : 'Turned down'}</Status>
              {t.canDecide && <>
                <button className="btn btn-primary" disabled={state.busy} onClick={() => run(() => api(`/budgets/transfers/${t.id}/approve`, { body: {} }))}>Approve</button>
                <button className="btn btn-ghost text-bad" disabled={state.busy} onClick={async () => { const reason = await askDialog({ title: `Turn down ${t.number}?`, label: 'Why', required: true, confirm: 'Turn down', danger: true }); if (reason) run(() => api(`/budgets/transfers/${t.id}/reject`, { body: { reason } })); }}>Turn down</button>
              </>}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
