// The kitchen and bar screens: a tablet on the pass, read from across the room with wet hands. Each station sees
// only its own dishes, oldest ticket first, with how long it has waited (amber after 10 minutes, red after 20). One
// tap starts a ticket, one tap marks this station's part done; when every station is done the floor is told it's
// ready to carry out. A chime sounds for every new ticket.
import { useEffect, useRef, useState } from 'react';
import { Check, ChefHat, Expand, Flame, Hourglass, Shrink, Timer, Volume2, VolumeX } from 'lucide-react';
import { api } from '../lib/api';
import { cn } from '../lib/format';
import { chime, usePoll } from './lib';

type Ticket = {
  id: string; number: string; table: string; status: 'New' | 'Preparing' | 'Ready'; guestName: string; note: string; sentAt: string; minutes: number;
  stationReady: boolean; lines: { id: string; name: string; quantity: number; options: string; note: string; station: string; ready: boolean }[];
  others: { station: string; ready: boolean }[];
};
type Board = { stations: string[]; station: string | null; orders: Ticket[]; waiting: number };

const STATION_KEY = 'kdine-station';

export default function Kitchen() {
  const [station, setStation] = useState<string>(() => { try { return localStorage.getItem(STATION_KEY) ?? ''; } catch { return ''; } });
  const { data, error, reload } = usePoll<Board>(`/dine/kitchen${station ? `?station=${encodeURIComponent(station)}` : ''}`, 3000);
  const [sound, setSound] = useState(true);
  const [full, setFull] = useState(false);
  const [busy, setBusy] = useState('');
  const [problem, setProblem] = useState('');
  const seen = useRef<Set<string> | null>(null);
  const [, tick] = useState(0);

  // A chime for each ticket the screen hasn't seen before (not for the ones already up when it opened).
  useEffect(() => {
    if (!data) return;
    const ids = new Set(data.orders.map((o) => o.id));
    if (seen.current && sound && data.orders.some((o) => !seen.current!.has(o.id) && o.status === 'New')) chime();
    seen.current = ids;
  }, [data, sound]);
  // The waiting times move on between loads.
  useEffect(() => { const id = window.setInterval(() => tick((n) => n + 1), 30_000); return () => window.clearInterval(id); }, []);
  useEffect(() => {
    const on = () => setFull(Boolean(document.fullscreenElement));
    document.addEventListener('fullscreenchange', on);
    return () => document.removeEventListener('fullscreenchange', on);
  }, []);

  const choose = (s: string) => { setStation(s); seen.current = null; try { localStorage.setItem(STATION_KEY, s); } catch { /* fine */ } };
  const act = async (id: string, fn: () => Promise<unknown>) => {
    setBusy(id); setProblem('');
    try { await fn(); await reload(); } catch (e) { setProblem((e as Error).message); }
    setBusy('');
  };

  const minutes = (t: Ticket) => Math.max(0, Math.floor((Date.now() - Date.parse(t.sentAt)) / 60000));
  const controls = (
    <>
      <button onClick={() => setSound(!sound)} aria-label={sound ? 'Mute the chime' : 'Turn the chime on'} title={sound ? 'Chime on' : 'Chime off'} className="grid h-10 w-10 place-items-center rounded-lg bg-white/8 hover:bg-white/15">
        {sound ? <Volume2 className="h-5 w-5" /> : <VolumeX className="h-5 w-5 text-white/50" />}
      </button>
      <button onClick={() => (full ? document.exitFullscreen() : document.documentElement.requestFullscreen()).catch(() => {})} aria-label={full ? 'Leave full screen' : 'Full screen'} title={full ? 'Leave full screen' : 'Full screen'} className="grid h-10 w-10 place-items-center rounded-lg bg-white/8 hover:bg-white/15">
        {full ? <Shrink className="h-5 w-5" /> : <Expand className="h-5 w-5" />}
      </button>
    </>
  );
  const active = data?.orders.filter((o) => o.status !== 'Ready' && !o.stationReady) ?? [];
  const done = data?.orders.filter((o) => o.status === 'Ready' || o.stationReady) ?? [];

  return (
    <div className="min-h-[calc(100vh-52px)] px-3 py-4 text-white sm:px-5">
      <div className="mb-4 flex flex-col gap-3 sm:flex-row sm:items-center">
        <div className="flex items-center gap-2.5">
          <h1 className="flex min-w-0 items-center gap-2 text-xl font-bold sm:text-2xl"><ChefHat className="h-6 w-6 shrink-0 text-[#D6B394]" /> <span className="truncate">{data?.station ?? 'All stations'}</span></h1>
          <span className="shrink-0 rounded-full bg-white/10 px-3 py-1 text-sm font-semibold tabular-nums">{active.length} to make</span>
          <span className="ml-auto flex gap-2 sm:hidden">{controls}</span>
        </div>
        <div className="no-scrollbar flex gap-1 overflow-x-auto rounded-xl bg-white/8 p-1 sm:ml-3" role="tablist" aria-label="Station">
          {['', ...(data?.stations ?? [])].map((s) => (
            <button key={s || 'all'} role="tab" aria-selected={station === s} onClick={() => choose(s)}
              className={cn('flex-1 shrink-0 rounded-lg px-4 py-2 text-sm font-semibold transition sm:flex-none', station === s ? 'bg-dine text-white shadow' : 'text-white/70 hover:text-white')}>{s || 'All'}</button>
          ))}
        </div>
        <span className="ml-auto hidden gap-2 sm:flex">{controls}</span>
      </div>

      {problem && <p className="mb-3 rounded-md bg-red-500/20 px-3 py-2 text-sm text-red-100">{problem}</p>}
      {error && !data && <p className="rounded-md bg-red-500/20 px-3 py-2 text-sm text-red-100">{error}</p>}

      {data && active.length === 0 && (
        <div className="grid place-items-center py-24 text-center text-white/50">
          <Check className="h-12 w-12" />
          <p className="mt-3 text-lg font-semibold">All caught up</p>
          <p className="text-sm">New orders appear here the moment a table sends them.</p>
        </div>
      )}

      <div className="grid grid-cols-1 items-start gap-3 sm:grid-cols-[repeat(auto-fill,minmax(19rem,1fr))] lg:gap-4 2xl:grid-cols-[repeat(auto-fill,minmax(22rem,1fr))]">
        {active.map((t) => {
          const m = minutes(t);
          const late = m >= 20 ? 'bad' : m >= 10 ? 'warn' : 'ok';
          return (
            <article key={t.id} className={cn('overflow-hidden rounded-2xl bg-white text-ink shadow-lg ring-4', late === 'bad' ? 'ring-red-500' : late === 'warn' ? 'ring-amber-400' : t.status === 'New' ? 'ring-[#C29772]' : 'ring-transparent')}>
              <header className={cn('flex items-start justify-between gap-3 px-4 py-3', late === 'bad' ? 'bg-red-600 text-white' : late === 'warn' ? 'bg-amber-400' : 'bg-dine-soft')}>
                <span className="min-w-0">
                  <span className="flex items-center gap-2">
                    <span className="truncate text-2xl leading-tight font-extrabold">{t.table}</span>
                    {t.status === 'New' && <span className="shrink-0 animate-pulse rounded-full bg-espresso px-2 py-0.5 text-[11px] font-bold tracking-wider text-white">NEW</span>}
                  </span>
                  <span className="mt-0.5 block truncate text-sm opacity-80">{t.number}{t.guestName ? ` · ${t.guestName}` : ''}</span>
                </span>
                <span className="shrink-0 text-right leading-none" aria-label={`Waiting ${m} minutes`}>
                  <span className="flex items-center justify-end gap-1 text-[28px] font-extrabold tabular-nums"><Timer className="h-5 w-5 opacity-70" />{m}</span>
                  <span className="text-xs font-semibold opacity-80">{m === 1 ? 'minute' : 'minutes'}</span>
                </span>
              </header>
              <ul className="divide-y divide-line">
                {t.lines.map((l) => (
                  <li key={l.id} className={cn('px-4 py-3', l.ready && 'opacity-40 line-through')}>
                    <p className="flex items-baseline gap-2 text-lg leading-snug font-bold lg:text-xl"><span className="shrink-0 rounded-md bg-dine-soft px-1.5 text-dine tabular-nums">{l.quantity}×</span><span className="min-w-0">{l.name}</span></p>
                    {l.options && <p className="mt-0.5 text-sm font-medium text-ink-2 lg:text-base">{l.options}</p>}
                    {l.note && <p className="mt-1 rounded-md bg-amber-100 px-2 py-1 text-sm font-semibold text-amber-900 lg:text-base">⚠ {l.note}</p>}
                    {!station && <p className="mt-0.5 text-xs font-semibold tracking-wide text-faint uppercase">{l.station}</p>}
                  </li>
                ))}
              </ul>
              {t.note && <p className="border-t border-line bg-amber-50 px-4 py-2 text-sm font-semibold text-amber-900">Order note: {t.note}</p>}
              {t.others.length > 0 && <p className="border-t border-line px-4 py-1.5 text-xs text-muted">Also: {t.others.map((o) => `${o.station}${o.ready ? ' ✓' : ''}`).join(' · ')}</p>}
              <footer className="grid grid-cols-2 gap-2 border-t border-line p-2.5">
                {t.status === 'New' ? (
                  <button className="btn btn-ghost py-3 text-base lg:py-4" disabled={busy === t.id} onClick={() => act(t.id, () => api(`/dine/orders/${t.id}/status`, { body: { status: 'Preparing' } }))}><Flame className="h-5 w-5" /> Start</button>
                ) : <span className="flex items-center justify-center gap-1.5 rounded-md bg-dine-soft text-sm font-semibold text-dine"><Hourglass className="h-4 w-4" /> Cooking</span>}
                <button className="btn bg-good py-3 text-base text-white hover:brightness-110 lg:py-4" disabled={busy === t.id}
                  onClick={() => act(t.id, () => station
                    ? api(`/dine/orders/${t.id}/station-ready`, { body: { station } })
                    : api(`/dine/orders/${t.id}/status`, { body: { status: 'Ready' } }))}>
                  <Check className="h-5 w-5" /> {station ? 'Done' : 'Ready'}
                </button>
              </footer>
            </article>
          );
        })}
      </div>

      {done.length > 0 && (
        <section className="mt-8">
          <h2 className="mb-3 text-sm font-semibold tracking-wider text-white/50 uppercase">Done, waiting to be carried out</h2>
          <div className="grid grid-cols-2 gap-2.5 sm:grid-cols-[repeat(auto-fill,minmax(13rem,1fr))]">
            {done.map((t) => (
              <div key={t.id} className={cn('rounded-xl px-4 py-3 ring-1', t.status === 'Ready' ? 'bg-green-500/15 ring-green-400/40' : 'bg-white/8 ring-white/10')}>
                <p className="flex items-center justify-between gap-2"><span className="truncate text-lg font-bold">{t.table}</span><span className="shrink-0 text-xs text-white/60">{t.number}</span></p>
                <p className={cn('mt-0.5 flex items-center gap-1.5 text-sm font-semibold', t.status === 'Ready' ? 'text-green-300' : 'text-white/60')}>
                  {t.status === 'Ready' ? <><Check className="h-4 w-4" /> Ready to carry out</> : <>Waiting on {t.others.filter((o) => !o.ready).map((o) => o.station).join(', ')}</>}
                </p>
              </div>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
