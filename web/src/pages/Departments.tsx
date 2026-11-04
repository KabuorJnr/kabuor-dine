// Departments: who's in each part of the business, who heads it (and so approves its requests), and what it may spend
// this month (from the budget finance approved) against what it has spent and already committed.
import { useState } from 'react';
import { Link } from 'react-router';
import { Plus } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, ksh } from '../lib/format';
import { Empty, ErrorNote, Field, Loading, PageError, PageHeader, Sheet, SheetFooter, Status } from '../components/ui';

type Dept = { id: string; name: string; code: string; headEmployeeId: string | null; head: string; monthlyBudget: number | null; hasBudget: boolean; budgetWaiting: boolean; active: boolean; spent: number; committed: number; available: number | null; staff: number };

export default function Departments() {
  const session = useSession();
  const canEdit = can(session, 'finance');
  const { data, error, reload } = useApi<Dept[]>('/departments');
  const [editing, setEditing] = useState<Dept | 'new' | null>(null);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const month = new Date().toLocaleDateString('en-GB', { month: 'long' });
  return (
    <div>
      <PageHeader print
        title="Departments"
        subtitle={<>Each department’s head approves its requests; finance signs off the money against the budget it approved (<Link className="font-semibold text-brand hover:underline" to="/finance/budgets">Budgets</Link>). Spending is for {month}, before VAT.</>}
        actions={canEdit && <button className="btn btn-primary" onClick={() => setEditing('new')}><Plus className="h-4 w-4" /> Add department</button>}
      />
      {data.length === 0 ? (
        <Empty title="No departments yet" action={canEdit && <button className="btn btn-primary" onClick={() => setEditing('new')}>Add the first one</button>}>
          Departments are made from what’s written on employee records the first time this page opens; add more here.
        </Empty>
      ) : (
        <div className="card overflow-x-auto">
          <table className="w-full min-w-[44rem] text-sm">
            <thead className="bg-paper text-left text-xs text-muted">
              <tr><th className="px-4 py-2.5">Department</th><th className="px-4 py-2.5">Head</th><th className="px-4 py-2.5 text-right">Staff</th><th className="px-4 py-2.5 text-right">Budget</th><th className="px-4 py-2.5 text-right">Spent</th><th className="px-4 py-2.5 text-right">Committed</th><th className="px-4 py-2.5">Left this month</th></tr>
            </thead>
            <tbody className="divide-y divide-line">
              {data.map((d) => {
                const used = d.monthlyBudget ? Math.min(100, (100 * (d.spent + d.committed)) / d.monthlyBudget) : 0;
                return (
                  <tr key={d.id} className={cn(canEdit && 'cursor-pointer hover:bg-paper', !d.active && 'opacity-60')} onClick={() => canEdit && setEditing(d)}>
                    <td className="px-4 py-3"><b>{d.name}</b>{d.code && <span className="ml-2 text-xs text-muted">{d.code}</span>}{!d.active && <span className="ml-2"><Status tone="plain">Closed</Status></span>}</td>
                    <td className="px-4 py-3">{d.head || <span className="text-muted">None (requests go straight to procurement)</span>}</td>
                    <td className="num px-4 py-3 text-right">{d.staff}</td>
                    <td className="num px-4 py-3 text-right">{d.hasBudget ? ksh(d.monthlyBudget ?? 0) : d.budgetWaiting ? <Status tone="info">With finance</Status> : <Status tone="warn">No budget</Status>}</td>
                    <td className="num px-4 py-3 text-right">{ksh(d.spent)}</td>
                    <td className="num px-4 py-3 text-right">{ksh(d.committed)}</td>
                    <td className="px-4 py-3">
                      {d.available === null ? <span className="text-muted">—</span> : (
                        <div className="min-w-32">
                          <span className={cn('num font-semibold', d.available < 0 && 'text-bad')}>{ksh(d.available)}</span>
                          <div className="mt-1 h-1.5 rounded-full bg-paper ring-1 ring-line"><div className={cn('h-1.5 rounded-full', used >= 100 ? 'bg-bad' : used >= 80 ? 'bg-warn' : 'bg-good')} style={{ width: `${used}%` }} /></div>
                        </div>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {editing && <DeptForm dept={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={() => (setEditing(null), reload())} />}
    </div>
  );
}

function DeptForm({ dept, onClose, onSaved }: { dept: Dept | null; onClose: () => void; onSaved: () => void }) {
  const employees = useApi<{ id: string; name: string; department: string }[]>('/directory/employees');
  const [f, setF] = useState({ name: dept?.name ?? '', code: dept?.code ?? '', headEmployeeId: dept?.headEmployeeId ?? '', active: dept?.active ?? true });
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(dept ? `/departments/${dept.id}` : '/departments', { method: dept ? 'PUT' : 'POST', body: { name: f.name, code: f.code, headEmployeeId: f.headEmployeeId || null, active: f.active } });
      onSaved();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const staff = employees.data ?? [];
  return (
    <Sheet open onClose={onClose} title={dept ? `Change ${dept.name}` : 'Add a department'}>
      <div className="space-y-4">
        <div className="grid grid-cols-[1fr_7rem] gap-3">
          <Field label="Name"><input className="field" autoFocus value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="Workshop" /></Field>
          <Field label="Code"><input className="field uppercase" value={f.code} onChange={(e) => setF({ ...f, code: e.target.value })} placeholder="WKS" /></Field>
        </div>
        <Field label="Head of department" hint="Approves the department’s requests from their portal. They need app access (People → their record).">
          <select className="field" value={f.headEmployeeId} onChange={(e) => setF({ ...f, headEmployeeId: e.target.value })}>
            <option value="">No head (requests go straight to procurement)</option>
            {staff.map((e) => <option key={e.id} value={e.id}>{e.name}{e.department ? ` · ${e.department}` : ''}</option>)}
          </select>
        </Field>
        <p className="rounded-lg bg-paper p-3 text-xs text-muted">Money isn’t set here: the department’s budget is prepared in <Link className="font-semibold text-brand hover:underline" to="/finance/budgets">Finance → Budgets</Link> and approved by finance. Without an approved budget, its requests can’t be approved.</p>
        {dept && <label className="flex items-center gap-2.5 text-sm"><input type="checkbox" className="h-4 w-4" checked={f.active} onChange={(e) => setF({ ...f, active: e.target.checked })} /> Open (untick to stop new requests)</label>}
        {dept && f.name.trim() !== dept.name && <p className="text-xs text-muted">Renaming it renames it on every employee record in it.</p>}
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy || f.name.trim().length < 2} onClick={save}>{dept ? 'Save' : 'Add department'}</button></SheetFooter>
      </div>
    </Sheet>
  );
}
