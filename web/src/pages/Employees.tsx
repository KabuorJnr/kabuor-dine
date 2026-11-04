// Employees: who works here, what they cost, and who's away. The editor shows what a salary really comes to
// (deductions, take-home, employer cost) while it's typed, so an offer is never a surprise.
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { ArrowLeft, KeyRound, Loader2, MessageCircle, Pencil, Plus } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { day, dayYear, ksh, msisdn, prettyPhone, toCents } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, SearchBox, Sheet, Stat, Status, SheetFooter, PageError, confirmDialog } from '../components/ui';

type Pay = { gross: number; nssfEmployee: number; shif: number; housingLevyEmployee: number; paye: number; net: number; employerCost: number; nssfEmployer: number; housingLevyEmployer: number; nita: number };

export function PayBreakdown({ p, compact }: { p: Pay | null; compact?: boolean }) {
  if (!p) return null;
  const rows: { label: string; amount: number; sub?: string }[] = [
    { label: 'Gross pay', amount: p.gross },
    { label: 'NSSF pension', amount: -p.nssfEmployee },
    { label: 'SHIF (SHA health)', amount: -p.shif },
    { label: 'Housing levy (AHL)', amount: -p.housingLevyEmployee },
    { label: 'PAYE income tax', amount: -p.paye, sub: 'After KES 2,400 monthly personal relief' },
  ];
  return (
    <div className={compact ? 'text-sm' : 'card p-4 text-sm'}>
      <dl className="space-y-2">
        {rows.map(({ label, amount: v, sub }) => (
          <div key={label} className="flex items-baseline justify-between gap-3">
            <dt className="min-w-0 flex-1 text-muted">
              <span className="font-medium text-ink">{label}</span>
              {sub && <span className="block text-2xs text-muted/80">{sub}</span>}
            </dt>
            <dd className="num shrink-0 text-right font-medium whitespace-nowrap">
              {v < 0 ? `−${ksh(-v)}` : ksh(v)}
            </dd>
          </div>
        ))}
        <div className="flex items-baseline justify-between gap-3 border-t border-line pt-2 text-base font-bold">
          <dt className="min-w-0 flex-1 text-ink">Take-home</dt>
          <dd className="num shrink-0 text-right text-brand whitespace-nowrap">{ksh(p.net)}</dd>
        </div>
      </dl>
      <div className="mt-3.5 rounded-lg border border-line/60 bg-paper/60 p-3 text-xs">
        <div className="flex items-baseline justify-between gap-2">
          <span className="font-semibold text-ink">Total employer cost:</span>
          <span className="num font-bold text-ink shrink-0 whitespace-nowrap">
            {ksh(p.gross + p.nssfEmployer + p.housingLevyEmployer + p.nita)}
          </span>
        </div>
        <span className="mt-1 block text-2xs text-muted leading-tight">
          Includes gross pay + employer NSSF, Housing Levy (1.5%) &amp; NITA (KES 50)
        </span>
      </div>
    </div>
  );
}

const blank = { name: '', phone: '', email: '', kraPin: '', nationalId: '', nssfNumber: '', shaNumber: '', jobTitle: '', department: '', startDate: '', endDate: '', basic: '', allowances: '', untaxed: '', payMethod: 'Mpesa', payPhone: '', bankName: '', bankAccount: '', annualLeaveDays: '21' };

