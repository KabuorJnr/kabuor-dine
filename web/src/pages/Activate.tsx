// Paying to start. A new business has just signed up; the owner is deciding "which plan, for how long, and is the
// money going to the right place?". So every plan and length shows its total before it's tapped, the button says
// exactly what will happen (amount, where to, which phone), and once the prompt is sent the screen follows the
// payment by itself until M-Pesa confirms it, then opens the account.
import { useEffect, useRef, useState } from 'react';
import { Check, CheckCircle2, Loader2, LogOut, Smartphone, XCircle } from 'lucide-react';
import { api, can, setSession, useApi, type Session } from '../lib/api';
import { cn, dayYear, ksh, prettyPhone } from '../lib/format';
import { ErrorNote, Field, Loading } from '../components/ui';
import { Frame } from './Auth';

type Plan = { id: string; name: string; monthly: number; yearly: number; users: number; staff: number; includes: string[] };
type Payment = { id: string; plan: string; months: number; amount: number; phone: string; status: 'Pending' | 'Paid' | 'Failed'; receipt: string; test: boolean; note: string; createdAt: string };
type Billing = { plans: Plan[]; phone: string; canPay: boolean; testMode: boolean; payTo: string; users: number; awaitingFirstPayment: boolean };

const LENGTHS = [1, 3, 6, 12] as const;
const total = (p: Plan, months: number) => (months >= 12 ? p.yearly * (months / 12) : p.monthly * months);
const lengthName = (m: number) => (m === 12 ? '1 year' : `${m} month${m > 1 ? 's' : ''}`);

export default function Activate({ session }: { session: Session }) {
  const first = session.user.name.split(' ')[0];
  if (!can(session, 'admin'))
    return (
      <Frame>
        <h1 className="font-display text-3xl font-semibold tracking-tight">{session.company.name} isn’t open yet</h1>
        <p className="mt-2 text-sm text-muted">The owner pays for a plan by M-Pesa to open the account. Once that’s done, sign in again and you’re in.</p>
        <SignOut />
      </Frame>
    );
  return (
    <Frame>
      <Steps />
      <h1 className="mt-6 font-display text-3xl font-semibold tracking-tight">Start your plan</h1>
      <p className="mt-1 text-sm text-muted">Hi {first}. {session.company.name} is ready: pay by M-Pesa to open it. It takes a minute.</p>
      <Checkout session={session} />
      <SignOut />
    </Frame>
  );
}

function Steps() {
  const steps = ['Your business', 'Pay to start', 'Set up'];
  return (
    <ol className="flex items-center gap-2 text-xs font-semibold" aria-label="Opening your account">
      {steps.map((s, i) => (
        <li key={s} className="flex items-center gap-2">
          <span className={cn('grid h-5 w-5 place-items-center rounded-full text-2xs', i === 0 ? 'bg-good text-white' : i === 1 ? 'bg-brand text-white' : 'bg-line text-muted')} aria-current={i === 1 ? 'step' : undefined}>
            {i === 0 ? <Check className="h-3 w-3" /> : i + 1}
          </span>
          <span className={i === 1 ? 'text-ink' : 'text-muted'}>{s}</span>
          {i < steps.length - 1 && <span className="h-px w-4 bg-line" />}
        </li>
      ))}
    </ol>
  );
}

function SignOut() {
  return (
    <button onClick={() => setSession(null)} className="mt-8 flex w-full items-center justify-center gap-2 text-sm text-muted hover:text-ink">
      <LogOut className="h-4 w-4" /> Sign out
    </button>
  );
}

