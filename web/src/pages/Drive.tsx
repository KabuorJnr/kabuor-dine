// The driver's link (/d/:token), opened from WhatsApp on the driver's phone. No login. The driver is deciding "where
// do I go next, and how do I prove I delivered?", so the screen leads with sharing (on or off, and when it last
// reached the office), then the drops in order: who, where, what's on board, with Call, Directions and Delivered.
// Location goes to the office only while the trip is on the road and sharing is on; the screen says so plainly.
import { useCallback, useEffect, useRef, useState } from 'react';
import { useParams } from 'react-router';
import { Check, Loader2, MapPin, Navigation, Package, Phone, Radio, Truck, X } from 'lucide-react';
import { cn, prettyPhone } from '../lib/format';
import { ErrorNote, Field, Loading, Sheet } from '../components/ui';

type DropStatus = 'Pending' | 'Dispatched' | 'Delivered' | 'PartialReturn' | 'Failed';
type Drop = {
  id: string; waybillNumber: string; customerName: string; deliveryAddress: string; contactPhone: string; status: DropStatus;
  recipientName: string; podConfirmedAt: string | null; items: { description: string; quantity: number; unit: string }[];
};
type View = {
  business: string; officePhone: string; tripNumber: string; routeName: string; vehiclePlate: string; driverName: string;
  status: 'Draft' | 'Dispatched' | 'Delivered' | 'Completed' | 'Cancelled'; sharing: boolean; lastPingAt: string | null; drops: Drop[];
};

