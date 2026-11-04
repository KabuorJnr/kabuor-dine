// Agreed deductions (HELB, SACCO, loans, insurance…). HR is deciding "what comes off this person's pay, and have
// they agreed?", so each row shows what it is, who gets it, how much a month, how far a loan has got, and whether
// the employee has agreed. Adding one asks only what that kind of deduction needs, and says when it will start.
import { useState } from 'react';
import { Check, Hourglass, Loader2, Plus, X } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { cn, ksh, toCents } from '../lib/format';
import { ErrorNote, Field, Sheet, Status, SheetFooter, askDialog, confirmDialog } from './ui';

export type Kind = 'Helb' | 'Sacco' | 'Loan' | 'Advance' | 'Insurance' | 'Union' | 'Pension' | 'Other';
export type DeductionLine = { deductionId: string | null; kind: Kind; name: string; payTo: string; amount: number; wanted: number | null };
export type Deduction = {
  id: string; kind: Kind; name: string; payTo: string; reference: string; monthlyAmount: number; total: number | null; recovered: number;
  startMonth: string; endMonth: string | null; status: 'AwaitingEmployee' | 'Active' | 'Declined' | 'Stopped' | 'Completed'; consent: string; note: string;
};

export const KINDS: { id: Kind; label: string; payTo: string; ref: string; repay?: boolean; hint: string }[] = [
  { id: 'Helb', label: 'HELB', payTo: 'HELB', ref: 'HELB account or ID number', hint: 'From HELB’s notice to the employer.' },
  { id: 'Sacco', label: 'SACCO', payTo: '', ref: 'Member number', hint: 'Shares, savings or a SACCO loan repayment.' },
  { id: 'Loan', label: 'Loan', payTo: '', ref: 'Loan reference (optional)', repay: true, hint: 'Money the business lent them; stops once repaid.' },
  { id: 'Advance', label: 'Advance', payTo: '', ref: 'Reference (optional)', repay: true, hint: 'A salary advance; stops once repaid.' },
  { id: 'Insurance', label: 'Insurance', payTo: '', ref: 'Policy number', hint: 'Medical or life cover paid on their behalf.' },
  { id: 'Union', label: 'Union', payTo: '', ref: 'Member number', hint: 'Trade union dues.' },
  { id: 'Pension', label: 'Pension', payTo: '', ref: 'Member number', hint: 'Voluntary pension: taken before tax, up to KSh 30,000 a month with NSSF.' },
  { id: 'Other', label: 'Other', payTo: '', ref: 'Reference (optional)', hint: 'Anything else they agreed to in writing.' },
];

export const parseLines = (slip: any): DeductionLine[] => {
  try {
    return JSON.parse(slip?.deductionLines ?? '[]');
  } catch {
    return [];
  }
};

const monthLabel = (iso: string) => new Date(`${iso.slice(0, 7)}-01T00:00:00`).toLocaleDateString('en-GB', { month: 'short', year: 'numeric' });
const STATUS: Record<Deduction['status'], [string, 'good' | 'warn' | 'plain' | 'bad']> = {
  AwaitingEmployee: ['Waiting for them to agree', 'warn'],
  Active: ['Active', 'good'],
  Declined: ['Declined', 'bad'],
  Stopped: ['Stopped', 'plain'],
  Completed: ['Repaid', 'plain'],
};

