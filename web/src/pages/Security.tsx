// Your sign-in security: the person is deciding "is my account safe enough, and what do I do if a phone is
// lost?" So: whether two-step sign-in is on (and how many recovery codes are left), turning it on in three short
// steps, changing the password, and ending every session at once.
import { useEffect, useState, type FormEvent } from 'react';
import QRCode from 'qrcode';
import { Check, KeyRound, Loader2, LogOut, Printer, ShieldCheck, ShieldAlert } from 'lucide-react';
import { api, setSession, useApi, useSession, type Session } from '../lib/api';
import { cn } from '../lib/format';
import { ErrorNote, Field, Loading, PageHeader } from '../components/ui';

export default function Security() {
  const session = useSession();
  const status = useApi<{ twoFactorEnabled: boolean; recoveryCodesLeft: number; needsSetupAgain?: boolean }>('/auth/security');
  if (!session) return null;
  return (
    <div className="max-w-2xl">
      <PageHeader title="Sign-in security" subtitle={`${session.user.name} · ${session.user.email || 'signs in with phone'}`} />
      <div className="space-y-5">
        {status.error ? <ErrorNote>{status.error}</ErrorNote> : !status.data ? <Loading /> : <TwoStep status={status.data} session={session} onChange={status.reload} />}
        <ChangePassword />
        <SignOutEverywhere />
      </div>
    </div>
  );
}

function TwoStep({ status, session, onChange }: { status: { twoFactorEnabled: boolean; recoveryCodesLeft: number; needsSetupAgain?: boolean }; session: Session; onChange: () => void }) {
  const [setup, setSetup] = useState<{ secret: string; uri: string; qr: string } | null>(null);
  const [code, setCode] = useState('');
  const [codes, setCodes] = useState<string[] | null>(null);
  const [password, setPassword] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });
  const run = async (fn: () => Promise<void>) => {
    setState({ busy: true, error: '' });
    try {
      await fn();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const start = () =>
    run(async () => {
      const r = await api<{ secret: string; uri: string }>('/auth/security/2fa/start', { body: {} });
      setSetup({ ...r, qr: await QRCode.toDataURL(r.uri, { margin: 1, width: 220 }) });
    });
  const enable = (e: FormEvent) => {
    e.preventDefault();
    run(async () => {
      const r = await api<{ recoveryCodes: string[] }>('/auth/security/2fa/enable', { body: { code } });
      setCodes(r.recoveryCodes);
      setSetup(null);
      setSession({ ...session, user: { ...session.user, twoFactorEnabled: true } });
    });
  };
  const disable = (e: FormEvent) => {
    e.preventDefault();
    run(async () => {
      await api('/auth/security/2fa/disable', { body: { password } });
      setSession(null); // other sessions ended; sign in again with the password only
    });
  };

  if (codes)
    return (
      <section className="card p-5">
        <h2 className="flex items-center gap-2 font-semibold text-good">
          <ShieldCheck className="h-5 w-5" /> Two-step sign-in is on
        </h2>
        <p className="mt-2 text-sm text-muted">Save these recovery codes somewhere safe, away from your phone (printed, or in a password manager). If you lose your phone, each code signs you in once. They won’t be shown again.</p>
        <ul className="mt-4 grid grid-cols-2 gap-2 rounded-md bg-paper p-4 font-mono text-sm">
          {codes.map((c) => (
            <li key={c}>{c}</li>
          ))}
        </ul>
        <div className="mt-4 flex flex-wrap gap-2">
          <button onClick={() => window.print()} className="btn btn-ghost">
            <Printer className="h-4 w-4" /> Print
          </button>
          <button onClick={() => navigator.clipboard?.writeText(codes.join('\n'))} className="btn btn-ghost">
            Copy
          </button>
          <button onClick={() => (setCodes(null), onChange())} className="btn btn-primary">
            <Check className="h-4 w-4" /> I’ve saved them
          </button>
        </div>
      </section>
    );

  return (
    <section className="card p-5">
      <div className="flex items-start gap-3">
        <span className={cn('grid h-10 w-10 shrink-0 place-items-center rounded-full', status.twoFactorEnabled ? 'bg-good-soft text-good' : 'bg-warn-soft text-warn')}>
          {status.twoFactorEnabled ? <ShieldCheck className="h-5 w-5" /> : <ShieldAlert className="h-5 w-5" />}
        </span>
        <div className="min-w-0 flex-1">
          <h2 className="font-semibold">Two-step sign-in {status.twoFactorEnabled ? 'is on' : 'is off'}</h2>
          <p className="text-sm text-muted">
            {status.twoFactorEnabled
              ? `Signing in needs your password and a code from your phone. ${status.recoveryCodesLeft} recovery code${status.recoveryCodesLeft === 1 ? '' : 's'} left.`
              : 'Anyone with your password can open your books. Add a code from your phone so a stolen password isn’t enough.'}
          </p>
        </div>
      </div>
      {status.needsSetupAgain && (
        <p className="mt-3 rounded-md bg-warn-soft px-3 py-2 text-sm text-warn">
          <b>Set it up again.</b> After a server change your authenticator app’s codes no longer match. Turn two-step sign-in off below (your password), then on again and scan the new code. Remove the old Kabuor entry from your app.
        </p>
      )}

      {!status.twoFactorEnabled && !setup && (
        <button onClick={start} disabled={state.busy} className="btn btn-primary mt-4">
          {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Turn on two-step sign-in
        </button>
      )}

      {setup && (
        <form onSubmit={enable} className="mt-5 grid gap-5 sm:grid-cols-[220px_1fr]">
          <img src={setup.qr} alt="QR code to scan with your authenticator app" className="h-[220px] w-[220px] rounded-md ring-1 ring-line" />
          <div className="space-y-3 text-sm">
            <ol className="list-decimal space-y-1.5 pl-5 text-ink-2">
              <li>Install Google Authenticator, Microsoft Authenticator or Authy on your phone.</li>
              <li>In the app, add an account and scan this code.</li>
              <li>Type the 6-digit code the app shows.</li>
            </ol>
            <p className="text-xs text-muted">
              Can’t scan? Enter this key: <span className="font-mono break-all text-ink">{setup.secret.replace(/(.{4})/g, '$1 ').trim()}</span>
            </p>
            <Field label="Code from the app">
              <input className="field num text-center font-display text-xl tracking-[0.3em]" autoFocus inputMode="numeric" autoComplete="one-time-code" maxLength={6} value={code} onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))} placeholder="000000" />
            </Field>
            <div className="flex gap-2">
              <button className="btn btn-primary" disabled={state.busy || code.length !== 6}>
                {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Confirm and turn on
              </button>
              <button type="button" onClick={() => setSetup(null)} className="btn btn-ghost">
                Cancel
              </button>
            </div>
          </div>
        </form>
      )}

      {status.twoFactorEnabled && (
        <form onSubmit={disable} className="mt-4 flex flex-wrap items-end gap-2 border-t border-line pt-4">
          <Field label="To turn it off, enter your password" className="min-w-56 flex-1">
            <input className="field" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </Field>
          <button className="btn btn-ghost" disabled={state.busy || !password}>
            Turn off
          </button>
        </form>
      )}
      {state.error && <div className="mt-3"><ErrorNote>{state.error}</ErrorNote></div>}
    </section>
  );
}