function Checkout({ session }: { session: Session }) {
  const { data, error, loading } = useApi<Billing>('/billing');
  const [planId, setPlanId] = useState('business');
  const [months, setMonths] = useState<number>(1);
  const [phone, setPhone] = useState<string | null>(null);
  const [payment, setPayment] = useState<Payment | null>(null);
  const [state, setState] = useState({ busy: false, error: '' });
  const [now, setNow] = useState(Date.now());

  // While the prompt is on the phone: ask how it went every 3 seconds, until it's paid or failed.
  const pending = payment?.status === 'Pending';
  useEffect(() => {
    if (!pending || !payment) return;
    const t = setInterval(async () => {
      setNow(Date.now());
      try {
        const r = await api<{ payment: Payment; awaitingFirstPayment: boolean }>(`/billing/payments/${payment.id}/check`, { body: {} });
        setPayment(r.payment);
      } catch {
        /* a dropped connection: try again on the next tick */
      }
    }, 3000);
    return () => clearInterval(t);
  }, [pending, payment?.id]);

  // Paid: a moment to read the receipt, then the account opens on the setup checklist.
  const opened = useRef(false);
  useEffect(() => {
    if (payment?.status !== 'Paid' || opened.current) return;
    opened.current = true;
    const t = setTimeout(() => setSession({ ...session, company: { ...session.company, awaitingFirstPayment: false } }), 1800);
    return () => clearTimeout(t);
  }, [payment?.status]);

  if (loading && !data) return <Loading />;
  if (error || !data) return <ErrorNote>{error ?? 'Couldn’t load the plans. Check your connection and refresh.'}</ErrorNote>;

  const plan = data.plans.find((p) => p.id === planId) ?? data.plans[0];
  const number = phone ?? prettyPhone(data.phone);
  const amount = total(plan, months);
  const runsTo = new Date();
  runsTo.setMonth(runsTo.getMonth() + months);

  const pay = async () => {
    setState({ busy: true, error: '' });
    try {
      const p = await api<Payment>('/billing/pay', { body: { plan: plan.id, months, phone: number } });
      setPayment(p);
      setNow(Date.now());
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };

  if (payment) {
    const waited = Math.max(0, Math.round((now - Date.parse(payment.createdAt)) / 1000));
    return (
      <section className="mt-6 card p-5" aria-live="polite">
        {payment.status === 'Pending' && (
          <>
            <div className="flex items-start gap-3">
              <span className="grid h-10 w-10 shrink-0 place-items-center rounded-full bg-mpesa/10 text-mpesa">
                <Smartphone className="h-5 w-5" />
              </span>
              <div>
                <p className="font-semibold">Check {prettyPhone(payment.phone)}</p>
                <p className="mt-0.5 text-sm text-muted">Enter your M-Pesa PIN to pay <b className="text-ink">{ksh(payment.amount)}</b>{data.payTo ? ` to ${data.payTo}` : ''}.</p>
              </div>
            </div>
            <p className="mt-4 flex items-center gap-2 text-sm text-muted">
              <Loader2 className="h-4 w-4 animate-spin" /> Waiting for M-Pesa to confirm{waited > 5 ? ` · ${waited}s` : ''}. This page updates by itself.
            </p>
            {waited >= 60 && (
              <button className="btn btn-ghost mt-4 w-full" onClick={() => setPayment(null)}>
                Didn’t get the prompt? Send it again
              </button>
            )}
          </>
        )}
        {payment.status === 'Paid' && (
          <div className="flex items-start gap-3">
            <CheckCircle2 className="h-10 w-10 shrink-0 text-good" />
            <div>
              <p className="font-semibold">Paid{payment.receipt ? ` · ${payment.receipt}` : ''}</p>
              <p className="mt-0.5 text-sm text-muted">{plan.name} until {dayYear(runsTo.toISOString())}. Opening your account…</p>
              {payment.test && <p className="mt-1 text-xs text-muted">Test payment: no money moved.</p>}
            </div>
          </div>
        )}
        {payment.status === 'Failed' && (
          <>
            <div className="flex items-start gap-3">
              <XCircle className="h-10 w-10 shrink-0 text-bad" />
              <div>
                <p className="font-semibold">Not paid</p>
                <p className="mt-0.5 text-sm text-muted">{payment.note || 'M-Pesa didn’t confirm the payment.'} Nothing was charged.</p>
              </div>
            </div>
            <button className="btn btn-primary mt-4 w-full" onClick={() => setPayment(null)}>Try again</button>
          </>
        )}
      </section>
    );
  }

  return (
    <div className="mt-6 space-y-5">
      <fieldset>
        <legend className="mb-2 text-sm font-semibold">Plan</legend>
        <div className="space-y-2" role="radiogroup" aria-label="Plan">
          {data.plans.map((p) => {
            const active = p.id === plan.id;
            const tooSmall = data.users > p.users;
            return (
              <button
                key={p.id}
                type="button"
                role="radio"
                aria-checked={active}
                disabled={tooSmall}
                onClick={() => setPlanId(p.id)}
                className={cn('flex w-full items-start gap-3 rounded-lg bg-white p-3.5 text-left ring-1 transition disabled:opacity-50', active ? 'ring-2 ring-brand' : 'ring-line hover:ring-faint')}
              >
                <span className={cn('mt-0.5 grid h-4 w-4 shrink-0 place-items-center rounded-full ring-1', active ? 'bg-brand ring-brand' : 'ring-faint')}>{active && <span className="h-1.5 w-1.5 rounded-full bg-white" />}</span>
                <span className="min-w-0 flex-1">
                  <span className="flex items-baseline justify-between gap-2">
                    <b>{p.name}</b>
                    <span className="num text-sm"><b>{ksh(p.monthly)}</b><span className="text-muted"> /month</span></span>
                  </span>
                  <span className="mt-0.5 block text-xs text-muted">{p.users >= 1000 ? 'Unlimited users' : `Up to ${p.users} users`} · {p.includes.filter((x) => !/users/.test(x)).slice(0, 2).join(' · ')}</span>
                </span>
              </button>
            );
          })}
        </div>
      </fieldset>

      <fieldset>
        <legend className="mb-2 text-sm font-semibold">How long</legend>
        <div className="grid grid-cols-2 gap-2" role="radiogroup" aria-label="How long">
          {LENGTHS.map((m) => (
            <button
              key={m}
              type="button"
              role="radio"
              aria-checked={months === m}
              onClick={() => setMonths(m)}
              className={cn('rounded-lg bg-white px-3 py-2.5 text-left ring-1 transition', months === m ? 'ring-2 ring-brand' : 'ring-line hover:ring-faint')}
            >
              <b className="block text-sm">{lengthName(m)}</b>
              <span className="num text-xs text-muted">{ksh(total(plan, m))}{m === 12 && <span className="text-good"> · 2 months free</span>}</span>
            </button>
          ))}
        </div>
      </fieldset>

      <Field label="M-Pesa number" hint="The payment prompt goes to this phone.">
        <input className="field num" inputMode="tel" autoComplete="tel" value={number} onChange={(e) => setPhone(e.target.value)} placeholder="0712 345 678" />
      </Field>

      <div className="rounded-lg bg-paper p-3.5 text-sm ring-1 ring-line">
        <p className="flex justify-between gap-3"><span className="text-muted">{plan.name}, {lengthName(months)}</span><b className="num">{ksh(amount)}</b></p>
        <p className="mt-1 flex justify-between gap-3 text-xs text-muted"><span>Runs until</span><span>{dayYear(runsTo.toISOString())}</span></p>
        {data.payTo && <p className="mt-1 flex justify-between gap-3 text-xs text-muted"><span>Paid to</span><span>Kabuor · {data.payTo}</span></p>}
      </div>

      {!data.canPay && <ErrorNote>M-Pesa payments aren’t switched on for this server yet. Call or WhatsApp Kabuor and we’ll start your account.</ErrorNote>}
      {data.canPay && data.testMode && <p className="text-xs text-muted">Test mode: this server has no M-Pesa keys, so the payment is recorded as a test and no money moves.</p>}
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <button className="btn btn-mpesa w-full py-3" disabled={state.busy || !data.canPay || number.replace(/\D/g, '').length < 9} onClick={pay}>
        {state.busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Smartphone className="h-4 w-4" />} Pay {ksh(amount)} by M-Pesa
      </button>
    </div>
  );
}