export function DeductionsCard({ employee, onChange }: { employee: { id: string; name: string; basicSalary: number }; onChange?: () => void }) {
  const list = useApi<Deduction[]>(`/employees/${employee.id}/deductions`);
  const access = useApi<{ hasAccess: boolean }>(`/employees/${employee.id}/access`);
  const hasApp = Boolean(access.data?.hasAccess);
  const [editing, setEditing] = useState<Deduction | 'new' | null>(null);
  const live = (list.data ?? []).filter((d) => d.status === 'Active' || d.status === 'AwaitingEmployee');
  const ended = (list.data ?? []).filter((d) => !live.includes(d));
  const monthly = live.filter((d) => d.status === 'Active').reduce((s, d) => s + Math.min(d.monthlyAmount, d.total == null ? d.monthlyAmount : d.total - d.recovered), 0);
  const stop = async (d: Deduction) => {
    if (!(await confirmDialog({ title: `Stop “${d.name}” for ${employee.name}?`, body: 'Payroll won’t take it from next month.', confirm: 'Stop deduction', danger: true }))) return;
    await api(`/employees/${employee.id}/deductions/${d.id}/stop`, { body: {} });
    list.reload();
    onChange?.();
  };
  return (
    <section className="card p-4">
      <div className="flex items-center justify-between gap-2">
        <h2 className="text-sm font-semibold text-muted">Deductions they agreed to</h2>
        <button onClick={() => setEditing('new')} className="btn btn-ghost py-1 text-sm">
          <Plus className="h-4 w-4" /> Add
        </button>
      </div>
      {live.length > 0 && <p className="num mt-1 text-xs text-muted">{ksh(monthly)} a month from pay, on top of PAYE, NSSF, SHIF and Housing Levy</p>}
      {list.data && list.data.length === 0 && <p className="mt-2 text-sm text-muted">None. Add HELB, a SACCO, a loan or an advance here; payroll takes it every month once they’ve agreed.</p>}
      <ul className="mt-2 divide-y divide-line">
        {live.map((d) => (
          <li key={d.id} className="py-2.5">
            <button onClick={() => setEditing(d)} className="w-full text-left">
              <span className="flex items-baseline justify-between gap-2">
                <b className="truncate text-sm">{d.name}</b>
                <span className="num shrink-0 text-sm font-semibold">{ksh(d.monthlyAmount)}<span className="font-normal text-muted">/mo</span></span>
              </span>
              <span className="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted">
                <Status tone={STATUS[d.status][1]}>{STATUS[d.status][0]}</Status>
                {d.payTo && <span>to {d.payTo}</span>}
                {d.reference && <span>· {d.reference}</span>}
                <span>· from {monthLabel(d.startMonth)}{d.endMonth ? ` to ${monthLabel(d.endMonth)}` : ''}</span>
              </span>
              {d.total != null && (
                <span className="mt-1.5 block">
                  <span className="block h-1.5 overflow-hidden rounded-full bg-paper ring-1 ring-line">
                    <span className="block h-full bg-good" style={{ width: `${Math.min(100, (100 * d.recovered) / d.total)}%` }} />
                  </span>
                  <span className="num text-xs text-muted">
                    {ksh(d.recovered)} of {ksh(d.total)} repaid · {Math.ceil((d.total - d.recovered) / d.monthlyAmount)} more month{Math.ceil((d.total - d.recovered) / d.monthlyAmount) === 1 ? '' : 's'}
                  </span>
                </span>
              )}
            </button>
            <div className="mt-1 flex justify-end">
              <button onClick={() => stop(d)} className="text-xs font-semibold text-muted hover:text-bad">Stop</button>
            </div>
          </li>
        ))}
      </ul>
      {ended.length > 0 && (
        <details className="mt-1 text-xs text-muted">
          <summary className="cursor-pointer">Ended ({ended.length})</summary>
          <ul className="mt-1 space-y-1">
            {ended.map((d) => (
              <li key={d.id}>
                {d.name} · {STATUS[d.status][0]}
                {d.note && ` · ${d.note}`}
              </li>
            ))}
          </ul>
        </details>
      )}
      <Sheet open={editing !== null} onClose={() => setEditing(null)} title={editing === 'new' ? `Add a deduction for ${employee.name}` : `Edit ${typeof editing === 'object' && editing ? editing.name : ''}`}>
        {editing !== null && (
          <DeductionForm
            employee={employee}
            hasApp={hasApp}
            existing={editing === 'new' ? null : editing}
            onDone={() => (setEditing(null), list.reload(), onChange?.())}
          />
        )}
      </Sheet>
    </section>
  );
}

