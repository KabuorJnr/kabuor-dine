// The sales pipeline. The person deciding "who do I call next, and what's it worth?" sees: follow-ups due
// (late first), the board by stage with counts and value, and how this month is going (won, lost and why).
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { ArrowLeft, Check, Loader2, MessageCircle, Phone, Plus, Trophy } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, dateTime, day, ksh, msisdn, prettyPhone, toCents, todayIso } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, Sheet, Stat, Status, SheetFooter, PageError } from '../components/ui';

const SOURCES = ['WhatsApp', 'Instagram', 'Facebook', 'Referral', 'WalkIn', 'Website', 'Call', 'Event', 'Other'];
const SOURCE_LABEL: Record<string, string> = { WalkIn: 'Walk-in' };
const KINDS: [string, string][] = [['Call', 'Call'], ['WhatsApp', 'WhatsApp'], ['Visit', 'Visit'], ['Meeting', 'Meeting'], ['Email', 'Email'], ['Note', 'Note']];
const plusDays = (n: number) => new Date(Date.now() + 3 * 3600e3 + n * 864e5).toISOString().slice(0, 10);

export function NewLead({ open, onClose }: { open: boolean; onClose: () => void }) {
  const navigate = useNavigate();
  const [f, setF] = useState({ name: '', business: '', phone: '', source: 'WhatsApp', value: '', interest: '', followUpOn: plusDays(1) });
  const [state, setState] = useState({ busy: false, error: '' });
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      const lead = await api('/crm/leads', { body: { ...f, estimatedValue: toCents(f.value), followUpOn: f.followUpOn || null } });
      navigate(`/pipeline/${lead.id}`);
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  const set = (k: keyof typeof f) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value });
  return (
    <Sheet open={open} onClose={onClose} title="New lead" description="Someone who might buy. Follow them up from the pipeline.">
      <form onSubmit={submit} className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label="Their name">
            <input className="field" required value={f.name} onChange={set('name')} />
          </Field>
          <Field label="Business">
            <input className="field" value={f.business} onChange={set('business')} placeholder="Optional" />
          </Field>
        </div>
        <Field label="Phone" hint="For calling, WhatsApp, and to spot them if they're already a customer.">
          <input className="field" inputMode="tel" value={f.phone} onChange={set('phone')} placeholder="0712 345 678" />
        </Field>
        <div>
          <p className="mb-1.5 text-sm font-semibold">Where they found you</p>
          <div className="flex flex-wrap gap-1.5">
            {SOURCES.map((s) => (
              <button key={s} type="button" aria-pressed={f.source === s} onClick={() => setF({ ...f, source: s })} className={f.source === s ? 'rounded-full bg-ink px-3 py-1.5 text-sm font-semibold text-white' : 'rounded-full bg-white px-3 py-1.5 text-sm font-semibold ring-1 ring-line'}>
                {SOURCE_LABEL[s] ?? s}
              </button>
            ))}
          </div>
        </div>
        <Field label="What they want">
          <input className="field" value={f.interest} onChange={set('interest')} placeholder="e.g. 200 bottles a month" />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Could be worth (KSh)">
            <input className="field num" inputMode="decimal" value={f.value} onChange={set('value')} />
          </Field>
          <Field label="Follow up on">
            <input className="field" type="date" value={f.followUpOn} onChange={set('followUpOn')} />
          </Field>
        </div>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter>
        <button className="btn btn-primary" disabled={state.busy}>
          {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Add lead
        </button>
        </SheetFooter>
      </form>
    </Sheet>
  );
}

