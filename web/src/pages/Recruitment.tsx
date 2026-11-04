// Hiring. HR (or the owner) is deciding "who do I move forward next, and which jobs still need people?", so the page
// leads with who is waiting on a decision (offers out, interviews coming up), shows each opening's progress against
// the number of people it's for, and moves an applicant on with one tap: an interview asks for its date, turning
// someone down asks why, so the reasons can be counted later.
import { useState } from 'react';
import { Link } from 'react-router';
import { CalendarDays, ChevronRight, Loader2, Mail, MessageCircle, Plus, UserPlus } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { cn, day, msisdn, prettyPhone, todayIso } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, SearchBox, Sheet, Stat, Status, SheetFooter, PageError } from '../components/ui';

type Stage = 'Applied' | 'Shortlisted' | 'Interview' | 'Offer' | 'Hired' | 'Rejected';
type Opening = {
  id: string; title: string; department: string; location: string; type: string; description: string; positions: number; open: boolean;
  createdAt: string; closedAt: string | null; applied: number; active: number; hired: number; stages: Record<Stage, number>;
};
type Overview = { counts: { open: number; closed: number; candidates: number; interviewsThisWeek: number; hiredLast30: number }; openings: Opening[]; types: string[] };
type Candidate = {
  id: string; openingId: string; opening: string; name: string; phone: string; email: string; source: string; stage: Stage;
  interviewOn: string | null; notes: string; rejectReason: string; createdAt: string; stageChangedAt: string;
};
type Candidates = { counts: Record<Stage, number>; active: number; items: Candidate[] };

const STAGES: Stage[] = ['Applied', 'Shortlisted', 'Interview', 'Offer', 'Hired', 'Rejected'];
const TONE: Record<Stage, 'good' | 'info' | 'warn' | 'bad' | 'plain'> = { Applied: 'plain', Shortlisted: 'info', Interview: 'warn', Offer: 'warn', Hired: 'good', Rejected: 'bad' };
const NEXT: Partial<Record<Stage, [Stage, string]>> = { Applied: ['Shortlisted', 'Shortlist'], Shortlisted: ['Interview', 'Invite to interview'], Interview: ['Offer', 'Make an offer'], Offer: ['Hired', 'Mark hired'] };