function DeductionForm({ employee, hasApp, existing, onDone }: { employee: { id: string; name: string; basicSalary: number }; hasApp: boolean; existing: Deduction | null; onDone: () => void }) {
  const thisMonth = new Date(Date.now() + 3 * 3600e3).toISOString().slice(0, 7);
  const [f, setF] = useState({
    kind: (existing?.kind ?? 'Helb') as Kind,
    name: existing?.name ?? '',
    payTo: existing?.payTo ?? '',
    reference: existing?.reference ?? '',
    monthly: existing ? String(existing.monthlyAmount / 100) : '',
    total: existing?.total != null ? String(existing.total / 100) : '',
    start: existing?.startMonth.slice(0, 7) ?? thisMonth,
    end: existing?.endMonth?.slice(0, 7) ?? '',
    consent: hasApp ? 'app' : 'form',
    note: existing?.note ?? '',
  });
  const [state, setState] = useState({ busy: false, error: '' });
  const k = KINDS.find((x) => x.id === f.kind)!;
  const monthly = toCents(f.monthly);
  const total = toCents(f.total);
  const months = k.repay && monthly > 0 && total > 0 ? Math.ceil(total / monthly) : null;
  const raised = existing && monthly > existing.monthlyAmount;
  const needsConsent = !existing || existing.status === 'AwaitingEmployee' || raised;
  const share = employee.basicSalary > 0 && monthly > 0 ? Math.round((100 * monthly) / employee.basicSalary) : null;

  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      const body = { kind: f.kind, name: f.name || null, payTo: f.payTo || null, reference: f.reference, monthlyAmount: monthly, total: k.repay ? total : null, startMonth: f.start, endMonth: f.end || null, consent: f.consent, note: f.note };
      if (existing) await api(`/employees/${employee.id}/deductions/${existing.id}`, { method: 'PUT', body });
      else await api(`/employees/${employee.id}/deductions`, { body });
      onDone();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };

  return (
    <div className="space-y-4">
      <fieldset>
        <legend className="mb-1.5 text-sm font-semibold">What is it?</legend>
        <div className="grid grid-cols-4 gap-1.5">
          {KINDS.map((x) => (
            <button key={x.id} type="button" disabled={Boolean(existing)} onClick={() => setF({ ...f, kind: x.id })} className={cn('rounded-md px-2 py-2 text-sm font-semibold ring-1', f.kind === x.id ? 'bg-brand-soft text-brand ring-2 ring-brand' : 'bg-white ring-line', existing && f.kind !== x.id && 'opacity-40')}>
              {x.label}
            </button>
          ))}
        </div>
        <p className="mt-1.5 text-xs text-muted">{k.hint}</p>
      </fieldset>

      {!k.repay && f.kind !== 'Helb' && (
        <Field label="Paid to">
          <input className="field" value={f.payTo} onChange={(e) => setF({ ...f, payTo: e.target.value })} placeholder={f.kind === 'Sacco' ? 'e.g. Stima Sacco' : f.kind === 'Insurance' ? 'e.g. Jubilee Insurance' : 'Who receives this money'} />
        </Field>
      )}
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Name on the payslip" hint="Leave blank for the usual name.">
          <input className="field" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder={KINDS.find((x) => x.id === f.kind)?.label} />
        </Field>
        <Field label={k.ref}>
          <input className="field" value={f.reference} onChange={(e) => setF({ ...f, reference: e.target.value })} />
        </Field>
        <Field label="Each month (KSh)" hint={share != null ? `${share}% of their basic salary` : undefined}>
          <input className="field num" inputMode="decimal" value={f.monthly} onChange={(e) => setF({ ...f, monthly: e.target.value })} />
        </Field>
        {k.repay && (
          <Field label="Total to recover (KSh)" hint={months ? `Repaid in ${months} month${months === 1 ? '' : 's'}` : undefined}>
            <input className="field num" inputMode="decimal" value={f.total} onChange={(e) => setF({ ...f, total: e.target.value })} />
          </Field>
        )}
        <Field label="First month">
          <input className="field" type="month" value={f.start} onChange={(e) => setF({ ...f, start: e.target.value })} />
        </Field>
        {!k.repay && (
          <Field label="Last month (optional)" hint="Blank: until stopped.">
            <input className="field" type="month" value={f.end} onChange={(e) => setF({ ...f, end: e.target.value })} />
          </Field>
        )}
      </div>

      {needsConsent && (
        <fieldset>
          <legend className="mb-1.5 text-sm font-semibold">{raised ? 'A higher amount needs their agreement again' : `How has ${employee.name.split(' ')[0]} agreed?`}</legend>
          <div className="space-y-1.5">
            {[
              hasApp && ['app', `Ask them in their app`, 'Nothing is taken until they tap Agree.'],
              ['form', 'I have their signed form', 'Keep the form on file; your name is recorded with it.'],
              f.kind === 'Helb' && ['helb', 'HELB sent us a notice', 'The employer must deduct what HELB’s notice says.'],
            ]
              .filter(Boolean)
              .map((o) => {
                const [id, label, sub] = o as string[];
                return (
                  <label key={id} className={cn('flex cursor-pointer gap-3 rounded-md p-3 ring-1', f.consent === id ? 'bg-brand-soft ring-2 ring-brand' : 'bg-white ring-line')}>
                    <input type="radio" name="consent" className="mt-1 accent-[var(--color-brand)]" checked={f.consent === id} onChange={() => setF({ ...f, consent: id })} />
                    <span>
                      <b className="block text-sm">{label}</b>
                      <span className="text-xs text-muted">{sub}</span>
                    </span>
                  </label>
                );
              })}
          </div>
          {!hasApp && <p className="mt-1.5 text-xs text-muted">Give them app access to let them agree from their phone.</p>}
        </fieldset>
      )}
      <Field label="Note (optional)">
        <input className="field" value={f.note} onChange={(e) => setF({ ...f, note: e.target.value })} placeholder="e.g. Loan agreed on 3 Oct, form in the HR file" />
      </Field>
      <p className="rounded-md bg-paper px-3 py-2 text-xs text-muted">Payroll never takes so much that they keep less than a third of their pay (Employment Act, s.19). If pay is short, HELB and loans come first and the rest is cut, with a note on the payslip.</p>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button onClick={save} disabled={state.busy || monthly <= 0 || (k.repay && total <= 0)} className="btn btn-primary">
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} {existing ? 'Save' : f.consent === 'app' && needsConsent ? 'Add and ask them to agree' : 'Add deduction'}
      </button>
      </SheetFooter>
    </div>
  );
}

