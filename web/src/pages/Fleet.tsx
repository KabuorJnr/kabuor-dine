// Fleet and dispatch. The dispatcher is deciding "what goes out today, on which vehicle, and what is still on the
// road?", so the page leads with what's moving (trips out, drops still to confirm, invoices waiting to go), plans a
// trip from the invoices that haven't gone out yet onto a vehicle that's free, prints a waybill per drop, and records
// who received each one (or why it came back). Vehicles are the asset register's Vehicles; each shows whether it's
// free, out on a trip or off the road.
import { useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { CheckCircle2, Copy, Loader2, MapPin, MapPinned, MessageCircle, Phone, Plus, Printer, Send, Truck, XCircle } from 'lucide-react';
import { api, can, getSession, useApi, useSession } from '../lib/api';
import { cn, dateTime, day, dayYear, ksh, msisdn, prettyPhone } from '../lib/format';
import { DocBox, DocFooter, Letterhead, PrintSheet, useLetterhead } from '../components/PrintDoc';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, Sheet, Stat, Status, SheetFooter, PageError, confirmDialog } from '../components/ui';

type TripStatus = 'Draft' | 'Dispatched' | 'Delivered' | 'Completed' | 'Cancelled';
type DropStatus = 'Pending' | 'Dispatched' | 'Delivered' | 'PartialReturn' | 'Failed';
type TripRow = {
  id: string; tripNumber: string; vehiclePlate: string; driverName: string; driverPhone: string; routeName: string; status: TripStatus;
  dispatchedAt: string | null; deliveredAt: string | null; createdAt: string; deliveriesCount: number; deliveredCount: number; failedCount: number; pendingCount: number;
};
type Drop = {
  id: string; waybillNumber: string; customerName: string; deliveryAddress: string; contactPhone: string; status: DropStatus;
  recipientName: string; podNotes: string; podConfirmedAt: string | null; invoiceNumber: string | null; invoiceTotal: number | null; salesDocId: string | null;
  items: { description: string; quantity: number; unit: string }[];
};
type Trip = Omit<TripRow, 'deliveriesCount' | 'deliveredCount' | 'failedCount' | 'pendingCount'> & { notes: string; deliveries: Drop[] };
type Counts = { all: number; open: number; draft: number; dispatched: number; delivered: number; completed: number; cancelled: number };
type Vehicle = {
  id: string; tag: string; name: string; plate: string; state: 'available' | 'out' | 'repair' | 'lost'; condition: string; assignedTo: string;
  currentTrip: { id: string; tripNumber: string; driverName: string; routeName: string; dispatchedAt: string | null; drops: number; done: number } | null;
  tripsThisMonth: number; dropsThisMonth: number; lastTripAt: string | null;
};
type Fleet = { counts: { all: number; available: number; out: number; repair: number }; vehicles: Vehicle[] };
type ToDeliver = { id: string; number: string; issueDate: string; total: number; customer: string; phone: string; address: string };

const TRIP: Record<TripStatus, [string, 'good' | 'info' | 'warn' | 'bad' | 'plain']> = {
  Draft: ['Planned', 'plain'], Dispatched: ['On the road', 'info'], Delivered: ['All drops done', 'good'], Completed: ['Completed', 'good'], Cancelled: ['Cancelled', 'plain'],
};
const DROP: Record<DropStatus, [string, 'good' | 'info' | 'warn' | 'bad' | 'plain']> = {
  Pending: ['Not sent', 'plain'], Dispatched: ['On the way', 'info'], Delivered: ['Delivered', 'good'], PartialReturn: ['Part returned', 'warn'], Failed: ['Not delivered', 'bad'],
};
const VEHICLE: Record<Vehicle['state'], [string, 'good' | 'info' | 'warn' | 'bad']> = {
  available: ['Free', 'good'], out: ['Out on a trip', 'info'], repair: ['Under repair', 'warn'], lost: ['Off the road', 'bad'],
};


