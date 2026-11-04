// The subscription. The owner is deciding "which plan, for how long, and is it worth it now?", so each plan shows
// whether today's team fits, the total for the chosen length (and what paying for a year saves), and when the
// paid time would run to. The trial banner in the frame says how many days are left before anything changes.
import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { Check, Loader2, Smartphone } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { cn, dayYear, ksh } from '../lib/format';
import { ErrorNote, Field, Loading, Status } from './ui';

type Plan = { id: string; name: string; monthly: number; yearly: number; users: number; staff: number; includes: string[] };
type Billing = {
  plan: string; planName: string; paidUntil: string | null; trialEnds: string; daysLeft: number; onTrial: boolean; locked: boolean; graceDays: number;
  users: number; staff: number; plans: Plan[]; testMode: boolean; canPay: boolean; payTo: string; phone: string;
  payments: { id: string; plan: string; months: number; amount: number; status: string; receipt: string; test: boolean; createdAt: string; note: string }[];
};

const LENGTHS = [1, 3, 6, 12] as const;
const total = (p: Plan, months: number) => (months >= 12 ? p.yearly * (months / 12) : p.monthly * months);

export default function PlanSettings() {
  const { data, error, loading, reload } = useApi<Billing>('/billing');
  const [choice, setChoice] = useState<string | null>(null);
  const [months, setMonths] = useState<number>(12);
  const [phone, setPhone] = useState<string | null>(null);
  const [state, setState] = useState({ busy: false, error: '', done: '' });
  const [waiting, setWaiting] = useState<string | null>(null);
  // A prompt is on the phone: follow it until M-Pesa says paid or not, then show the new plan.
  useEffect(() => {
    if (!waiting) return;
    const t = setInterval(async () => {
      try {
        const r = await api<{ payment: { status: string; note: string; receipt: string } }>(`/billing/payments/${waiting}/check`, { body: {} });
        if (r.payment.status === 'Pending') return;
        setWaiting(null);
        setState({ busy: false, error: r.payment.status === 'Failed' ? `Not paid: ${r.payment.note || 'M-Pesa didn’t confirm it'}. Nothing was charged.` : '', done: r.payment.status === 'Paid' ? `Paid${r.payment.receipt ? ` (${r.payment.receipt})` : ''}. Your plan is updated.` : '' });
        reload();
      } catch {
        /* try again on the next tick */
      }
    }, 3000);
    return () => clearInterval(t);
  }, [waiting, reload]);
  if (loading && !data) return <Loading />;
  if (error || !data) return <ErrorNote>{error ?? 'Couldn’t load your plan.'}</ErrorNote>;

  const picked = data.plans.find((p) => p.id === (choice ?? (data.plan || 'business')))!;
  const runsTo = new Date(Math.max(Date.now(), data.paidUntil ? Date.parse(data.paidUntil) : 0));
  runsTo.setMonth(runsTo.getMonth() + months);
  const tooSmall = data.users > picked.users || data.staff > picked.staff;

  const number = phone ?? data.phone ?? '';
  const pay = async () => {
    setState({ busy: true, error: '', done: '' });
    try {
      const r = await api<any>('/billing/pay', { body: { plan: picked.id, months, phone: number } });
      if (r.status === 'Pending') setWaiting(r.id);
      setState({ busy: false, error: '', done: r.status === 'Paid' ? `Paid. You’re on ${picked.name} until ${dayYear(runsTo.toISOString())}.` : `Check ${number} for the M-Pesa prompt and enter your PIN. This updates by itself once it’s paid.` });
      reload();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message, done: '' });
    }
  };

  return (
    <div className="space-y-4">
      <section className={cn('card flex flex-wrap items-center gap-x-6 gap-y-2 p-5', data.locked && 'ring-2 ring-bad')}>
        <div className="min-w-0">
          <p className="text-xs font-semibold tracking-wide text-muted uppercase">Your plan</p>
          <p className="text-lg font-bold">{data.planName}</p>
        </div>
        <p className="text-sm">
          {data.locked ? (
            <b className="text-bad">{data.paidUntil ? 'Plan ran out' : 'Trial ended'}. You can still see and download everything; {data.paidUntil ? 'renew' : 'choose a plan'} to add and change records again.</b>
          ) : data.onTrial ? (
            <><b>{Math.max(data.daysLeft, 0)} days</b> of the free trial left (ends {dayYear(data.trialEnds)}), then {data.graceDays} days’ grace.</>
          ) : (
            <>Paid until <b>{dayYear(data.paidUntil)}</b> · {data.daysLeft} days</>
          )}
        </p>
        <p className="text-sm text-muted sm:ml-auto">{data.users} user{data.users === 1 ? '' : 's'} · {data.staff} on payroll</p>
      </section>

      <section className="grid gap-3 md:grid-cols-3">
        {data.plans.map((p) => {
          const fits = data.users <= p.users && data.staff <= p.staff;
          const active = p.id === picked.id;
          return (
            <button key={p.id} onClick={() => setChoice(p.id)} className={cn('card flex flex-col gap-3 p-5 text-left', active ? 'ring-2 ring-brand' : 'hover:ring-faint')} aria-pressed={active}>
              <div className="flex items-start justify-between gap-2">
                <b className="text-base">{p.name}</b>
                {data.plan === p.id && !data.onTrial ? <Status tone="good">Current</Status> : fits ? <Status tone="plain">Fits your team</Status> : <Status tone="warn">Too small for {data.users} users</Status>}
              </div>
              <p><span className="text-2xl font-bold">{ksh(p.monthly)}</span> <span className="text-sm text-muted">a month</span></p>
              <p className="text-xs text-muted">or {ksh(p.yearly)} a year: two months free</p>
              <ul className="space-y-1 text-sm">
                {p.includes.map((x) => <li key={x} className="flex gap-2"><Check className="mt-0.5 h-4 w-4 shrink-0 text-good" />{x}</li>)}
              </ul>
            </button>
          );
        })}
      </section>

      <section className="card space-y-4 p-5">
        <div className="flex flex-wrap items-end gap-4">
          <Field label="How long">
            <div className="flex gap-1.5" role="group" aria-label="How long">
              {LENGTHS.map((m) => (
                <button key={m} onClick={() => setMonths(m)} className={cn('rounded-md px-3 py-2 text-sm font-semibold ring-1', months === m ? 'bg-brand text-white ring-brand' : 'bg-white ring-line')}>
                  {m === 12 ? '1 year' : `${m} month${m > 1 ? 's' : ''}`}
                </button>
              ))}
            </div>
          </Field>
          <Field label="M-Pesa number" hint="The prompt goes to this phone.">
            <input className="field w-48" inputMode="tel" placeholder="07xx xxx xxx" value={number} onChange={(e) => setPhone(e.target.value)} />
          </Field>
        </div>
        <p className="text-sm">
          <b className="text-base">{ksh(total(picked, months))}</b> for {picked.name}, {months === 12 ? 'a year' : `${months} month${months > 1 ? 's' : ''}`}
          {months === 12 && <span className="text-good"> (saves {ksh(picked.monthly * 2)})</span>}
          , running to <b>{dayYear(runsTo.toISOString())}</b>.
        </p>
        {tooSmall && <p className="text-sm text-warn">{picked.name} has room for {picked.users} users and {picked.staff} staff; you have {data.users} and {data.staff}. Choose a bigger plan or remove users in <Link className="underline" to="/settings?tab=team">Team</Link>.</p>}
        {data.payTo && <p className="text-xs text-muted">Paid to Kabuor · {data.payTo}. The prompt shows that name on your phone before you enter your PIN.</p>}
        {!data.canPay && <ErrorNote>M-Pesa payments aren’t switched on for this server yet. Call or WhatsApp Kabuor to renew.</ErrorNote>}
        {data.canPay && data.testMode && <p className="text-xs text-muted">Test mode: this server has no M-Pesa keys, so payments are recorded as test payments and no money moves.</p>}
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        {state.done && <p className="rounded-md bg-good-soft px-4 py-2.5 text-sm font-medium text-good">{state.done}</p>}
        <button className="btn btn-mpesa" disabled={state.busy || Boolean(waiting) || !data.canPay || tooSmall || number.replace(/\D/g, '').length < 9} onClick={pay}>
          {state.busy || waiting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Smartphone className="h-4 w-4" />} {waiting ? 'Waiting for your PIN…' : `Pay ${ksh(total(picked, months))} by M-Pesa`}
        </button>
      </section>

      {data.payments.length > 0 && (
        <section className="card p-5">
          <h3 className="mb-2 text-sm font-semibold">Payments</h3>
          <ul className="divide-y divide-line text-sm">
            {data.payments.map((p) => (
              <li key={p.id} className="flex flex-wrap justify-between gap-2 py-2">
                <span>{dayYear(p.createdAt)} · {data.plans.find((x) => x.id === p.plan)?.name} · {p.months === 12 ? '1 year' : `${p.months} mo`}</span>
                <span className="flex items-center gap-2">
                  {ksh(p.amount)}
                  <Status tone={p.status === 'Paid' ? 'good' : p.status === 'Failed' ? 'bad' : 'warn'}>{p.test ? 'Test · ' : ''}{p.status === 'Paid' ? p.receipt : p.status === 'Failed' ? p.note : 'Waiting for PIN'}</Status>
                </span>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

/** Shown in the frame to owners in the last 10 days of the trial or the last week of paid time, and once it has ended. */
export function TrialBanner() {
  const { data } = useApi<Billing>('/billing');
  if (!data) return null;
  if (!data.onTrial && !data.locked) {
    if (!data.paidUntil || data.daysLeft > 7) return null;
    return (
      <div className="mb-4 flex flex-wrap items-center gap-x-4 gap-y-2 rounded-md bg-warn-soft px-4 py-2.5 text-sm text-warn">
        <span className="font-medium">Your {data.planName} plan runs out {data.daysLeft <= 0 ? 'today' : `in ${data.daysLeft} day${data.daysLeft === 1 ? '' : 's'}`} ({dayYear(data.paidUntil)}). After that, records become read-only until it’s renewed.</span>
        <Link to="/settings?tab=plan" className="font-semibold underline">Renew</Link>
      </div>
    );
  }
  if (!data.locked && data.daysLeft > 10) return null;
  return (
    <div className={cn('mb-4 flex flex-wrap items-center gap-x-4 gap-y-2 rounded-md px-4 py-2.5 text-sm', data.locked ? 'bg-bad-soft text-bad' : 'bg-warn-soft text-warn')}>
      <span className="font-medium">
        {data.locked ? `${data.paidUntil ? 'Your plan has run out' : 'Your free trial has ended'}. Everything is still here to see and download; ${data.paidUntil ? 'renew' : 'choose a plan'} to keep working.` : data.daysLeft > 0 ? `${data.daysLeft} day${data.daysLeft === 1 ? '' : 's'} left on your free trial.` : `Trial over: ${data.graceDays + data.daysLeft} days of grace left before records become read-only.`}
      </span>
      <Link to="/settings?tab=plan" className="font-semibold underline">{data.paidUntil ? 'Renew' : 'Choose a plan'}</Link>
    </div>
  );
}