export function EmployeeEditor({ open, onClose, existing, onSaved }: { open: boolean; onClose: () => void; existing?: any; onSaved: (e: any) => void }) {
  const [f, setF] = useState(blank);
  const [preview, setPreview] = useState<Pay | null>(null);
  const [state, setState] = useState({ busy: false, error: '' });
  useEffect(() => {
    if (!open) return;
    const e = existing;
    setF(e ? { ...blank, ...e, startDate: e.startDate ?? '', endDate: e.endDate ?? '', basic: String(e.basicSalary / 100), allowances: e.taxableAllowances ? String(e.taxableAllowances / 100) : '', untaxed: e.nonTaxableAllowances ? String(e.nonTaxableAllowances / 100) : '', annualLeaveDays: String(e.annualLeaveDays) } : blank);
    setState({ busy: false, error: '' });
  }, [open, existing]);
  // Live take-home preview as the salary is typed.
  useEffect(() => {
    const basic = toCents(f.basic);
    if (!basic) return setPreview(null);
    const t = setTimeout(() => api<Pay>('/payroll/preview', { body: { basic, taxableAllowances: toCents(f.allowances), nonTaxableAllowances: toCents(f.untaxed) } }).then(setPreview).catch(() => {}), 250);
    return () => clearTimeout(t);
  }, [f.basic, f.allowances, f.untaxed]);
  const set = (k: keyof typeof blank) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setF({ ...f, [k]: e.target.value });

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      const body = { ...f, startDate: f.startDate || null, endDate: f.endDate || null, basicSalary: toCents(f.basic), taxableAllowances: toCents(f.allowances), nonTaxableAllowances: toCents(f.untaxed), annualLeaveDays: Number(f.annualLeaveDays) || 21 };
      const saved = existing ? await api(`/employees/${existing.id}`, { method: 'PUT', body }) : await api('/employees', { body });
      onSaved(saved);
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };

  return (
    <Sheet open={open} onClose={onClose} title={existing ? `Edit ${existing.name}` : 'Add an employee'} description="Pay and statutory deductions are worked out as you type." wide>
      <form onSubmit={save} className="grid gap-6 lg:grid-cols-[1fr_20rem]">
        <div className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Full name" className="sm:col-span-2">
              <input className="field" required value={f.name} onChange={set('name')} />
            </Field>
            <Field label="Job title">
              <input className="field" value={f.jobTitle} onChange={set('jobTitle')} placeholder="e.g. Sales rep" />
            </Field>
            <Field label="Department">
              <input className="field" value={f.department} onChange={set('department')} placeholder="e.g. Sales" />
            </Field>
            <Field label="Phone">
              <input className="field" inputMode="tel" value={f.phone} onChange={set('phone')} placeholder="0712 345 678" />
            </Field>
            <Field label="Started on">
              <input className="field" type="date" value={f.startDate} onChange={set('startDate')} />
            </Field>
          </div>
          <fieldset className="grid gap-3 rounded-lg bg-white p-4 ring-1 ring-line sm:grid-cols-2">
            <legend className="px-1 text-sm font-semibold">Monthly pay (KSh)</legend>
            <Field label="Basic salary" className="sm:col-span-2">
              <input className="field num" inputMode="decimal" required value={f.basic} onChange={set('basic')} placeholder="e.g. 50000" />
            </Field>
            <Field label="Taxed allowances" hint="House, transport…">
              <input className="field num" inputMode="decimal" value={f.allowances} onChange={set('allowances')} placeholder="0" />
            </Field>
            <Field label="Untaxed allowances" hint="e.g. refunds, per diem">
              <input className="field num" inputMode="decimal" value={f.untaxed} onChange={set('untaxed')} placeholder="0" />
            </Field>
          </fieldset>
          <fieldset className="grid gap-3 sm:grid-cols-2">
            <legend className="mb-1.5 text-sm font-semibold">Paid by</legend>
            <div className="flex gap-2 sm:col-span-2">
              {[['Mpesa', 'M-Pesa'], ['Bank', 'Bank'], ['Cash', 'Cash']].map(([v, l]) => (
                <button key={v} type="button" aria-pressed={f.payMethod === v} onClick={() => setF({ ...f, payMethod: v })} className={f.payMethod === v ? 'btn bg-brand-soft py-2 text-brand ring-1 ring-brand/40' : 'btn btn-ghost py-2'}>
                  {l}
                </button>
              ))}
            </div>
            {f.payMethod === 'Mpesa' && (
              <Field label="M-Pesa number for pay" hint="Leave empty to use their phone." className="sm:col-span-2">
                <input className="field" inputMode="tel" value={f.payPhone} onChange={set('payPhone')} />
              </Field>
            )}
            {f.payMethod === 'Bank' && (
              <>
                <Field label="Bank">
                  <input className="field" value={f.bankName} onChange={set('bankName')} placeholder="e.g. Equity" />
                </Field>
                <Field label="Account number">
                  <input className="field" value={f.bankAccount} onChange={set('bankAccount')} />
                </Field>
              </>
            )}
          </fieldset>
          <details className="rounded-lg bg-white p-4 ring-1 ring-line">
            <summary className="cursor-pointer text-sm font-semibold">Statutory numbers and leave</summary>
            <div className="mt-3 grid gap-3 sm:grid-cols-2">
              <Field label="KRA PIN" hint="Needed for the P9 and PAYE return.">
                <input className="field uppercase" value={f.kraPin} onChange={set('kraPin')} maxLength={11} />
              </Field>
              <Field label="National ID">
                <input className="field" value={f.nationalId} onChange={set('nationalId')} />
              </Field>
              <Field label="NSSF number">
                <input className="field" value={f.nssfNumber} onChange={set('nssfNumber')} />
              </Field>
              <Field label="SHA (SHIF) number">
                <input className="field" value={f.shaNumber} onChange={set('shaNumber')} />
              </Field>
              <Field label="Annual leave days" hint="21 is the legal minimum.">
                <input className="field num" inputMode="decimal" value={f.annualLeaveDays} onChange={set('annualLeaveDays')} />
              </Field>
              <Field label="Left on" hint="Only when they leave.">
                <input className="field" type="date" value={f.endDate} onChange={set('endDate')} />
              </Field>
            </div>
          </details>
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
          <SheetFooter>
          <button className="btn btn-primary" disabled={state.busy}>
            {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} {existing ? 'Save changes' : 'Add employee'}
          </button>
          </SheetFooter>
        </div>
        <aside className="h-fit lg:sticky lg:top-0">
          <p className="mb-2 text-sm font-semibold text-muted">What this comes to, each month</p>
          {preview ? <PayBreakdown p={preview} /> : <p className="card p-4 text-sm text-muted">Type a salary to see take-home pay and deductions.</p>}
        </aside>
      </form>
    </Sheet>
  );
}

