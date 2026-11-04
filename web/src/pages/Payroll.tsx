// Payroll, a month at a time: draft → check and adjust → approve (posts to the books) → pay → pay KRA, NSSF
// and SHA by the 9th. Each step says what it does before it's pressed.
import { useMemo, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { ArrowLeft, Check, Download, Loader2, MessageCircle, Plus, Trash2 } from 'lucide-react';
import { api, can, getSession, useApi, useSession } from '../lib/api';
import { cn, dayYear, ksh, msisdn, toCents, todayIso } from '../lib/format';
import { Empty, ErrorNote, Field, Loading, PageHeader, Sheet, Stat, Status, SheetFooter, PageError, confirmDialog } from '../components/ui';
import { parseLines } from '../components/Deductions';

const monthName = (iso: string) => new Date(`${iso}T00:00:00`).toLocaleDateString('en-GB', { month: 'long', year: 'numeric' });
const KIND_LABEL: Record<string, string> = { PAYE: 'PAYE (KRA)', NSSF: 'NSSF', SHIF: 'SHIF (SHA)', AHL: 'Housing Levy (KRA)', NITA: 'NITA' };

function StatusBadge({ s }: { s: string }) {
  return s === 'Draft' ? <Status tone="plain">Draft</Status> : s === 'Approved' ? <Status tone="warn">Approved, not paid</Status> : <Status tone="good">Paid</Status>;
}

export function PayrollList() {
  const navigate = useNavigate();
  const { data, error } = useApi<any[]>('/payroll/runs');
  const [month, setMonth] = useState(todayIso().slice(0, 7));
  const [state, setState] = useState({ busy: false, error: '' });
  const start = async () => {
    setState({ busy: true, error: '' });
    try {
      const run = await api('/payroll/runs', { body: { month } });
      navigate(`/payroll/${run.id}`);
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <>
      <PageHeader print title="Payroll" subtitle="PAYE, SHIF, Housing Levy, NSSF and NITA worked out for every employee, every month." />
      <section className="card mb-5 flex flex-wrap items-end gap-3 p-4">
        <Field label="Month to pay">
          <input className="field" type="month" value={month} onChange={(e) => setMonth(e.target.value)} />
        </Field>
        <button className="btn btn-primary" onClick={start} disabled={state.busy || !month}>
          {state.busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Plus className="h-4 w-4" />} Start {month && monthName(`${month}-01`)} payroll
        </button>
        {state.error && <div className="w-full"><ErrorNote>{state.error}</ErrorNote></div>}
      </section>
      {error && <ErrorNote>{error}</ErrorNote>}
      {!data ? (
        <Loading />
      ) : data.length === 0 ? (
        <Empty title="No payroll yet">Start a month above. Everyone employed that month is added with their saved pay; you can add overtime or deductions before approving.</Empty>
      ) : (
        <ul className="card divide-y divide-line">
          {data.map((r) => (
            <li key={r.id}>
              <Link to={`/payroll/${r.id}`} className="flex flex-wrap items-center gap-3 px-4 py-3 hover:bg-paper">
                <span className="min-w-0 flex-1">
                  <b className="block text-ink">{monthName(r.period)}</b>
                  <span className="text-sm text-muted block truncate">
                    {r.people} {r.people === 1 ? 'person' : 'people'} · take-home {ksh(r.net)} · PAYE {ksh(r.paye)}
                  </span>
                </span>
                <span className="shrink-0"><StatusBadge s={r.status} /></span>
                <span className="num text-right shrink-0 whitespace-nowrap">
                  <b className="block">{ksh(r.employerCost)}</b>
                  <span className="text-xs text-muted block">total cost</span>
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

function PayslipSheet({ slip, run, company, open, onClose, onSaved }: { slip: any; run: any; company: string; open: boolean; onClose: () => void; onSaved: () => void }) {
  const [extra, setExtra] = useState('');
  const [deduct, setDeduct] = useState('');
  const [note, setNote] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });
  const draft = run.status === 'Draft';
  const employee = useApi<any>(open && slip ? `/employees/${slip.employeeId}` : null);
  if (!slip) return null;
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(`/payroll/runs/${run.id}/payslips/${slip.id}`, { method: 'PUT', body: { extraEarnings: extra === '' ? null : toCents(extra), otherDeductions: deduct === '' ? null : toCents(deduct), note } });
      onSaved();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const lines: { label: string; amount: number; sub?: string; isTotal?: boolean }[] = [
    { label: 'Basic salary', amount: slip.basic },
    { label: 'Taxable allowances', amount: slip.taxableAllowances },
    { label: 'Overtime, bonus, commission', amount: slip.extraEarnings },
    { label: 'Gross pay', amount: slip.gross, isTotal: true },
    { label: 'NSSF pension', amount: -slip.nssfEmployee },
    { label: 'SHIF (SHA health)', amount: -slip.shif },
    { label: 'Housing levy (AHL)', amount: -slip.housingLevyEmployee },
    {
      label: 'PAYE income tax',
      amount: -slip.paye,
      sub: slip.payeBeforeRelief > 0 ? `Tax ${ksh(slip.payeBeforeRelief)} less personal relief ${ksh(slip.personalRelief)}` : undefined,
    },
    { label: 'Untaxed allowances', amount: slip.nonTaxableAllowances },
    ...parseLines(slip)
      .filter((l) => l.amount > 0)
      .map((l) => ({
        label: `${l.name}${l.payTo ? ` (${l.payTo})` : ''}${l.kind === 'Pension' ? ', before tax' : ''}`,
        amount: -l.amount,
        sub: l.wanted != null ? `Cut from ${ksh(l.wanted)} so they keep a third of their pay (Employment Act s.19)` : undefined,
      })),
  ];
  const wa = msisdn(employee.data?.employee.phone);
  const text = `Payslip, ${company}, ${monthName(run.period)}\n${slip.employeeName}\nGross: ${ksh(slip.gross)}\nNSSF: ${ksh(slip.nssfEmployee)}\nSHIF: ${ksh(slip.shif)}\nHousing Levy: ${ksh(slip.housingLevyEmployee)}\nPAYE: ${ksh(slip.paye)}${parseLines(slip).filter((l) => l.amount > 0).map((l) => `\n${l.name}: ${ksh(l.amount)}`).join('')}\nTake-home: ${ksh(slip.net)}`;
  return (
    <Sheet open={open} onClose={onClose} title={`${slip.employeeName} · ${monthName(run.period)}`} description="Payslip: pay, deductions and take-home for the month." wide>
      <dl className="card divide-y divide-line/60 p-4 text-sm">
        {lines
          .filter((line, i) => line.amount !== 0 || i < 4)
          .map((line) => (
            <div
              key={line.label}
              className={cn(
                'flex items-baseline justify-between gap-3 py-2 first:pt-0 last:pb-0',
                line.isTotal && 'font-semibold bg-paper/50 -mx-4 px-4 py-2 border-y border-line my-1'
              )}
            >
              <div className="min-w-0 flex-1">
                <dt className={line.isTotal ? 'text-ink font-semibold' : 'text-muted'}>{line.label}</dt>
                {line.sub && <span className="block text-xs text-muted/80 mt-0.5">{line.sub}</span>}
              </div>
              <dd className={cn('num shrink-0 text-right font-medium whitespace-nowrap', line.isTotal && 'text-ink font-semibold')}>
                {line.amount < 0 ? `−${ksh(-line.amount)}` : ksh(line.amount)}
              </dd>
            </div>
          ))}
        <div className="flex items-baseline justify-between gap-3 pt-3 text-base font-bold">
          <dt className="text-ink">Take-home pay</dt>
          <dd className="num shrink-0 text-right text-lg text-brand whitespace-nowrap">{ksh(slip.net)}</dd>
        </div>
      </dl>
      {slip.note && <p className="mt-2 text-sm text-muted">Note: {slip.note}</p>}
      {wa && (
        <SheetFooter cancel={false}>
        <a className="btn btn-mpesa" href={`https://wa.me/${wa}?text=${encodeURIComponent(text)}`} target="_blank" rel="noreferrer">
          <MessageCircle className="h-4 w-4" /> Send payslip on WhatsApp
        </a>
        </SheetFooter>
      )}
      {draft && (
        <div className="mt-5 space-y-3">
          <p className="text-sm font-semibold">Change this month only</p>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <Field label="Overtime / bonus (KSh)" hint={`Now ${ksh(slip.extraEarnings)}`}>
              <input className="field num" inputMode="decimal" value={extra} onChange={(e) => setExtra(e.target.value)} placeholder={String(slip.extraEarnings / 100)} />
            </Field>
            <Field label="One-off deduction (KSh)" hint="Agreed deductions (HELB, SACCO, loans) are added automatically from the employee’s page.">
              <input className="field num" inputMode="decimal" value={deduct} onChange={(e) => setDeduct(e.target.value)} placeholder={String((parseLines(slip).find((l) => !l.deductionId)?.amount ?? 0) / 100)} />
            </Field>
          </div>
          <Field label="Note on the payslip">
            <input className="field" value={note} onChange={(e) => setNote(e.target.value)} placeholder={slip.note || 'e.g. Advance taken 2 Oct'} />
          </Field>
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
          <SheetFooter>
          <button className="btn btn-primary" onClick={save} disabled={state.busy}>
            {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Recalculate
          </button>
          </SheetFooter>
        </div>
      )}
    </Sheet>
  );
}

function PaySheet({ title, text, open, onClose, onConfirm }: { title: string; text: string; open: boolean; onClose: () => void; onConfirm: (method: string, reference: string) => Promise<void> }) {
  const [method, setMethod] = useState('Bank');
  const [reference, setReference] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });
  const go = async () => {
    setState({ busy: true, error: '' });
    try {
      await onConfirm(method, reference);
      onClose();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open={open} onClose={onClose} title={title}>
      <p className="text-sm text-muted">{text}</p>
      <div className="mt-4 grid grid-cols-3 gap-2">
        {[['Mpesa', 'M-Pesa'], ['Bank', 'Bank'], ['Cash', 'Cash']].map(([v, l]) => (
          <button key={v} type="button" aria-pressed={method === v} onClick={() => setMethod(v)} className={method === v ? 'btn bg-brand-soft text-brand ring-1 ring-brand/40' : 'btn btn-ghost'}>
            {l}
          </button>
        ))}
      </div>
      <Field label="Reference" hint="M-Pesa code, bank reference or KRA payment number (PRN)." className="mt-4">
        <input className="field uppercase" value={reference} onChange={(e) => setReference(e.target.value)} />
      </Field>
      {state.error && <div className="mt-3"><ErrorNote>{state.error}</ErrorNote></div>}
      <SheetFooter>
      <button className="btn btn-primary" onClick={go} disabled={state.busy}>
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Record payment
      </button>
      </SheetFooter>
    </Sheet>
  );
}

export function PayrollRun() {
  const { id } = useParams();
  const navigate = useNavigate();
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const { data, error, reload } = useApi<any>(`/payroll/runs/${id}`);
  const [paying, setPaying] = useState<null | { kind: string; amount: number }>(null);
  const [state, setState] = useState({ busy: '', error: '' });
  const slipId = params.get('slip');
  const slip = useMemo(() => data?.payslips.find((p: any) => p.id === slipId || p.employeeId === slipId), [data, slipId]);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const run = data.run;
  const t = data.totals;
  const finance = can(session, 'finance');

  const act = async (name: string, fn: () => Promise<unknown>) => {
    setState({ busy: name, error: '' });
    try {
      await fn();
      reload();
      setState({ busy: '', error: '' });
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };
  const p10 = async () => {
    const res = await fetch(`/api/payroll/runs/${run.id}/p10.csv`, { headers: { Authorization: `Bearer ${getSession()?.token}` } });
    const url = URL.createObjectURL(await res.blob());
    const a = Object.assign(document.createElement('a'), { href: url, download: `PAYE-${run.period.slice(0, 7)}.csv` });
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <>
      <Link to="/payroll" className="mb-3 inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink">
        <ArrowLeft className="h-4 w-4" /> Payroll
      </Link>
      <PageHeader print
        title={`${monthName(run.period)} payroll`}
        subtitle={<StatusBadge s={run.status} />}
        actions={
          <>
            {run.status === 'Draft' && (
              <button className="btn btn-ghost text-bad" onClick={async () => (await confirmDialog({ title: 'Delete this draft payroll?', body: 'Nothing has been paid or booked from it yet.', confirm: 'Delete draft', danger: true })) && act('delete', async () => (await api(`/payroll/runs/${run.id}`, { method: 'DELETE' }), navigate('/payroll')))}>
                <Trash2 className="h-4 w-4" /> Delete draft
              </button>
            )}
            {run.status === 'Draft' && finance && (
              <button className="btn btn-primary" disabled={Boolean(state.busy)} onClick={() => act('approve', () => api(`/payroll/runs/${run.id}/approve`, { body: {} }))}>
                {state.busy === 'approve' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />} Approve payroll
              </button>
            )}
            {run.status === 'Approved' && finance && (
              <button className="btn btn-primary" onClick={() => setPaying({ kind: 'NET', amount: t.net })}>
                Record salaries paid
              </button>
            )}
            {run.status !== 'Draft' && (
              <button className="btn btn-ghost" onClick={p10}>
                <Download className="h-4 w-4" /> PAYE file (P10)
              </button>
            )}
          </>
        }
      />
      {run.status === 'Draft' && <p className="mb-4 rounded-md bg-brand-soft px-4 py-3 text-sm">Check each person, add overtime or advances, then approve. {finance ? 'Approving posts the payroll to your books.' : 'The owner or an accountant approves it.'}</p>}
      {state.error && <div className="mb-4"><ErrorNote>{state.error}</ErrorNote></div>}
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Take-home pay" value={ksh(t.net)}>
          to {data.payslips.length} {data.payslips.length === 1 ? 'person' : 'people'}
        </Stat>
        <Stat label="Gross pay" value={ksh(t.gross)} />
        <Stat label="PAYE" value={ksh(t.paye)} />
        <Stat label="Employer contributions" value={ksh(t.employerCost)}>
          NSSF, Housing Levy, NITA
        </Stat>
      </div>

      {/* The payslip table needs every column in view to check a month; the remittances sit beside it only when there is room. */}
      <div className="mt-5 grid gap-5 2xl:grid-cols-[1fr_22rem]">
        <section className="card overflow-x-auto">
          <table className="w-full min-w-[46rem] text-sm">
            <thead className="border-b border-line bg-paper text-left text-xs text-muted">
              <tr>
                <th className="px-4 py-2.5 font-semibold whitespace-nowrap">Employee</th>
                <th className="px-3 py-2.5 text-right font-semibold whitespace-nowrap">Gross</th>
                <th className="px-3 py-2.5 text-right font-semibold whitespace-nowrap">NSSF</th>
                <th className="px-3 py-2.5 text-right font-semibold whitespace-nowrap">SHIF</th>
                <th className="px-3 py-2.5 text-right font-semibold whitespace-nowrap">Housing</th>
                <th className="px-3 py-2.5 text-right font-semibold whitespace-nowrap">PAYE</th>
                <th className="px-4 py-2.5 text-right font-semibold whitespace-nowrap">Take-home</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line">
              {data.payslips.map((p: any) => (
                <tr key={p.id} onClick={() => setParams({ slip: p.id })} className="cursor-pointer hover:bg-paper">
                  <td className="px-4 py-3 min-w-[12rem]">
                    <b className="block text-ink">{p.employeeName}</b>
                    {(p.extraEarnings > 0 || p.otherDeductions > 0) && (
                      <span className="block text-xs text-muted mt-0.5 whitespace-nowrap">
                        {[p.extraEarnings > 0 && `+${ksh(p.extraEarnings)} extra`, p.otherDeductions > 0 && `−${ksh(p.otherDeductions)} deducted`].filter(Boolean).join(' · ')}
                      </span>
                    )}
                  </td>
                  <td className="num px-3 py-3 text-right whitespace-nowrap">{ksh(p.gross)}</td>
                  <td className="num px-3 py-3 text-right text-muted whitespace-nowrap">{ksh(p.nssfEmployee)}</td>
                  <td className="num px-3 py-3 text-right text-muted whitespace-nowrap">{ksh(p.shif)}</td>
                  <td className="num px-3 py-3 text-right text-muted whitespace-nowrap">{ksh(p.housingLevyEmployee)}</td>
                  <td className="num px-3 py-3 text-right text-muted whitespace-nowrap">{ksh(p.paye)}</td>
                  <td className="num px-4 py-3 text-right font-semibold whitespace-nowrap text-ink">{ksh(p.net)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
        <aside className="card h-fit p-4">
          <h2 className="text-sm font-semibold text-muted">Pay to KRA, NSSF and SHA</h2>
          {(() => {
            const due = data.statutory[0]?.due;
            const unpaid = data.statutory.some((x: any) => !x.paid && x.amount > 0);
            const late = unpaid && run.status !== 'Draft' && due < todayIso();
            return <p className={cn('mt-0.5 text-xs', late ? 'font-semibold text-bad' : 'text-muted')}>{late ? `Overdue since ${dayYear(due)}: penalties and interest apply` : `Due by ${dayYear(due)}`}</p>;
          })()}
          <ul className="mt-3 space-y-3 text-sm divide-y divide-line/60">
            {data.statutory.map((s: any) => (
              <li key={s.kind} className="flex items-center justify-between gap-3 pt-2.5 first:pt-0">
                <span className="min-w-0 flex-1">
                  <b className="block truncate text-ink">{KIND_LABEL[s.kind] ?? `${s.label} (deductions)`}</b>
                  <span className="num text-muted text-xs block">{ksh(s.amount)}</span>
                </span>
                <span className="shrink-0">
                  {s.paid ? (
                    <Status tone="good">Paid</Status>
                  ) : run.status === 'Draft' || !finance || s.amount === 0 ? (
                    <Status tone="plain">{s.amount === 0 ? 'Nothing due' : 'Not yet'}</Status>
                  ) : (
                    <button className="btn btn-ghost py-1 px-2.5 text-xs font-semibold whitespace-nowrap" onClick={() => setPaying({ kind: s.kind, amount: s.amount })}>
                      Record paid
                    </button>
                  )}
                </span>
              </li>
            ))}
          </ul>
        </aside>
      </div>

      <PayslipSheet slip={slip} run={run} company={session?.company.name ?? ''} open={Boolean(slip)} onClose={() => setParams({})} onSaved={reload} />
      <PaySheet
        open={Boolean(paying)}
        onClose={() => setPaying(null)}
        title={paying?.kind === 'NET' ? `Salaries for ${monthName(run.period)}` : `${KIND_LABEL[paying?.kind ?? ''] ?? paying?.kind?.replace('TO:', '') ?? ''} for ${monthName(run.period)}`}
        text={paying?.kind === 'NET' ? `Record that ${ksh(paying.amount)} take-home pay has been sent (M-Pesa bulk payment, bank transfer or cash).` : `Record that ${ksh(paying?.amount ?? 0)} has been paid.`}
        onConfirm={async (method, reference) => {
          if (paying?.kind === 'NET') await api(`/payroll/runs/${run.id}/pay`, { body: { method, reference } });
          else await api(`/payroll/runs/${run.id}/remit`, { body: { kind: paying?.kind, method, reference } });
          reload();
        }}
      />
    </>
  );
}
