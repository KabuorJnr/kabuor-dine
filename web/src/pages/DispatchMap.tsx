// The dispatch office's live map. The dispatcher is deciding "is everything on the road going to plan, and who do
// I call?", so the map shows every trip that's out as a vehicle (moving, stopped, or gone quiet), the list beside it
// puts the ones that need a look first (no signal, then stopped longest), and choosing one shows its trail, the drops
// confirmed along it, the next customer, and the driver's number. It refreshes itself every 10 seconds.
import { useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import { ArrowLeft, MapPin, MessageCircle, Phone, Radio, Truck } from 'lucide-react';
import { useApi } from '../lib/api';
import { cn, dateTime, msisdn, prettyPhone } from '../lib/format';
import { ErrorNote, Loading } from '../components/ui';

type State = 'moving' | 'stopped' | 'quiet' | 'waiting';
type LiveTrip = {
  id: string; tripNumber: string; vehiclePlate: string; driverName: string; driverPhone: string; routeName: string; status: string; dispatchedAt: string | null;
  state: State; lastLat: number | null; lastLng: number | null; lastSpeed: number | null; lastHeading: number | null; lastAccuracy: number | null;
  lastPingAt: string | null; stoppedSince: string | null; linkSent: boolean; done: number; drops: number;
  next: { customerName: string; deliveryAddress: string; contactPhone: string; waybillNumber: string } | null;
  confirmed: { waybillNumber: string; customerName: string; status: string; lat: number; lng: number; podConfirmedAt: string }[];
  trail: [number, number][];
};
type Live = { counts: { onRoad: number; moving: number; stopped: number; quiet: number; waiting: number }; quietAfterMinutes: number; trips: LiveTrip[]; at: string };

const NAIROBI: L.LatLngTuple = [-1.2864, 36.8172];
const esc = (t: string) => t.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);
const mins = (iso: string | null, now: number) => (iso ? Math.max(0, Math.round((now - Date.parse(iso)) / 60_000)) : 0);
/** "8 min", "1 h 25 min", "2 days": how long, read at a glance. */
const span = (m: number) => (m < 60 ? `${m} min` : m < 48 * 60 ? `${Math.floor(m / 60)} h${m % 60 ? ` ${m % 60} min` : ''}` : `${Math.round(m / 1440)} days`);
const STATE: Record<State, { label: string; dot: string }> = {
  moving: { label: 'Moving', dot: 'bg-good' },
  stopped: { label: 'Stopped', dot: 'bg-warn' },
  quiet: { label: 'No signal', dot: 'bg-bad' },
  waiting: { label: 'Not sharing yet', dot: 'bg-faint' },
};

/** One line on how a trip is doing, in the words a dispatcher would say on the phone. */
function howItIs(t: LiveTrip, now: number) {
  if (t.state === 'moving') return `Moving · ${Math.round((t.lastSpeed ?? 0) * 3.6)} km/h`;
  if (t.state === 'stopped') return `Stopped ${span(mins(t.stoppedSince ?? t.lastPingAt, now))}`;
  if (t.state === 'quiet') return `No signal for ${span(mins(t.lastPingAt, now))}`;
  return t.linkSent ? 'Driver hasn’t opened the link' : 'Driver link not sent';
}

function vehicleIcon(t: LiveTrip, selected: boolean) {
  const arrow = t.state === 'moving' && t.lastHeading != null;
  return L.divIcon({
    className: '',
    iconSize: [0, 0],
    html: `<div class="veh veh-${t.state}${selected ? ' veh-on' : ''}"><span class="veh-dot">${
      arrow ? `<svg viewBox="0 0 24 24" style="transform:rotate(${Math.round(t.lastHeading!)}deg)"><path d="M12 3 19 20 12 16 5 20Z" fill="currentColor"/></svg>` : '<i></i>'
    }</span><span class="veh-label">${esc(t.vehiclePlate || t.tripNumber)}</span></div>`,
  });
}

function dropIcon(n: number, status: string) {
  return L.divIcon({ className: '', iconSize: [0, 0], html: `<div class="drop-pin drop-${status === 'Failed' ? 'bad' : status === 'PartialReturn' ? 'warn' : 'good'}">${n}</div>` });
}