/** Log a call, visit or note on a lead or customer, with an optional follow-up. */
export function LogActivity({ leadId, customerId, onDone }: { leadId?: string; customerId?: string; onDone: () => void }) {
  const [kind, setKind] = useState('Call');
  const [text, setText] = useState('');
  const [followUp, setFollowUp] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      await api('/crm/activities', { body: { leadId, customerId, kind, text, followUpOn: followUp || null } });
      setText('');
      setFollowUp('');
      setState({ busy: false, error: '' });
      onDone();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  return (
    <form onSubmit={submit} className="space-y-2.5">
      <div className="flex flex-wrap gap-1.5">
        {KINDS.map(([v, l]) => (
          <button key={v} type="button" aria-pressed={kind === v} onClick={() => setKind(v)} className={kind === v ? 'rounded-full bg-ink px-3 py-1 text-xs font-semibold text-white' : 'rounded-full bg-white px-3 py-1 text-xs font-semibold ring-1 ring-line'}>
            {l}
          </button>
        ))}
      </div>
      <textarea className="field" rows={2} value={text} onChange={(e) => setText(e.target.value)} placeholder="What was said or agreed?" aria-label="What happened" />
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <span className="text-muted">Follow up:</span>
        {[['', 'No'], [plusDays(1), 'Tomorrow'], [plusDays(3), 'In 3 days'], [plusDays(7), 'Next week']].map(([v, l]) => (
          <button key={l} type="button" aria-pressed={followUp === v} onClick={() => setFollowUp(v)} className={followUp === v ? 'rounded-full bg-brand-soft px-2.5 py-1 font-semibold text-brand' : 'rounded-full px-2.5 py-1 text-muted ring-1 ring-line'}>
            {l}
          </button>
        ))}
        <input type="date" className="rounded-md px-2 py-1 ring-1 ring-line" value={followUp} onChange={(e) => setFollowUp(e.target.value)} aria-label="Follow-up date" />
      </div>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <button className="btn btn-primary py-2" disabled={state.busy}>
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Save
      </button>
    </form>
  );
}

export function ActivityList({ items, onChange }: { items: any[]; onChange: () => void }) {
  const done = async (id: string) => {
    await api(`/crm/activities/${id}/done`, { body: {} });
    onChange();
  };
  if (!items.length) return <p className="text-sm text-muted">Nothing logged yet.</p>;
  return (
    <ol className="space-y-3">
      {items.map((a) => (
        <li key={a.id} className="flex gap-3 text-sm">
          <span className="mt-1.5 h-2 w-2 shrink-0 rounded-full bg-brand" />
          <span className="min-w-0 flex-1">
            <b>{a.kind}</b> <span className="text-xs text-muted">· {a.actor} · {dateTime(a.at)}</span>
            {a.text && <span className="block whitespace-pre-line text-ink-2">{a.text}</span>}
            {a.followUpOn && (
              <span className={cn('mt-1 inline-flex items-center gap-2 text-xs font-semibold', a.followUpDone ? 'text-faint line-through' : a.followUpOn < todayIso() ? 'text-bad' : 'text-warn')}>
                Follow up {day(a.followUpOn)}
                {!a.followUpDone && (
                  <button onClick={() => done(a.id)} className="rounded-full px-2 py-0.5 text-brand no-underline ring-1 ring-line">
                    Done
                  </button>
                )}
              </span>
            )}
          </span>
        </li>
      ))}
    </ol>
  );
}