export default function Recruitment() {
  const overview = useApi<Overview>('/recruitment');
  const [openingId, setOpeningId] = useState<string | null>(null);
  const [stage, setStage] = useState<'active' | Stage | 'all'>('active');
  const [q, setQ] = useState('');
  const list = useApi<Candidates>(`/recruitment/candidates?stage=${stage}${openingId ? `&openingId=${openingId}` : ''}${q.trim() ? `&q=${encodeURIComponent(q.trim())}` : ''}`);
  const [editing, setEditing] = useState<Opening | 'new' | null>(null);
  const [adding, setAdding] = useState(false);
  const [person, setPerson] = useState<Candidate | null>(null);
  const refresh = () => {
    overview.reload();
    list.reload();
  };

  if (overview.error) return <PageError>{overview.error}</PageError>;
  if (!overview.data) return <Loading />;
  const o = overview.data;
  const openOnes = o.openings.filter((x) => x.open);
  const selected = o.openings.find((x) => x.id === openingId) ?? null;
  const counts = list.data?.counts;

  return (
    <>
      <PageHeader print
        title="Recruitment"
        subtitle="Your job openings and everyone who applied, from first contact to hired."
        actions={
          <>
            <button className="btn btn-primary" onClick={() => setEditing('new')}><Plus className="h-4 w-4" /> New opening</button>
            <button className="btn btn-ghost" disabled={!openOnes.length} onClick={() => setAdding(true)} title={openOnes.length ? undefined : 'Open a job first'}><UserPlus className="h-4 w-4" /> Add applicant</button>
          </>
        }
      />

      {o.openings.length === 0 ? (
        <Empty title="No openings yet" action={<button className="btn btn-primary" onClick={() => setEditing('new')}><Plus className="h-4 w-4" /> New opening</button>}>
          Add the job you’re hiring for (say, a delivery driver or a shop attendant), then the people who apply: walk-ins, referrals, WhatsApp. Each one moves from applied to hired, with interview dates and reasons kept.
        </Empty>
      ) : (
        <>
          <div className="mb-5 grid grid-cols-2 gap-3 lg:grid-cols-4">
            <Stat label="Open jobs" value={o.counts.open}>{openOnes.reduce((n, x) => n + Math.max(0, x.positions - x.hired), 0)} position{openOnes.reduce((n, x) => n + Math.max(0, x.positions - x.hired), 0) === 1 ? '' : 's'} to fill</Stat>
            <Stat label="Applicants in progress" value={o.counts.candidates}>Not yet hired or turned down</Stat>
            <Stat label="Interviews this week" value={o.counts.interviewsThisWeek}>Next 7 days</Stat>
            <Stat label="Hired, last 30 days" value={o.counts.hiredLast30} />
          </div>

          <div className="grid gap-5 lg:grid-cols-[19rem_1fr]">
            <aside className="space-y-2" aria-label="Openings">
              <button onClick={() => setOpeningId(null)} className={cn('card flex w-full items-center justify-between gap-2 px-4 py-3 text-left', !openingId ? 'ring-2 ring-brand' : 'hover:ring-faint')}>
                <b className="text-sm">All openings</b>
                <span className="num text-xs text-muted">{o.counts.candidates} in progress</span>
              </button>
              {o.openings.map((x) => (
                <button key={x.id} onClick={() => setOpeningId(x.id)} className={cn('card block w-full px-4 py-3 text-left', openingId === x.id ? 'ring-2 ring-brand' : 'hover:ring-faint', !x.open && 'opacity-70')}>
                  <span className="flex items-start justify-between gap-2">
                    <b className="text-sm leading-snug">{x.title}</b>
                    {!x.open && <Status tone="plain">Closed</Status>}
                  </span>
                  <span className="mt-0.5 block truncate text-xs text-muted">{[x.department, x.location, x.type].filter(Boolean).join(' · ')}</span>
                  <span className="mt-2 flex items-center justify-between text-xs">
                    <span className={x.hired >= x.positions ? 'font-semibold text-good' : 'text-ink-2'}>{x.hired} of {x.positions} hired</span>
                    <span className="text-muted">{x.active} in progress</span>
                  </span>
                  <span className="mt-1 flex h-1.5 overflow-hidden rounded-full bg-paper ring-1 ring-line">
                    <span className="bg-good" style={{ width: `${Math.min(100, (x.hired / x.positions) * 100)}%` }} />
                  </span>
                </button>
              ))}
            </aside>

            <section className="min-w-0 space-y-3">
              {selected && (
                <div className="card flex flex-wrap items-start justify-between gap-3 p-4">
                  <div className="min-w-0">
                    <h2 className="font-display text-lg font-semibold">{selected.title}</h2>
                    <p className="text-sm text-muted">{[selected.department, selected.location, selected.type].filter(Boolean).join(' · ')} · for {selected.positions} {selected.positions === 1 ? 'person' : 'people'}</p>
                    {selected.description && <p className="mt-2 max-w-2xl text-sm whitespace-pre-line text-ink-2">{selected.description}</p>}
                  </div>
                  <button className="btn btn-ghost" onClick={() => setEditing(selected)}>Edit</button>
                </div>
              )}
              <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                <Chips label="Stage" value={stage} onChange={setStage} options={[['active', 'In progress', list.data?.active], ...STAGES.map((s) => [s, s, counts?.[s]] as [Stage, string, number | undefined]), ['all', 'All']]} />
              </div>
              <SearchBox value={q} onChange={setQ} placeholder="Search applicants by name, phone or email" />
              {list.error ? <ErrorNote>{list.error}</ErrorNote> : !list.data ? <Loading /> : list.data.items.length === 0 ? (
                <p className="card p-5 text-sm text-muted">{q ? 'Nobody matches that search.' : stage === 'active' ? 'Nobody is waiting on a decision.' : `Nobody at ${stage === 'all' ? 'any stage' : stage.toLowerCase()} here.`}</p>
              ) : (
                <ul className="card divide-y divide-line">
                  {list.data.items.map((c) => (
                    <CandidateRow key={c.id} c={c} showOpening={!openingId} onOpen={() => setPerson(c)} onMoved={refresh} />
                  ))}
                </ul>
              )}
            </section>
          </div>
        </>
      )}

      <Sheet open={Boolean(editing)} onClose={() => setEditing(null)} title={editing === 'new' ? 'New opening' : 'Edit opening'}>
        {editing && <OpeningForm opening={editing === 'new' ? null : editing} types={o.types} onSaved={(id) => { setEditing(null); refresh(); if (id) setOpeningId(id); }} />}
      </Sheet>
      <Sheet open={adding} onClose={() => setAdding(false)} title="Add applicant">
        {adding && <CandidateForm openings={openOnes} openingId={selected?.open ? selected.id : openOnes[0]?.id} onSaved={() => { setAdding(false); refresh(); }} />}
      </Sheet>
      <Sheet open={Boolean(person)} onClose={() => setPerson(null)} title={person?.name ?? ''}>
        {person && <CandidateSheet c={person} onMoved={(c) => { setPerson(c); refresh(); }} />}
      </Sheet>
    </>
  );
}

