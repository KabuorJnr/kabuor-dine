// Agents: what they want you to check, and which ones are working. On each task the reviewer is deciding
// "should this happen?", so the card says who is asking, what they found, exactly what approving does, lets you
// change the wording first, and keeps the timeline one tap away. The setup cards say plainly what each agent
// does by itself and what it always asks about first.
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { Activity, Bot, Check, ChevronDown, ExternalLink, Hand, Loader2, Pencil, Play, Plus, Sparkles, Zap } from 'lucide-react';
import { AgentExecutionTrace } from '../components/AgentExecutionTrace';
import { OrderIntake, StudioSheet, type CustomDraft } from '../components/AgentStudio';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, dateTime, ksh } from '../lib/format';
import { Chips, Empty, ErrorNote, Loading, PageHeader, Status } from '../components/ui';

type Step = { at: string; who: string; text: string };
type Task = {
  id: string; agentKey: string; agent: string; title: string; detail: string; action: string; status: string; result: string; link: string;
  createdAt: string; decidedAt: string | null; decidedBy: string; draft: string | null; steps: Step[]; byItself: boolean; canDecide: boolean;
};
type Agent = {
  key: string; name: string; job: string; onItsOwn: string[]; asksFirst: string[]; every: string; enabled: boolean; enabledBy: string | null;
  lastRunAt: string | null; lastRunSummary: string | null; settings: Record<string, any>; toReview: number; canManage: boolean; canRun: boolean;
  whenSure: string | null; trigger: string | null; custom: boolean;
  month: { tasks: number; approved: number; changed: number; dismissed: number; byItself: number; collected: number };
};

const APPROVE: Record<string, [string, string]> = {
  send_reminder: ['Send reminder', 'Opens WhatsApp with this message ready; you tap send. (With an SMS account set up, it goes by SMS straight away.)'],
  match_payment: ['Apply payment', 'Applies the payment to the invoice and updates what the customer owes.'],
  send_order: ['Mark order sent', 'Marks the order as sent to the supplier. Share it with them from the order page.'],
  accept_purchase: ['Record the bill', 'Records the supplier’s invoice as a bill you owe, confirms it to KRA, and marks the matching order received.'],
  send_quote: ['Send quote', 'Marks the quote as sent and opens WhatsApp with it ready for the customer.'],
  issue_credit: ['Issue credit note', 'Issues an official credit note in the general ledger, balances the disputed invoice, and drafts customer explanation.'],
  resolve_dispute: ['Resolve dispute', 'Confirms the dispute resolution, updates customer billing ledger, and closes the case.'],
  review_variance: ['Approve variance', 'Confirms the sales quote-to-invoice price difference as authorized and records the audit justification.'],
};