/** One A4 page per drop: the waybill the driver carries and the customer checks the goods against and signs. */
function Waybills({ trip, onDone }: { trip: Trip; onDone: () => void }) {
  return (
    <PrintSheet name={`Waybills ${trip.tripNumber} · ${trip.routeName}`} onDone={onDone}>
      {trip.deliveries.map((d, i) => (
        <section
          key={d.id}
          className={cn(
            'relative flex min-h-[250mm] max-h-[265mm] flex-col justify-between break-inside-avoid overflow-hidden',
            i < trip.deliveries.length - 1 && 'break-after-page'
          )}
        >
          <div>
            <Letterhead
              title="Waybill"
              number={d.waybillNumber}
              facts={[
                ['Date', trip.dispatchedAt ? dayYear(trip.dispatchedAt) : dayYear(trip.createdAt)],
                ['Trip', trip.tripNumber],
                ['Drop', `${i + 1} of ${trip.deliveries.length}`],
              ]}
            />
            <div className="mt-3.5 grid grid-cols-2 gap-3">
              <DocBox label="Deliver to" className="p-3">
                <p className="text-sm font-semibold text-ink">{d.customerName}</p>
                <div className="mt-0.5 text-xs leading-relaxed text-ink-2">
                  {d.deliveryAddress && <p className="whitespace-pre-line">{d.deliveryAddress}</p>}
                  {d.contactPhone && <p>{prettyPhone(d.contactPhone)}</p>}
                </div>
              </DocBox>
              <DocBox label="Carried by" className="p-3">
                <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-xs">
                  <dt className="text-muted">Vehicle</dt>
                  <dd className="font-semibold text-ink">{trip.vehiclePlate}</dd>
                  <dt className="text-muted">Driver</dt>
                  <dd className="font-semibold text-ink">
                    {trip.driverName}
                    {trip.driverPhone && ` · ${prettyPhone(trip.driverPhone)}`}
                  </dd>
                  <dt className="text-muted">Route</dt>
                  <dd className="text-ink">{trip.routeName}</dd>
                  <dt className="text-muted">Left at</dt>
                  <dd className="text-ink">{trip.dispatchedAt ? dateTime(trip.dispatchedAt) : '________________'}</dd>
                </dl>
              </DocBox>
            </div>

            <table className="doc-table mt-3.5">
              <thead>
                <tr>
                  <th className="w-8">#</th>
                  <th>Goods</th>
                  <th className="text-right">Qty sent</th>
                  <th className="w-28 text-right">Qty received</th>
                </tr>
              </thead>
              <tbody>
                {d.items.length ? (
                  d.items.map((l, n) => (
                    <tr key={n}>
                      <td className="num text-muted">{n + 1}</td>
                      <td className="font-medium text-ink">{l.description}</td>
                      <td className="num text-right whitespace-nowrap">
                        {l.quantity}
                        {l.unit && <span className="text-muted"> {l.unit}</span>}
                      </td>
                      <td className="border-l border-line" />
                    </tr>
                  ))
                ) : (
                  <tr>
                    <td colSpan={4} className="text-muted">Goods as listed on the attached invoice.</td>
                  </tr>
                )}
              </tbody>
            </table>
            {d.invoiceNumber && (
              <p className="mt-1.5 text-right text-xs text-muted">
                For invoice <b className="num text-ink">{d.invoiceNumber}</b>
                {d.invoiceTotal != null && <> · value <b className="num text-ink">{ksh(d.invoiceTotal)}</b></>}
              </p>
            )}

            <div className="mt-3 h-14 rounded-md p-2 text-2xs font-semibold tracking-wide text-muted uppercase ring-1 ring-line">
              Notes, shortages or goods returned
            </div>

            <p className="mt-2.5 text-xs text-ink-2">Received the goods above in good order and condition, unless noted.</p>

            <div className="mt-4 grid grid-cols-4 gap-3 text-2xs text-muted">
              {['Received by (name)', 'Customer signature', 'Date and time', 'Customer stamp'].map((s) => (
                <p key={s} className="border-t border-ink pt-1">{s}</p>
              ))}
            </div>
            <div className="mt-4 grid grid-cols-4 gap-3 text-2xs text-muted">
              <p className="border-t border-ink pt-1">Driver’s signature</p>
              <p className="border-t border-ink pt-1">Released by (store)</p>
            </div>
          </div>

          <DocFooter className="mt-3">The driver brings this signed copy back to the store.</DocFooter>
        </section>
      ))}
    </PrintSheet>
  );
}

/** The driver's private link, sent on WhatsApp: on it they share location while the trip is out (the office's live
 * map) and confirm each drop from their phone. Made when the trip is opened, so sending it is one tap. */