function FollowUps() {
  const [scope, setScope] = useState('due');
  const { data, error, reload } = useApi<any>(`/crm/follow-ups?scope=${scope}`);
  const done = async (id: string) => {
    await api(`/crm/activities/${id}/done`, { body: {} });
    reload();
  };
  return (
    <>
      <div className="mb-4">
        <Chips label="Follow-ups" value={scope} onChange={setScope} options={[['due', 'Due now', data?.counts.due], ['upcoming', 'Coming up', data?.counts.upcoming]]} />
      </div>
      {error && <ErrorNote>{error}</ErrorNote>}
      {!data ? (
        <Loading />
      ) : data.items.length === 0 ? (
        <Empty title={scope === 'due' ? 'Nobody to chase today' : 'Nothing coming up'}>Set a follow-up when you log a call or add a lead, and it shows here on the day.</Empty>
      ) : (
        <ul className="card divide-y divide-line">
          {data.items.map((f: any) => {
            const wa = msisdn(f.phone);
            return (
              <li key={f.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
                <Link to={f.leadId && !f.customerId ? `/pipeline/${f.leadId}` : `/customers/${f.customerId}`} className="min-w-0 flex-1">
                  <b className="block truncate">{f.who}</b>
                  <span className="text-sm text-muted">
                    <span className={f.late ? 'font-semibold text-bad' : ''}>{f.late ? `Late, was ${day(f.followUpOn)}` : day(f.followUpOn)}</span>
                    {f.text && ` · ${f.text}`}
                  </span>
                </Link>
                {f.phone && (
                  <a href={`tel:+${wa ?? f.phone}`} className="grid h-9 w-9 place-items-center rounded-full ring-1 ring-line" aria-label={`Call ${f.who}`}>
                    <Phone className="h-4 w-4" />
                  </a>
                )}
                {wa && (
                  <a href={`https://wa.me/${wa}`} target="_blank" rel="noreferrer" className="grid h-9 w-9 place-items-center rounded-full bg-mpesa text-white" aria-label={`WhatsApp ${f.who}`}>
                    <MessageCircle className="h-4 w-4" />
                  </a>
                )}
                <button className="btn btn-ghost py-1.5" onClick={() => done(f.id)}>
                  <Check className="h-4 w-4" /> Done
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </>
  );
}

function Board() {
  const { data, error } = useApi<any>('/crm/pipeline');
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const m = data.thisMonth;
  return (
    <>
      <div className="mb-5 grid gap-3 sm:grid-cols-3">
        <Stat label="Open leads worth" value={ksh(data.stages.reduce((s: number, st: any) => s + st.value, 0))}>
          {data.stages.reduce((s: number, st: any) => s + st.count, 0)} leads
        </Stat>
        <Stat label="Won this month" value={`${m.won}`} tone="good">
          {ksh(m.wonValue)}
          {m.winRate !== null && ` · ${m.winRate}% win rate`}
        </Stat>
        <Stat label="Lost this month" value={`${m.lost}`}>
          {m.lostReasons.length ? `Top reason: ${m.lostReasons[0].reason}` : 'None lost'}
        </Stat>
      </div>
      <div className="-mx-4 overflow-x-auto px-4 sm:mx-0 sm:px-0">
        <div className="grid min-w-[48rem] grid-cols-3 gap-3">
          {data.stages.map((s: any) => (
            <section key={s.stage} className="rounded-lg bg-white/60 p-3 ring-1 ring-line">
              <header className="mb-2 flex items-baseline justify-between px-1">
                <h2 className="font-semibold">{s.stage}</h2>
                <span className="num text-xs text-muted">
                  {s.count} · {ksh(s.value)}
                </span>
              </header>
              <ul className="space-y-2">
                {s.leads.map((l: any) => (
                  <li key={l.id}>
                    <Link to={`/pipeline/${l.id}`} className="block rounded-md bg-white p-3 ring-1 ring-line hover:ring-brand">
                      <b className="block truncate text-sm">{l.business || l.name}</b>
                      <span className="block truncate text-xs text-muted">
                        {l.business ? `${l.name} · ` : ''}
                        {SOURCE_LABEL[l.source] ?? l.source}
                        {l.interest && ` · ${l.interest}`}
                      </span>
                      <span className="mt-1.5 flex items-center justify-between text-xs">
                        <b className="num">{l.estimatedValue ? ksh(l.estimatedValue) : '—'}</b>
                        {l.nextFollowUp ? <span className={l.nextFollowUp < todayIso() ? 'font-semibold text-bad' : 'text-muted'}>Follow up {day(l.nextFollowUp)}</span> : <span className="text-warn">No follow-up</span>}
                      </span>
                    </Link>
                  </li>
                ))}
                {s.leads.length === 0 && <li className="px-1 py-4 text-center text-xs text-faint">None</li>}
              </ul>
            </section>
          ))}
        </div>
      </div>
    </>
  );
}

export function Pipeline() {
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const view = params.get('view') ?? 'board';
  const [adding, setAdding] = useState(false);
  return (
    <>
      <PageHeader print
        title="Pipeline"
        subtitle="Everyone who might buy, where they are, and who to talk to next."
        actions={
          can(session, 'crm') && (
            <button className="btn btn-primary" onClick={() => setAdding(true)}>
              <Plus className="h-4 w-4" /> New lead
            </button>
          )
        }
      />
      <div className="mb-4">
        <Chips label="View" value={view} onChange={(v) => setParams(v === 'board' ? {} : { view: v })} options={[['board', 'Board'], ['follow-ups', 'Follow-ups']]} />
      </div>
      {view === 'follow-ups' ? <FollowUps /> : <Board />}
      <NewLead open={adding} onClose={() => setAdding(false)} />
    </>
  );
}

export function LeadView() {
  const { id } = useParams();
  const navigate = useNavigate();
  const { data, error, reload } = useApi<any>(`/crm/leads/${id}`);
  const [lostReason, setLostReason] = useState('');
  const [losing, setLosing] = useState(false);
  const [state, setState] = useState({ busy: '', error: '' });
  useEffect(() => setLosing(false), [id]);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const l = data.lead;
  const wa = msisdn(l.phone);
  const open = l.stage !== 'Won' && l.stage !== 'Lost';
  const move = async (stage: string, reason?: string) => {
    setState({ busy: stage, error: '' });
    try {
      if (stage === 'Won') {
        const r = await api(`/crm/leads/${l.id}/convert`, { body: {} });
        navigate(`/customers/${r.customer.id}`);
        return;
      }
      await api(`/crm/leads/${l.id}/stage`, { body: { stage, lostReason: reason } });
      setLosing(false);
      reload();
      setState({ busy: '', error: '' });
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };
  return (
    <>
      <Link to="/pipeline" className="mb-3 inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink">
        <ArrowLeft className="h-4 w-4" /> Pipeline
      </Link>
      <PageHeader
        title={l.business || l.name}
        subtitle={[l.business && l.name, l.phone && prettyPhone(l.phone), `${SOURCE_LABEL[l.source] ?? l.source}`, l.number].filter(Boolean).join(' · ')}
        actions={
          <>
            {l.phone && (
              <a className="btn btn-ghost" href={`tel:+${wa ?? l.phone}`}>
                <Phone className="h-4 w-4" /> Call
              </a>
            )}
            {wa && (
              <a className="btn btn-mpesa" href={`https://wa.me/${wa}`} target="_blank" rel="noreferrer">
                <MessageCircle className="h-4 w-4" /> WhatsApp
              </a>
            )}
          </>
        }
      />
      <div className="mb-5 flex flex-wrap items-center gap-2">
        {['New', 'Contacted', 'Quoted'].map((s) => (
          <button key={s} disabled={!open || Boolean(state.busy)} onClick={() => move(s)} className={cn('rounded-full px-3.5 py-1.5 text-sm font-semibold ring-1', l.stage === s ? 'bg-brand-soft text-brand ring-brand/40' : 'bg-white ring-line disabled:opacity-50')}>
            {s}
          </button>
        ))}
        {open ? (
          <>
            <button className="btn btn-primary py-1.5" disabled={Boolean(state.busy)} onClick={() => move('Won')}>
              <Trophy className="h-4 w-4" /> Won: make them a customer
            </button>
            <button className="btn btn-ghost py-1.5 text-bad" onClick={() => setLosing(true)}>
              Lost
            </button>
          </>
        ) : (
          <Status tone={l.stage === 'Won' ? 'good' : 'plain'}>
            {l.stage}
            {l.lostReason && `: ${l.lostReason}`}
          </Status>
        )}
        {l.customerId && (
          <Link to={`/customers/${l.customerId}`} className="text-sm font-semibold text-brand">
            Open customer →
          </Link>
        )}
      </div>
      {losing && (
        <div className="mb-5 flex flex-wrap items-end gap-2 rounded-md bg-bad-soft/60 p-3">
          <Field label="Why was it lost?" className="flex-1">
            <input className="field" value={lostReason} onChange={(e) => setLostReason(e.target.value)} placeholder="e.g. Too expensive, went with another supplier" />
          </Field>
          <button className="btn bg-bad py-2.5 text-white" disabled={!lostReason.trim()} onClick={() => move('Lost', lostReason)}>
            Mark lost
          </button>
        </div>
      )}
      {state.error && <div className="mb-4"><ErrorNote>{state.error}</ErrorNote></div>}
      <div className="grid gap-5 lg:grid-cols-[1fr_18rem]">
        <section className="card p-4 sm:p-5">
          <h2 className="mb-3 text-sm font-semibold text-muted">Conversation</h2>
          <LogActivity leadId={l.id} onDone={reload} />
          <div className="mt-5 border-t border-line pt-4">
            <ActivityList items={data.activities} onChange={reload} />
          </div>
        </section>
        <aside className="card h-fit space-y-2 p-4 text-sm">
          <p>
            <span className="text-muted">Could be worth</span>
            <b className="num block text-lg">{l.estimatedValue ? ksh(l.estimatedValue) : '—'}</b>
          </p>
          {l.interest && (
            <p>
              <span className="text-muted">Wants</span>
              <span className="block">{l.interest}</span>
            </p>
          )}
          <p>
            <span className="text-muted">Looked after by</span>
            <span className="block">{l.ownerName || '—'}</span>
          </p>
          <p className="text-xs text-muted">In {l.stage} since {day(l.stageChangedAt)}</p>
        </aside>
      </div>
    </>
  );
}