export default function DispatchMap() {
  const { data, error, reload } = useApi<Live>('/dispatch/live');
  const [selected, setSelected] = useState<string | null>(null);
  const [filter, setFilter] = useState<'all' | State>('all');
  const [now, setNow] = useState(Date.now());
  const mapEl = useRef<HTMLDivElement>(null);
  const map = useRef<L.Map | null>(null);
  const vehicles = useRef(new Map<string, L.Marker>());
  const overlay = useRef<L.LayerGroup | null>(null);
  const fitted = useRef(false);

  // Live: the feed every 10 seconds, the "minutes ago" every 15.
  useEffect(() => {
    const feed = setInterval(reload, 10_000);
    const clock = setInterval(() => setNow(Date.now()), 15_000);
    return () => (clearInterval(feed), clearInterval(clock));
  }, [reload]);

  useEffect(() => {
    if (!mapEl.current || map.current) return;
    const m = L.map(mapEl.current, { zoomControl: false, attributionControl: true }).setView(NAIROBI, 11);
    L.control.zoom({ position: 'topright' }).addTo(m);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19, attribution: '© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>' }).addTo(m);
    overlay.current = L.layerGroup().addTo(m);
    map.current = m;
    return () => {
      m.remove();
      map.current = null;
      vehicles.current.clear();
      fitted.current = false;
    };
  }, []);

  const trips = useMemo(() => data?.trips ?? [], [data]);
  const placed = useMemo(() => trips.filter((t) => t.lastLat != null && t.lastLng != null), [trips]);
  const sel = trips.find((t) => t.id === selected) ?? null;

  // Vehicles: moved in place on each refresh, added and removed as trips go out and come back.
  useEffect(() => {
    const m = map.current;
    if (!m) return;
    const seen = new Set<string>();
    for (const t of placed) {
      seen.add(t.id);
      const at: L.LatLngTuple = [t.lastLat!, t.lastLng!];
      const existing = vehicles.current.get(t.id);
      if (existing) existing.setLatLng(at).setIcon(vehicleIcon(t, t.id === selected)).setZIndexOffset(t.id === selected ? 1000 : 0);
      else vehicles.current.set(t.id, L.marker(at, { icon: vehicleIcon(t, t.id === selected), keyboard: false }).on('click', () => setSelected(t.id)).addTo(m));
    }
    for (const [id, marker] of vehicles.current) if (!seen.has(id)) (marker.remove(), vehicles.current.delete(id));
    if (!fitted.current && placed.length) {
      fitted.current = true;
      if (placed.length === 1) m.setView([placed[0].lastLat!, placed[0].lastLng!], 14);
      // Clear of the office panel: down the left on wide screens, along the bottom on phones.
      else m.fitBounds(L.latLngBounds(placed.map((t) => [t.lastLat!, t.lastLng!] as L.LatLngTuple)), { paddingTopLeft: window.innerWidth >= 1024 ? [400, 60] : [40, 40], paddingBottomRight: window.innerWidth >= 1024 ? [60, 60] : [40, 300], maxZoom: 14 });
    }
  }, [placed, selected]);

  // The chosen trip: its trail, how sure the phone is of its spot, and the drops confirmed along the way.
  useEffect(() => {
    const g = overlay.current;
    if (!g) return;
    g.clearLayers();
    if (!sel) return;
    if (sel.trail.length > 1) {
      L.polyline(sel.trail, { color: '#ffffff', weight: 8, opacity: 0.9 }).addTo(g);
      L.polyline(sel.trail, { color: '#2357f5', weight: 4, opacity: 0.95 }).addTo(g);
    }
    if (sel.lastLat != null && sel.lastLng != null && (sel.lastAccuracy ?? 0) > 25)
      L.circle([sel.lastLat, sel.lastLng], { radius: sel.lastAccuracy!, color: '#2357f5', weight: 1, fillOpacity: 0.08 }).addTo(g);
    sel.confirmed.forEach((d, i) =>
      L.marker([d.lat, d.lng], { icon: dropIcon(i + 1, d.status), keyboard: false })
        .bindTooltip(`${esc(d.customerName)} · ${d.status === 'Failed' ? 'not delivered' : d.status === 'PartialReturn' ? 'part returned' : 'delivered'} ${esc(dateTime(d.podConfirmedAt))}`)
        .addTo(g),
    );
  }, [sel]);

  const choose = (t: LiveTrip) => {
    setSelected(t.id);
    if (t.lastLat != null && t.lastLng != null) map.current?.flyTo([t.lastLat, t.lastLng], Math.max(map.current.getZoom(), 14), { duration: 0.6 });
  };
  const shown = useMemo(() => trips.filter((t) => filter === 'all' || t.state === filter), [trips, filter]);
  const c = data?.counts;

  return (
    <div className="relative -mx-4 -mt-5 -mb-28 h-[calc(100svh-61px-3.6rem)] overflow-hidden sm:-mx-6 lg:-mx-8 lg:-mt-6 lg:-mb-12 lg:h-[calc(100svh-3.5rem)]">
      <div ref={mapEl} className="dispatch-map absolute inset-0 z-0 bg-paper" aria-label="Map of vehicles on the road" />

      {/* The office panel: on wide screens a column over the map, on phones a sheet along the bottom. */}
      <aside className="absolute inset-x-2 bottom-2 z-[500] flex max-h-[46%] flex-col overflow-hidden rounded-lg bg-white shadow-xl ring-1 ring-line lg:inset-x-auto lg:top-3 lg:bottom-3 lg:left-3 lg:max-h-none lg:w-[23rem]">
        {sel ? (
          <TripCard t={sel} now={now} onBack={() => setSelected(null)} />
        ) : (
          <>
            <header className="border-b border-line p-4">
              <div className="flex items-center justify-between gap-2">
                <h1 className="font-display text-lg font-semibold">Dispatch office</h1>
                <span className="flex items-center gap-1.5 text-xs text-muted">
                  <span className="relative flex h-2 w-2"><span className="absolute inline-flex h-full w-full animate-ping rounded-full bg-good opacity-60" /><span className="relative inline-flex h-2 w-2 rounded-full bg-good" /></span>
                  Live
                </span>
              </div>
              <p className="mt-0.5 text-sm text-muted">{c ? `${c.onRoad} trip${c.onRoad === 1 ? '' : 's'} on the road` : 'Loading…'}</p>
              {c && c.onRoad > 0 && (
                <div className="mt-3 flex flex-wrap gap-1.5" role="group" aria-label="Show">
                  {([['all', 'All', c.onRoad], ['moving', 'Moving', c.moving], ['stopped', 'Stopped', c.stopped], ['quiet', 'No signal', c.quiet], ['waiting', 'Not sharing', c.waiting]] as const)
                    .filter(([v, , n]) => v === 'all' || n > 0)
                    .map(([v, label, n]) => (
                      <button key={v} aria-pressed={filter === v} onClick={() => setFilter(v)}
                        className={cn('flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-semibold ring-1', filter === v ? 'bg-brand-soft text-brand ring-brand/40' : 'bg-white text-ink ring-line hover:bg-paper')}>
                        {v !== 'all' && <span className={cn('h-2 w-2 rounded-full', STATE[v].dot)} />}
                        {label} <span className={filter === v ? 'text-white/70' : 'text-muted'}>{n}</span>
                      </button>
                    ))}
                </div>
              )}
            </header>
            <div className="min-h-0 flex-1 overflow-y-auto">
              {error ? (
                <div className="p-4"><ErrorNote>{error}</ErrorNote></div>
              ) : !data ? (
                <Loading />
              ) : trips.length === 0 ? (
                <div className="p-5 text-center text-sm">
                  <Truck className="mx-auto h-8 w-8 text-faint" />
                  <p className="mt-2 font-semibold">No trips on the road</p>
                  <p className="mt-1 text-muted">Send a trip out from Fleet &amp; dispatch and send its driver the link. They show up here as they move.</p>
                  <Link to="/fleet" className="btn btn-ghost mt-3">Go to Fleet &amp; dispatch</Link>
                </div>
              ) : (
                <ul className="divide-y divide-line">
                  {shown.map((t) => (
                    <li key={t.id}>
                      <button onClick={() => choose(t)} className="flex w-full items-start gap-3 px-4 py-3 text-left hover:bg-paper">
                        <span className={cn('mt-1.5 h-2.5 w-2.5 shrink-0 rounded-full', STATE[t.state].dot)} aria-label={STATE[t.state].label} />
                        <span className="min-w-0 flex-1">
                          <span className="flex items-baseline justify-between gap-2">
                            <b className="truncate">{t.vehiclePlate || t.tripNumber}</b>
                            <span className="num shrink-0 text-xs text-muted">{t.done}/{t.drops} drops</span>
                          </span>
                          <span className="block truncate text-sm text-ink-2">{t.driverName}{t.routeName && ` · ${t.routeName}`}</span>
                          <span className={cn('block text-xs font-semibold', t.state === 'quiet' ? 'text-bad' : t.state === 'stopped' ? 'text-warn' : t.state === 'moving' ? 'text-good' : 'text-muted')}>{howItIs(t, now)}</span>
                        </span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </>
        )}
      </aside>
    </div>
  );
}

function TripCard({ t, now, onBack }: { t: LiveTrip; now: number; onBack: () => void }) {
  const phone = msisdn(t.driverPhone);
  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-y-auto">
      <header className="border-b border-line p-4">
        <button onClick={onBack} className="mb-2 inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink"><ArrowLeft className="h-4 w-4" /> All trips</button>
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <h2 className="font-display text-xl font-semibold">{t.vehiclePlate || t.tripNumber}</h2>
            <p className="truncate text-sm text-ink-2">{t.driverName} · {t.tripNumber}</p>
          </div>
          <span className={cn('mt-1 flex shrink-0 items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-semibold', t.state === 'moving' ? 'bg-good-soft text-good' : t.state === 'stopped' ? 'bg-warn-soft text-warn' : t.state === 'quiet' ? 'bg-bad-soft text-bad' : 'bg-paper text-muted')}>
            <span className={cn('h-2 w-2 rounded-full', STATE[t.state].dot)} /> {STATE[t.state].label}
          </span>
        </div>
        <p className="mt-2 text-sm font-semibold">{howItIs(t, now)}</p>
        {t.lastPingAt && <p className="text-xs text-muted">Last seen {dateTime(t.lastPingAt)}{t.lastAccuracy != null && ` · within ${Math.round(t.lastAccuracy)} m`}</p>}
        {t.state === 'quiet' && <p className="mt-2 rounded-md bg-bad-soft px-3 py-2 text-xs text-bad">The phone hasn’t reported in a while: low battery, no network, or the link was closed. Call the driver.</p>}
        <div className="mt-3 h-1.5 overflow-hidden rounded-full bg-paper" aria-label={`${t.done} of ${t.drops} drops done`}>
          <div className="h-full rounded-full bg-good" style={{ width: `${t.drops ? (t.done / t.drops) * 100 : 0}%` }} />
        </div>
        <p className="mt-1 text-xs text-muted">{t.done} of {t.drops} drops done{t.routeName && ` · ${t.routeName}`}</p>
      </header>

      <div className="space-y-4 p-4 text-sm">
        <div className="grid grid-cols-2 gap-2">
          {t.driverPhone ? <a className="btn btn-primary" href={`tel:${t.driverPhone}`}><Phone className="h-4 w-4" /> Call driver</a> : <span />}
          {phone ? <a className="btn btn-mpesa" target="_blank" rel="noreferrer" href={`https://wa.me/${phone}`}><MessageCircle className="h-4 w-4" /> WhatsApp</a> : <span />}
        </div>
        {t.next ? (
          <section>
            <p className="text-2xs font-semibold tracking-wide text-muted uppercase">Next drop</p>
            <p className="mt-1 font-semibold">{t.next.customerName}</p>
            {t.next.deliveryAddress && <p className="flex items-start gap-1 text-ink-2"><MapPin className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted" />{t.next.deliveryAddress}</p>}
            {t.next.contactPhone && <a className="mt-1 inline-flex items-center gap-1 font-semibold text-brand hover:underline" href={`tel:${t.next.contactPhone}`}><Phone className="h-3.5 w-3.5" /> {prettyPhone(t.next.contactPhone)}</a>}
          </section>
        ) : (
          <p className="rounded-md bg-good-soft px-3 py-2 text-good">Every drop is done. Waiting for the vehicle back at the yard.</p>
        )}
        {t.confirmed.length > 0 && (
          <section>
            <p className="text-2xs font-semibold tracking-wide text-muted uppercase">Confirmed on the map</p>
            <ul className="mt-1 space-y-1">
              {t.confirmed.map((d, i) => (
                <li key={d.waybillNumber} className="flex items-center gap-2">
                  <span className={cn('grid h-5 w-5 shrink-0 place-items-center rounded-full text-2xs font-bold text-white', d.status === 'Failed' ? 'bg-bad' : d.status === 'PartialReturn' ? 'bg-warn' : 'bg-good')}>{i + 1}</span>
                  <span className="min-w-0 flex-1 truncate">{d.customerName}</span>
                  <span className="shrink-0 text-xs text-muted">{dateTime(d.podConfirmedAt)}</span>
                </li>
              ))}
            </ul>
          </section>
        )}
        {!t.lastPingAt && (
          <p className="flex items-start gap-2 rounded-md bg-paper px-3 py-2 text-ink-2 ring-1 ring-line">
            <Radio className="mt-0.5 h-4 w-4 shrink-0 text-muted" />
            {t.linkSent ? 'The driver has the link but hasn’t started sharing. Call them to open it and tap Start.' : 'Send the driver their link from the trip, so they can share location and confirm drops.'}
          </p>
        )}
        <Link to={`/fleet?trip=${t.id}`} className="btn btn-ghost w-full">Open the trip</Link>
      </div>
    </div>
  );
}