function DriverLink({ trip }: { trip: Trip }) {
  const live = trip.status === 'Draft' || trip.status === 'Dispatched' || trip.status === 'Delivered';
  const [url, setUrl] = useState('');
  const [error, setError] = useState('');
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!live) return;
    api<{ url: string }>(`/dispatch/trips/${trip.id}/driver-link`, { body: {} }).then(
      (r) => {
        const resolved = r.url.startsWith('http')
          ? r.url.replace(/^http:\/\/[^/]+/, window.location.origin)
          : `${window.location.origin}${r.url.startsWith('/') ? '' : '/'}${r.url}`;
        setUrl(resolved);
      },
      (e) => setError((e as Error).message)
    );
  }, [trip.id, live]);
  if (!live) return null;
  const phone = msisdn(trip.driverPhone);
  const text = `Hello ${trip.driverName}, this is your trip ${trip.tripNumber}${trip.routeName ? ` (${trip.routeName})` : ''} for ${getSession()?.company.name ?? 'us'}: ${url}\nOpen it, tap Start sharing, and tap Done at each drop.`;
  return (
    <section className="rounded-md bg-brand-soft p-3 text-sm">
      <p className="font-semibold">Driver’s link</p>
      <p className="text-ink-2">{trip.driverName} opens it on their phone to share location on the live map and confirm each drop. No login needed.</p>
      {error ? (
        <p className="mt-2 text-bad">{error}</p>
      ) : (
        <div className="mt-2 flex flex-wrap gap-2">
          {phone && (
            <a className={cn('btn btn-mpesa', !url && 'pointer-events-none opacity-50')} target="_blank" rel="noreferrer" href={url ? `https://wa.me/${phone}?text=${encodeURIComponent(text)}` : undefined}>
              <MessageCircle className="h-4 w-4" /> Send on WhatsApp
            </a>
          )}
          <button
            className="btn btn-ghost"
            disabled={!url}
            onClick={async () => {
              try {
                await navigator.clipboard.writeText(url);
                setCopied(true);
                setTimeout(() => setCopied(false), 1500);
              } catch {}
            }}
          >
            {copied ? <CheckCircle2 className="h-4 w-4 text-good" /> : <Copy className="h-4 w-4" />} {copied ? 'Copied' : 'Copy link'}
          </button>
        </div>
      )}
    </section>
  );
}

