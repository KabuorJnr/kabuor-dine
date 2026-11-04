import { useState, type FormEvent, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
import { ArrowLeft, BadgeCheck, ChefHat, QrCode, Receipt, Eye, EyeOff, KeyRound, Loader2, Lock, Mail, MessageCircle, ShieldCheck, Smartphone, Wallet } from 'lucide-react';
import { api, setSession, useApi, MOCK_ACCOUNTS, type Session } from '../lib/api';
import { cn, ksh } from '../lib/format';
import { ErrorNote } from '../components/ui';
import { DineLockup } from '../dine/Logo';

const SUPPORT_PHONE = '0705 607 954';
const SUPPORT_WHATSAPP = 'https://wa.me/254705607954';

/** The right-hand panel on a computer: what Kabuor ERP does, and a small picture of it (sample figures). */
function Showcase() {
  return (
    <aside className="relative hidden flex-col justify-center gap-8 overflow-hidden rounded-2xl bg-brand px-9 py-10 text-white lg:flex">
      <div className="pointer-events-none absolute -top-24 -right-24 h-72 w-72 rounded-full bg-white/10" />
      <div className="pointer-events-none absolute -bottom-32 -left-20 h-80 w-80 rounded-full bg-white/[0.06]" />
      <div className="relative">
        <h2 className="font-display text-[28px] leading-tight font-semibold tracking-tight">Run the whole business from one place</h2>
        <p className="mt-3 max-w-sm text-[15px] leading-relaxed text-white/80">Sales, M-Pesa, stock, payroll and the books, with VAT worked out as you go.</p>
      </div>
      <ul className="relative space-y-3 text-[15px]">
        {[
          [BadgeCheck, 'Invoices signed for KRA eTIMS as you send them'],
          [Wallet, 'M-Pesa payments matched to their invoices'],
          [ShieldCheck, 'Each person sees only what their role needs'],
        ].map(([Icon, text]) => {
          const I = Icon as typeof BadgeCheck;
          return (
            <li key={text as string} className="flex items-center gap-3">
              <span className="grid h-8 w-8 shrink-0 place-items-center rounded-full bg-white/15">
                <I className="h-4 w-4" />
              </span>
              {text as string}
            </li>
          );
        })}
      </ul>
      <div className="relative rounded-xl bg-white p-4 text-ink shadow-[0_24px_48px_rgb(0_0_40/0.25)]" aria-hidden>
        <div className="flex items-center justify-between">
          <p className="text-xs font-semibold">Today</p>
          <span className="rounded-full bg-good-soft px-2 py-0.5 text-[10px] font-semibold text-good">Sample figures</span>
        </div>
        <div className="mt-3 grid grid-cols-3 gap-2">
          {[
            ['Money in, 7 days', 'KSh 1.84M'],
            ['Owed to you', 'KSh 732K'],
            ['Signed for KRA', '348'],
          ].map(([k, v]) => (
            <div key={k} className="rounded-lg bg-paper px-2.5 py-2">
              <p className="truncate text-[10px] text-muted">{k}</p>
              <p className="num mt-0.5 text-sm font-semibold">{v}</p>
            </div>
          ))}
        </div>
        <svg viewBox="0 0 300 56" className="mt-3 h-14 w-full" preserveAspectRatio="none">
          <defs>
            <linearGradient id="authTrend" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor="#2357f5" stopOpacity="0.22" />
              <stop offset="100%" stopColor="#2357f5" stopOpacity="0" />
            </linearGradient>
          </defs>
          <path d="M0,42 Q35,14 70,26 T140,12 T210,28 T300,8 L300,56 L0,56 Z" fill="url(#authTrend)" />
          <path d="M0,42 Q35,14 70,26 T140,12 T210,28 T300,8" fill="none" stroke="#2357f5" strokeWidth="2.2" strokeLinecap="round" />
        </svg>
      </div>
    </aside>
  );
}

/** The signed-out pages' layout: the form on the left, and on a computer the showcase on the right. */
export function Frame({ children, hideShowcase = false, dine = true }: { children: ReactNode; hideShowcase?: boolean; dine?: boolean }) {
  return (
    <div className="flex min-h-screen items-center justify-center bg-paper px-4 py-6 sm:py-10">
      <div className={cn('w-full rounded-3xl bg-white p-2 shadow-[0_8px_40px_rgb(10_11_30/0.08)] ring-1 ring-line', hideShowcase ? 'max-w-[480px]' : 'grid max-w-[1040px] lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]')}>
        <div className="flex min-w-0 flex-col px-5 pt-6 pb-4 sm:px-10 sm:pt-9">
          {dine
            ? <Link to="/" className="self-start" aria-label="Kabuor Dine home"><DineLockup className="h-11" /></Link>
            : <img src="/brand/kabuor-erp-logo-blue.svg" alt="Kabuor ERP" className="h-6 w-auto self-start" />}
          <div className="flex flex-1 flex-col justify-center py-8">{children}</div>
          <footer className="flex items-center justify-between border-t border-line pt-4 text-xs text-faint">
            <span>© {new Date().getFullYear()} Kabuor Technologies</span>
            <span className="flex gap-4">
              <Link to="/terms" className="hover:text-ink">Terms</Link>
              <Link to="/privacy" className="hover:text-ink">Privacy</Link>
            </span>
          </footer>
        </div>
        {!hideShowcase && (dine ? <DineShowcase /> : <Showcase />)}
      </div>
    </div>
  );
}

/** Kabuor Dine's side panel on sign-up: what a restaurant gets, in three lines. */
function DineShowcase() {
  return (
    <div className="hidden flex-col justify-between rounded-[20px] bg-gradient-to-br from-dine-soft to-cream p-9 text-espresso lg:flex">
      <div>
        <DineLockup className="h-20" />
        <h2 className="font-brand mt-6 text-3xl leading-tight font-extrabold tracking-[-0.03em]">Guests order from the table. The kitchen sees it at once. The bill pays itself.</h2>
      </div>
      <ul className="space-y-5">
        {([[QrCode, 'A QR menu on every table', 'Guests order on their own phone, with their choices: spice, sides, no onions.'],
          [ChefHat, 'Kitchen and bar screens', 'Each station sees its own tickets, oldest first, with a timer.'],
          [Receipt, 'M-Pesa at the table, eTIMS receipts', 'Every bill lands in your books, stock goes down, KRA signs it.']] as const).map(([Icon, t, d]) => (
          <li key={t} className="flex gap-3">
            <span className="grid h-10 w-10 shrink-0 place-items-center rounded-xl bg-white shadow-sm ring-1 ring-dine/10"><Icon className="h-5 w-5 text-dine" /></span>
            <span><span className="block font-semibold">{t}</span><span className="text-sm text-muted">{d}</span></span>
          </li>
        ))}
      </ul>
      <p className="text-xs text-muted">By Kabuor Technologies · on the same books as Kabuor ERP</p>
    </div>
  );
}

function Heading({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="mb-6">
      <h1 className="font-display text-[26px] leading-tight font-semibold tracking-tight sm:text-[28px]">{title}</h1>
      {children && <p className="mt-1.5 text-[15px] text-muted">{children}</p>}
    </div>
  );
}

/** A labelled input, with an optional icon inside it on the left. */
function Input({ label, hint, icon: Icon, end, className, ...props }: { label: ReactNode; hint?: ReactNode; icon?: typeof Mail; end?: ReactNode } & React.InputHTMLAttributes<HTMLInputElement>) {
  return (
    <label className={cn('block min-w-0', className)}>
      <span className="mb-1.5 block text-sm font-semibold">{label}</span>
      <span className="relative block">
        {Icon && <Icon className="pointer-events-none absolute top-1/2 left-3 h-4 w-4 -translate-y-1/2 text-faint" />}
        <input className={cn('field', Icon && 'pl-9', end ? 'pr-10' : '')} {...props} />
        {end}
      </span>
      {hint && <span className="mt-1 block text-xs text-muted">{hint}</span>}
    </label>
  );
}

function Submit({ busy, children, disabled }: { busy: boolean; children: ReactNode; disabled?: boolean }) {
  return (
    <button type="submit" disabled={busy || disabled} className="btn btn-primary w-full py-3 text-[15px]">
      {busy && <Loader2 className="h-4 w-4 animate-spin" />}
      {children}
    </button>
  );
}

const DEVICES = 'kerp-devices';
function devices(): Record<string, string> {
  try { return JSON.parse(localStorage.getItem(DEVICES) ?? '{}'); } catch { return {}; }
}
function rememberDevice(login: string, token: string) {
  try { localStorage.setItem(DEVICES, JSON.stringify({ ...devices(), [login.trim().toLowerCase()]: token })); } catch {}
}

export function SignIn() {
  const [f, setF] = useState({ email: '', password: '' });
  const [remember, setRemember] = useState(true);
  const [showPassword, setShowPassword] = useState(false);
  const [state, setState] = useState({ busy: false, error: '' });
  const [challenge, setChallenge] = useState<string | null>(null);
  const [code, setCode] = useState('');
  const [recovery, setRecovery] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      const r = await api<Session | { needsCode: true; challenge: string }>('/auth/login', { body: { ...f, device: devices()[f.email.trim().toLowerCase()] } });
      if ('needsCode' in r) {
        setChallenge(r.challenge);
        setState({ busy: false, error: '' });
      } else {
        setSession(r);
        if (!r.user.isPlatformAdmin && window.location.pathname.startsWith('/platform')) {
          window.location.href = '/';
        }
      }
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };

  const submitCode = async (e: FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      const r = await api<Session & { device?: string }>('/auth/login/code', { body: { challenge, code, remember } });
      if (r.device) rememberDevice(f.email, r.device);
      setSession(r);
      if (!r.user.isPlatformAdmin && window.location.pathname.startsWith('/platform')) {
        window.location.href = '/';
      }
    } catch (err) {
      const message = (err as Error).message;
      if (/password again|too long/i.test(message)) setChallenge(null);
      setState({ busy: false, error: message });
    }
  };

  // Two-step sign-in: the 6 digits from the authenticator app, or one of the recovery codes (e.g. ab12-cd34).
  if (challenge) {
    return (
      <Frame hideShowcase>
        <span className="mb-5 grid h-11 w-11 place-items-center rounded-full bg-brand-soft text-brand">
          {recovery ? <KeyRound className="h-5 w-5" /> : <Smartphone className="h-5 w-5" />}
        </span>
        <Heading title={recovery ? 'Use a recovery code' : 'Enter your sign-in code'}>
          {recovery ? 'One of the codes you saved when you turned on two-step sign-in. Each works once.' : 'Open your authenticator app and type the 6-digit code it shows for Kabuor ERP.'}
        </Heading>
        <form onSubmit={submitCode} className="space-y-4">
          {recovery ? (
            <Input label="Recovery code" autoFocus required autoComplete="off" spellCheck={false} maxLength={12} value={code} onChange={(e) => setCode(e.target.value)} placeholder="e.g. ab12-cd34" />
          ) : (
            <Input
              label="Code"
              autoFocus
              required
              inputMode="numeric"
              autoComplete="one-time-code"
              maxLength={6}
              value={code}
              onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))}
              placeholder="123456"
              className="[&_input]:num [&_input]:text-lg [&_input]:tracking-[0.4em]"
            />
          )}
          <label className="flex cursor-pointer items-center gap-2.5 text-sm select-none">
            <input type="checkbox" className="h-4 w-4 rounded accent-brand" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
            Don’t ask for a code on this device for 30 days
          </label>
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
          <Submit busy={state.busy} disabled={recovery ? code.replace(/[^a-z0-9]/gi, '').length < 8 : code.length !== 6}>
            Verify and sign in
          </Submit>
        </form>
        <div className="mt-6 flex items-center justify-between text-sm">
          <button type="button" onClick={() => (setRecovery(!recovery), setCode(''), setState({ busy: false, error: '' }))} className="font-semibold text-brand hover:underline">
            {recovery ? 'Use the app’s code instead' : 'Lost your phone? Use a recovery code'}
          </button>
          <button type="button" onClick={() => (setChallenge(null), setCode(''), setRecovery(false))} className="text-muted hover:text-ink">
            Back
          </button>
        </div>
      </Frame>
    );
  }

  // There's no reset-by-email: a forgotten password is reset by the business's owner or an admin (Settings → Team),
  // who gets a one-time password to hand over. Owners who are locked out reach Kabuor support.
  if (helpOpen) {
    return (
      <Frame hideShowcase>
        <button type="button" onClick={() => setHelpOpen(false)} className="mb-6 inline-flex items-center gap-1.5 self-start text-sm font-semibold text-muted hover:text-ink">
          <ArrowLeft className="h-4 w-4" /> Back to sign in
        </button>
        <Heading title="Forgot your password?">Your business resets it for you, so nobody outside it can.</Heading>
        <ol className="space-y-4 text-[15px]">
          <li className="flex gap-3">
            <span className="grid h-7 w-7 shrink-0 place-items-center rounded-full bg-brand-soft text-sm font-semibold text-brand">1</span>
            <span>Ask the owner or an admin at your business to open <b>Settings → Team</b>, choose your name and reset your password.</span>
          </li>
          <li className="flex gap-3">
            <span className="grid h-7 w-7 shrink-0 place-items-center rounded-full bg-brand-soft text-sm font-semibold text-brand">2</span>
            <span>They give you a one-time password. Sign in with it and choose a new one of your own.</span>
          </li>
        </ol>
        <div className="mt-7 rounded-xl bg-paper p-4 text-sm">
          <p className="font-semibold">Are you the owner?</p>
          <p className="mt-1 text-muted">Message Kabuor support on WhatsApp ({SUPPORT_PHONE}). We check it’s you, then help you back in.</p>
          <a href={SUPPORT_WHATSAPP} target="_blank" rel="noreferrer" className="btn btn-ghost mt-3">
            <MessageCircle className="h-4 w-4" /> WhatsApp support
          </a>
        </div>
      </Frame>
    );
  }

  return (
    <Frame>
      <Heading title="Sign in">Welcome back to Kabuor ERP.</Heading>

      {MOCK_ACCOUNTS.length > 0 && (
        // Development builds only (MOCK_ACCOUNTS is empty in production).
        <div className="mb-6 rounded-xl border border-dashed border-warn/50 bg-warn-soft/40 p-3">
          <p className="mb-2 text-xs font-semibold text-warn">Test accounts (development only)</p>
          <div className="grid grid-cols-2 gap-1.5">
            {MOCK_ACCOUNTS.map((acc) => (
              <button
                key={acc.id}
                type="button"
                disabled={state.busy}
                onClick={async () => {
                  setState({ busy: true, error: '' });
                  try {
                    const r = await api<Session>('/auth/login', { body: { email: acc.email, password: acc.password } });
                    setSession(r);
                    window.location.href = '/';
                  } catch {
                    setSession(acc.session);
                    window.location.href = '/';
                  }
                }}
                className="min-w-0 rounded-lg bg-white px-2.5 py-1.5 text-left ring-1 ring-line transition hover:ring-brand disabled:opacity-50"
              >
                <span className="block truncate text-xs font-semibold">{acc.name}</span>
                <span className="block truncate text-[11px] text-muted">{acc.roleTitle}</span>
              </button>
            ))}
          </div>
        </div>
      )}

      <form onSubmit={submit} className="space-y-4">
        <Input
          label="Email or phone"
          icon={Mail}
          type="text"
          inputMode="email"
          autoComplete="username"
          autoFocus
          required
          value={f.email}
          onChange={(e) => setF({ ...f, email: e.target.value })}
          placeholder="you@yourbusiness.co.ke or 0712 345 678"
        />
        <div>
          <Input
            label="Password"
            icon={Lock}
            type={showPassword ? 'text' : 'password'}
            autoComplete="current-password"
            required
            value={f.password}
            onChange={(e) => setF({ ...f, password: e.target.value })}
            end={
              <button type="button" onClick={() => setShowPassword(!showPassword)} aria-label={showPassword ? 'Hide password' : 'Show password'} className="absolute top-1/2 right-2 grid h-8 w-8 -translate-y-1/2 place-items-center rounded-md text-faint hover:text-ink">
                {showPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
              </button>
            }
          />
          <button type="button" onClick={() => setHelpOpen(true)} className="mt-2 text-sm font-semibold text-brand hover:underline">
            Forgot password?
          </button>
        </div>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <Submit busy={state.busy}>Sign in</Submit>
      </form>

      <p className="mt-6 text-sm text-muted">
        New to Kabuor ERP?{' '}
        <Link to="/signup" className="font-semibold text-brand hover:underline">
          Open a business account
        </Link>
      </p>
    </Frame>
  );
}

