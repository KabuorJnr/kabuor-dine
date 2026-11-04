// The kitchen and bar screens: a tablet on the pass, read from across the room with wet hands. Each station sees
// only its own dishes, oldest ticket first, with how long it has waited (amber after 10 minutes, red after 20). One
// tap starts a ticket, one tap marks this station's part done; when every station is done the floor is told it's
// ready to carry out. A chime sounds for every new ticket.
import { useEffect, useRef, useState } from 'react';
import { Check, ChefHat, Expand, Flame, Shrink, Volume2, VolumeX } from 'lucide-react';
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
  const active = data?.orders.filter((o) => o.status !== 'Ready' && !o.stationReady) ?? [];
  const done = data?.orders.filter((o) => o.status === 'Ready' || o.stationReady) ?? [];

  return (
    <div className="min-h-[calc(100vh-52px)] px-3 py-4 text-white sm:px-5">
      <div className="mb-4 flex flex-wrap items-center gap-2">
        <h1 className="mr-2 flex items-center gap-2 text-xl font-bold"><ChefHat className="h-5 w-5 text-[#D6B394]" /> {data?.station ?? 'All stations'}</h1>
        <div className="no-scrollbar flex gap-1 overflow-x-auto rounded-lg bg-white/8 p-1" role="tablist" aria-label="Station">
          {['', ...(data?.stations ?? [])].map((s) => (
            <button key={s || 'all'} role="tab" aria-selected={station === s} onClick={() => choose(s)}
              className={cn('shrink-0 rounded-md px-3.5 py-2 text-sm font-semibold', station === s ? 'bg-dine text-white' : 'text-white/70 hover:text-white')}>{s || 'All'}</button>
          ))}
        </div>
        <span className="ml-auto rounded-full bg-white/10 px-3 py-1 text-sm font-semibold">{active.length} to make</span>
        <button onClick={() => setSound(!sound)} aria-label={sound ? 'Mute the chime' : 'Turn the chime on'} className="grid h-10 w-10 place-items-center rounded-md bg-white/8 hover:bg-white/15">
          {sound ? <Volume2 className="h-5 w-5" /> : <VolumeX className="h-5 w-5 text-white/50" />}
        </button>
        <button onClick={() => (full ? document.exitFullscreen() : document.documentElement.requestFullscreen()).catch(() => {})} aria-label={full ? 'Leave full screen' : 'Full screen'} className="grid h-10 w-10 place-items-center rounded-md bg-white/8 hover:bg-white/15">
          {full ? <Shrink className="h-5 w-5" /> : <Expand className="h-5 w-5" />}
        </button>
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

      <div className="grid grid-cols-[repeat(auto-fill,minmax(17rem,1fr))] items-start gap-3">
        {active.map((t) => {
          const m = minutes(t);
          const late = m >= 20 ? 'bad' : m >= 10 ? 'warn' : 'ok';
          return (
            <article key={t.id} className={cn('overflow-hidden rounded-xl bg-white text-ink shadow-lg ring-4', late === 'bad' ? 'ring-red-500' : late === 'warn' ? 'ring-amber-400' : t.status === 'New' ? 'ring-[#C29772]' : 'ring-transparent')}>
              <header className={cn('flex items-center justify-between gap-2 px-4 py-2.5', late === 'bad' ? 'bg-red-600 text-white' : late === 'warn' ? 'bg-amber-400' : 'bg-dine-soft')}>
                <span className="text-xl font-extrabold">{t.table}</span>
                <span className="text-right text-sm">
                  <span className="block font-bold">{m} min</span>
                  <span className="block text-xs opacity-80">{t.number}{t.guestName ? ` · ${t.guestName}` : ''}</span>
                </span>
              </header>
              <ul className="divide-y divide-line">
                {t.lines.map((l) => (
                  <li key={l.id} className={cn('px-4 py-2.5', l.ready && 'opacity-40 line-through')}>
                    <p className="text-lg leading-snug font-bold"><span className="mr-1.5 text-dine">{l.quantity}×</span>{l.name}</p>
                    {l.options && <p className="text-sm font-medium text-ink-2">{l.options}</p>}
                    {l.note && <p className="mt-0.5 rounded bg-amber-100 px-2 py-0.5 text-sm font-semibold text-amber-900">⚠ {l.note}</p>}
                    {!station && <p className="text-xs text-faint">{l.station}</p>}
                  </li>
                ))}
              </ul>
              {t.note && <p className="border-t border-line bg-amber-50 px-4 py-2 text-sm font-semibold text-amber-900">Order note: {t.note}</p>}
              {t.others.length > 0 && <p className="border-t border-line px-4 py-1.5 text-xs text-muted">Also: {t.others.map((o) => `${o.station}${o.ready ? ' ✓' : ''}`).join(' · ')}</p>}
              <footer className="grid grid-cols-2 gap-2 border-t border-line p-2.5">
                {t.status === 'New' ? (
                  <button className="btn btn-ghost py-3" disabled={busy === t.id} onClick={() => act(t.id, () => api(`/dine/orders/${t.id}/status`, { body: { status: 'Preparing' } }))}><Flame className="h-4 w-4" /> Start</button>
                ) : <span className="grid place-items-center text-sm font-semibold text-dine">Preparing</span>}
                <button className="btn bg-good py-3 text-white hover:brightness-110" disabled={busy === t.id}
                  onClick={() => act(t.id, () => station
                    ? api(`/dine/orders/${t.id}/station-ready`, { body: { station } })
                    : api(`/dine/orders/${t.id}/status`, { body: { status: 'Ready' } }))}>
                  <Check className="h-4 w-4" /> {station ? 'Done' : 'Ready'}
                </button>
              </footer>
            </article>
          );
        })}
      </div>

      {done.length > 0 && (
        <section className="mt-8">
          <h2 className="mb-2 text-sm font-semibold tracking-wider text-white/50 uppercase">Done, waiting to be carried out</h2>
          <div className="flex flex-wrap gap-2">
            {done.map((t) => (
              <span key={t.id} className="rounded-lg bg-white/10 px-3 py-2 text-sm">
                <span className="font-bold">{t.table}</span> · {t.number} {t.status === 'Ready' ? <span className="text-green-300">ready</span> : <span className="text-white/60">waiting on {t.others.filter((o) => !o.ready).map((o) => o.station).join(', ')}</span>}
              </span>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