export default function Fleet() {
  const session = useSession();
  const canEdit = can(session, 'fleet');
  const canAddVehicle = can(session, 'stock'); // vehicles live on the asset register
  const [params, setParams] = useSearchParams();
  const view = params.get('view') === 'vehicles' ? 'vehicles' : 'trips';
  const [filter, setFilter] = useState<'open' | 'Draft' | 'Dispatched' | 'Completed' | 'Cancelled' | 'all'>('open');
  const trips = useApi<TripRow[]>(`/dispatch/trips?status=${filter}`);
  const counts = useApi<Counts>('/dispatch/counts');
  const fleet = useApi<Fleet>('/dispatch/fleet');
  const toDeliver = useApi<ToDeliver[]>(canEdit ? '/dispatch/to-deliver' : null);
  const [planning, setPlanning] = useState(false);
  const [addingVehicle, setAddingVehicle] = useState(false);
  const openId = params.get('trip');
  const open = (id: string | null) => {
    const next = new URLSearchParams(params);
    if (id) next.set('trip', id);
    else next.delete('trip');
    setParams(next, { replace: true });
  };
  const refresh = () => {
    trips.reload();
    counts.reload();
    fleet.reload();
    toDeliver.reload();
  };

  const c = counts.data;
  const onRoad = useMemo(() => (trips.data ?? []).filter((t) => t.status === 'Dispatched').reduce((n, t) => n + t.pendingCount, 0), [trips.data]);

  return (
    <>
      <PageHeader print
        title="Fleet & dispatch"
        subtitle="Plan trips from issued invoices, print waybills, and confirm every delivery."
        actions={
          <>
            {canEdit && (
              <button className="btn btn-primary" onClick={() => setPlanning(true)}>
                <Plus className="h-4 w-4" /> Plan a trip
              </button>
            )}
            <Link to="/fleet/live" className="btn btn-ghost">
              <MapPinned className="h-4 w-4" /> Live map
            </Link>
            {canAddVehicle && (
              <button className="btn btn-ghost" onClick={() => setAddingVehicle(true)}>
                <Truck className="h-4 w-4" /> Add a vehicle
              </button>
            )}
          </>
        }
      />

      <div className="mb-5 grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="On the road" value={c ? c.dispatched : '—'}>{!c?.dispatched ? 'No trips out now' : filter === 'open' || filter === 'Dispatched' || filter === 'all' ? `${onRoad} drop${onRoad === 1 ? '' : 's'} still to confirm` : 'Trips out now'}</Stat>
        <Stat label="Planned, not sent" value={c ? c.draft : '—'}>Trips waiting to leave</Stat>
        <Stat label="Invoices to deliver" value={toDeliver.data ? toDeliver.data.length : '—'}>{toDeliver.data?.length ? `${ksh(toDeliver.data.reduce((s, i) => s + i.total, 0))} of goods` : 'Issued in the last 60 days'}</Stat>
        <Stat label="Vehicles free" value={fleet.data ? `${fleet.data.counts.available} of ${fleet.data.counts.all}` : '—'}>
          {fleet.data?.counts.repair ? <span className="text-warn">{fleet.data.counts.repair} off the road</span> : fleet.data?.counts.all ? 'Ready to go out' : 'Add your vehicles'}
        </Stat>
      </div>

      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div className="inline-flex rounded-full bg-white p-0.5 ring-1 ring-line" role="tablist" aria-label="Show">
          {(['trips', 'vehicles'] as const).map((v) => (
            <button key={v} role="tab" aria-selected={view === v} onClick={() => setParams(v === 'trips' ? {} : { view: v }, { replace: true })} className={cn('rounded-full px-4 py-1.5 text-sm font-semibold', view === v ? 'bg-brand-soft text-brand' : 'text-muted hover:text-ink')}>
              {v === 'trips' ? 'Trips' : 'Vehicles'}
            </button>
          ))}
        </div>
        {view === 'trips' && (
          <Chips
            label="Trips"
            value={filter}
            onChange={setFilter}
            options={[['open', 'Open', c?.open], ['Draft', 'Planned', c?.draft], ['Dispatched', 'On the road', c?.dispatched], ['Completed', 'Completed', c?.completed], ['Cancelled', 'Cancelled', c?.cancelled], ['all', 'All', c?.all]]}
          />
        )}
      </div>

      {view === 'trips' ? (
        trips.error ? <ErrorNote>{trips.error}</ErrorNote> : !trips.data ? <Loading /> : trips.data.length === 0 ? (
          <Empty
            title={filter === 'open' ? 'No trips out or planned' : 'No trips here'}
            action={canEdit && filter === 'open' && <button className="btn btn-primary" onClick={() => setPlanning(true)}><Plus className="h-4 w-4" /> Plan a trip</button>}
          >
            {filter === 'open' && (toDeliver.data?.length ? `${toDeliver.data.length} issued invoice${toDeliver.data.length === 1 ? ' is' : 's are'} waiting to go out.` : 'Plan a trip from your issued invoices: each drop gets a waybill to print and sign.')}
          </Empty>
        ) : (
          <ul className="card divide-y divide-line">
            {trips.data.map((t) => {
              const done = t.deliveredCount + t.failedCount;
              return (
                <li key={t.id}>
                  <button onClick={() => open(t.id)} className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-paper sm:gap-4">
                    <span className="grid h-10 w-10 shrink-0 place-items-center rounded-full bg-paper text-ink-2 ring-1 ring-line"><Truck className="h-5 w-5" /></span>
                    <span className="min-w-0 flex-1">
                      <span className="flex flex-wrap items-center gap-x-2">
                        <b className="truncate">{t.routeName}</b>
                        <span className="text-sm text-muted">{t.tripNumber}</span>
                      </span>
                      <span className="block truncate text-sm text-muted">{t.vehiclePlate} · {t.driverName}{t.dispatchedAt ? ` · out ${dateTime(t.dispatchedAt)}` : ` · planned ${day(t.createdAt)}`}</span>
                    </span>
                    <span className="hidden w-40 shrink-0 sm:block">
                      <span className="flex justify-between text-xs text-muted"><span>{done} of {t.deliveriesCount} done</span>{t.failedCount > 0 && <span className="text-bad">{t.failedCount} back</span>}</span>
                      <span className="mt-1 flex h-1.5 overflow-hidden rounded-full bg-paper ring-1 ring-line">
                        <span className="bg-good" style={{ width: `${(t.deliveredCount / t.deliveriesCount) * 100}%` }} />
                        <span className="bg-bad" style={{ width: `${(t.failedCount / t.deliveriesCount) * 100}%` }} />
                      </span>
                    </span>
                    <Status tone={TRIP[t.status][1]}>{TRIP[t.status][0]}</Status>
                  </button>
                </li>
              );
            })}
          </ul>
        )
      ) : (
        <Vehicles fleet={fleet} onOpenTrip={open} onAdd={canEdit ? () => setAddingVehicle(true) : undefined} />
      )}

      <Sheet open={Boolean(openId)} onClose={() => open(null)} title="Trip" wide>
        {openId && <TripDetail id={openId} canEdit={canEdit} onChange={refresh} />}
      </Sheet>
      <Sheet open={planning} onClose={() => setPlanning(false)} title="Plan a trip" description="Pick a vehicle and the invoices to deliver. Each drop gets a waybill the customer signs." wide>
        {planning && <PlanTrip fleet={fleet.data} invoices={toDeliver.data ?? []} onDone={(id) => { setPlanning(false); refresh(); open(id); }} onAddVehicle={() => setAddingVehicle(true)} />}
      </Sheet>
      <Sheet open={addingVehicle} onClose={() => setAddingVehicle(false)} title="Add a vehicle" description="Add it once; then pick it when you plan a trip.">
        {addingVehicle && <AddVehicle onDone={() => { setAddingVehicle(false); fleet.reload(); }} />}
      </Sheet>
    </>
  );
}