const INDUSTRIES = [
  { id: 'distribution', label: 'Wholesale & distribution', desc: 'Price lists, trade terms, dispatch' },
  { id: 'manufacturing', label: 'Manufacturing', desc: 'Product blueprints and batch production' },
  { id: 'transportation', label: 'Transport & logistics', desc: 'Vehicles, trips and waybills' },
  { id: 'retail', label: 'Retail & supermarket', desc: 'Till, barcodes and cashiers' },
  { id: 'services', label: 'Services & consulting', desc: 'Projects and milestone invoices' },
  { id: 'general', label: 'General trading', desc: 'Invoices, stock, eTIMS, payroll' },
];

type ClientConfig = { payToStart: boolean; plans: { id: string; name: string; monthly: number }[] };

export function SignUp() {
  const { data: cfg } = useApi<ClientConfig>('/client-config');
  // ?product=dine: Kabuor Dine's sign-up (a restaurant), from the Dine page on the company site.
  const [params] = useSearchParams();
  // Kabuor Dine signs restaurants up; ?product=erp opens the full ERP instead.
  const dine = params.get('product') !== 'erp';
  const erpPlans = cfg?.plans.filter((p) => p.id !== 'dine') ?? [];
  const from = dine ? cfg?.plans.find((p) => p.id === 'dine')?.monthly ?? null : erpPlans.length ? Math.min(...erpPlans.map((p) => p.monthly)) : null;
  const [f, setF] = useState({ companyName: '', ownerName: '', email: '', phone: '', password: '', kraPin: '', vatRegistered: true, industry: 'general' });
  const [showPassword, setShowPassword] = useState(false);
  const [state, setState] = useState({ busy: false, error: '' });
  const set = (k: keyof typeof f) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.type === 'checkbox' ? e.target.checked : e.target.value });

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      const sess = await api<Session>('/auth/register', { body: { ...f, kraPin: f.kraPin.trim().toUpperCase(), product: dine ? 'dine' : undefined } });
      setSession(sess);
      window.location.href = '/';
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };

  return (
    <Frame dine={dine}>
      <Heading title={dine ? 'Open your Kabuor Dine account' : 'Open your business account'}>
        {cfg?.payToStart ? <>Next you choose a plan{from ? <> (from {ksh(from)} a month)</> : ''} and pay by M-Pesa.</> : 'Free for your first 30 days.'}
      </Heading>

      <form onSubmit={submit} className="space-y-5">
        <Input label={dine ? 'Restaurant name' : 'Business name'} required autoComplete="organization" value={f.companyName} onChange={set('companyName')} placeholder={dine ? 'e.g. Ziwa Grill & Café' : 'e.g. Mama Pili Enterprises Ltd'} />

        {!dine && <fieldset>
          <legend className="mb-1.5 text-sm font-semibold">What does the business do?</legend>
          <div className="grid grid-cols-2 gap-2" role="radiogroup">
            {INDUSTRIES.map((ind) => {
              const on = f.industry === ind.id;
              return (
                <button
                  key={ind.id}
                  type="button"
                  role="radio"
                  aria-checked={on}
                  onClick={() => setF({ ...f, industry: ind.id })}
                  className={cn('min-w-0 rounded-lg px-3 py-2.5 text-left ring-1 transition', on ? 'bg-brand-soft ring-2 ring-brand' : 'bg-white ring-line hover:ring-ink-2/30')}
                >
                  <span className={cn('block text-sm leading-snug font-semibold', on && 'text-brand')}>{ind.label}</span>
                  <span className="mt-0.5 block text-xs leading-snug text-muted">{ind.desc}</span>
                </button>
              );
            })}
          </div>
          <p className="mt-1.5 text-xs text-muted">This only shapes your first steps. You can change it later in Settings.</p>
        </fieldset>}

        <div className="grid gap-4 sm:grid-cols-2">
          <Input label="Your name" required autoComplete="name" value={f.ownerName} onChange={set('ownerName')} placeholder="First and last name" />
          <Input label="Phone" required type="tel" inputMode="tel" autoComplete="tel" value={f.phone} onChange={set('phone')} placeholder="0712 345 678" />
        </div>
        <Input label="Email" type="email" required autoComplete="email" value={f.email} onChange={set('email')} placeholder="you@yourbusiness.co.ke" />
        <Input
          label="Password"
          type={showPassword ? 'text' : 'password'}
          required
          minLength={10}
          autoComplete="new-password"
          value={f.password}
          onChange={set('password')}
          hint="At least 10 characters."
          end={
            <button type="button" onClick={() => setShowPassword(!showPassword)} aria-label={showPassword ? 'Hide password' : 'Show password'} className="absolute top-1/2 right-2 grid h-8 w-8 -translate-y-1/2 place-items-center rounded-md text-faint hover:text-ink">
              {showPassword ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
            </button>
          }
        />

        <div className="rounded-xl bg-paper p-4">
          <label className="flex cursor-pointer items-start gap-2.5 select-none">
            <input type="checkbox" className="mt-0.5 h-4 w-4 rounded accent-brand" checked={f.vatRegistered} onChange={set('vatRegistered')} />
            <span>
              <span className="block text-sm font-semibold">The business is registered for VAT</span>
              <span className="block text-xs text-muted">Invoices then charge VAT at 16%.</span>
            </span>
          </label>
          <Input
            label={<>KRA PIN <span className="font-normal text-muted">(optional, you can add it later)</span></>}
            className="mt-4"
            autoComplete="off"
            spellCheck={false}
            maxLength={11}
            value={f.kraPin}
            onChange={(e) => setF({ ...f, kraPin: e.target.value.toUpperCase() })}
            placeholder="P051234567X"
          />
        </div>

        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <Submit busy={state.busy}>{cfg?.payToStart ? 'Continue to payment' : 'Create account'}</Submit>
        <p className="text-xs text-muted">
          By continuing you agree to the{' '}
          <Link to="/terms" className="underline hover:text-ink">terms</Link> and the{' '}
          <Link to="/privacy" className="underline hover:text-ink">privacy notice</Link>.
        </p>
      </form>

      <p className="mt-6 text-sm text-muted">
        Already have an account?{' '}
        <Link to="/signin" className="font-semibold text-brand hover:underline">
          Sign in
        </Link>
      </p>
    </Frame>
  );
}