function ChangePassword() {
  const [f, setF] = useState({ current: '', next: '', again: '' });
  const [state, setState] = useState({ busy: false, error: '', done: false });
  useEffect(() => {
    if (!state.done) return;
    const t = setTimeout(() => setState((s) => ({ ...s, done: false })), 4000);
    return () => clearTimeout(t);
  }, [state.done]);
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (f.next !== f.again) return setState({ busy: false, error: 'The two new passwords don’t match.', done: false });
    setState({ busy: true, error: '', done: false });
    try {
      setSession(await api<Session>('/auth/password', { body: { current: f.current, new: f.next } }));
      setF({ current: '', next: '', again: '' });
      setState({ busy: false, error: '', done: true });
    } catch (err) {
      setState({ busy: false, error: (err as Error).message, done: false });
    }
  };
  return (
    <form onSubmit={submit} className="card space-y-4 p-5">
      <h2 className="flex items-center gap-2 font-semibold">
        <KeyRound className="h-5 w-5 text-muted" /> Change password
      </h2>
      <p className="-mt-2 text-sm text-muted">Other phones and computers signed in as you will need the new password.</p>
      <Field label="Current password">
        <input className="field" type="password" autoComplete="current-password" required value={f.current} onChange={(e) => setF({ ...f, current: e.target.value })} />
      </Field>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="New password" hint="At least 10 characters.">
          <input className="field" type="password" autoComplete="new-password" required minLength={10} value={f.next} onChange={(e) => setF({ ...f, next: e.target.value })} />
        </Field>
        <Field label="New password again">
          <input className="field" type="password" autoComplete="new-password" required minLength={10} value={f.again} onChange={(e) => setF({ ...f, again: e.target.value })} />
        </Field>
      </div>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      {state.done && <p className="text-sm font-semibold text-good">Password changed. Other devices have been signed out.</p>}
      <button className="btn btn-primary" disabled={state.busy}>
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Change password
      </button>
    </form>
  );
}

function SignOutEverywhere() {
  const [sure, setSure] = useState(false);
  const [busy, setBusy] = useState(false);
  const go = async () => {
    setBusy(true);
    try {
      await api('/auth/security/sign-out-everywhere', { body: {} });
    } finally {
      setSession(null);
    }
  };
  return (
    <section className="card p-5">
      <h2 className="flex items-center gap-2 font-semibold">
        <LogOut className="h-5 w-5 text-muted" /> Sign out everywhere
      </h2>
      <p className="mt-1 text-sm text-muted">Lost a phone, or signed in on a shared computer? This ends every session for your account, including this one.</p>
      {sure ? (
        <div className="mt-4 flex gap-2">
          <button onClick={go} disabled={busy} className="btn bg-bad text-white hover:brightness-110">
            {busy && <Loader2 className="h-4 w-4 animate-spin" />} Yes, sign out everywhere
          </button>
          <button onClick={() => setSure(false)} className="btn btn-ghost">
            Cancel
          </button>
        </div>
      ) : (
        <button onClick={() => setSure(true)} className="btn btn-ghost mt-4">
          Sign out everywhere
        </button>
      )}
    </section>
  );
}
