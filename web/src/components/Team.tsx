// The team: who can sign in and what each person can do. The owner is deciding "does everyone have the right access,
// and is anyone's login a risk?", so each person shows their role, whether two-step sign-in is on, and when they last
// signed in; and every change says what will happen before it's made (a new role ends their sessions; a reset gives
// a one-time password they must replace; switching off keeps everything they did).
import { useState } from 'react';
import { Check, Copy, KeyRound, Loader2, MessageCircle, Plus, ShieldCheck } from 'lucide-react';
import { api, useApi, useSession } from '../lib/api';
import { cn, msisdn } from '../lib/format';
import { Chips, ErrorNote, Field, Loading, Sheet, Status, SheetFooter, PageError } from './ui';

type Person = {
  id: string; name: string; email: string; phone: string; role: string; active: boolean; twoFactorEnabled: boolean;
  mustChangePassword: boolean; lastSignInAt: string | null; lockedUntil: string | null; createdAt: string;
};
type Billing = { planName: string; users: number; userLimit: number };

export const ROLES: [string, string][] = [
  ['Owner', 'Everything, including people, settings and billing'],
  ['Accountant', 'Finance, payments, matching, ledger and reports'],
  ['Procurement', 'Suppliers, purchase orders, vendor bills and receiving'],
  ['Sales', 'Point of sale (POS), customers, quotes, invoices and recording payments'],
  ['Storekeeper', 'Items, stock, transfers, counts, fleet and dispatch'],
  ['Dispatcher', 'The fleet and dispatch office: trips, waybills, drivers and the live map (nothing else)'],
  ['Maintenance', 'The maintenance team: assets, faults, work orders, preventive plans and the parts they use'],
  ['HR', 'Employees, hiring, leave and payroll (approval stays with the owner or accountant)'],
  ['Viewer', 'Can look, can’t change anything (never sees salaries)'],
];