export function ChooseOwnPassword({ session }: { session: Session }) {
  const [f, setF] = useState({ current: '', next: '', again: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (f.next !== f.again) return setState({ busy: false, error: 'The two new passwords don’t match.' });
    setState({ busy: true, error: '' });
    try {
      setSession(await api<Session>('/auth/password', { body: { current: f.current, new: f.next } }));
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  return (
    <Frame hideShowcase>
      <Heading title="Choose your own password">Hi {session.user.name.split(' ')[0]}. Replace the one-time password with one only you know.</Heading>
      <form onSubmit={submit} className="space-y-4">
        <Input label="One-time password" type="password" autoComplete="current-password" required value={f.current} onChange={(e) => setF({ ...f, current: e.target.value })} />
        <Input label="New password" type="password" autoComplete="new-password" required minLength={10} value={f.next} onChange={(e) => setF({ ...f, next: e.target.value })} hint="At least 10 characters." />
        <Input label="New password again" type="password" autoComplete="new-password" required minLength={10} value={f.again} onChange={(e) => setF({ ...f, again: e.target.value })} />
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <Submit busy={state.busy}>Save and continue</Submit>
      </form>
      <button onClick={() => setSession(null)} className="mt-6 text-sm text-muted hover:text-ink">
        Sign out
      </button>
    </Frame>
  );
}