function Vehicles({ fleet, onOpenTrip, onAdd }: { fleet: ReturnType<typeof useApi<Fleet>>; onOpenTrip: (id: string) => void; onAdd?: () => void }) {
  if (fleet.error) return <PageError>{fleet.error}</PageError>;
  if (!fleet.data) return <Loading />;
  if (!fleet.data.vehicles.length)
    return (
      <Empty title="No vehicles yet" action={onAdd && <button className="btn btn-primary" onClick={onAdd}><Truck className="h-4 w-4" /> Add a vehicle</button>}>
        Add your lorries, vans and motorbikes with their number plates. Each one shows whether it’s free, out on a trip or off the road.
      </Empty>
    );
  return (
    <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
      {fleet.data.vehicles.map((v) => (
        <article key={v.id} className="card flex flex-col gap-3 p-4">
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0">
              <p className="font-display text-lg font-semibold tracking-wide">{v.plate}</p>
              <p className="truncate text-sm text-muted">{v.name}{v.tag ? ` · ${v.tag}` : ''}</p>
            </div>
            <Status tone={VEHICLE[v.state][1]}>{VEHICLE[v.state][0]}</Status>
          </div>
          {v.currentTrip ? (
            <button onClick={() => onOpenTrip(v.currentTrip!.id)} className="rounded-md bg-brand-soft/60 p-3 text-left text-sm hover:bg-brand-soft">
              <b className="block">{v.currentTrip.routeName}</b>
              <span className="text-muted">{v.currentTrip.tripNumber} · {v.currentTrip.driverName} · {v.currentTrip.done} of {v.currentTrip.drops} drops done</span>
            </button>
          ) : (
            <p className="text-sm text-muted">{v.lastTripAt ? `Last out ${day(v.lastTripAt)}` : 'Not used on a trip yet'}</p>
          )}
          <dl className="mt-auto grid grid-cols-2 gap-2 border-t border-line pt-3 text-sm">
            <div><dt className="text-xs text-muted">Trips this month</dt><dd className="num font-semibold">{v.tripsThisMonth}</dd></div>
            <div><dt className="text-xs text-muted">Drops delivered</dt><dd className="num font-semibold">{v.dropsThisMonth}</dd></div>
          </dl>
        </article>
      ))}
    </div>
  );
}