export function ago(iso: string | null) {
  if (!iso) return 'Never signed in';
  const mins = Math.floor((Date.now() - Date.parse(iso)) / 60000);
  if (mins < 2) return 'Signed in just now';
  if (mins < 60) return `Signed in ${mins} min ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `Signed in ${hours} h ago`;
  const days = Math.floor(hours / 24);
  return days === 1 ? 'Signed in yesterday' : days < 60 ? `Signed in ${days} days ago` : `Last signed in ${new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })}`;
}

const initials = (name: string) => name.split(' ').map((w) => w[0]).slice(0, 2).join('').toUpperCase();

export default function Team() {
  const session = useSession();
  const { data, error, reload } = useApi<Person[]>('/settings/users');
  const billing = useApi<Billing>('/billing');
  const [show, setShow] = useState<'active' | 'off' | 'portal'>('active');
  const [adding, setAdding] = useState(false);
  const [open, setOpen] = useState<Person | null>(null);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const staff = data.filter((u) => u.role !== 'Employee');
  const groups = { active: staff.filter((u) => u.active), off: staff.filter((u) => !u.active), portal: data.filter((u) => u.role === 'Employee') };
  const shown = groups[show];
  const b = billing.data;
  const full = b ? b.users >= b.userLimit : false;
  const refresh = () => {
    reload();
    billing.reload();
  };

  return (
    <>
      <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
        <Chips label="Show" value={show} onChange={setShow} options={[['active', 'Active', groups.active.length], ['off', 'Switched off', groups.off.length], ['portal', 'Staff portal', groups.portal.length]]} />
        <div className="flex items-center gap-3">
          {b && <span className={cn('text-sm', full ? 'text-warn' : 'text-muted')}>{b.userLimit >= 1000 ? `${b.users} users` : `${b.users} of ${b.userLimit} users on ${b.planName}`}</span>}
          <button className="btn btn-primary" onClick={() => setAdding(true)} disabled={full} title={full ? 'Your plan is full: choose a bigger plan or switch someone off' : undefined}>
            <Plus className="h-4 w-4" /> Add a person
          </button>
        </div>
      </div>
      {show === 'portal' && <p className="mb-3 text-sm text-muted">Employees who sign in to the staff portal (leave, payslips). Their access is given and removed from their record in People.</p>}
      {shown.length === 0 ? (
        <p className="card p-5 text-sm text-muted">{show === 'off' ? 'Nobody’s login is switched off.' : show === 'portal' ? 'No employees have staff portal access yet.' : 'Only you so far.'}</p>
      ) : (
        <ul className="card divide-y divide-line">
          {shown.map((u) => {
            const me = u.id === session?.user.id;
            const locked = u.lockedUntil && Date.parse(u.lockedUntil) > Date.now();
            return (
              <li key={u.id}>
                <button className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-paper disabled:hover:bg-transparent" onClick={() => setOpen(u)} disabled={u.role === 'Employee'}>
                  <span className={cn('grid h-9 w-9 shrink-0 place-items-center rounded-full text-xs font-bold', u.active ? 'bg-ink text-white' : 'bg-paper text-muted ring-1 ring-line')}>{initials(u.name)}</span>
                  <span className="min-w-0 flex-1">
                    <b className="block truncate">{u.name}{me && <span className="font-normal text-muted"> (you)</span>}</b>
                    <span className="block truncate text-sm text-muted">{u.email || u.phone} · {ago(u.lastSignInAt)}</span>
                  </span>
                  <span className="hidden shrink-0 flex-wrap justify-end gap-1.5 sm:flex">
                    {locked && <Status tone="bad">Locked out</Status>}
                    {u.mustChangePassword && <Status tone="warn">Password not set yet</Status>}
                    {u.role !== 'Employee' && (u.twoFactorEnabled ? <Status tone="good"><ShieldCheck className="h-3 w-3" /> 2-step on</Status> : <Status tone="plain">2-step off</Status>)}
                  </span>
                  <Status tone={u.role === 'Owner' ? 'info' : 'plain'}>{u.role === 'Employee' ? 'Staff portal' : u.role}</Status>
                </button>
              </li>
            );
          })}
        </ul>
      )}
      <Sheet open={adding} onClose={() => setAdding(false)} title="Add a person">
        {adding && <AddPerson onDone={() => { setAdding(false); refresh(); }} />}
      </Sheet>
      <Sheet open={Boolean(open)} onClose={() => setOpen(null)} title={open?.name ?? ''}>
        {open && <PersonSheet person={open} me={open.id === session?.user.id} onChange={(p) => { setOpen(p); refresh(); }} />}
      </Sheet>
    </>
  );
}

function RolePicker({ value, onChange, includeOwner }: { value: string; onChange: (r: string) => void; includeOwner?: boolean }) {
  return (
    <div className="space-y-1.5" role="radiogroup" aria-label="Role">
      {ROLES.filter(([r]) => includeOwner || r !== 'Owner').map(([r, desc]) => (
        <label key={r} className="flex cursor-pointer items-start gap-2.5 rounded-md bg-white p-3 ring-1 ring-line has-[:checked]:ring-2 has-[:checked]:ring-brand">
          <input type="radio" name="role" className="mt-1 accent-[var(--color-brand)]" checked={value === r} onChange={() => onChange(r)} />
          <span>
            <b className="block text-sm">{r}</b>
            <span className="text-xs text-muted">{desc}</span>
          </span>
        </label>
      ))}
    </div>
  );
}

function AddPerson({ onDone }: { onDone: () => void }) {
  const [f, setF] = useState({ name: '', email: '', phone: '', role: 'Sales', password: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      await api('/settings/users', { body: f });
      onDone();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  return (
    <form onSubmit={submit} className="space-y-4">
      <Field label="Name">
        <input className="field" required value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} />
      </Field>
      <Field label="Email (they sign in with it)">
        <input className="field" type="email" required value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} />
      </Field>
      <Field label="Phone">
        <input className="field" inputMode="tel" value={f.phone} onChange={(e) => setF({ ...f, phone: e.target.value })} />
      </Field>
      <fieldset>
        <legend className="mb-1.5 text-sm font-semibold">What they can do</legend>
        <RolePicker value={f.role} onChange={(role) => setF({ ...f, role })} />
      </fieldset>
      <Field label="Starting password" hint="At least 10 characters. Share it with them privately.">
        <input className="field" type="text" required minLength={10} value={f.password} onChange={(e) => setF({ ...f, password: e.target.value })} autoComplete="new-password" />
      </Field>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button className="btn btn-primary" disabled={state.busy}>
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Add {f.name.split(' ')[0] || 'person'} as {f.role === 'HR' ? 'HR' : f.role.toLowerCase()}
      </button>
      </SheetFooter>
    </form>
  );
}

function PersonSheet({ person, me, onChange }: { person: Person; me: boolean; onChange: (p: Person) => void }) {
  const [role, setRole] = useState(person.role);
  const [state, setState] = useState({ busy: '', error: '' });
  const [grant, setGrant] = useState<{ password: string } | null>(null);
  const [copied, setCopied] = useState(false);
  const first = person.name.split(' ')[0];
  const run = async (what: string, fn: () => Promise<void>) => {
    setState({ busy: what, error: '' });
    try {
      await fn();
      setState({ busy: '', error: '' });
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };
  const share = grant && `Hi ${first}, your Kabuor ERP password was reset. Sign in at ${location.origin} with ${person.email} and this one-time password: ${grant.password} . You'll choose your own straight away.`;
  const wa = msisdn(person.phone);

  return (
    <div className="space-y-6">
      <div className="text-sm text-muted">
        <p>{person.email}{person.phone && ` · ${person.phone}`}</p>
        <p>{ago(person.lastSignInAt)} · two-step sign-in {person.twoFactorEnabled ? 'on' : 'off'}</p>
      </div>

      <section className="space-y-3">
        <h3 className="text-sm font-semibold">Role</h3>
        <RolePicker value={role} onChange={setRole} includeOwner />
        {role !== person.role && (
          <>
            <p className="text-sm text-muted">{me ? 'You’ll' : `${first} will`} be signed out and sign in again with the {role} menu.</p>
            <button className="btn btn-primary w-full" disabled={Boolean(state.busy)} onClick={() => run('role', async () => { await api(`/settings/users/${person.id}/role`, { method: 'PUT', body: { role } }); onChange({ ...person, role }); })}>
              {state.busy === 'role' && <Loader2 className="h-4 w-4 animate-spin" />} Make {me ? 'me' : first} {role === 'HR' ? 'HR' : /^[AEIOU]/.test(role) ? `an ${role}` : `a ${role}`}
            </button>
          </>
        )}
      </section>

      {!me && person.active && (
        <section className="space-y-3 border-t border-line pt-5">
          <h3 className="text-sm font-semibold">Forgotten password</h3>
          {grant ? (
            <div className="space-y-3 rounded-md bg-good-soft p-3">
              <p className="text-sm text-good">Give {first} this one-time password. It works once; they choose their own straight after. It won’t be shown again.</p>
              <p className="rounded bg-white px-3 py-2 text-center font-mono text-lg tracking-wider ring-1 ring-line">{grant.password}</p>
              <div className="flex gap-2">
                <button className="btn btn-ghost flex-1" onClick={async () => { try { await navigator.clipboard.writeText(grant.password); setCopied(true); setTimeout(() => setCopied(false), 1500); } catch {} }}>
                  {copied ? <Check className="h-4 w-4 text-good" /> : <Copy className="h-4 w-4" />} {copied ? 'Copied' : 'Copy'}
                </button>
                {wa && share && <a className="btn btn-mpesa flex-1" href={`https://wa.me/${wa}?text=${encodeURIComponent(share)}`} target="_blank" rel="noreferrer"><MessageCircle className="h-4 w-4" /> Send on WhatsApp</a>}
              </div>
            </div>
          ) : (
            <>
              <p className="text-sm text-muted">Makes a one-time password for {first} and signs them out everywhere. {person.lockedUntil && Date.parse(person.lockedUntil) > Date.now() ? 'It also lifts the lockout from too many wrong tries.' : ''}</p>
              <button className="btn btn-ghost w-full" disabled={Boolean(state.busy)} onClick={() => run('reset', async () => { const r = await api<{ password: string }>(`/settings/users/${person.id}/reset-password`, { body: {} }); setGrant(r); onChange({ ...person, mustChangePassword: true, lockedUntil: null }); })}>
                {state.busy === 'reset' ? <Loader2 className="h-4 w-4 animate-spin" /> : <KeyRound className="h-4 w-4" />} Reset {first}’s password
              </button>
            </>
          )}
        </section>
      )}

      {!me && (
        <section className="space-y-3 border-t border-line pt-5">
          <h3 className="text-sm font-semibold">{person.active ? 'Switch off this login' : 'Switched off'}</h3>
          <p className="text-sm text-muted">
            {person.active
              ? `${first} is signed out at once and can’t sign in. Everything they did stays, with their name on it. You can switch it on again.`
              : `${first} can’t sign in. Switching on again counts towards your plan’s users.`}
          </p>
          <button className={cn('btn w-full', person.active ? 'btn-ghost text-bad' : 'btn-primary')} disabled={Boolean(state.busy)} onClick={() => run('active', async () => { await api(`/settings/users/${person.id}/${person.active ? 'deactivate' : 'activate'}`, { body: {} }); onChange({ ...person, active: !person.active }); })}>
            {state.busy === 'active' && <Loader2 className="h-4 w-4 animate-spin" />} {person.active ? `Switch off ${first}’s login` : `Switch ${first}’s login on`}
          </button>
        </section>
      )}

      {state.error && <ErrorNote>{state.error}</ErrorNote>}
    </div>
  );
}