/** For the employee: deductions waiting for their agreement, and the ones running. */
export function MyDeductions({ deductions, onChange }: { deductions: Deduction[]; onChange: () => void }) {
  const [state, setState] = useState({ busy: '', error: '' });
  const decide = async (d: Deduction, agree: boolean) => {
    const reason = agree ? '' : await askDialog({ title: `Say no to “${d.name}”?`, label: 'Why not? (HR sees this)', confirm: 'Say no', danger: true });
    if (reason === null) return;
    setState({ busy: d.id, error: '' });
    try {
      await api(`/me/deductions/${d.id}/${agree ? 'agree' : 'decline'}`, { body: agree ? {} : { reason } });
      onChange();
      setState({ busy: '', error: '' });
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };
  const waiting = deductions.filter((d) => d.status === 'AwaitingEmployee');
  const active = deductions.filter((d) => d.status === 'Active');
  if (!waiting.length && !active.length) return null;
  return (
    <section className="space-y-3">
      {waiting.map((d) => (
        <div key={d.id} className="rounded-lg bg-warn-soft p-4">
          <p className="flex items-center gap-2 text-sm font-semibold text-warn">
            <Hourglass className="h-4 w-4" /> HR asks to take this from your pay
          </p>
          <p className="mt-1 font-semibold">{d.name}{d.payTo ? ` → ${d.payTo}` : ''}</p>
          <p className="num text-sm text-ink-2">
            {ksh(d.monthlyAmount)} a month from {monthLabel(d.startMonth)}
            {d.total != null ? `, until ${ksh(d.total)} is repaid (${Math.ceil(d.total / d.monthlyAmount)} months)` : d.endMonth ? ` to ${monthLabel(d.endMonth)}` : ', until it’s stopped'}
            {d.reference && ` · ${d.reference}`}
          </p>
          {d.note && <p className="mt-1 text-xs text-muted">{d.note}</p>}
          <div className="mt-3 flex gap-2">
            <button onClick={() => decide(d, true)} disabled={Boolean(state.busy)} className="btn btn-primary">
              {state.busy === d.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />} I agree
            </button>
            <button onClick={() => decide(d, false)} disabled={Boolean(state.busy)} className="btn btn-ghost">
              <X className="h-4 w-4" /> I don’t agree
            </button>
          </div>
        </div>
      ))}
      {active.length > 0 && (
        <div className="card p-4">
          <p className="text-xs font-semibold tracking-wider text-faint uppercase">Taken from your pay each month</p>
          <ul className="mt-2 space-y-2 text-sm">
            {active.map((d) => (
              <li key={d.id} className="flex items-baseline justify-between gap-3">
                <span className="min-w-0">
                  <b className="block truncate">{d.name}</b>
                  <span className="text-xs text-muted">{d.total != null ? `${ksh(d.recovered)} of ${ksh(d.total)} repaid` : d.payTo ? `to ${d.payTo}` : ''}</span>
                </span>
                <span className="num shrink-0 font-semibold">{ksh(d.monthlyAmount)}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
    </section>
  );
}