function TripDetail({ id, canEdit, onChange }: { id: string; canEdit: boolean; onChange: () => void }) {
  const { data: t, error, reload } = useApi<Trip>(`/dispatch/trips/${id}`);
  const [printing, setPrinting] = useState(false);
  useLetterhead(); // loaded before Print is pressed, so the waybills have it
  const [busy, setBusy] = useState('');
  const [err, setErr] = useState('');
  const [pod, setPod] = useState<string | null>(null);
  if (error) return <PageError>{error}</PageError>;
  if (!t) return <Loading />;
  const run = async (what: string, path: string, ask?: Parameters<typeof confirmDialog>[0]) => {
    if (ask && !(await confirmDialog(ask))) return;
    setBusy(what);
    setErr('');
    try {
      await api(path, { body: {} });
      reload();
      onChange();
    } catch (e) {
      setErr((e as Error).message);
    } finally {
      setBusy('');
    }
  };
  const open = t.deliveries.filter((d) => d.status === 'Pending' || d.status === 'Dispatched').length;
  const live = t.status === 'Dispatched' || t.status === 'Delivered';

  return (
    <div className="space-y-5">
      {printing && <Waybills trip={t} onDone={() => setPrinting(false)} />}
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-sm text-muted">{t.tripNumber}</p>
          <h3 className="font-display text-2xl font-semibold">{t.routeName}</h3>
          <p className="mt-1 text-sm text-muted">{t.vehiclePlate} · {t.driverName}{t.driverPhone && <> · <a className="text-brand hover:underline" href={`tel:${t.driverPhone}`}>{prettyPhone(t.driverPhone)}</a></>}</p>
        </div>
        <Status tone={TRIP[t.status][1]}>{TRIP[t.status][0]}</Status>
      </header>

      {t.notes && <p className="rounded-md bg-paper p-3 text-sm ring-1 ring-line">{t.notes}</p>}
      {canEdit && <DriverLink trip={t} />}
      {err && <ErrorNote>{err}</ErrorNote>}

      <div className="flex flex-wrap gap-2">
        {canEdit && t.status === 'Draft' && (
          <button className="btn btn-primary" disabled={Boolean(busy)} onClick={() => run('dispatch', `/dispatch/trips/${t.id}/dispatch`)}>
            {busy === 'dispatch' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />} Send out now
          </button>
        )}
        {canEdit && live && (
          <button className={cn('btn', open === 0 ? 'btn-primary' : 'btn-ghost')} disabled={Boolean(busy)} onClick={() => run('complete', `/dispatch/trips/${t.id}/complete`, open ? { title: 'Complete the trip anyway?', body: `${open} drop${open === 1 ? ' has' : 's have'} no proof of delivery. Completing the trip marks ${open === 1 ? 'it' : 'them'} as brought back, not delivered.`, confirm: 'Complete trip' } : undefined)}>
            {busy === 'complete' ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />} {open ? 'Back at the yard: complete trip' : 'Complete trip'}
          </button>
        )}
        <button className="btn btn-ghost" onClick={() => setPrinting(true)}>
          <Printer className="h-4 w-4" /> Print {t.deliveries.length === 1 ? 'waybill' : `${t.deliveries.length} waybills`}
        </button>
        {canEdit && (t.status === 'Draft' || t.status === 'Dispatched') && !t.deliveries.some((d) => d.status === 'Delivered' || d.status === 'PartialReturn') && (
          <button className="btn btn-ghost text-bad" disabled={Boolean(busy)} onClick={() => run('cancel', `/dispatch/trips/${t.id}/cancel`, { title: 'Cancel this trip?', body: 'Its invoices can go on another trip.', confirm: 'Cancel trip', cancel: 'Keep trip', danger: true })}>
            <XCircle className="h-4 w-4" /> Cancel trip
          </button>
        )}
      </div>

      <section>
        <h4 className="mb-2 text-sm font-semibold">{t.deliveries.length} drop{t.deliveries.length === 1 ? '' : 's'}{live && open > 0 && <span className="font-normal text-muted"> · {open} still to confirm</span>}</h4>
        <ol className="card divide-y divide-line">
          {t.deliveries.map((d, i) => (
            <li key={d.id} className="p-4">
              <div className="flex items-start gap-3">
                <span className="grid h-7 w-7 shrink-0 place-items-center rounded-full bg-paper text-xs font-bold text-muted ring-1 ring-line">{i + 1}</span>
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
                    <b>{d.customerName}</b>
                    <Status tone={DROP[d.status][1]}>{DROP[d.status][0]}</Status>
                  </div>
                  <p className="mt-0.5 flex items-start gap-1.5 text-sm text-muted"><MapPin className="mt-0.5 h-3.5 w-3.5 shrink-0" />{d.deliveryAddress}</p>
                  <p className="mt-0.5 text-sm text-muted">
                    {d.waybillNumber}{d.invoiceNumber && ` · ${d.invoiceNumber}`}{d.invoiceTotal != null && ` · ${ksh(d.invoiceTotal)}`}
                    {d.contactPhone && <> · <a className="inline-flex items-center gap-1 text-brand hover:underline" href={`tel:${d.contactPhone}`}><Phone className="h-3 w-3" />{prettyPhone(d.contactPhone)}</a></>}
                  </p>
                  {d.podConfirmedAt && (
                    <p className="mt-1.5 text-sm">
                      {d.recipientName ? <>Received by <b>{d.recipientName}</b></> : 'Not received'} · {dateTime(d.podConfirmedAt)}
                      {d.podNotes && <span className="block text-muted">“{d.podNotes}”</span>}
                    </p>
                  )}
                </div>
                {canEdit && live && t.status !== 'Completed' && pod !== d.id && (
                  <button className="btn btn-ghost shrink-0 py-1.5 text-sm" onClick={() => setPod(d.id)}>{d.podConfirmedAt ? 'Change' : 'Record delivery'}</button>
                )}
              </div>
              {pod === d.id && <PodForm drop={d} onCancel={() => setPod(null)} onSaved={() => { setPod(null); reload(); onChange(); }} />}
            </li>
          ))}
        </ol>
      </section>
    </div>
  );
}