/** The driver's link talks to the API with its own key (the token), never a staff sign-in. */
async function drive<T>(token: string, path = '', body?: unknown): Promise<T> {
  const res = await fetch(`/api/drive/${token}${path}`, {
    method: body === undefined ? 'GET' : 'POST',
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw Object.assign(new Error(data.error ?? 'No connection. It will try again.'), { status: res.status });
  return data as T;
}

const open = (s: DropStatus) => s === 'Pending' || s === 'Dispatched';
const metres = (a: GeolocationCoordinates, b: GeolocationCoordinates) => {
  const r = (d: number) => (d * Math.PI) / 180;
  const h = Math.sin(r(b.latitude - a.latitude) / 2) ** 2 + Math.cos(r(a.latitude)) * Math.cos(r(b.latitude)) * Math.sin(r(b.longitude - a.longitude) / 2) ** 2;
  return 2 * 6_371_000 * Math.asin(Math.sqrt(h));
};
const since = (t: number | null, now: number) => (t == null ? '' : now - t < 60_000 ? `${Math.max(1, Math.round((now - t) / 1000))} s ago` : `${Math.round((now - t) / 60_000)} min ago`);

type Share = 'off' | 'starting' | 'on' | 'denied' | 'unavailable' | 'stopped-by-trip';

/** Sends the phone's location while sharing is on: every 15 s or after 75 m, and a heartbeat each minute so the
 * office can tell a parked van from a lost signal. Keeps the screen awake where the phone allows it, because a
 * browser can't send location once the screen is off or the link is closed. */
function useSharing(token: string, active: boolean, onEnded: () => void) {
  const key = `kerp-drive-${token}`;
  const [want, setWant] = useState(() => {
    try {
      return localStorage.getItem(key) === 'on';
    } catch {
      return false;
    }
  });
  const [state, setState] = useState<Share>('off');
  const [sentAt, setSentAt] = useState<number | null>(null);
  const [problem, setProblem] = useState('');
  const last = useRef<{ coords: GeolocationCoordinates; sent: number } | null>(null);
  const latest = useRef<GeolocationCoordinates | null>(null);

  const remember = (on: boolean) => {
    setWant(on);
    try {
      localStorage.setItem(key, on ? 'on' : 'off');
    } catch {}
  };

  const send = useCallback(
    async (c: GeolocationCoordinates) => {
      try {
        await drive(token, '/ping', { lat: c.latitude, lng: c.longitude, accuracy: c.accuracy, speed: c.speed, heading: c.heading });
        last.current = { coords: c, sent: Date.now() };
        setSentAt(Date.now());
        setProblem('');
      } catch (e) {
        if ((e as { status?: number }).status === 409) {
          setState('stopped-by-trip');
          onEnded();
        } else setProblem((e as Error).message);
      }
    },
    [token, onEnded],
  );

  useEffect(() => {
    if (!want || !active) {
      setState((s) => (s === 'denied' || s === 'unavailable' ? s : 'off'));
      return;
    }
    if (!('geolocation' in navigator)) {
      setState('unavailable');
      return;
    }
    setState('starting');
    const watch = navigator.geolocation.watchPosition(
      (p) => {
        setState('on');
        latest.current = p.coords;
        const l = last.current;
        if (!l || Date.now() - l.sent >= 15_000 || metres(l.coords, p.coords) >= 75) send(p.coords);
      },
      (err) => {
        if (err.code === err.PERMISSION_DENIED) {
          setState('denied');
          remember(false);
        } else setProblem('Looking for GPS signal…');
      },
      { enableHighAccuracy: true, maximumAge: 10_000, timeout: 30_000 },
    );
    const beat = setInterval(() => latest.current && last.current && Date.now() - last.current.sent >= 60_000 && send(latest.current), 20_000);
    // Keep the screen on while sharing (Android Chrome, recent iPhones); ask again when the driver comes back to it.
    let lock: { release: () => Promise<void> } | null = null;
    const wake = async () => {
      try {
        lock = await (navigator as unknown as { wakeLock?: { request: (t: 'screen') => Promise<{ release: () => Promise<void> }> } }).wakeLock?.request('screen') ?? null;
      } catch {}
    };
    const onVisible = () => document.visibilityState === 'visible' && wake();
    wake();
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      navigator.geolocation.clearWatch(watch);
      clearInterval(beat);
      document.removeEventListener('visibilitychange', onVisible);
      lock?.release().catch(() => {});
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [want, active, send]);

  return { state, want, sentAt, problem, start: () => remember(true), stop: () => remember(false), position: () => latest.current };
}

function Confirm({ token, drop, position, onClose, onDone }: { token: string; drop: Drop; position: () => GeolocationCoordinates | null; onClose: () => void; onDone: () => void }) {
  const [status, setStatus] = useState<'Delivered' | 'PartialReturn' | 'Failed'>('Delivered');
  const [name, setName] = useState('');
  const [notes, setNotes] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    const c = position();
    try {
      await drive(token, `/drops/${drop.id}`, { recipientName: name, podNotes: notes, status, lat: c?.latitude, lng: c?.longitude });
      onDone();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title={drop.customerName}>
      <form onSubmit={save} className="space-y-4">
        <div className="grid grid-cols-3 gap-2" role="group" aria-label="What happened">
          {([['Delivered', 'Delivered', 'good'], ['PartialReturn', 'Part returned', 'warn'], ['Failed', 'Not delivered', 'bad']] as const).map(([v, l, tone]) => (
            <button key={v} type="button" aria-pressed={status === v} onClick={() => setStatus(v)}
              className={cn('rounded-md px-2 py-3 text-sm font-semibold ring-1', status === v ? (tone === 'good' ? 'bg-good text-white ring-good' : tone === 'warn' ? 'bg-warn text-white ring-warn' : 'bg-bad text-white ring-bad') : 'bg-white ring-line')}>
              {l}
            </button>
          ))}
        </div>
        <Field label={status === 'Failed' ? 'Who you spoke to (if anyone)' : 'Received by'}>
          <input className="field" value={name} onChange={(e) => setName(e.target.value)} placeholder="Their name" autoComplete="off" required={status !== 'Failed'} />
        </Field>
        <Field label={status === 'Delivered' ? 'Notes (optional)' : status === 'Failed' ? 'Why it couldn’t be delivered' : 'What came back, and why'}>
          <textarea className="field min-h-20" value={notes} onChange={(e) => setNotes(e.target.value)} required={status !== 'Delivered'} />
        </Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <button className="btn btn-primary w-full py-3.5 text-base" disabled={state.busy}>
          {state.busy ? <Loader2 className="h-5 w-5 animate-spin" /> : <Check className="h-5 w-5" />} Confirm
        </button>
        {position() && <p className="text-center text-xs text-muted">Your location is saved with it, as proof you were there.</p>}
      </form>
    </Sheet>
  );
}

export default function Drive() {
  const { token = '' } = useParams();
  const [view, setView] = useState<View | null>(null);
  const [error, setError] = useState('');
  const [confirming, setConfirming] = useState<Drop | null>(null);
  const [now, setNow] = useState(Date.now());
  const load = useCallback(() => drive<View>(token).then(setView, (e) => setError((e as Error).message)), [token]);
  useEffect(() => {
    load();
    const t = setInterval(() => setNow(Date.now()), 5_000);
    const refresh = setInterval(load, 60_000); // the office may send the trip out or change it meanwhile
    return () => (clearInterval(t), clearInterval(refresh));
  }, [load]);
  const onRoad = view?.status === 'Dispatched' || view?.status === 'Delivered';
  const share = useSharing(token, Boolean(onRoad), load);

  useEffect(() => {
    document.title = view ? `${view.tripNumber} · ${view.business}` : 'Driver';
  }, [view]);

  if (error && !view) return <div className="mx-auto max-w-md p-4 pt-16"><ErrorNote>{error}</ErrorNote></div>;
  if (!view) return <Loading className="pt-24" />;
  const done = view.drops.filter((d) => !open(d.status)).length;
  const next = view.drops.find((d) => open(d.status));

  return (
    <div className="min-h-svh bg-paper pb-10">
      <header className="bg-ink px-4 pt-[max(env(safe-area-inset-top),1rem)] pb-5 text-white">
        <div className="mx-auto max-w-md">
          <p className="text-xs font-semibold tracking-wide text-white/60 uppercase">{view.business}</p>
          <h1 className="mt-1 font-display text-2xl font-semibold">{view.routeName || view.tripNumber}</h1>
          <p className="mt-1 flex flex-wrap items-center gap-x-2 text-sm text-white/75">
            <Truck className="h-4 w-4" /> {view.vehiclePlate} · {view.tripNumber} · {done} of {view.drops.length} done
          </p>
        </div>
      </header>

      <main className="mx-auto max-w-md space-y-4 px-4">
        {/* Sharing: the one thing that must be on, so it sits first and says what the office sees. */}
        <section className="-mt-3 rounded-lg bg-white p-4 shadow-sm ring-1 ring-line">
          {!onRoad ? (
            <p className="flex items-start gap-3 text-sm">
              <Radio className="mt-0.5 h-5 w-5 shrink-0 text-faint" />
              {view.status === 'Draft' ? (
                <span><b className="block">Waiting for the office to send this trip out</b><span className="text-muted">Your location isn’t shared until it does. Load up and check the drops below.</span></span>
              ) : (
                <span><b className="block">This trip has ended</b><span className="text-muted">Your location is no longer shared. Thank you.</span></span>
              )}
            </p>
          ) : share.state === 'on' ? (
            <div className="flex items-center gap-3">
              <span className="relative flex h-3.5 w-3.5 shrink-0"><span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-good opacity-60" /><span className="relative inline-flex h-3.5 w-3.5 rounded-full bg-good" /></span>
              <div className="min-w-0 flex-1 text-sm">
                <b className="block">Sharing your location with the office</b>
                <span className="text-muted">{share.sentAt ? `Last sent ${since(share.sentAt, now)}` : 'Sending…'}{share.problem && ` · ${share.problem}`}</span>
              </div>
              <button className="btn btn-ghost px-3 py-2" onClick={share.stop}>Stop</button>
            </div>
          ) : share.state === 'denied' ? (
            <div className="space-y-2 text-sm">
              <b className="block text-bad">Location is blocked for this page</b>
              <p className="text-muted">Tap the lock or ⓘ beside the address bar, allow Location, then tap Start again.</p>
              <button className="btn btn-primary w-full py-3" onClick={share.start}>Start sharing</button>
            </div>
          ) : share.state === 'unavailable' ? (
            <p className="text-sm text-bad">This phone’s browser can’t share location. Open the link in Chrome.</p>
          ) : (
            <div className="space-y-3">
              <p className="text-sm text-muted">The office sees where the vehicle is while this trip is out, so they can answer customers without calling you. Only while this page is open and the trip is on the road.</p>
              <button className="btn btn-primary w-full py-3.5 text-base" onClick={share.start} disabled={share.state === 'starting'}>
                {share.state === 'starting' ? <Loader2 className="h-5 w-5 animate-spin" /> : <Navigation className="h-5 w-5" />} Start sharing my location
              </button>
            </div>
          )}
        </section>
        {onRoad && share.state === 'on' && <p className="px-1 text-xs text-muted">Keep this page open with the screen on. If the phone locks, sharing pauses until you open it again.</p>}

        <h2 className="px-1 pt-2 text-sm font-semibold text-muted">Drops · {done} of {view.drops.length} done</h2>
        <ol className="space-y-3">
          {view.drops.map((d, i) => {
            const isNext = d.id === next?.id;
            return (
              <li key={d.id} className={cn('rounded-lg bg-white p-4 ring-1', isNext ? 'ring-2 ring-brand' : 'ring-line', !open(d.status) && 'opacity-75')}>
                <div className="flex items-start gap-3">
                  <span className={cn('grid h-8 w-8 shrink-0 place-items-center rounded-full text-sm font-bold', !open(d.status) ? (d.status === 'Failed' ? 'bg-bad-soft text-bad' : 'bg-good-soft text-good') : isNext ? 'bg-brand text-white' : 'bg-paper text-ink ring-1 ring-line')}>
                    {!open(d.status) ? (d.status === 'Failed' ? <X className="h-4 w-4" /> : <Check className="h-4 w-4" />) : i + 1}
                  </span>
                  <div className="min-w-0 flex-1">
                    {isNext && <p className="text-2xs font-semibold tracking-wide text-brand uppercase">Next</p>}
                    <p className="font-semibold">{d.customerName}</p>
                    {d.deliveryAddress && <p className="mt-0.5 flex items-start gap-1 text-sm text-ink-2"><MapPin className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted" />{d.deliveryAddress}</p>}
                    {d.items.length > 0 && (
                      <p className="mt-1 flex items-start gap-1 text-sm text-muted">
                        <Package className="mt-0.5 h-3.5 w-3.5 shrink-0" />
                        {d.items.map((l) => `${l.quantity}${l.unit ? ` ${l.unit}` : ''} × ${l.description}`).join(', ')}
                      </p>
                    )}
                    <p className="mt-1 text-xs text-faint">{d.waybillNumber}</p>
                    {!open(d.status) && (
                      <p className="mt-1 text-sm font-semibold text-good">
                        {d.status === 'Failed' ? <span className="text-bad">Not delivered</span> : d.status === 'PartialReturn' ? 'Part returned' : 'Delivered'}
                        {d.recipientName && ` · ${d.recipientName}`}
                      </p>
                    )}
                  </div>
                </div>
                {open(d.status) && (
                  <div className="mt-3 grid grid-cols-3 gap-2">
                    {d.contactPhone ? (
                      <a className="btn btn-ghost px-2" href={`tel:${d.contactPhone}`} aria-label={`Call ${d.customerName} on ${prettyPhone(d.contactPhone)}`}><Phone className="h-4 w-4 shrink-0" /> Call</a>
                    ) : <span />}
                    {d.deliveryAddress ? (
                      <a className="btn btn-ghost px-2" target="_blank" rel="noreferrer" href={`https://www.google.com/maps/dir/?api=1&destination=${encodeURIComponent(`${d.deliveryAddress}, Kenya`)}`}><Navigation className="h-4 w-4 shrink-0" /> Directions</a>
                    ) : <span />}
                    <button className="btn btn-primary px-2" disabled={!onRoad} onClick={() => setConfirming(d)}><Check className="h-4 w-4 shrink-0" /> Done</button>
                  </div>
                )}
              </li>
            );
          })}
        </ol>
        {view.officePhone && (
          <a href={`tel:${view.officePhone}`} className="btn btn-ghost w-full"><Phone className="h-4 w-4" /> Call the office · {prettyPhone(view.officePhone)}</a>
        )}
      </main>
      {confirming && <Confirm token={token} drop={confirming} position={share.position} onClose={() => setConfirming(null)} onDone={() => (setConfirming(null), load())} />}
    </div>
  );
}