function Contact({ c }: { c: Candidate }) {
  const wa = msisdn(c.phone);
  return (
    <span className="flex shrink-0 gap-1">
      {wa && <a href={`https://wa.me/${wa}`} target="_blank" rel="noreferrer" onClick={(e) => e.stopPropagation()} className="grid h-8 w-8 place-items-center rounded-full text-mpesa hover:bg-paper" aria-label={`WhatsApp ${c.name}`} title={prettyPhone(c.phone)}><MessageCircle className="h-4 w-4" /></a>}
      {c.email && <a href={`mailto:${c.email}`} onClick={(e) => e.stopPropagation()} className="grid h-8 w-8 place-items-center rounded-full text-muted hover:bg-paper" aria-label={`Email ${c.name}`} title={c.email}><Mail className="h-4 w-4" /></a>}
    </span>
  );
}

function CandidateRow({ c, showOpening, onOpen, onMoved }: { c: Candidate; showOpening: boolean; onOpen: () => void; onMoved: () => void }) {
  const next = NEXT[c.stage];
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  const quick = next && next[0] !== 'Interview';
  const move = async () => {
    if (!next) return;
    setBusy(true);
    setErr('');
    try {
      await api(`/recruitment/candidates/${c.id}/stage`, { body: { stage: next[0] } });
      onMoved();
    } catch (e) {
      setErr((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  const interviewSoon = c.stage === 'Interview' && c.interviewOn;
  return (
    <li className="flex items-center gap-3 px-4 py-3">
      <button onClick={onOpen} className="min-w-0 flex-1 text-left">
        <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <b className="truncate">{c.name}</b>
          <Status tone={TONE[c.stage]}>{c.stage}</Status>
          {interviewSoon && <span className={cn('inline-flex items-center gap-1 text-xs font-semibold', c.interviewOn! < todayIso() ? 'text-bad' : 'text-warn')}><CalendarDays className="h-3.5 w-3.5" />{day(c.interviewOn)}</span>}
        </span>
        <span className="block truncate text-sm text-muted">
          {showOpening && `${c.opening} · `}{c.phone ? prettyPhone(c.phone) : c.email}{c.source && ` · via ${c.source}`} · applied {day(c.createdAt)}
        </span>
        {err && <span className="mt-1 block text-xs text-bad">{err}</span>}
      </button>
      <Contact c={c} />
      {next && (quick ? (
        <button className="btn btn-ghost hidden py-1.5 text-sm sm:inline-flex" disabled={busy} onClick={move}>{busy ? <Loader2 className="h-4 w-4 animate-spin" /> : next[1]}</button>
      ) : (
        <button className="btn btn-ghost hidden py-1.5 text-sm sm:inline-flex" onClick={onOpen}>{next[1]}</button>
      ))}
      <ChevronRight className="h-4 w-4 shrink-0 text-faint sm:hidden" />
    </li>
  );
}

function CandidateSheet({ c, onMoved }: { c: Candidate; onMoved: (c: Candidate) => void }) {
  const [target, setTarget] = useState<Stage | null>(null);
  const [f, setF] = useState({ interviewOn: c.interviewOn ?? '', reason: '', notes: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async (stage: Stage) => {
    setState({ busy: true, error: '' });
    try {
      const r = await api<any>(`/recruitment/candidates/${c.id}/stage`, { body: { stage, interviewOn: f.interviewOn || null, reason: f.reason, notes: f.notes } });
      setTarget(null);
      setF({ interviewOn: r.interviewOn ?? '', reason: '', notes: '' });
      setState({ busy: false, error: '' });
      onMoved({ ...c, stage: r.stage, interviewOn: r.interviewOn, notes: r.notes, rejectReason: r.rejectReason });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const needs = target === 'Interview' || target === 'Rejected';
  return (
    <div className="space-y-5">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="text-sm text-muted">{c.opening}</p>
          <div className="mt-1 flex items-center gap-2"><Status tone={TONE[c.stage]}>{c.stage}</Status>{c.interviewOn && <span className="text-sm text-muted">Interview {day(c.interviewOn)}</span>}</div>
        </div>
        <Contact c={c} />
      </div>
      <dl className="grid grid-cols-2 gap-3 text-sm">
        <div><dt className="text-xs text-muted">Phone</dt><dd>{c.phone ? prettyPhone(c.phone) : '—'}</dd></div>
        <div><dt className="text-xs text-muted">Email</dt><dd className="truncate">{c.email || '—'}</dd></div>
        <div><dt className="text-xs text-muted">Heard about it</dt><dd>{c.source || '—'}</dd></div>
        <div><dt className="text-xs text-muted">Applied</dt><dd>{day(c.createdAt)}</dd></div>
      </dl>
      {c.rejectReason && <p className="rounded-md bg-bad-soft p-3 text-sm text-bad">Turned down: {c.rejectReason}</p>}
      {c.notes && <p className="rounded-md bg-paper p-3 text-sm whitespace-pre-line ring-1 ring-line">{c.notes}</p>}

      <section>
        <h3 className="mb-2 text-sm font-semibold">Move to</h3>
        <div className="flex flex-wrap gap-1.5">
          {STAGES.filter((s) => s !== c.stage).map((s) => (
            <button key={s} type="button" onClick={() => (s === 'Interview' || s === 'Rejected' ? setTarget(s) : save(s))} disabled={state.busy} className={cn('rounded-full px-3 py-1.5 text-sm font-semibold ring-1', target === s ? 'bg-brand-soft text-brand ring-brand/40' : s === 'Rejected' ? 'bg-white text-bad ring-line hover:ring-bad' : 'bg-white ring-line hover:ring-faint')}>
              {s}
            </button>
          ))}
        </div>
        {needs && (
          <div className="mt-3 space-y-3 rounded-md bg-paper p-3 ring-1 ring-line">
            {target === 'Interview' ? (
              <Field label="Interview date"><input type="date" className="field" min={todayIso()} required value={f.interviewOn} onChange={(e) => setF({ ...f, interviewOn: e.target.value })} /></Field>
            ) : (
              <Field label="Why not" hint="Kept so you can see why people don’t make it."><input className="field" required value={f.reason} onChange={(e) => setF({ ...f, reason: e.target.value })} placeholder="e.g. no driving licence" /></Field>
            )}
            <button className="btn btn-primary" disabled={state.busy} onClick={() => save(target!)}>{state.busy && <Loader2 className="h-4 w-4 animate-spin" />} {target === 'Interview' ? 'Invite to interview' : 'Turn down'}</button>
          </div>
        )}
      </section>

      <Field label="Add a note">
        <textarea className="field min-h-20" value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} placeholder="What you learnt from the call or interview" />
      </Field>
      {f.notes.trim() && <button className="btn btn-ghost" disabled={state.busy} onClick={() => save(c.stage)}>Save note</button>}
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      {c.stage === 'Hired' && <p className="rounded-md bg-good-soft p-3 text-sm text-good">Hired. Add them in <Link className="font-semibold underline" to="/people">Employees</Link> to put them on payroll and give them the staff portal.</p>}
    </div>
  );
}

function OpeningForm({ opening, types, onSaved }: { opening: Opening | null; types: string[]; onSaved: (id?: string) => void }) {
  const [f, setF] = useState({ title: opening?.title ?? '', department: opening?.department ?? '', location: opening?.location ?? '', type: opening?.type ?? 'Full-time', positions: String(opening?.positions ?? 1), description: opening?.description ?? '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async (open?: boolean) => {
    setState({ busy: true, error: '' });
    try {
      const body = { ...f, positions: Number(f.positions) || 1, open };
      const r = opening ? await api<any>(`/recruitment/openings/${opening.id}`, { method: 'PUT', body }) : await api<any>('/recruitment/openings', { body });
      onSaved(r.id);
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <form onSubmit={(e) => (e.preventDefault(), save())} className="space-y-4">
      <Field label="Job title"><input className="field" required value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} placeholder="e.g. Delivery driver" /></Field>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Department"><input className="field" value={f.department} onChange={(e) => setF({ ...f, department: e.target.value })} placeholder="e.g. Logistics" /></Field>
        <Field label="Where"><input className="field" value={f.location} onChange={(e) => setF({ ...f, location: e.target.value })} placeholder="e.g. Kisumu" /></Field>
      </div>
      <Chips label="Type" value={f.type} onChange={(v) => setF({ ...f, type: v })} options={types.map((t) => [t, t] as [string, string])} />
      <Field label="How many people" hint="The opening shows as filled once this many are hired."><input className="field num w-28" inputMode="numeric" value={f.positions} onChange={(e) => setF({ ...f, positions: e.target.value.replace(/\D/g, '') })} /></Field>
      <Field label="What the job is (optional)"><textarea className="field min-h-28" value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} placeholder="Duties, hours, what you need (a licence, experience)" /></Field>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <div className="flex flex-wrap gap-2">
        <button className="btn btn-primary flex-1 py-3" disabled={state.busy}>{state.busy && <Loader2 className="h-4 w-4 animate-spin" />} {opening ? 'Save' : 'Open this job'}</button>
        {opening && <button type="button" className="btn btn-ghost py-3" disabled={state.busy} onClick={() => save(!opening.open)}>{opening.open ? 'Close it' : 'Open it again'}</button>}
      </div>
    </form>
  );
}

function CandidateForm({ openings, openingId, onSaved }: { openings: Opening[]; openingId?: string; onSaved: () => void }) {
  const [f, setF] = useState({ openingId: openingId ?? '', name: '', phone: '', email: '', source: '', notes: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      await api('/recruitment/candidates', { body: f });
      onSaved();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  return (
    <form onSubmit={submit} className="space-y-4">
      <Field label="Applying for">
        <select className="field" required value={f.openingId} onChange={(e) => setF({ ...f, openingId: e.target.value })}>
          {openings.map((o) => <option key={o.id} value={o.id}>{o.title}</option>)}
        </select>
      </Field>
      <Field label="Name"><input className="field" required value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} /></Field>
      <div className="grid grid-cols-2 gap-3">
        <Field label="Phone"><input className="field" inputMode="tel" value={f.phone} onChange={(e) => setF({ ...f, phone: e.target.value })} placeholder="0712 345 678" /></Field>
        <Field label="Email"><input className="field" type="email" value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} /></Field>
      </div>
      <Chips label="How they heard" value={f.source} onChange={(v) => setF({ ...f, source: v })} options={[['Referral', 'Referral'], ['Walk-in', 'Walk-in'], ['WhatsApp', 'WhatsApp'], ['Job board', 'Job board'], ['Other', 'Other']]} />
      <Field label="Notes (optional)"><textarea className="field min-h-20" value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} placeholder="Experience, availability, salary asked" /></Field>
      <p className="text-xs text-muted">Applicants’ details are personal data: they stay inside your business and only people who look after staff can see them.</p>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button className="btn btn-primary" disabled={state.busy}>{state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Add {f.name.split(' ')[0] || 'applicant'}</button>
      </SheetFooter>
    </form>
  );
}