function PodForm({ drop, onCancel, onSaved }: { drop: Drop; onCancel: () => void; onSaved: () => void }) {
  const [f, setF] = useState({ status: (drop.status === 'Pending' || drop.status === 'Dispatched' ? 'Delivered' : drop.status) as DropStatus, recipientName: drop.recipientName, podNotes: drop.podNotes });
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      await api(`/dispatch/deliveries/${drop.id}/pod`, { body: f });
      onSaved();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  return (
    <form onSubmit={save} className="mt-3 space-y-3 rounded-md bg-paper p-3 ring-1 ring-line">
      <Chips label="What happened" value={f.status} onChange={(v) => setF({ ...f, status: v })} options={[['Delivered', 'Delivered'], ['PartialReturn', 'Part returned'], ['Failed', 'Not delivered']]} />
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={f.status === 'Failed' ? 'Who was there (if anyone)' : 'Received by'}>
          <input className="field" value={f.recipientName} onChange={(e) => setF({ ...f, recipientName: e.target.value })} required={f.status !== 'Failed'} placeholder="Name of the person who signed" />
        </Field>
        <Field label={f.status === 'Delivered' ? 'Notes (optional)' : f.status === 'Failed' ? 'Why it wasn’t delivered' : 'What came back'}>
          <input className="field" value={f.podNotes} onChange={(e) => setF({ ...f, podNotes: e.target.value })} required={f.status !== 'Delivered'} placeholder={f.status === 'Failed' ? 'e.g. shop closed' : f.status === 'PartialReturn' ? 'e.g. 2 cartons damaged' : ''} />
        </Field>
      </div>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <div className="flex gap-2">
        <button className="btn btn-primary" disabled={state.busy}>{state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Save</button>
        <button type="button" className="btn btn-ghost" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}

type ExtraDrop = { customerName: string; deliveryAddress: string; contactPhone: string };

function PlanTrip({ fleet, invoices, onDone, onAddVehicle }: { fleet: Fleet | null; invoices: ToDeliver[]; onDone: (id: string) => void; onAddVehicle: () => void }) {
  const free = (fleet?.vehicles ?? []).filter((v) => v.state === 'available');
  const [vehicleId, setVehicleId] = useState<string>(free[0]?.id ?? '');
  const [f, setF] = useState({ vehiclePlate: '', driverName: '', driverPhone: '', routeName: '', notes: '' });
  const [picked, setPicked] = useState<string[]>([]);
  const [extra, setExtra] = useState<ExtraDrop[]>([]);
  const [state, setState] = useState({ busy: false, error: '' });
  const drops = picked.length + extra.length;
  const value = invoices.filter((i) => picked.includes(i.id)).reduce((s, i) => s + i.total, 0);
  const toggle = (id: string) => setPicked((p) => (p.includes(id) ? p.filter((x) => x !== id) : [...p, id]));

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      const trip = await api<{ id: string }>('/dispatch/trips', {
        body: {
          ...f,
          vehicleId: vehicleId || null,
          deliveries: [
            ...picked.map((id) => ({ salesDocId: id })),
            ...extra.filter((x) => x.customerName.trim()).map((x) => ({ customerName: x.customerName, deliveryAddress: x.deliveryAddress, contactPhone: x.contactPhone })),
          ],
        },
      });
      onDone(trip.id);
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };

  return (
    <form onSubmit={submit} className="space-y-5">
      <fieldset>
        <legend className="mb-2 text-sm font-semibold">Vehicle</legend>
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-3" role="radiogroup" aria-label="Vehicle">
          {(fleet?.vehicles ?? []).map((v) => {
            const usable = v.state === 'available';
            return (
              <button key={v.id} type="button" role="radio" aria-checked={vehicleId === v.id} disabled={!usable} onClick={() => setVehicleId(v.id)} className={cn('min-w-0 rounded-md bg-white px-3 py-2.5 text-left ring-1 transition disabled:opacity-50', vehicleId === v.id ? 'bg-brand-soft/40 ring-2 ring-brand' : 'ring-line hover:ring-faint')}>
                <b className="block tracking-wide">{v.plate}</b>
                <span className="block truncate text-xs text-muted">{usable ? v.name : VEHICLE[v.state][0]}</span>
              </button>
            );
          })}
          <button type="button" role="radio" aria-checked={vehicleId === ''} onClick={() => setVehicleId('')} className={cn('min-w-0 rounded-md bg-white px-3 py-2.5 text-left ring-1 transition', vehicleId === '' ? 'bg-brand-soft/40 ring-2 ring-brand' : 'ring-line hover:ring-faint')}>
            <b className="block">Another vehicle</b>
            <span className="block truncate text-xs text-muted">Hired, or not on the register</span>
          </button>
        </div>
        {!fleet?.vehicles.length && <p className="mt-2 text-xs text-muted">Your own vehicles show here once they’re added. <button type="button" className="font-semibold text-brand hover:underline" onClick={onAddVehicle}>Add a vehicle</button></p>}
        {vehicleId === '' && (
          <Field label="Number plate" className="mt-3">
            <input className="field uppercase" required value={f.vehiclePlate} onChange={(e) => setF({ ...f, vehiclePlate: e.target.value })} placeholder="KCA 123B" />
          </Field>
        )}
      </fieldset>

      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Driver">
          <input className="field" required value={f.driverName} onChange={(e) => setF({ ...f, driverName: e.target.value })} />
        </Field>
        <Field label="Driver’s phone" hint="Printed on the waybills, so customers can call.">
          <input className="field" inputMode="tel" value={f.driverPhone} onChange={(e) => setF({ ...f, driverPhone: e.target.value })} placeholder="0712 345 678" />
        </Field>
        <Field label="Route or destination" className="sm:col-span-2">
          <input className="field" required value={f.routeName} onChange={(e) => setF({ ...f, routeName: e.target.value })} placeholder="e.g. Kisumu town, then Ahero" />
        </Field>
      </div>

      <fieldset>
        <legend className="mb-2 text-sm font-semibold">Invoices to deliver <span className="font-normal text-muted">· {invoices.length} waiting</span></legend>
        {invoices.length ? (
          <ul className="card max-h-72 divide-y divide-line overflow-y-auto">
            {invoices.map((i) => (
              <li key={i.id}>
                <label className="flex cursor-pointer items-center gap-3 px-3 py-2.5 hover:bg-paper">
                  <input type="checkbox" className="h-4 w-4 accent-[var(--color-brand)]" checked={picked.includes(i.id)} onChange={() => toggle(i.id)} />
                  <span className="min-w-0 flex-1">
                    <b className="block truncate text-sm">{i.customer}</b>
                    <span className="block truncate text-xs text-muted">{i.number} · {day(i.issueDate)}{i.address ? ` · ${i.address}` : ''}</span>
                  </span>
                  <span className="num shrink-0 text-sm font-semibold">{ksh(i.total)}</span>
                </label>
              </li>
            ))}
          </ul>
        ) : (
          <p className="rounded-md bg-paper p-3 text-sm text-muted ring-1 ring-line">No issued invoices are waiting to go out. Add drops without an invoice below.</p>
        )}
      </fieldset>

      <fieldset className="space-y-2">
        <legend className="mb-1 text-sm font-semibold">Other drops</legend>
        {extra.map((x, i) => (
          <div key={i} className="grid gap-2 rounded-md bg-paper p-2 ring-1 ring-line sm:grid-cols-[1fr_1.3fr_0.8fr_auto]">
            <input className="field" placeholder="Customer" value={x.customerName} onChange={(e) => setExtra(extra.map((y, j) => (j === i ? { ...y, customerName: e.target.value } : y)))} aria-label="Customer" />
            <input className="field" placeholder="Address" value={x.deliveryAddress} onChange={(e) => setExtra(extra.map((y, j) => (j === i ? { ...y, deliveryAddress: e.target.value } : y)))} aria-label="Address" />
            <input className="field" placeholder="Phone" inputMode="tel" value={x.contactPhone} onChange={(e) => setExtra(extra.map((y, j) => (j === i ? { ...y, contactPhone: e.target.value } : y)))} aria-label="Phone" />
            <button type="button" className="btn btn-ghost" onClick={() => setExtra(extra.filter((_, j) => j !== i))} aria-label="Remove this drop"><XCircle className="h-4 w-4" /></button>
          </div>
        ))}
        <button type="button" className="btn btn-ghost" onClick={() => setExtra([...extra, { customerName: '', deliveryAddress: '', contactPhone: '' }])}><Plus className="h-4 w-4" /> Add a drop without an invoice</button>
      </fieldset>

      <Field label="Notes for the driver (optional)">
        <input className="field" value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} placeholder="e.g. collect empties from the Ahero shop" />
      </Field>

      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button className="btn btn-primary" disabled={state.busy || drops === 0}>
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} {drops === 0 ? 'Choose at least one drop' : `Plan trip with ${drops} drop${drops === 1 ? '' : 's'}${value ? ` · ${ksh(value)}` : ''}`}
      </button>
      </SheetFooter>
    </form>
  );
}