export function EmployeeList() {
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const [filter, setFilter] = useState(params.get('filter') ?? 'all');
  const [q, setQ] = useState('');
  const [adding, setAdding] = useState(false);
  const { data, error } = useApi<any>(`/employees?filter=${filter}&q=${encodeURIComponent(q)}`);
  return (
    <>
      <PageHeader print
        title="Employees"
        subtitle={data && `${data.counts.all} working here · ${ksh(data.monthlyPay)} a month in salaries before deductions`}
        actions={
          <button className="btn btn-primary" onClick={() => setAdding(true)}>
            <Plus className="h-4 w-4" /> Add employee
          </button>
        }
      />
      <div className="mb-4 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <Chips label="Filter employees" value={filter} onChange={setFilter} options={[['all', 'Working here', data?.counts.all], ['on-leave', 'On leave today', data?.counts.onLeave], ['left', 'Left', data?.counts.left]]} />
        <SearchBox value={q} onChange={setQ} placeholder="Search name, number, job or phone" />
      </div>
      {error && <ErrorNote>{error}</ErrorNote>}
      {!data ? (
        <Loading />
      ) : data.items.length === 0 ? (
        <Empty title={q || filter !== 'all' ? 'Nobody matches' : 'No employees yet'} action={!q && filter === 'all' && <button className="btn btn-primary" onClick={() => setAdding(true)}>Add your first employee</button>}>
          {q || filter !== 'all' ? 'Try another filter.' : 'Add the people you pay. Payroll works out PAYE, SHIF, Housing Levy and NSSF for each of them.'}
        </Empty>
      ) : (
        <ul className="card divide-y divide-line">
          {data.items.map((e: any) => (
            <li key={e.id}>
              <Link to={`/people/${e.id}`} className="flex items-center gap-3 px-4 py-3 hover:bg-paper">
                <span className="grid h-10 w-10 shrink-0 place-items-center rounded-full bg-brand-soft font-semibold text-brand">{e.name.slice(0, 1)}</span>
                <span className="min-w-0 flex-1">
                  <b className="block truncate text-ink">{e.name}</b>
                  <span className="text-sm text-muted block truncate">{[e.jobTitle, e.department, e.number].filter(Boolean).join(' · ')}</span>
                </span>
                {e.onLeave && <span className="shrink-0"><Status tone="warn">On leave</Status></span>}
                {!e.active && <span className="shrink-0"><Status tone="plain">Left {day(e.endDate)}</Status></span>}
                <span className="num hidden text-right font-semibold sm:block shrink-0 whitespace-nowrap">{ksh(e.basicSalary + e.taxableAllowances)}</span>
              </Link>
            </li>
          ))}
        </ul>
      )}
      <EmployeeEditor open={adding} onClose={() => setAdding(false)} onSaved={(e) => navigate(`/people/${e.id}`)} />
    </>
  );
}