export default function Agents() {
  const [params, setParams] = useSearchParams();
  const tab = (params.get('tab') as 'review' | 'agents' | 'done') ?? 'review';
  const tasks = useApi<{ counts: { review: number; done: number }; items: Task[] }>(tab === 'agents' ? null : `/agents/tasks?status=${tab === 'done' ? 'done' : 'review'}`);
  const agents = useApi<Agent[]>('/agents');
  const session = useSession();
  const on = agents.data?.filter((a) => a.enabled).length ?? 0;
  const [studio, setStudio] = useState<{ open: boolean; editing: CustomDraft | null }>({ open: false, editing: null });
  const [activeTrace, setActiveTrace] = useState<Task | null>(null);

  return (
    <div>
      <PageHeader
        title="Agents"
        subtitle={agents.data && `${on} of ${agents.data.length} working · they draft and check; you decide what leaves the business`}
        actions={can(session, 'admin') ? <button className="btn btn-primary" onClick={() => setStudio({ open: true, editing: null })}><Plus className="h-4 w-4" /> Build an agent</button> : undefined}
      />
      <StudioSheet open={studio.open} editing={studio.editing} onClose={() => setStudio({ open: false, editing: null })} onSaved={() => (agents.reload(), setParams({ tab: 'agents' }))} />
      <AgentExecutionTrace task={activeTrace} open={Boolean(activeTrace)} onClose={() => setActiveTrace(null)} />
      <div className="mb-4">
        <Chips
          label="Show"
          value={tab}
          onChange={(v) => setParams(v === 'review' ? {} : { tab: v })}
          options={[
            ['review', 'To review', tasks.data?.counts.review],
            ['agents', 'Agents', agents.data?.length],
            ['done', 'History', tasks.data?.counts.done],
          ]}
        />
      </div>
      {tab === 'agents' ? (
        agents.error ? <ErrorNote>{agents.error}</ErrorNote> : !agents.data ? <Loading /> : (
          <div className="grid gap-4 lg:grid-cols-2">
            {agents.data.map((a) => (
              <AgentCard key={a.key} a={a} onChange={() => (agents.reload(), tasks.reload())}
                onEdit={() => setStudio({ open: true, editing: { key: a.key, name: a.name, instructions: a.settings.instructions ?? a.job, skills: a.settings.skills ?? [], every: a.settings.every ?? 'daily' } })} />
            ))}
          </div>
        )
      ) : tasks.error ? (
        <ErrorNote>{tasks.error}</ErrorNote>
      ) : !tasks.data ? (
        <Loading />
      ) : tasks.data.items.length === 0 ? (
        tab === 'review' ? (
          <Empty title="Nothing to review" action={on === 0 ? <button onClick={() => setParams({ tab: 'agents' })} className="btn btn-primary">See the agents</button> : undefined}>
            {on === 0 ? 'No agent is switched on yet. Each one drafts work for you to approve; nothing is sent without you.' : 'Your agents have nothing waiting for you. They check again on their own schedule.'}
          </Empty>
        ) : (
          <Empty title="No history yet" />
        )
      ) : (
        <ul className="space-y-3">
          {tasks.data.items.map((t) => (
            <TaskCard key={t.id} t={t} onDone={() => (tasks.reload(), agents.reload())} onShowTrace={(task) => setActiveTrace(task)} />
          ))}
        </ul>
      )}
    </div>
  );
}