function AddVehicle({ onDone }: { onDone: () => void }) {
  const [f, setF] = useState({ name: '', serialNumber: '', assignedTo: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      await api('/assets', { body: { name: f.name, category: 'VEH', serialNumber: f.serialNumber.trim().toUpperCase(), assignedTo: f.assignedTo, status: 'InUse', condition: 'Good' } });
      onDone();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  return (
    <form onSubmit={submit} className="space-y-4">
      <Field label="Number plate">
        <input className="field uppercase" required minLength={3} value={f.serialNumber} onChange={(e) => setF({ ...f, serialNumber: e.target.value })} placeholder="KCA 123B" />
      </Field>
      <Field label="What it is" hint="Make, model and what it carries.">
        <input className="field" required value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. Isuzu FRR, 5 tonnes" />
      </Field>
      <Field label="Usual driver (optional)">
        <input className="field" value={f.assignedTo} onChange={(e) => setF({ ...f, assignedTo: e.target.value })} />
      </Field>
      <p className="text-xs text-muted">It’s added to the asset register under Vehicles, with a tag and a QR label you can print from Assets.</p>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button className="btn btn-primary" disabled={state.busy}>{state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Add {f.serialNumber.trim().toUpperCase() || 'vehicle'}</button>
      </SheetFooter>
    </form>
  );
}