/** Self-service login for an employee: they sign in with their phone to apply for leave and see payslips.
 * The starting password is shown once; they replace it on first sign-in. */
function AppAccess({ employee }: { employee: any }) {
  const { data, reload } = useApi<{ hasAccess: boolean; mustChangePassword: boolean; phone: string | null }>(`/employees/${employee.id}/access`);
  const [grant, setGrant] = useState<{ phone: string; password: string; reset: boolean } | null>(null);
  const [state, setState] = useState({ busy: false, error: '' });
  const act = async (fn: () => Promise<unknown>) => {
    setState({ busy: true, error: '' });
    try {
      await fn();
      reload();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const give = () => act(async () => setGrant(await api(`/employees/${employee.id}/access`, { body: {} })));
  const wa = msisdn(employee.phone);
  const message = grant && `Hi ${employee.name.split(' ')[0]}, you can now apply for leave and see your payslips on Kabuor ERP: ${window.location.origin}
Sign in with your phone ${grant.phone} and this one-time password: ${grant.password}
You'll choose your own password the first time.`;
  return (
    <section className="card p-4">
      <h2 className="flex items-center gap-2 text-sm font-semibold text-muted">
        <KeyRound className="h-4 w-4" /> App access
      </h2>
      {!data ? null : grant ? (
        <div className="mt-2 space-y-2 text-sm">
          <p>{grant.reset ? 'Login reset.' : 'Login created.'} Give {employee.name.split(' ')[0]} these details; the password works once:</p>
          <p className="rounded-md bg-paper p-3 font-mono text-xs">
            Phone: {prettyPhone(grant.phone)}
            <br />
            Password: {grant.password}
          </p>
          {wa && (
            <a className="btn btn-mpesa w-full" href={`https://wa.me/${wa}?text=${encodeURIComponent(message!)}`} target="_blank" rel="noreferrer">
              <MessageCircle className="h-4 w-4" /> Send on WhatsApp
            </a>
          )}
          <button className="text-xs font-semibold text-muted hover:underline" onClick={() => setGrant(null)}>
            Done, hide the password
          </button>
        </div>
      ) : data.hasAccess ? (
        <div className="mt-2 space-y-2 text-sm">
          <p>
            Signs in with <b>{prettyPhone(data.phone)}</b> to apply for leave and see payslips.
            {data.mustChangePassword && <span className="block text-xs text-warn">Hasn’t signed in and set a password yet.</span>}
          </p>
          <div className="flex flex-wrap gap-2">
            <button className="btn btn-ghost py-1.5 text-sm" disabled={state.busy} onClick={give}>
              Reset password
            </button>
            <button className="btn btn-ghost py-1.5 text-sm text-bad" disabled={state.busy} onClick={async () => (await confirmDialog({ title: `Remove ${employee.name}’s app access?`, body: 'They can no longer sign in to see their payslips or ask for leave.', confirm: 'Remove access', danger: true })) && act(() => api(`/employees/${employee.id}/access`, { method: 'DELETE' }))}>
              Remove access
            </button>
          </div>
        </div>
      ) : (
        <div className="mt-2 space-y-2 text-sm">
          <p className="text-muted">Let {employee.name.split(' ')[0]} apply for leave and see their own payslips and P9 from their phone. They see nothing else in the business.</p>
          <button className="btn btn-primary w-full" disabled={state.busy || !employee.phone} onClick={give}>
            {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Give app access
          </button>
          {!employee.phone && <p className="text-xs text-warn">Add their phone number first: they sign in with it.</p>}
        </div>
      )}
      {state.error && <div className="mt-2"><ErrorNote>{state.error}</ErrorNote></div>}
    </section>
  );
}

import { DeductionsCard } from '../components/Deductions';

export function EmployeeView() {
  const { id } = useParams();
  const { data, error, reload } = useApi<any>(`/employees/${id}`);
  const [editing, setEditing] = useState(false);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const e = data.employee;
  const lb = data.leaveBalance;
  const wa = msisdn(e.phone);
  return (
    <>
      <Link to="/people" className="mb-3 inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink">
        <ArrowLeft className="h-4 w-4" /> Employees
      </Link>
      <PageHeader print
        title={e.name}
        subtitle={[e.jobTitle, e.department, e.number, `since ${dayYear(e.startDate)}`].filter(Boolean).join(' · ')}
        actions={
          <>
            {wa && (
              <a className="btn btn-ghost" href={`https://wa.me/${wa}`} target="_blank" rel="noreferrer">
                <MessageCircle className="h-4 w-4" /> WhatsApp
              </a>
            )}
            <button className="btn btn-ghost" onClick={() => setEditing(true)}>
              <Pencil className="h-4 w-4" /> Edit
            </button>
          </>
        }
      />
      <div className="grid gap-3 sm:grid-cols-3">
        <Stat label="Take-home pay" value={ksh(data.monthlyPay.net)}>
          of {ksh(data.monthlyPay.gross)} gross a month
        </Stat>
        <Stat label="Annual leave left" value={`${lb.left} days`}>
          {lb.earned} earned this year · {lb.taken} taken
        </Stat>
        <Stat label="Paid by" value={e.payMethod === 'Mpesa' ? 'M-Pesa' : e.payMethod}>
          {e.payMethod === 'Mpesa' ? prettyPhone(e.payPhone) : e.payMethod === 'Bank' ? `${e.bankName} ${e.bankAccount}` : 'Cash'}
        </Stat>
      </div>
      <div className="mt-5 grid gap-5 lg:grid-cols-[1fr_22rem]">
        <section className="card overflow-hidden">
          <h2 className="border-b border-line px-4 py-3 text-sm font-semibold text-muted">Payslips</h2>
          {data.payslips.length ? (
            <ul className="divide-y divide-line">
              {data.payslips.map((p: any) => (
                <li key={p.runId}>
                  <Link to={`/payroll/${p.runId}?slip=${e.id}`} className="flex items-center justify-between gap-3 px-4 py-3 hover:bg-paper">
                    <span className="min-w-0 flex-1">
                      <b className="block text-ink truncate">
                        {new Date(`${p.period}T00:00:00`).toLocaleDateString('en-GB', { month: 'long', year: 'numeric' })} {p.status === 'Draft' && <Status tone="plain">Draft</Status>}
                      </b>
                      <span className="text-sm text-muted block truncate">
                        Gross {ksh(p.gross)} · PAYE {ksh(p.paye)}
                      </span>
                    </span>
                    <b className="num shrink-0 text-right whitespace-nowrap">{ksh(p.net)}</b>
                  </Link>
                </li>
              ))}
            </ul>
          ) : (
            <p className="p-4 text-sm text-muted">No payslips yet. They appear here after each payroll.</p>
          )}
        </section>
        <aside className="space-y-4">
          <AppAccess employee={e} />
          <DeductionsCard employee={e} />
          <PayBreakdown p={data.monthlyPay} />
          <section className="card p-4">
            <h2 className="text-sm font-semibold text-muted">Leave</h2>
            {data.leave.length ? (
              <ul className="mt-2 space-y-2 text-sm">
                {data.leave.slice(0, 8).map((l: any) => (
                  <li key={l.id} className="flex justify-between gap-2">
                    <span>
                      <b>{l.type}</b> · {l.days} day{l.days === 1 ? '' : 's'}
                      <span className="block text-xs text-muted">
                        {day(l.from)} – {day(l.to)}
                      </span>
                    </span>
                    <Status tone={l.status === 'Approved' ? 'good' : l.status === 'Pending' ? 'warn' : 'plain'}>{l.status}</Status>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="mt-2 text-sm text-muted">No leave taken.</p>
            )}
            <Link to="/leave" className="mt-3 inline-block text-sm font-semibold text-brand">
              Request leave →
            </Link>
          </section>
          <p className="text-xs text-muted">
            KRA PIN {e.kraPin || '—'} · NSSF {e.nssfNumber || '—'} · SHA {e.shaNumber || '—'}
          </p>
        </aside>
      </div>
      <EmployeeEditor open={editing} onClose={() => setEditing(false)} existing={e} onSaved={() => (setEditing(false), reload())} />
    </>
  );
}