function TaskCard({ t, onDone, onShowTrace }: { t: Task; onDone: () => void; onShowTrace: (t: Task) => void }) {
  const [text, setText] = useState(t.draft ?? '');
  const [open, setOpen] = useState(false);
  const [state, setState] = useState({ busy: '', error: '' });
  const review = t.status === 'NeedsReview';
  const [label, effect] = APPROVE[t.action] ?? ['Mark handled', 'Nothing is sent or changed; this just clears it from your list.'];

  const decide = async (approve: boolean) => {
    setState({ busy: approve ? 'approve' : 'dismiss', error: '' });
    try {
      const r = await api<{ result: string }>(`/agents/tasks/${t.id}/${approve ? 'approve' : 'dismiss'}`, { body: { text: t.action === 'send_reminder' ? text : undefined } });
      if (r.result?.startsWith('https://wa.me/')) window.open(r.result, '_blank', 'noopener');
      onDone();
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };

  return (
    <li className="card p-4 sm:p-5">
      <div className="flex flex-wrap items-center gap-2 text-xs text-muted">
        <span className="inline-flex items-center gap-1 rounded-full bg-indigo/10 px-2 py-0.5 font-semibold text-indigo">
          <Bot className="h-3.5 w-3.5" /> {t.agent}
        </span>
        <span>{dateTime(t.createdAt)}</span>
        {!review && (
          <Status tone={t.status === 'Done' ? 'good' : t.status === 'Failed' ? 'bad' : 'plain'}>
            {t.status === 'Done' ? (t.byItself ? 'Done by itself' : `Approved by ${t.decidedBy}`) : t.status === 'Dismissed' ? 'Dismissed' : 'Failed'}
          </Status>
        )}
      </div>
      <h2 className="mt-2 font-semibold text-base leading-snug">{t.title}</h2>
      {t.detail && <p className="mt-1 text-sm text-muted">{t.detail}</p>}

      {review && t.action === 'send_reminder' && (
        <label className="mt-3 block">
          <span className="mb-1 flex justify-between text-xs font-semibold text-muted">
            Message to send <span className="num font-normal">{text.length}/480</span>
          </span>
          <textarea className="field min-h-24" value={text} onChange={(e) => setText(e.target.value)} maxLength={480} />
        </label>
      )}
      {!review && t.result && !t.result.startsWith('https://') && <p className="mt-2 text-sm font-medium text-good">{t.result}</p>}

      {review && t.canDecide && <p className="mt-3 rounded-md bg-paper px-3 py-2 text-xs text-muted"><b className="text-ink-2">If you approve:</b> {effect}</p>}
      {review && !t.canDecide && <p className="mt-3 rounded-md bg-paper px-3 py-2 text-xs text-muted">Waiting for someone whose role covers this (the owner or accountant).</p>}
      {state.error && <div className="mt-3"><ErrorNote>{state.error}</ErrorNote></div>}

      <div className="mt-3 flex flex-wrap items-center gap-2">
        {review && t.canDecide && (
          <>
            <button onClick={() => decide(true)} disabled={Boolean(state.busy) || (t.action === 'send_reminder' && text.trim().length < 10)} className="btn btn-primary">
              {state.busy === 'approve' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />} {label}
            </button>
            <button onClick={() => decide(false)} disabled={Boolean(state.busy)} className="btn btn-ghost">
              {t.action === 'send_order' || t.action === 'send_quote' ? 'Cancel the draft' : 'Dismiss'}
            </button>
          </>
        )}
        {t.link && (
          <Link to={t.link} className="btn btn-ghost">
            <ExternalLink className="h-4 w-4" /> Open
          </Link>
        )}
        <button
          type="button"
          onClick={() => onShowTrace(t)}
          className="btn btn-ghost text-indigo font-semibold hover:bg-indigo/10 flex items-center gap-1.5"
          title="Inspect agent decision graph & reasoning trace (Figure 8)"
        >
          <Activity className="h-4 w-4" /> Trace Graph
        </button>
        <button onClick={() => setOpen(!open)} className="ml-auto inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink" aria-expanded={open}>
          Timeline ({t.steps.length}) <ChevronDown className={cn('h-4 w-4 transition', open && 'rotate-180')} />
        </button>
      </div>
      {open && (
        <ol className="mt-3 space-y-2 border-l-2 border-line pl-4">
          {t.steps.map((s, i) => (
            <li key={i} className="relative text-sm">
              <span className={cn('absolute top-1.5 -left-[21px] h-2.5 w-2.5 rounded-full', s.who.includes('agent') ? 'bg-indigo' : 'bg-brand')} />
              <b>{s.who}</b> <span className="text-xs text-faint">{dateTime(s.at)}</span>
              <p className="text-muted">{s.text}</p>
            </li>
          ))}
        </ol>
      )}
    </li>
  );
}

function AgentCard({ a, onChange, onEdit }: { a: Agent; onChange: () => void; onEdit: () => void }) {
  const session = useSession();
  const [settings, setSettings] = useState(a.settings);
  const [state, setState] = useState({ busy: '', error: '', note: '' });
  const call = async (what: string, fn: () => Promise<any>) => {
    setState({ busy: what, error: '', note: '' });
    try {
      const r = await fn();
      setState({ busy: '', error: '', note: r?.summary ?? '' });
      onChange();
    } catch (e) {
      setState({ busy: '', error: (e as Error).message, note: '' });
    }
  };
  const save = (enabled: boolean, s = settings) => call('save', () => api(`/agents/${a.key}`, { method: 'PUT', body: { enabled, settings: s } }));

  return (
    <section className="card flex flex-col p-4 sm:p-5">
      <div className="flex items-start gap-3">
        <span className={cn('grid h-10 w-10 shrink-0 place-items-center rounded-full', a.enabled ? 'bg-indigo text-white' : 'bg-paper text-faint ring-1 ring-line')}>
          <Bot className="h-5 w-5" />
        </span>
        <div className="min-w-0 flex-1">
          <h2 className="flex items-center gap-2 font-semibold">{a.name}{a.custom && <span className="rounded-full bg-paper px-2 py-0.5 text-2xs font-semibold text-muted ring-1 ring-line">Yours</span>}</h2>
          <p className="text-sm text-muted">{a.job}</p>
          {a.trigger && <p className="mt-1 flex items-center gap-1 text-xs text-indigo"><Zap className="h-3.5 w-3.5" /> Also wakes up: {a.trigger.charAt(0).toLowerCase() + a.trigger.slice(1)}</p>}
        </div>
        {a.canManage ? (
          <button role="switch" aria-checked={a.enabled} aria-label={`${a.name} ${a.enabled ? 'on' : 'off'}`} onClick={() => save(!a.enabled)} disabled={Boolean(state.busy)} className={cn('relative h-7 w-12 shrink-0 rounded-full transition', a.enabled ? 'bg-good' : 'bg-line')}>
            <span className={cn('absolute top-1 h-5 w-5 rounded-full bg-white shadow transition', a.enabled ? 'left-6' : 'left-1')} />
          </button>
        ) : (
          <Status tone={a.enabled ? 'good' : 'plain'}>{a.enabled ? 'On' : 'Off'}</Status>
        )}
      </div>

      <div className="mt-4 grid gap-3 text-sm sm:grid-cols-2">
        <div>
          <p className="mb-1 text-xs font-semibold tracking-wider text-faint uppercase">Does by itself</p>
          <ul className="space-y-1">
            {a.onItsOwn.map((x) => (
              <li key={x} className="flex gap-2"><Check className="mt-0.5 h-4 w-4 shrink-0 text-good" /> {x}</li>
            ))}
          </ul>
        </div>
        <div>
          <p className="mb-1 text-xs font-semibold tracking-wider text-faint uppercase">Always asks you first</p>
          <ul className="space-y-1">
            {a.asksFirst.map((x) => (
              <li key={x} className="flex gap-2"><Hand className="mt-0.5 h-4 w-4 shrink-0 text-gold" /> {x}</li>
            ))}
          </ul>
        </div>
      </div>

      {a.whenSure && a.canManage && <ReviewPolicy a={a} onSave={(s) => save(a.enabled, { ...settings, ...s })} busy={state.busy === 'save'} />}
      {a.whenSure && !a.canManage && a.settings.review === 'sure' && <p className="mt-3 text-xs text-muted">Set to act alone when sure: {a.whenSure}</p>}

      {a.key === 'orders' && a.enabled && a.canRun && <OrderIntake onDone={onChange} />}

      {a.key === 'collections' && a.canManage && (
        <div className="mt-4 grid grid-cols-3 gap-2 rounded-md bg-paper p-3 text-sm">
          <label>
            <span className="mb-1 block text-xs font-semibold text-muted">Remind every</span>
            <select className="field py-1.5" value={settings.remindEveryDays} onChange={(e) => setSettings({ ...settings, remindEveryDays: Number(e.target.value) })}>
              {[3, 7, 14, 30].map((d) => <option key={d} value={d}>{d} days</option>)}
            </select>
          </label>
          <label>
            <span className="mb-1 block text-xs font-semibold text-muted">Start after</span>
            <select className="field py-1.5" value={settings.minDaysLate} onChange={(e) => setSettings({ ...settings, minDaysLate: Number(e.target.value) })}>
              {[1, 3, 7, 14].map((d) => <option key={d} value={d}>{d} day{d === 1 ? '' : 's'} late</option>)}
            </select>
          </label>
          <label>
            <span className="mb-1 block text-xs font-semibold text-muted">Language</span>
            <select className="field py-1.5" value={settings.language} onChange={(e) => setSettings({ ...settings, language: e.target.value })}>
              <option value="en">English</option>
              <option value="sw">Kiswahili</option>
            </select>
          </label>
          {JSON.stringify(settings) !== JSON.stringify(a.settings) && (
            <button onClick={() => save(a.enabled)} className="btn btn-ghost col-span-3 py-1.5">Save settings</button>
          )}
        </div>
      )}

      <div className="mt-4 flex flex-wrap items-center gap-x-3 gap-y-2 border-t border-line pt-3 text-sm text-muted">
        <span className="flex-1">
          {a.enabled ? (
            <>
              {a.every}{a.enabledBy && <> · switched on by {a.enabledBy}</>}
              {a.lastRunAt && <span className="block text-xs">Last run {dateTime(a.lastRunAt)}: {a.lastRunSummary}</span>}
            </>
          ) : a.canManage ? 'Off. Switch it on to start; it works with your rights for this job.' : 'Off. The owner can switch it on.'}
        </span>
        {a.toReview > 0 && <Link to="/agents" className="font-semibold text-brand hover:underline">{a.toReview} to review</Link>}
        {a.custom && a.canManage && <button onClick={onEdit} className="btn btn-ghost py-1.5"><Pencil className="h-4 w-4" /> Edit</button>}
        {a.canRun && a.key !== 'orders' && can(session, 'read') && (
          <button onClick={() => call('run', () => api(`/agents/${a.key}/run`, { body: {} }))} disabled={Boolean(state.busy)} className="btn btn-ghost py-1.5">
            {state.busy === 'run' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Play className="h-4 w-4" />} Run now
          </button>
        )}
      </div>
      {a.month.tasks > 0 && <Track m={a.month} />}
      {state.note && <p className="mt-2 flex items-center gap-1.5 text-sm font-medium text-good"><Sparkles className="h-4 w-4" /> {state.note}</p>}
      {state.error && <div className="mt-2"><ErrorNote>{state.error}</ErrorNote></div>}
    </section>
  );
}

/** How much it may do alone. The owner's choice, per agent: what "sure" means is spelled out, with the limit. */
function ReviewPolicy({ a, onSave, busy }: { a: Agent; onSave: (s: Record<string, any>) => void; busy: boolean }) {
  const [review, setReview] = useState<string>(a.settings.review ?? 'always');
  const [limit, setLimit] = useState(String(a.settings.limit ?? 5000));
  const usesLimit = a.key !== 'collections';
  const dirty = review !== (a.settings.review ?? 'always') || (usesLimit && Number(limit) !== Number(a.settings.limit ?? 5000));
  return (
    <div className="mt-4 space-y-2 rounded-md bg-paper p-3 text-sm">
      <p className="text-xs font-semibold tracking-wider text-faint uppercase">How much it decides</p>
      <div className="grid gap-2 sm:grid-cols-2" role="radiogroup" aria-label="How much it decides">
        {[['always', 'Ask me every time', 'Nothing happens until a person approves.'], ['sure', 'Act alone when sure', a.whenSure!]].map(([id, label, hint]) => (
          <button key={id} role="radio" aria-checked={review === id} onClick={() => setReview(id)} className={cn('rounded-md p-2.5 text-left ring-1', review === id ? 'bg-white ring-2 ring-brand' : 'bg-white/60 ring-line')}>
            <b className="block text-sm">{label}</b>
            <span className="text-xs text-muted">{hint}</span>
          </button>
        ))}
      </div>
      {review === 'sure' && usesLimit && (
        <label className="flex flex-wrap items-center gap-2">
          <span className="text-xs font-semibold text-muted">Up to KSh</span>
          <input className="field num w-32 py-1.5" inputMode="numeric" value={limit} onChange={(e) => setLimit(e.target.value.replace(/\D/g, ''))} aria-label="Limit in shillings" />
          <span className="text-xs text-muted">each; above that it asks.</span>
        </label>
      )}
      {dirty && <button className="btn btn-ghost py-1.5" disabled={busy} onClick={() => onSave({ review, limit: Number(limit) || 0 })}>{busy && <Loader2 className="h-4 w-4 animate-spin" />} Save</button>}
    </div>
  );
}

/** Last 30 days: what people did with its work. Many "changed" or "dismissed" says it needs a narrower job. */
function Track({ m }: { m: Agent['month'] }) {
  const decided = m.approved + m.dismissed;
  const rate = decided ? Math.round((m.approved / decided) * 100) : null;
  return (
    <p className="mt-2 text-xs text-muted">
      <b className="text-ink-2">Last 30 days:</b> {m.tasks} task{m.tasks === 1 ? '' : 's'}
      {rate !== null && <> · {rate}% approved{m.changed > 0 && ` (${m.changed} changed first)`}</>}
      {m.dismissed > 0 && <> · {m.dismissed} dismissed</>}
      {m.byItself > 0 && <> · {m.byItself} done alone</>}
      {m.collected > 0 && <> · <b className="text-good">{ksh(m.collected)} paid after its reminders</b></>}
    </p>
  );
}
