// Maintenance: keeping the business's assets working. The maintenance lead is deciding "what's broken, what do we do
// first, who does it and when, and what did it cost?", so the page leads with the open work (overdue and emergencies
// first), new fault reports waiting for a decision, and the schedule. Each work order opens in a panel that carries it
// from planned to financially closed: parts from the store, hours, money paid out, contractor orders, what failed and
// why, and the final cost against the estimate.
import { Fragment, useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { CalendarClock, CircleCheck, ClipboardList, Lock, Pause, Play, Plus, RotateCcw, ShoppingBag, Trash2, Undo2, Wrench } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, day, dayYear, dateTime, ksh, toCents, todayIso } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageError, PageHeader, SearchBox, Sheet, SheetFooter, Stat, Status, Tabs, askDialog, confirmDialog } from '../components/ui';
import DocTrail from '../components/DocTrail';

type Priority = 'Emergency' | 'High' | 'Normal' | 'Low';
type WoStatus = 'Open' | 'Planned' | 'InProgress' | 'OnHold' | 'Completed' | 'Closed' | 'Cancelled';
type WoRow = {
  id: string; number: string; type: string; priority: Priority; status: WoStatus; title: string; assetId: string | null; asset: string | null; location: string;
  assignedTo: string; plannedStart: string | null; dueDate: string | null; estimatedHours: number; createdAt: string; completedAt: string | null; onHoldReason: string; overdue: boolean;
};
type Option = { id: string; name: string };
type AssetOption = { id: string; tag: string; name: string; locationId: string | null; departmentId: string | null; category?: string; status?: string; meterUnit?: string };

export const PRIORITY: Record<Priority, [string, 'bad' | 'warn' | 'info' | 'plain']> = { Emergency: ['Emergency', 'bad'], High: ['High', 'warn'], Normal: ['Normal', 'info'], Low: ['Low', 'plain'] };
const STATUS: Record<WoStatus, [string, 'good' | 'warn' | 'bad' | 'info' | 'plain']> = {
  Open: ['Not planned', 'plain'], Planned: ['Planned', 'info'], InProgress: ['In progress', 'warn'], OnHold: ['On hold', 'bad'], Completed: ['Done, to close', 'good'], Closed: ['Closed', 'plain'], Cancelled: ['Cancelled', 'plain'],
};
const TYPES: [string, string][] = [['Breakdown', 'Breakdown'], ['Corrective', 'Repair'], ['Preventive', 'Preventive'], ['Inspection', 'Inspection'], ['Improvement', 'Improvement']];
const typeName = (t: string) => TYPES.find(([k]) => k === t)?.[1] ?? t;

// Preventive work comes due on its own: make the work orders once per visit to the page.
let generated = false;

export default function Maintenance() {
  const session = useSession();
  const canWork = can(session, 'maintain');
  const [params, setParams] = useSearchParams();
  const tab = (params.get('tab') ?? 'work') as 'work' | 'faults' | 'schedule' | 'plans' | 'insights';
  const openId = params.get('wo');
  const setTab = (t: string) => setParams(t === 'work' ? {} : { tab: t }, { replace: true });
  const open = (id: string | null) => setParams((p) => { const n = new URLSearchParams(p); if (id) n.set('wo', id); else n.delete('wo'); return n; });
  const faults = useApi<{ counts: { new: number } }>('/maintenance/faults');
  const [creating, setCreating] = useState(false);
  const [reporting, setReporting] = useState(false);
  const [tick, setTick] = useState(0);
  const refresh = () => (setTick((t) => t + 1), faults.reload());

  useEffect(() => {
    if (!canWork || generated) return;
    generated = true;
    api<{ made: number }>('/maintenance/plans/generate', { body: {} }).then((r) => r.made > 0 && refresh()).catch(() => {});
  }, [canWork]); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div>
      <PageHeader print
        title="Maintenance"
        subtitle="Faults, work orders, the schedule and preventive servicing for everything the business runs on."
        actions={<>
          {canWork && <button className="btn btn-primary" onClick={() => setCreating(true)}><Plus className="h-4 w-4" /> New work order</button>}
          <button className="btn btn-ghost" onClick={() => setReporting(true)}><Wrench className="h-4 w-4" /> Report a fault</button>
        </>}
      />
      <div className="mb-4">
        <Tabs label="Maintenance" value={tab} onChange={setTab} options={[['work', 'Work orders'], ['faults', 'Faults', faults.data?.counts.new], ['schedule', 'Schedule'], ['plans', 'Preventive plans'], ['insights', 'Insights']]} />
      </div>
      {tab === 'work' && <WorkOrders key={tick} onOpen={open} />}
      {tab === 'faults' && <Faults key={tick} canWork={canWork} onOpen={open} onChange={refresh} />}
      {tab === 'schedule' && <Schedule key={tick} onOpen={open} />}
      {tab === 'plans' && <Plans key={tick} canWork={canWork} onOpen={open} />}
      {tab === 'insights' && <Insights />}
      {creating && <WorkOrderForm onClose={() => setCreating(false)} onSaved={(id) => (setCreating(false), refresh(), open(id))} />}
      {reporting && <FaultForm onClose={() => setReporting(false)} onSaved={() => (setReporting(false), refresh())} />}
      {openId && <WorkOrderSheet id={openId} onClose={() => open(null)} onChange={refresh} />}
    </div>
  );
}

function WoList({ items, onOpen }: { items: WoRow[]; onOpen: (id: string) => void }) {
  return (
    <ul className="card divide-y divide-line">
      {items.map((w) => (
        <li key={w.id}>
          <button className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-paper" onClick={() => onOpen(w.id)}>
            <span className={cn('h-2.5 w-2.5 shrink-0 rounded-full', w.priority === 'Emergency' ? 'bg-bad' : w.priority === 'High' ? 'bg-warn' : w.priority === 'Normal' ? 'bg-brand' : 'bg-line')} title={`${w.priority} priority`} />
            <span className="min-w-0 flex-1">
              <span className="flex items-center gap-2">
                <code className="shrink-0 text-xs font-semibold text-muted">{w.number}</code>
                <b className="truncate text-sm">{w.title}</b>
              </span>
              <span className="mt-0.5 block truncate text-sm text-muted">
                {[typeName(w.type), w.asset ?? w.location, w.assignedTo ? `with ${w.assignedTo}` : 'not assigned', w.status === 'OnHold' && w.onHoldReason ? `waiting: ${w.onHoldReason}` : ''].filter(Boolean).join(' · ')}
              </span>
            </span>
            <span className={cn('hidden shrink-0 text-right text-xs sm:block', w.overdue ? 'font-semibold text-bad' : 'text-muted')}>
              {w.dueDate ? `${w.overdue ? 'Overdue · ' : 'Due '}${day(w.dueDate)}` : w.plannedStart ? `Starts ${day(w.plannedStart)}` : ''}
            </span>
            <Status tone={STATUS[w.status][1]}>{STATUS[w.status][0]}</Status>
          </button>
        </li>
      ))}
    </ul>
  );
}

function WorkOrders({ onOpen }: { onOpen: (id: string) => void }) {
  const [view, setView] = useState('open');
  const [q, setQ] = useState('');
  const { data, error } = useApi<{ counts: Record<string, number>; items: WoRow[] }>(`/maintenance/work-orders?view=${view}&q=${encodeURIComponent(q)}`);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const c = data.counts;
  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Open jobs" value={c.open}>{c.unplanned} not planned yet</Stat>
        <Stat label="Overdue" value={c.overdue} tone={c.overdue > 0 ? 'bad' : undefined}>Past their due date</Stat>
        <Stat label="On hold" value={c.onHold}>Waiting for parts or access</Stat>
        <Stat label="Done, to close" value={c.toClose}>Costs to check and close</Stat>
      </div>
      <SearchBox value={q} onChange={setQ} placeholder="Search number, job, asset or technician" />
      <Chips label="Show" value={view} onChange={setView} options={[['open', 'Open', c.open], ['overdue', 'Overdue', c.overdue], ['unplanned', 'Not planned', c.unplanned], ['inprogress', 'In progress', c.inProgress], ['onhold', 'On hold', c.onHold], ['toclose', 'To close', c.toClose], ['closed', 'Closed', c.closed], ['all', 'All']]} />
      {data.items.length === 0 ? (
        <Empty title={view === 'open' ? 'No open work' : 'Nothing here'}>{view === 'open' ? 'When something breaks, report it as a fault; maintenance turns it into a work order.' : 'Try another filter.'}</Empty>
      ) : <WoList items={data.items} onOpen={onOpen} />}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------- faults

type Fault = {
  id: string; number: string; title: string; description: string; symptom: string; priority: Priority; assetDown: boolean; status: 'New' | 'Accepted' | 'Rejected' | 'Resolved';
  reportedBy: string; createdAt: string; resolvedAt: string | null; rejectReason: string; asset: string | null; location: string; workOrder: { number: string; status: string } | null; workOrderId: string | null;
};

function Faults({ canWork, onOpen, onChange }: { canWork: boolean; onOpen: (id: string) => void; onChange: () => void }) {
  const [status, setStatus] = useState('open');
  const { data, error, reload } = useApi<{ counts: { open: number; new: number; closed: number }; items: Fault[] }>(`/maintenance/faults?status=${status}`);
  const [accepting, setAccepting] = useState<Fault | null>(null);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const reject = async (f: Fault) => {
    const reason = await askDialog({ title: `Turn down ${f.number}?`, body: f.title, label: 'Why (the person who reported it sees this)', placeholder: 'Not a fault, a duplicate of FR-0004, already fixed…', required: true, confirm: 'Turn down', danger: true });
    if (!reason) return;
    await api(`/maintenance/faults/${f.id}/reject`, { body: { reason } });
    reload();
    onChange();
  };
  return (
    <div className="space-y-4">
      <Chips label="Faults" value={status} onChange={setStatus} options={[['open', 'Open', data.counts.open], ['closed', 'Dealt with', data.counts.closed], ['all', 'All']]} />
      {data.items.length === 0 ? (
        <Empty title="No faults reported">Anyone can report one here, or from their own portal by scanning the asset’s label.</Empty>
      ) : (
        <ul className="card divide-y divide-line">
          {data.items.map((f) => (
            <li key={f.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
              <span className="min-w-0 flex-1">
                <span className="flex flex-wrap items-center gap-2">
                  <code className="text-xs font-semibold text-muted">{f.number}</code>
                  <b className="text-sm">{f.title}</b>
                  {f.assetDown && <Status tone="bad">Out of use</Status>}
                  <Status tone={PRIORITY[f.priority][1]}>{PRIORITY[f.priority][0]}</Status>
                </span>
                <span className="mt-0.5 block text-sm text-muted">
                  {[f.asset ?? f.location, f.symptom, `reported by ${f.reportedBy} ${dateTime(f.createdAt)}`].filter(Boolean).join(' · ')}
                </span>
                {f.description && <span className="mt-1 block text-sm">{f.description}</span>}
                {f.status === 'Rejected' && <span className="mt-1 block text-sm text-muted">Turned down: {f.rejectReason}</span>}
              </span>
              {f.status === 'New' && canWork ? (
                <span className="flex gap-2">
                  <button className="btn btn-primary" onClick={() => setAccepting(f)}>Make a work order</button>
                  <button className="btn btn-ghost" onClick={() => reject(f)}>Turn down</button>
                </span>
              ) : f.workOrderId ? (
                <button className="btn btn-ghost" onClick={() => onOpen(f.workOrderId!)}>{f.workOrder?.number} · {STATUS[(f.workOrder?.status ?? 'Open') as WoStatus]?.[0]}</button>
              ) : (
                <Status tone={f.status === 'Resolved' ? 'good' : f.status === 'Rejected' ? 'plain' : 'info'}>{f.status === 'New' ? 'Waiting' : f.status}</Status>
              )}
            </li>
          ))}
        </ul>
      )}
      {accepting && <AcceptSheet fault={accepting} onClose={() => setAccepting(null)} onDone={(id) => (setAccepting(null), reload(), onChange(), onOpen(id))} />}
    </div>
  );
}

function usePickers() {
  const assets = useApi<{ items: AssetOption[] }>('/assets');
  const locations = useApi<Option[]>('/locations');
  const departments = useApi<Option[]>('/departments');
  const employees = useApi<{ id: string; name: string; jobTitle: string }[]>('/directory/employees');
  const suppliers = useApi<Option[]>('/directory/suppliers');
  return { assets: assets.data?.items ?? [], locations: locations.data ?? [], departments: departments.data ?? [], employees: employees.data ?? [], suppliers: suppliers.data ?? [] };
}

function AcceptSheet({ fault, onClose, onDone }: { fault: Fault; onClose: () => void; onDone: (id: string) => void }) {
  const { employees } = usePickers();
  const [f, setF] = useState({ title: fault.title, assigneeEmployeeId: '', assignedTo: '', plannedStart: todayIso(), dueDate: '', priority: fault.priority as string, estimatedHours: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      const r = await api<{ id: string }>(`/maintenance/faults/${fault.id}/accept`, { body: { title: f.title, assigneeEmployeeId: f.assigneeEmployeeId || null, assignedTo: f.assignedTo, plannedStart: f.plannedStart || null, dueDate: f.dueDate || null, priority: f.priority, estimatedHours: Number(f.estimatedHours) || 0 } });
      onDone(r.id);
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title={`Work order for ${fault.number}`} description={fault.assetDown ? 'It’s out of use: it goes in as a breakdown, and its downtime counts from the report.' : 'It goes in as a repair.'}>
      <div className="space-y-4">
        <Field label="The job"><input className="field" value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} /></Field>
        <Field label="Priority"><Chips label="Priority" value={f.priority} onChange={(v) => setF({ ...f, priority: v })} options={(Object.keys(PRIORITY) as Priority[]).map((p) => [p, PRIORITY[p][0]])} /></Field>
        <Assignee employees={employees} id={f.assigneeEmployeeId} name={f.assignedTo} onChange={(id, name) => setF({ ...f, assigneeEmployeeId: id, assignedTo: name })} />
        <div className="grid grid-cols-2 gap-3">
          <Field label="Start on"><input className="field" type="date" value={f.plannedStart} onChange={(e) => setF({ ...f, plannedStart: e.target.value })} /></Field>
          <Field label="Done by"><input className="field" type="date" value={f.dueDate} onChange={(e) => setF({ ...f, dueDate: e.target.value })} /></Field>
        </div>
        <Field label="Hours it should take"><input className="field num" inputMode="decimal" value={f.estimatedHours} onChange={(e) => setF({ ...f, estimatedHours: e.target.value })} /></Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy} onClick={save}>Make the work order</button></SheetFooter>
      </div>
    </Sheet>
  );
}

/** Who does the job: an employee from the list, or anyone else by name (a contractor's technician). */
function Assignee({ employees, id, name, onChange }: { employees: { id: string; name: string; jobTitle: string }[]; id: string; name: string; onChange: (id: string, name: string) => void }) {
  return (
    <Field label="Who does it" hint="An employee, or type a name (a contractor’s technician).">
      <div className="grid grid-cols-2 gap-2">
        <select className="field" value={id} onChange={(e) => onChange(e.target.value, e.target.value ? employees.find((x) => x.id === e.target.value)?.name ?? '' : '')}>
          <option value="">Choose an employee…</option>
          {employees.map((e) => <option key={e.id} value={e.id}>{e.name}{e.jobTitle ? ` (${e.jobTitle})` : ''}</option>)}
        </select>
        <input className="field" value={id ? '' : name} disabled={!!id} onChange={(e) => onChange('', e.target.value)} placeholder="or a name" />
      </div>
    </Field>
  );
}

export function FaultForm({ onClose, onSaved, assetId }: { onClose: () => void; onSaved: () => void; assetId?: string }) {
  const { assets, locations } = usePickers();
  const codes = useApi<{ modes: string[] }>('/maintenance/codes');
  const [f, setF] = useState({ assetId: assetId ?? '', locationId: '', title: '', description: '', symptom: '', priority: 'Normal', assetDown: false });
  const [state, setState] = useState({ busy: false, error: '', done: '' });
  const save = async () => {
    setState({ busy: true, error: '', done: '' });
    try {
      const r = await api<{ number: string }>('/maintenance/faults', { body: { ...f, assetId: f.assetId || null, locationId: f.locationId || null } });
      setState({ busy: false, error: '', done: r.number });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message, done: '' });
    }
  };
  if (state.done)
    return (
      <Sheet open onClose={onSaved} title="Fault reported">
        <p className="rounded-md bg-good-soft p-3 text-sm text-good"><b>{state.done}</b> is with maintenance. You’ll see here when it’s taken on.</p>
        <SheetFooter cancel="Close" />
      </Sheet>
    );
  return (
    <Sheet open onClose={onClose} title="Report a fault" description="Say what’s broken and where. Maintenance decides what happens next.">
      <div className="space-y-4">
        <Field label="Which asset">
          <select className="field" value={f.assetId} onChange={(e) => setF({ ...f, assetId: e.target.value })}>
            <option value="">Not in the register / not sure</option>
            {assets.map((a) => <option key={a.id} value={a.id}>{a.tag} · {a.name}</option>)}
          </select>
        </Field>
        {!f.assetId && (
          <Field label="Where">
            <select className="field" value={f.locationId} onChange={(e) => setF({ ...f, locationId: e.target.value })}>
              <option value="">Choose a place…</option>
              {locations.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </select>
          </Field>
        )}
        <Field label="What’s wrong"><input className="field" autoFocus value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} placeholder="Generator won’t start" /></Field>
        <Field label="What it’s doing">
          <select className="field" value={f.symptom} onChange={(e) => setF({ ...f, symptom: e.target.value })}>
            <option value="">Choose…</option>
            {(codes.data?.modes ?? []).map((m) => <option key={m}>{m}</option>)}
          </select>
        </Field>
        <Field label="More detail (optional)"><textarea className="field min-h-20" value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} placeholder="When it started, any noise or smell, what you tried" /></Field>
        <label className="flex items-center gap-2.5 text-sm"><input type="checkbox" className="h-4 w-4" checked={f.assetDown} onChange={(e) => setF({ ...f, assetDown: e.target.checked })} /> It can’t be used at all</label>
        <Field label="How urgent"><Chips label="Priority" value={f.priority} onChange={(v) => setF({ ...f, priority: v })} options={(Object.keys(PRIORITY) as Priority[]).map((p) => [p, PRIORITY[p][0]])} /></Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy || f.title.trim().length < 3} onClick={save}>Report it</button></SheetFooter>
      </div>
    </Sheet>
  );
}

// ---------------------------------------------------------------------------------------------- work order form

type WoDetail = {
  id: string; number: string; type: string; priority: Priority; status: WoStatus; title: string; description: string;
  asset: { id: string; tag: string; name: string; criticality: string; status: string } | null; assetId: string | null; locationId: string | null; location: string;
  departmentId: string | null; department: string; supplierId: string | null; contractor: string; assignedTo: string; assigneeEmployeeId: string | null;
  plannedStart: string | null; dueDate: string | null; estimatedHours: number; estimatedCost: number; startedAt: string | null; completedAt: string | null; closedAt: string | null;
  downFrom: string | null; downTo: string | null; downtimeHours: number | null; onHoldReason: string; failureMode: string; failureCause: string; remedy: string; completionNotes: string; cancelReason: string;
  createdBy: string; completedBy: string; closedBy: string; createdAt: string; overdue: boolean;
  fault: { number: string; reportedBy: string; createdAt: string; description: string } | null;
  parts: { id: string; itemId: string | null; description: string; unit: string; plannedQuantity: number; issuedQuantity: number; unitCost: number; inStock: number | null }[];
  labour: { id: string; worker: string; date: string; hours: number; note: string; hourlyRate: number | null; cost: number | null }[];
  costs: { id: string; description: string; amount: number; method: string; reference: string; paidTo: string; date: string; actor: string }[];
  purchases: { id: string; number: string; kind: string; status: string; net: number; total: number; supplier: string }[];
  totals: { parts: number; labour: number | null; direct: number; bills: number; services: number; onOrder: number; plannedParts: number; total: number; hours: number; estimate: number; labourHidden: boolean };
  codes: { modes: string[]; causes: string[]; remedies: string[] };
  tools: { id: string; assetId: string; tag: string; name: string; status: string; checkedOutTo: string; checkedOutAt: string | null; returnedAt: string | null; returnCondition: string; busyOn: string | null }[];
  readiness: { key: string; ok: boolean; label: string }[]; ready: boolean;
};

function WorkOrderForm({ existing, onClose, onSaved }: { existing?: WoDetail; onClose: () => void; onSaved: (id: string) => void }) {
  const { assets, locations, departments, employees, suppliers } = usePickers();
  const [f, setF] = useState({
    title: existing?.title ?? '', description: existing?.description ?? '', type: existing?.type ?? 'Corrective', priority: existing?.priority ?? 'Normal',
    assetId: existing?.assetId ?? '', locationId: existing?.locationId ?? '', departmentId: existing?.departmentId ?? '', assigneeEmployeeId: existing?.assigneeEmployeeId ?? '',
    assignedTo: existing?.assignedTo ?? '', supplierId: existing?.supplierId ?? '', plannedStart: existing?.plannedStart ?? '', dueDate: existing?.dueDate ?? '',
    estimatedHours: existing?.estimatedHours ? String(existing.estimatedHours) : '', estimatedCost: existing?.estimatedCost ? String(existing.estimatedCost / 100) : '',
  });
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      const body = {
        ...f, assetId: f.assetId || null, locationId: f.locationId || null, departmentId: f.departmentId || null, assigneeEmployeeId: f.assigneeEmployeeId || null, supplierId: f.supplierId || null,
        plannedStart: f.plannedStart || null, dueDate: f.dueDate || null, estimatedHours: Number(f.estimatedHours) || 0, estimatedCost: f.estimatedCost ? toCents(f.estimatedCost) : 0,
      };
      const r = await api<{ id: string }>(existing ? `/maintenance/work-orders/${existing.id}` : '/maintenance/work-orders', { method: existing ? 'PUT' : 'POST', body });
      onSaved(r.id);
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title={existing ? `Change ${existing.number}` : 'New work order'} description="What to do, on what, who does it and when. It’s planned once someone and a start day are set." size="md">
      <div className="space-y-4">
        <Field label="The job"><input className="field" autoFocus value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} placeholder="Replace the conveyor belt" /></Field>
        <Field label="Kind of work"><Chips label="Type" value={f.type} onChange={(v) => setF({ ...f, type: v })} options={TYPES} /></Field>
        <Field label="Priority"><Chips label="Priority" value={f.priority} onChange={(v) => setF({ ...f, priority: v as Priority })} options={(Object.keys(PRIORITY) as Priority[]).map((p) => [p, PRIORITY[p][0]])} /></Field>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Asset">
            <select className="field" value={f.assetId} onChange={(e) => {
              const a = assets.find((x) => x.id === e.target.value);
              setF({ ...f, assetId: e.target.value, locationId: a?.locationId ?? f.locationId, departmentId: a?.departmentId ?? f.departmentId });
            }}>
              <option value="">None (a place or the building)</option>
              {assets.map((a) => <option key={a.id} value={a.id}>{a.tag} · {a.name}</option>)}
            </select>
          </Field>
          <Field label="Location">
            <select className="field" value={f.locationId} onChange={(e) => setF({ ...f, locationId: e.target.value })}>
              <option value="">Not set</option>
              {locations.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </select>
          </Field>
        </div>
        <Field label="Charge the cost to" hint="Defaults to the asset’s department.">
          <select className="field" value={f.departmentId} onChange={(e) => setF({ ...f, departmentId: e.target.value })}>
            <option value="">No department</option>
            {departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </select>
        </Field>
        <Assignee employees={employees} id={f.assigneeEmployeeId} name={f.assignedTo} onChange={(id, name) => setF({ ...f, assigneeEmployeeId: id, assignedTo: name })} />
        <Field label="Contractor (optional)" hint="Their orders and bills become this job’s service costs.">
          <select className="field" value={f.supplierId} onChange={(e) => setF({ ...f, supplierId: e.target.value })}>
            <option value="">None</option>
            {suppliers.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select>
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Start on"><input className="field" type="date" value={f.plannedStart} onChange={(e) => setF({ ...f, plannedStart: e.target.value })} /></Field>
          <Field label="Done by"><input className="field" type="date" value={f.dueDate} onChange={(e) => setF({ ...f, dueDate: e.target.value })} /></Field>
          <Field label="Hours it should take"><input className="field num" inputMode="decimal" value={f.estimatedHours} onChange={(e) => setF({ ...f, estimatedHours: e.target.value })} /></Field>
          <Field label="Expected cost (KSh)"><input className="field num" inputMode="decimal" value={f.estimatedCost} onChange={(e) => setF({ ...f, estimatedCost: e.target.value })} /></Field>
        </div>
        <Field label="Instructions"><textarea className="field min-h-24" value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} placeholder="Steps, safety notes, what to check" /></Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy || f.title.trim().length < 3} onClick={save}>{existing ? 'Save changes' : 'Create work order'}</button></SheetFooter>
      </div>
    </Sheet>
  );
}

// ---------------------------------------------------------------------------------------------- the work order

function WorkOrderSheet({ id, onClose, onChange }: { id: string; onClose: () => void; onChange: () => void }) {
  const session = useSession();
  const canWork = can(session, 'maintain');
  const canClose = canWork || can(session, 'finance');
  const { data: w, error, reload } = useApi<WoDetail>(`/maintenance/work-orders/${id}`);
  const [state, setState] = useState({ busy: '', error: '' });
  const [editing, setEditing] = useState(false);
  const [completing, setCompleting] = useState(false);
  const [ordering, setOrdering] = useState(false);
  const run = async (what: string, fn: () => Promise<unknown>) => {
    setState({ busy: what, error: '' });
    try {
      await fn();
      reload();
      onChange();
      setState({ busy: '', error: '' });
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };
  const act = (kind: string, extra: Record<string, unknown> = {}) => run(kind, () => api(`/maintenance/work-orders/${id}/actions`, { body: { kind, ...extra } }));
  if (error) return <Sheet open onClose={onClose} title="Work order"><ErrorNote>{error}</ErrorNote></Sheet>;
  if (!w) return <Sheet open onClose={onClose} title="Work order"><Loading /></Sheet>;
  const live = ['Open', 'Planned', 'InProgress', 'OnHold'].includes(w.status);
  const editable = live && canWork;
  const t = w.totals;
  const facts: [string, React.ReactNode][] = [
    ['Asset', w.asset ? <Link className="text-brand hover:underline" to={`/assets?id=${w.asset.id}`}>{w.asset.tag} · {w.asset.name}</Link> : '—'],
    ['Location', w.location || '—'], ['Charged to', w.department || '—'], ['Who', [w.assignedTo, w.contractor].filter(Boolean).join(' · ') || 'Not assigned'],
    ['Start', w.plannedStart ? dayYear(w.plannedStart) : 'Not scheduled'], ['Done by', w.dueDate ? <span className={cn(w.overdue && 'font-semibold text-bad')}>{dayYear(w.dueDate)}{w.overdue && ' (overdue)'}</span> : '—'],
    ['Estimate', `${w.estimatedHours ? `${w.estimatedHours} h` : '—'}${w.estimatedCost ? ` · ${ksh(w.estimatedCost)}` : ''}`],
    ...(w.downFrom ? [['Out of use', `${dateTime(w.downFrom)} → ${w.downTo ? dateTime(w.downTo) : 'still down'} (${w.downtimeHours} h)`] as [string, string]] : []),
  ];
  return (
    <Sheet open onClose={onClose} title={`${w.number} · ${w.title}`} description={`${typeName(w.type)} · opened by ${w.createdBy} ${dateTime(w.createdAt)}`} size="lg">
      <div className="space-y-6 text-sm">
        <div className="flex flex-wrap items-center gap-2">
          <Status tone={STATUS[w.status][1]}>{STATUS[w.status][0]}</Status>
          <Status tone={PRIORITY[w.priority][1]}>{PRIORITY[w.priority][0]} priority</Status>
          {w.asset?.criticality === 'High' && <Status tone="warn">Critical asset</Status>}
          {w.status === 'OnHold' && <span className="text-muted">Waiting: {w.onHoldReason}</span>}
          <span className="flex-1" />
          {editable && <button className="btn btn-ghost" onClick={() => setEditing(true)}>Change</button>}
        </div>

        {canWork && (
          <div className="flex flex-wrap gap-2">
            {(w.status === 'Open' || w.status === 'Planned') && <button className="btn btn-primary" disabled={!!state.busy} onClick={() => act('start')}><Play className="h-4 w-4" /> Start work</button>}
            {w.status === 'OnHold' && <button className="btn btn-primary" disabled={!!state.busy} onClick={() => act('resume')}><Play className="h-4 w-4" /> Resume</button>}
            {live && <button className="btn btn-primary" disabled={!!state.busy} onClick={() => setCompleting(true)}><CircleCheck className="h-4 w-4" /> Complete</button>}
            {live && w.status !== 'OnHold' && <button className="btn btn-ghost" disabled={!!state.busy} onClick={async () => { const reason = await askDialog({ title: 'Put on hold?', label: 'What is it waiting for?', placeholder: 'Parts on order, the contractor, the machine to be free', required: true, confirm: 'Put on hold' }); if (reason) act('hold', { reason }); }}><Pause className="h-4 w-4" /> Hold</button>}
            {w.status === 'Completed' && <button className="btn btn-ghost" disabled={!!state.busy} onClick={() => act('reopen')}><RotateCcw className="h-4 w-4" /> Reopen</button>}
            {live && <button className="btn btn-ghost text-bad" disabled={!!state.busy} onClick={async () => { const reason = await askDialog({ title: `Cancel ${w.number}?`, label: 'Why', required: true, confirm: 'Cancel the job', danger: true }); if (reason) act('cancel', { reason }); }}>Cancel job</button>}
          </div>
        )}
        {w.status === 'Completed' && canClose && (
          <div className="flex flex-wrap items-center gap-3 rounded-md bg-good-soft p-3">
            <span className="flex-1 text-good">The work is done. Check the costs below, then close it: nothing more can be charged after that.</span>
            <button className="btn btn-primary" disabled={!!state.busy} onClick={async () => (await confirmDialog({ title: `Close ${w.number}?`, body: `Final cost ${ksh(t.total)}${t.estimate ? ` against an estimate of ${ksh(t.estimate)}` : ''}. Nothing more can be charged to it.`, confirm: 'Close it' })) && act('close')}><Lock className="h-4 w-4" /> Close financially</button>
          </div>
        )}
        {state.error && <ErrorNote>{state.error}</ErrorNote>}

        <dl className="grid grid-cols-[7.5rem_1fr] gap-x-3 gap-y-1.5 sm:grid-cols-[8rem_1fr_8rem_1fr]">
          {facts.map(([k, v]) => <Fragment key={k}><dt className="text-muted">{k}</dt><dd className="min-w-0">{v}</dd></Fragment>)}
        </dl>
        {w.fault && (
          <div className="rounded-md bg-paper p-3 ring-1 ring-line">
            <p className="font-semibold">From fault {w.fault.number}</p>
            <p className="text-muted">Reported by {w.fault.reportedBy}, {dateTime(w.fault.createdAt)}{w.fault.description && `: ${w.fault.description}`}</p>
          </div>
        )}
        {w.description && <div><p className="mb-1 font-semibold">Instructions</p><p className="whitespace-pre-line">{w.description}</p></div>}
        {(w.status === 'Completed' || w.status === 'Closed') && (
          <div className="rounded-md bg-paper p-3 ring-1 ring-line">
            <p className="font-semibold">Done {w.completedAt && dateTime(w.completedAt)} by {w.completedBy}</p>
            {(w.failureMode || w.remedy) && <p className="mt-1">{[w.failureMode && `Failed: ${w.failureMode}`, w.failureCause && `Why: ${w.failureCause}`, w.remedy && `Done: ${w.remedy}`].filter(Boolean).join(' · ')}</p>}
            {w.completionNotes && <p className="mt-1 text-muted">{w.completionNotes}</p>}
            {w.closedAt && <p className="mt-1 text-muted">Closed {dateTime(w.closedAt)} by {w.closedBy}</p>}
          </div>
        )}

        <DocTrail kind="workorder" id={w.id} />
        {live && <Readiness w={w} />}
        <Costs w={w} />
        <Parts w={w} editable={editable} onChange={() => (reload(), onChange())} />
        <Tools w={w} editable={editable} onChange={reload} />
        <LabourSection w={w} editable={editable} onChange={reload} />
        <Payments w={w} editable={live && canClose} onChange={reload} />
        <section>
          <div className="mb-2 flex items-center justify-between">
            <p className="font-semibold">Orders and bills for this job</p>
            {editable && <button className="btn btn-ghost" onClick={() => setOrdering(true)}><ShoppingBag className="h-4 w-4" /> Order parts or a contractor</button>}
          </div>
          {w.purchases.length === 0 ? <p className="text-muted">None. Parts not in the store, and contractors, are ordered from here; their bills become this job’s cost.</p> : (
            <ul className="divide-y divide-line rounded-md ring-1 ring-line">
              {w.purchases.map((p) => (
                <li key={p.id} className="flex items-center gap-3 px-3 py-2">
                  <Link className="font-semibold text-brand hover:underline" to={`/purchases/${p.id}`}>{p.number}</Link>
                  <span className="flex-1 truncate text-muted">{p.kind === 'Order' ? 'Order' : 'Bill'} · {p.supplier}</span>
                  <span className="num">{ksh(p.total)}</span>
                  <Status tone={p.status === 'Void' ? 'plain' : p.status === 'Draft' ? 'warn' : 'info'}>{p.status}</Status>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
      {editing && <WorkOrderForm existing={w} onClose={() => setEditing(false)} onSaved={() => (setEditing(false), reload(), onChange())} />}
      {completing && <CompleteSheet w={w} onClose={() => setCompleting(false)} onDone={() => (setCompleting(false), reload(), onChange())} />}
      {ordering && <OrderSheet w={w} onClose={() => setOrdering(false)} onDone={() => (setOrdering(false), reload())} />}
    </Sheet>
  );
}

function Costs({ w }: { w: WoDetail }) {
  const t = w.totals;
  const rows: [string, number | null, string?][] = [
    ['Parts from the store', t.parts], ['Labour', t.labour, t.labourHidden ? `${t.hours} h (cost shown to finance and HR)` : `${t.hours} h`],
    ['Paid out and billed', t.services], ...(t.onOrder > 0 ? [['Still on order', t.onOrder, 'not yet billed'] as [string, number, string]] : []),
  ];
  const over = t.estimate > 0 && t.total > t.estimate;
  return (
    <section className="rounded-md ring-1 ring-line">
      <div className="grid grid-cols-2 divide-x divide-line border-b border-line sm:grid-cols-4">
        {rows.map(([k, v, note]) => (
          <div key={k} className="px-3 py-2.5">
            <p className="text-xs text-muted">{k}</p>
            <p className="num font-semibold">{v === null ? '—' : ksh(v)}</p>
            {note && <p className="text-xs text-muted">{note}</p>}
          </div>
        ))}
      </div>
      <div className="flex items-center justify-between px-3 py-2.5">
        <span className="font-semibold">Cost of the job{t.labourHidden && ' (without labour)'}</span>
        <span className={cn('num text-base font-semibold', over && 'text-bad')}>{ksh(t.total)}{t.estimate > 0 && <span className="ml-2 text-xs font-normal text-muted">estimate {ksh(t.estimate)}</span>}</span>
      </div>
    </section>
  );
}

function Parts({ w, editable, onChange }: { w: WoDetail; editable: boolean; onChange: () => void }) {
  const items = useApi<{ id: string; name: string; unit: string; trackStock: boolean; averageCost: number }[]>(editable ? '/items' : null);
  const [f, setF] = useState({ itemId: '', description: '', quantity: '1', unitCost: '' });
  const [state, setState] = useState({ busy: '', error: '' });
  const run = async (what: string, fn: () => Promise<unknown>) => {
    setState({ busy: what, error: '' });
    try { await fn(); onChange(); setState({ busy: '', error: '' }); setF({ itemId: '', description: '', quantity: '1', unitCost: '' }); } catch (e) { setState({ busy: '', error: (e as Error).message }); }
  };
  const issue = async (p: WoDetail['parts'][number], back = false) => {
    const left = back ? p.issuedQuantity : Math.max(p.plannedQuantity - p.issuedQuantity, 1);
    const qty = await askDialog({ title: back ? `Return ${p.description} to the store?` : `Issue ${p.description}?`, body: back ? 'Unused parts go back into stock at what they were issued at.' : 'They leave the store and their cost goes on this job.', label: 'How many', placeholder: String(left), confirm: back ? 'Return' : 'Issue' });
    if (qty === null) return;
    run(p.id, () => api(`/maintenance/work-orders/${w.id}/parts/${p.id}/${back ? 'return' : 'issue'}`, { body: { quantity: Number(qty || left) } }));
  };
  return (
    <section>
      <p className="mb-2 font-semibold">Parts and materials</p>
      {w.parts.length > 0 && (
        <div className="overflow-x-auto rounded-md ring-1 ring-line">
          <table className="w-full text-sm">
            <thead className="bg-paper text-left text-xs text-muted"><tr><th className="px-3 py-2">Part</th><th className="px-3 py-2 text-right">Needed</th><th className="px-3 py-2 text-right">Issued</th><th className="px-3 py-2 text-right">In stock</th><th className="px-3 py-2 text-right">Cost each</th><th /></tr></thead>
            <tbody className="divide-y divide-line">
              {w.parts.map((p) => (
                <tr key={p.id}>
                  <td className="px-3 py-2">{p.description}{!p.itemId && <span className="ml-1 text-xs text-muted">(to buy)</span>}</td>
                  <td className="num px-3 py-2 text-right">{p.plannedQuantity} {p.unit}</td>
                  <td className="num px-3 py-2 text-right">{p.issuedQuantity}</td>
                  <td className={cn('num px-3 py-2 text-right', p.inStock !== null && p.inStock < p.plannedQuantity - p.issuedQuantity && 'text-bad')}>{p.inStock ?? '—'}</td>
                  <td className="num px-3 py-2 text-right">{ksh(p.unitCost)}</td>
                  <td className="px-2 py-1.5 text-right whitespace-nowrap">
                    {editable && p.itemId && <button className="btn btn-ghost px-2 py-1 text-xs" disabled={!!state.busy} onClick={() => issue(p)}>Issue</button>}
                    {editable && p.itemId && p.issuedQuantity > 0 && <button className="btn btn-ghost px-2 py-1 text-xs" disabled={!!state.busy} onClick={() => issue(p, true)} aria-label="Return to store"><Undo2 className="h-3.5 w-3.5" /></button>}
                    {editable && p.issuedQuantity === 0 && <button className="btn btn-ghost px-2 py-1 text-xs" disabled={!!state.busy} onClick={() => run(p.id, () => api(`/maintenance/work-orders/${w.id}/parts/${p.id}`, { method: 'DELETE' }))} aria-label="Remove"><Trash2 className="h-3.5 w-3.5" /></button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {editable && (
        <div className="mt-2 grid gap-2 sm:grid-cols-[1fr_6rem_8rem_auto]">
          <select className="field" value={f.itemId} onChange={(e) => setF({ ...f, itemId: e.target.value, description: '' })} aria-label="Part">
            <option value="">A part to buy (type it) or choose from stock…</option>
            {(items.data ?? []).map((i) => <option key={i.id} value={i.id}>{i.name}{i.trackStock ? '' : ' (not stocked)'}</option>)}
          </select>
          <input className="field num" inputMode="decimal" value={f.quantity} onChange={(e) => setF({ ...f, quantity: e.target.value })} aria-label="Quantity" />
          <input className="field num" inputMode="decimal" disabled={!!f.itemId} value={f.unitCost} onChange={(e) => setF({ ...f, unitCost: e.target.value })} placeholder="KSh each" aria-label="Cost each" />
          <button className="btn btn-ghost" disabled={!!state.busy || (!f.itemId && f.description.trim().length < 2)} onClick={() => run('add', () => api(`/maintenance/work-orders/${w.id}/parts`, { body: { itemId: f.itemId || null, description: f.description, quantity: Number(f.quantity) || 1, unitCost: f.unitCost ? toCents(f.unitCost) : null } }))}><Plus className="h-4 w-4" /> Add</button>
          {!f.itemId && <input className="field sm:col-span-4" value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} placeholder="Part to buy, e.g. Starter motor 24V" aria-label="Part description" />}
        </div>
      )}
      {state.error && <div className="mt-2"><ErrorNote>{state.error}</ErrorNote></div>}
    </section>
  );
}

function LabourSection({ w, editable, onChange }: { w: WoDetail; editable: boolean; onChange: () => void }) {
  const employees = useApi<{ id: string; name: string }[]>(editable ? '/directory/employees' : null);
  const [f, setF] = useState({ employeeId: '', worker: '', date: todayIso(), hours: '', rate: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const add = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(`/maintenance/work-orders/${w.id}/labour`, { body: { employeeId: f.employeeId || null, worker: f.worker, date: f.date, hours: Number(f.hours), hourlyRate: f.rate ? toCents(f.rate) : null } });
      setF({ ...f, hours: '', rate: '' });
      onChange();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <section>
      <p className="mb-2 font-semibold">Hours worked</p>
      {w.labour.length > 0 && (
        <ul className="mb-2 divide-y divide-line rounded-md ring-1 ring-line">
          {w.labour.map((l) => (
            <li key={l.id} className="flex items-center gap-3 px-3 py-2">
              <span className="w-24 shrink-0 text-muted">{day(l.date)}</span>
              <span className="flex-1">{l.worker}{l.note && <span className="text-muted"> · {l.note}</span>}</span>
              <span className="num">{l.hours} h</span>
              {l.cost !== null && <span className="num w-24 text-right text-muted">{ksh(l.cost)}</span>}
              {editable && <button className="text-faint hover:text-bad" aria-label="Remove" onClick={() => api(`/maintenance/work-orders/${w.id}/labour/${l.id}`, { method: 'DELETE' }).then(onChange)}><Trash2 className="h-3.5 w-3.5" /></button>}
            </li>
          ))}
        </ul>
      )}
      {editable && (
        <div className="grid gap-2 sm:grid-cols-[1fr_9rem_5rem_auto]">
          <select className="field" value={f.employeeId} onChange={(e) => setF({ ...f, employeeId: e.target.value })} aria-label="Who">
            <option value="">Someone else (type the name)…</option>
            {(employees.data ?? []).map((e) => <option key={e.id} value={e.id}>{e.name}</option>)}
          </select>
          <input className="field" type="date" value={f.date} onChange={(e) => setF({ ...f, date: e.target.value })} aria-label="Day" />
          <input className="field num" inputMode="decimal" value={f.hours} onChange={(e) => setF({ ...f, hours: e.target.value })} placeholder="Hours" aria-label="Hours" />
          <button className="btn btn-ghost" disabled={state.busy || !(Number(f.hours) > 0)} onClick={add}><Plus className="h-4 w-4" /> Add</button>
          {!f.employeeId && (
            <div className="grid grid-cols-2 gap-2 sm:col-span-4">
              <input className="field" value={f.worker} onChange={(e) => setF({ ...f, worker: e.target.value })} placeholder="Name (a casual or contractor)" aria-label="Worker" />
              <input className="field num" inputMode="decimal" value={f.rate} onChange={(e) => setF({ ...f, rate: e.target.value })} placeholder="KSh an hour" aria-label="Rate" />
            </div>
          )}
        </div>
      )}
      {editable && <p className="mt-1 text-xs text-muted">An employee’s hours are costed at their pay. Wages are already in the books through payroll, so hours aren’t posted again.</p>}
      {state.error && <div className="mt-2"><ErrorNote>{state.error}</ErrorNote></div>}
    </section>
  );
}

function Payments({ w, editable, onChange }: { w: WoDetail; editable: boolean; onChange: () => void }) {
  const [f, setF] = useState({ description: '', amount: '', method: 'Mpesa', reference: '', paidTo: '' });
  const [open, setOpen] = useState(false);
  const [state, setState] = useState({ busy: false, error: '' });
  const add = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(`/maintenance/work-orders/${w.id}/costs`, { body: { ...f, amount: toCents(f.amount) } });
      setF({ description: '', amount: '', method: 'Mpesa', reference: '', paidTo: '' });
      setOpen(false);
      onChange();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <section>
      <div className="mb-2 flex items-center justify-between">
        <p className="font-semibold">Money paid out for the job</p>
        {editable && !open && <button className="btn btn-ghost" onClick={() => setOpen(true)}><Plus className="h-4 w-4" /> Record a payment</button>}
      </div>
      {w.costs.length === 0 && !open && <p className="text-muted">None. A fundi paid by M-Pesa or a part bought for cash goes here, straight into the books.</p>}
      {w.costs.length > 0 && (
        <ul className="divide-y divide-line rounded-md ring-1 ring-line">
          {w.costs.map((c) => (
            <li key={c.id} className="flex items-center gap-3 px-3 py-2">
              <span className="w-24 shrink-0 text-muted">{day(c.date)}</span>
              <span className="min-w-0 flex-1 truncate">{c.description}{c.paidTo && <span className="text-muted"> · {c.paidTo}</span>}</span>
              <span className="text-xs text-muted">{c.method}{c.reference && ` ${c.reference}`}</span>
              <span className="num">{ksh(c.amount)}</span>
            </li>
          ))}
        </ul>
      )}
      {open && (
        <div className="mt-2 grid gap-2 rounded-md bg-paper p-3 ring-1 ring-line sm:grid-cols-2">
          <input className="field sm:col-span-2" autoFocus value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} placeholder="What for, e.g. Auto-electrician: starter motor" />
          <input className="field num" inputMode="decimal" value={f.amount} onChange={(e) => setF({ ...f, amount: e.target.value })} placeholder="Amount (KSh)" />
          <select className="field" value={f.method} onChange={(e) => setF({ ...f, method: e.target.value })}><option value="Mpesa">M-Pesa</option><option value="Cash">Cash</option><option value="Bank">Bank</option></select>
          <input className="field" value={f.paidTo} onChange={(e) => setF({ ...f, paidTo: e.target.value })} placeholder="Paid to" />
          <input className="field" value={f.reference} onChange={(e) => setF({ ...f, reference: e.target.value })} placeholder="M-Pesa code or reference" />
          <div className="flex gap-2 sm:col-span-2">
            <button className="btn btn-primary" disabled={state.busy || f.description.trim().length < 3 || !toCents(f.amount)} onClick={add}>Record {f.amount && ksh(toCents(f.amount))}</button>
            <button className="btn btn-ghost" onClick={() => setOpen(false)}>Cancel</button>
          </div>
        </div>
      )}
      {state.error && <div className="mt-2"><ErrorNote>{state.error}</ErrorNote></div>}
    </section>
  );
}

function CompleteSheet({ w, onClose, onDone }: { w: WoDetail; onClose: () => void; onDone: () => void }) {
  const reactive = w.type === 'Breakdown' || w.type === 'Corrective';
  const [f, setF] = useState({ failureMode: w.failureMode, failureCause: '', remedy: '', notes: '', back: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(`/maintenance/work-orders/${w.id}/actions`, { body: { kind: 'complete', failureMode: f.failureMode, failureCause: f.failureCause, remedy: f.remedy, notes: f.notes, backInServiceAt: f.back ? new Date(f.back).toISOString() : null } });
      onDone();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const pick = (label: string, key: 'failureMode' | 'failureCause' | 'remedy', list: string[], hint?: string) => (
    <Field label={label} hint={hint}>
      <select className="field" value={f[key]} onChange={(e) => setF({ ...f, [key]: e.target.value })}>
        <option value="">Choose…</option>
        {list.map((x) => <option key={x}>{x}</option>)}
      </select>
    </Field>
  );
  return (
    <Sheet open onClose={onClose} title={`Complete ${w.number}`} description="The work is done and the asset is back in service. What’s recorded here builds the reliability figures (time between failures, time to repair, repeat failures).">
      <div className="space-y-4">
        {pick('What failed', 'failureMode', w.codes.modes, reactive ? 'Needed for a breakdown or repair.' : undefined)}
        {pick('Why it failed', 'failureCause', w.codes.causes, w.asset?.criticality === 'High' ? 'Needed: this is a critical asset.' : undefined)}
        {pick('What was done', 'remedy', w.codes.remedies)}
        <Field label="Notes"><textarea className="field min-h-20" value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} placeholder="What was found, what to watch next time" /></Field>
        {w.downFrom && <Field label="Back in service at" hint="Leave empty for now."><input className="field" type="datetime-local" value={f.back} onChange={(e) => setF({ ...f, back: e.target.value })} /></Field>}
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy} onClick={save}><CircleCheck className="h-4 w-4" /> Mark complete</button></SheetFooter>
      </div>
    </Sheet>
  );
}

function OrderSheet({ w, onClose, onDone }: { w: WoDetail; onClose: () => void; onDone: () => void }) {
  const suppliers = useApi<Option[]>('/directory/suppliers');
  const [f, setF] = useState({ supplierId: w.supplierId ?? '', serviceDescription: '', serviceCost: '' });
  const [state, setState] = useState<{ busy: boolean; error: string; po?: { id: string; number: string } }>({ busy: false, error: '' });
  const missing = w.parts.filter((p) => p.plannedQuantity - p.issuedQuantity - Math.max(0, p.inStock ?? 0) > 0);
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      const po = await api<{ id: string; number: string }>(`/maintenance/work-orders/${w.id}/order`, { body: { supplierId: f.supplierId, serviceDescription: f.serviceDescription, serviceCost: f.serviceCost ? toCents(f.serviceCost) : null } });
      setState({ busy: false, error: '', po });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  if (state.po)
    return (
      <Sheet open onClose={onDone} title="Order drafted">
        <p className="rounded-md bg-good-soft p-3 text-sm text-good"><b>{state.po.number}</b> is a draft for this job. Procurement checks it and sends it; its bill becomes the job’s cost.</p>
        <SheetFooter cancel="Close"><Link className="btn btn-primary" to={`/purchases/${state.po.id}`}>Open {state.po.number}</Link></SheetFooter>
      </Sheet>
    );
  return (
    <Sheet open onClose={onClose} title="Order for this job" description="A draft purchase order with the parts that aren’t in the store, and the contractor’s work if you add it.">
      <div className="space-y-4 text-sm">
        <Field label="Supplier or contractor">
          <select className="field" value={f.supplierId} onChange={(e) => setF({ ...f, supplierId: e.target.value })}>
            <option value="">Choose…</option>
            {(suppliers.data ?? []).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select>
        </Field>
        <div className="rounded-md bg-paper p-3 ring-1 ring-line">
          <p className="font-semibold">Parts to buy</p>
          {missing.length === 0 ? <p className="text-muted">None: everything planned is in the store.</p> : missing.map((p) => <p key={p.id}>{p.plannedQuantity - p.issuedQuantity - Math.max(0, p.inStock ?? 0)} {p.unit} {p.description}</p>)}
        </div>
        <Field label="Contractor’s work (optional)"><input className="field" value={f.serviceDescription} onChange={(e) => setF({ ...f, serviceDescription: e.target.value })} placeholder="Rewind the motor, labour and transport" /></Field>
        <Field label="Their price (KSh)"><input className="field num" inputMode="decimal" value={f.serviceCost} onChange={(e) => setF({ ...f, serviceCost: e.target.value })} /></Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy || !f.supplierId} onClick={save}>Draft the order</button></SheetFooter>
      </div>
    </Sheet>
  );
}

// ---------------------------------------------------------------------------------------------- schedule

type SchedRow = { id: string; number: string; type: string; priority: Priority; status: WoStatus; title: string; asset: string | null; assignedTo: string; plannedStart: string | null; dueDate: string | null; estimatedHours: number };

function Schedule({ onOpen }: { onOpen: (id: string) => void }) {
  const { data, error } = useApi<{ days: { date: string; hours: number; items: SchedRow[] }[]; late: SchedRow[]; unscheduled: SchedRow[]; byPerson: { person: string; jobs: number; hours: number }[] }>('/maintenance/schedule?days=14');
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const Row = ({ r }: { r: SchedRow }) => (
    <button className="flex w-full items-center gap-3 px-3 py-2 text-left hover:bg-paper" onClick={() => onOpen(r.id)}>
      <span className={cn('h-2 w-2 shrink-0 rounded-full', r.priority === 'Emergency' ? 'bg-bad' : r.priority === 'High' ? 'bg-warn' : 'bg-brand')} />
      <code className="text-xs text-muted">{r.number}</code>
      <span className="min-w-0 flex-1 truncate">{r.title}{r.asset && <span className="text-muted"> · {r.asset}</span>}</span>
      <span className="hidden text-xs text-muted sm:inline">{r.assignedTo || 'Not assigned'}</span>
      {r.estimatedHours > 0 && <span className="num text-xs text-muted">{r.estimatedHours} h</span>}
    </button>
  );
  const withWork = data.days.filter((d) => d.items.length > 0);
  return (
    <div className="grid grid-cols-1 gap-4 lg:grid-cols-[minmax(0,1fr)_20rem]">
      <div className="min-w-0 space-y-4">
        {data.late.length > 0 && (
          <section className="card overflow-hidden">
            <p className="border-b border-line bg-bad-soft px-3 py-2 text-sm font-semibold text-bad">Late ({data.late.length})</p>
            <div className="divide-y divide-line text-sm">{data.late.map((r) => <Row key={r.id} r={r} />)}</div>
          </section>
        )}
        {withWork.length === 0 ? <Empty title="Nothing scheduled in the next two weeks">Give work orders a start day to see them here.</Empty> : withWork.map((d) => (
          <section key={d.date} className="card overflow-hidden">
            <p className="flex items-center justify-between border-b border-line bg-paper px-3 py-2 text-sm font-semibold">
              <span>{new Date(`${d.date}T00:00:00`).toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'short' })}{d.date === todayIso() && ' · today'}</span>
              <span className="num text-xs font-normal text-muted">{d.hours} h planned</span>
            </p>
            <div className="divide-y divide-line text-sm">{d.items.map((r) => <Row key={r.id} r={r} />)}</div>
          </section>
        ))}
      </div>
      <div className="min-w-0 space-y-4">
        <section className="card p-4">
          <p className="mb-2 flex items-center gap-2 font-semibold"><CalendarClock className="h-4 w-4 text-muted" /> Who’s doing what</p>
          {data.byPerson.length === 0 ? <p className="text-sm text-muted">No one has scheduled work.</p> : (
            <ul className="space-y-1.5 text-sm">{data.byPerson.map((p) => <li key={p.person} className="flex justify-between gap-2"><span className="truncate">{p.person}</span><span className="num text-muted">{p.jobs} jobs · {p.hours} h</span></li>)}</ul>
          )}
        </section>
        <section className="card overflow-hidden">
          <p className="border-b border-line bg-paper px-3 py-2 text-sm font-semibold">Not scheduled ({data.unscheduled.length})</p>
          {data.unscheduled.length === 0 ? <p className="p-3 text-sm text-muted">Everything has a day.</p> : <div className="divide-y divide-line text-sm">{data.unscheduled.map((r) => <Row key={r.id} r={r} />)}</div>}
        </section>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------- plans

type Plan = {
  id: string; name: string; assetId: string | null; asset: string | null; locationId: string | null; tasks: string; every: number; unit: 'Days' | 'Weeks' | 'Months'; nextDue: string; leadDays: number;
  assignedTo: string; estimatedHours: number; priority: Priority; lastDoneOn: string | null; active: boolean; due: boolean; openWorkOrder: { id: string; number: string; dueDate: string | null } | null;
  meterEvery: number; nextDueMeter: number | null; leadMeter: number; meter: { unit: string; reading: number; perDay: number | null; left: number | null; dueAround: string | null } | null;
};

function Plans({ canWork, onOpen }: { canWork: boolean; onOpen: (id: string) => void }) {
  const { data, error, reload } = useApi<Plan[]>('/maintenance/plans');
  const [editing, setEditing] = useState<Plan | 'new' | null>(null);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const unit = (p: Plan) => (p.every === 1 ? p.unit.slice(0, -1).toLowerCase() : `${p.every} ${p.unit.toLowerCase()}`);
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted">Servicing that repeats. A work order is made automatically a few days before each one is due.</p>
        {canWork && <button className="btn btn-primary" onClick={() => setEditing('new')}><Plus className="h-4 w-4" /> New plan</button>}
      </div>
      {data.length === 0 ? (
        <Empty title="No preventive plans yet" action={canWork && <button className="btn btn-primary" onClick={() => setEditing('new')}>Add the first one</button>}>
          Servicing before things fail costs less than repairing them after: oil changes, filter swaps, safety checks.
        </Empty>
      ) : (
        <ul className="card divide-y divide-line">
          {data.map((p) => (
            <li key={p.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
              <button className="min-w-0 flex-1 text-left" onClick={() => canWork && setEditing(p)}>
                <b className="text-sm">{p.name}</b>{!p.active && <span className="ml-2 text-xs text-muted">(paused)</span>}
                <span className="mt-0.5 block text-sm text-muted">{[p.asset, `every ${unit(p)}${p.meter ? ` or ${p.meterEvery} ${p.meter.unit}` : ''}`, p.assignedTo, p.lastDoneOn && `last done ${day(p.lastDoneOn)}`].filter(Boolean).join(' · ')}</span>
                {p.meter && (
                  <span className="mt-0.5 block text-xs text-muted">
                    At {p.meter.reading} {p.meter.unit}; due at {p.nextDueMeter} ({p.meter.left} to go){p.meter.perDay ? ` · used about ${p.meter.perDay} a day, so around ${day(p.meter.dueAround!)}` : ' · record readings to see when'}
                  </span>
                )}
              </button>
              <span className={cn('text-sm', p.due && 'font-semibold text-warn')}>Next {dayYear(p.meter?.dueAround && p.meter.dueAround < p.nextDue ? p.meter.dueAround : p.nextDue)}</span>
              {p.openWorkOrder && <button className="btn btn-ghost" onClick={() => onOpen(p.openWorkOrder!.id)}>{p.openWorkOrder.number}</button>}
            </li>
          ))}
        </ul>
      )}
      {editing && <PlanForm plan={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={() => (setEditing(null), reload())} />}
    </div>
  );
}

function PlanForm({ plan, onClose, onSaved }: { plan: Plan | null; onClose: () => void; onSaved: () => void }) {
  const { assets, locations } = usePickers();
  const [f, setF] = useState({
    name: plan?.name ?? '', assetId: plan?.assetId ?? '', locationId: plan?.locationId ?? '', tasks: plan?.tasks ?? '', every: String(plan?.every ?? 3), unit: plan?.unit ?? 'Months',
    nextDue: plan?.nextDue ?? todayIso(), leadDays: String(plan?.leadDays ?? 7), assignedTo: plan?.assignedTo ?? '', estimatedHours: plan?.estimatedHours ? String(plan.estimatedHours) : '',
    priority: plan?.priority ?? 'Normal', active: plan?.active ?? true,
    meterEvery: plan?.meterEvery ? String(plan.meterEvery) : '', leadMeter: plan?.leadMeter ? String(plan.leadMeter) : '',
  });
  const meterAsset = assets.find((a) => a.id === f.assetId)?.meterUnit;
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(plan ? `/maintenance/plans/${plan.id}` : '/maintenance/plans', { method: plan ? 'PUT' : 'POST', body: { ...f, assetId: f.assetId || null, locationId: f.locationId || null, every: Number(f.every), leadDays: Number(f.leadDays), estimatedHours: Number(f.estimatedHours) || 0, meterEvery: Number(f.meterEvery) || 0, leadMeter: Number(f.leadMeter) || 0 } });
      onSaved();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title={plan ? 'Change plan' : 'New preventive plan'} description="What to service, how often, and how much notice to give.">
      <div className="space-y-4">
        <Field label="Service"><input className="field" autoFocus value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="Generator: oil and filters" /></Field>
        <Field label="Asset">
          <select className="field" value={f.assetId} onChange={(e) => setF({ ...f, assetId: e.target.value })}>
            <option value="">None (a place)</option>
            {assets.map((a) => <option key={a.id} value={a.id}>{a.tag} · {a.name}</option>)}
          </select>
        </Field>
        {!f.assetId && (
          <Field label="Where">
            <select className="field" value={f.locationId} onChange={(e) => setF({ ...f, locationId: e.target.value })}>
              <option value="">Choose…</option>
              {locations.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </select>
          </Field>
        )}
        <div className="grid grid-cols-[6rem_1fr] gap-3">
          <Field label="Every"><input className="field num" inputMode="numeric" value={f.every} onChange={(e) => setF({ ...f, every: e.target.value.replace(/\D/g, '') })} /></Field>
          <Field label="&nbsp;"><Chips label="Unit" value={f.unit} onChange={(v) => setF({ ...f, unit: v as Plan['unit'] })} options={[['Days', 'Days'], ['Weeks', 'Weeks'], ['Months', 'Months']]} /></Field>
        </div>
        {meterAsset && (
          <div className="grid grid-cols-2 gap-3 rounded-md bg-paper p-3 ring-1 ring-line">
            <Field label={`Or every (${meterAsset})`} hint="Whichever comes first."><input className="field num" inputMode="decimal" value={f.meterEvery} onChange={(e) => setF({ ...f, meterEvery: e.target.value })} placeholder="500" /></Field>
            <Field label={`Notice (${meterAsset})`} hint="Make the job this many before."><input className="field num" inputMode="decimal" value={f.leadMeter} onChange={(e) => setF({ ...f, leadMeter: e.target.value })} placeholder="50" /></Field>
          </div>
        )}
        <div className="grid grid-cols-2 gap-3">
          <Field label="Next due"><input className="field" type="date" value={f.nextDue} onChange={(e) => setF({ ...f, nextDue: e.target.value })} /></Field>
          <Field label="Days of notice"><input className="field num" inputMode="numeric" value={f.leadDays} onChange={(e) => setF({ ...f, leadDays: e.target.value.replace(/\D/g, '') })} /></Field>
          <Field label="Who does it"><input className="field" value={f.assignedTo} onChange={(e) => setF({ ...f, assignedTo: e.target.value })} /></Field>
          <Field label="Hours it takes"><input className="field num" inputMode="decimal" value={f.estimatedHours} onChange={(e) => setF({ ...f, estimatedHours: e.target.value })} /></Field>
        </div>
        <Field label="Checklist" hint="One step per line. It goes on each work order."><textarea className="field min-h-28" value={f.tasks} onChange={(e) => setF({ ...f, tasks: e.target.value })} placeholder={'Drain and replace oil\nReplace oil and fuel filters\nCheck battery and belts\nRun on load for 15 minutes'} /></Field>
        {plan && <label className="flex items-center gap-2.5 text-sm"><input type="checkbox" className="h-4 w-4" checked={f.active} onChange={(e) => setF({ ...f, active: e.target.checked })} /> Active (untick to pause it)</label>}
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy || f.name.trim().length < 3} onClick={save}>{plan ? 'Save' : 'Add plan'}</button></SheetFooter>
      </div>
    </Sheet>
  );
}

// ---------------------------------------------------------------------------------------------- insights

type InsightData = {
  days: number; backlog: { open: number; overdue: number; hours: number; onHold: number; toClose: number }; completed: number;
  reliability: { breakdowns: number; mttrHours: number | null; mtbfDays: number | null; repeats: number; repeatRate: number | null };
  preventive: { due: number; onTime: number; compliance: number | null }; mix: { planned: number; reactive: number; plannedShare: number | null };
  cost: { booked: number; labour: number | null };
  byAsset: { id: string; tag: string; name: string; cost: number; failures: number; downtimeHours: number }[];
  modes: { mode: string; count: number }[]; causes: { cause: string; count: number }[];
};

function Insights() {
  const [days, setDays] = useState('90');
  const { data, error } = useApi<InsightData>(`/maintenance/insights?days=${days}`);
  const topMode = useMemo(() => data?.modes[0], [data]);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const r = data.reliability;
  return (
    <div className="space-y-4">
      <Chips label="Period" value={days} onChange={setDays} options={[['30', 'Last 30 days'], ['90', 'Last 90 days'], ['365', 'Last year']]} />
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Time to repair (MTTR)" value={r.mttrHours === null ? '—' : `${r.mttrHours} h`}>Average, out of use to back in service</Stat>
        <Stat label="Time between failures (MTBF)" value={r.mtbfDays === null ? '—' : `${r.mtbfDays} days`}>Asset-days per breakdown</Stat>
        <Stat label="Preventive on time" value={data.preventive.compliance === null ? '—' : `${data.preventive.compliance}%`} tone={data.preventive.compliance !== null && data.preventive.compliance < 85 ? 'bad' : undefined}>{data.preventive.onTime} of {data.preventive.due} due; aim for 90% or more</Stat>
        <Stat label="Planned work" value={data.mix.plannedShare === null ? '—' : `${data.mix.plannedShare}%`}>{data.mix.planned} planned, {data.mix.reactive} repairs</Stat>
        <Stat label="Breakdowns" value={r.breakdowns}>{r.repeats > 0 ? `${r.repeats} repeats within 30 days (${r.repeatRate}%)` : 'No repeats within 30 days'}</Stat>
        <Stat label="Backlog" value={`${data.backlog.open} jobs`}>{data.backlog.hours} h of work · {data.backlog.overdue} overdue</Stat>
        <Stat label="Maintenance spend" value={ksh(data.cost.booked)}>Parts, payments and bills{data.cost.labour !== null && ` · plus ${ksh(data.cost.labour)} labour`}</Stat>
        <Stat label="Completed" value={data.completed}>{data.backlog.toClose} waiting to be closed</Stat>
      </div>
      <div className="grid gap-4 lg:grid-cols-2">
        <section className="card overflow-hidden">
          <p className="border-b border-line px-4 py-3 font-semibold">Assets costing the most</p>
          {data.byAsset.length === 0 ? <p className="p-4 text-sm text-muted">No maintenance costs or breakdowns in this period.</p> : (
            <table className="w-full text-sm">
              <thead className="bg-paper text-left text-xs text-muted"><tr><th className="px-4 py-2">Asset</th><th className="px-4 py-2 text-right">Cost</th><th className="px-4 py-2 text-right">Breakdowns</th><th className="px-4 py-2 text-right">Down</th></tr></thead>
              <tbody className="divide-y divide-line">
                {data.byAsset.map((a) => (
                  <tr key={a.id}><td className="px-4 py-2"><Link className="hover:underline" to={`/assets?id=${a.id}`}><code className="text-xs text-muted">{a.tag}</code> {a.name}</Link></td><td className="num px-4 py-2 text-right whitespace-nowrap">{ksh(a.cost)}</td><td className="num px-4 py-2 text-right">{a.failures}</td><td className="num px-4 py-2 text-right">{a.downtimeHours} h</td></tr>
                ))}
              </tbody>
            </table>
          )}
        </section>
        <section className="card p-4">
          <p className="mb-3 font-semibold">How things fail</p>
          {data.modes.length === 0 ? <p className="text-sm text-muted">Completed repairs will show what failed and why here.</p> : (
            <div className="grid gap-4 sm:grid-cols-2">
              <Bars title="What failed" rows={data.modes.map((m) => [m.mode, m.count])} />
              <Bars title="Why" rows={data.causes.map((c) => [c.cause, c.count])} />
            </div>
          )}
          {topMode && topMode.count >= 3 && <p className="mt-3 text-sm text-muted">“{topMode.mode}” comes up most: worth a preventive plan for the assets it hits.</p>}
        </section>
      </div>
      <p className="flex items-center gap-2 text-xs text-muted"><ClipboardList className="h-3.5 w-3.5" /> Figures come from completed work orders: record what failed and why when completing a job to keep them accurate.</p>
    </div>
  );
}

function Bars({ title, rows }: { title: string; rows: [string, number][] }) {
  const max = Math.max(1, ...rows.map((r) => r[1]));
  return (
    <div>
      <p className="mb-2 text-xs font-semibold text-muted">{title}</p>
      <ul className="space-y-1.5 text-sm">
        {rows.slice(0, 6).map(([k, n]) => (
          <li key={k}>
            <div className="flex justify-between gap-2"><span className="truncate">{k}</span><span className="num text-muted">{n}</span></div>
            <div className="mt-0.5 h-1.5 rounded-full bg-paper"><div className="h-1.5 rounded-full bg-brand" style={{ width: `${(100 * n) / max}%` }} /></div>
          </li>
        ))}
      </ul>
    </div>
  );
}

/** Can the job go ahead? Parts in stores, tools free, someone assigned, a day planned. */
function Readiness({ w }: { w: WoDetail }) {
  return (
    <section className={cn('rounded-md p-3 ring-1', w.ready ? 'bg-good-soft ring-good/30' : 'bg-warn-soft ring-warn/30')}>
      <p className={cn('font-semibold', w.ready ? 'text-good' : 'text-warn')}>{w.ready ? 'Ready to go' : 'Not ready yet'}</p>
      <ul className="mt-1 grid gap-1 sm:grid-cols-2">
        {w.readiness.map((r) => (
          <li key={r.key} className="flex items-start gap-1.5">
            <span aria-hidden className={r.ok ? 'text-good' : 'text-warn'}>{r.ok ? '✓' : '!'}</span>
            <span className="sr-only">{r.ok ? 'Done:' : 'Missing:'}</span>{r.label}
          </li>
        ))}
      </ul>
    </section>
  );
}

/** Tools from the asset register: reserved for the job, handed out, handed back (damaged ones are reported as faults). */
function Tools({ w, editable, onChange }: { w: WoDetail; editable: boolean; onChange: () => void }) {
  const assets = useApi<{ items: AssetOption[] }>(editable ? '/assets' : null);
  const [pick, setPick] = useState('');
  const [state, setState] = useState({ busy: '', error: '' });
  const run = async (what: string, fn: () => Promise<unknown>) => {
    setState({ busy: what, error: '' });
    try { await fn(); onChange(); setState({ busy: '', error: '' }); setPick(''); } catch (e) { setState({ busy: '', error: (e as Error).message }); }
  };
  const options = (assets.data?.items ?? []).filter((a) => a.id !== w.assetId && !w.tools.some((t) => t.assetId === a.id && !t.returnedAt))
    .sort((a, b) => Number(b.category === 'TLS') - Number(a.category === 'TLS') || a.name.localeCompare(b.name));
  if (!editable && w.tools.length === 0) return null;
  return (
    <section>
      <p className="mb-2 font-semibold">Tools for the job</p>
      {w.tools.length === 0 ? <p className="text-muted">No special tools. Add the torque wrench, meter or jack the job needs, so it’s free when the work starts.</p> : (
        <ul className="divide-y divide-line rounded-md ring-1 ring-line">
          {w.tools.map((t) => (
            <li key={t.id} className="flex flex-wrap items-center gap-2 px-3 py-2">
              <span className="min-w-0 flex-1"><b>{t.name}</b> <code className="text-xs text-muted">{t.tag}</code>
                <span className="block text-xs text-muted">
                  {t.returnedAt ? `Back ${dateTime(t.returnedAt)}${t.returnCondition === 'Damaged' ? ' damaged (reported)' : ''}` : t.checkedOutAt ? `With ${t.checkedOutTo} since ${dateTime(t.checkedOutAt)}` : t.busyOn ? `Reserved · out on ${t.busyOn} right now` : 'Reserved for this job'}
                </span>
              </span>
              {t.busyOn && !t.checkedOutAt && <Status tone="warn">Busy</Status>}
              {editable && !t.checkedOutAt && <button className="btn btn-ghost" disabled={!!state.busy} onClick={async () => { const to = await askDialog({ title: `Hand out ${t.name}?`, label: 'To whom', placeholder: w.assignedTo || 'Technician', confirm: 'Hand out' }); if (to !== null) run(t.id, () => api(`/maintenance/work-orders/${w.id}/tools/${t.id}/out`, { body: { to: to || w.assignedTo } })); }}>Hand out</button>}
              {!t.checkedOutAt && editable && <button className="btn btn-ghost px-2" aria-label={`Remove ${t.name}`} disabled={!!state.busy} onClick={() => run(t.id, () => api(`/maintenance/work-orders/${w.id}/tools/${t.id}`, { method: 'DELETE' }))}><Trash2 className="h-4 w-4" /></button>}
              {t.checkedOutAt && !t.returnedAt && (
                <>
                  <button className="btn btn-ghost" disabled={!!state.busy} onClick={() => run(t.id, () => api(`/maintenance/work-orders/${w.id}/tools/${t.id}/back`, { body: { condition: 'Good' } }))}>Back, fine</button>
                  <button className="btn btn-ghost text-bad" disabled={!!state.busy} onClick={async () => { const note = await askDialog({ title: `${t.name} came back damaged?`, body: 'A fault is reported on it so it’s fixed before the next job.', label: 'What’s wrong with it', required: true, confirm: 'Report it' }); if (note) run(t.id, () => api(`/maintenance/work-orders/${w.id}/tools/${t.id}/back`, { body: { condition: 'Damaged', note } })); }}>Back, damaged</button>
                </>
              )}
            </li>
          ))}
        </ul>
      )}
      {editable && (
        <div className="mt-2 flex gap-2">
          <select className="field min-w-0 flex-1" value={pick} onChange={(e) => setPick(e.target.value)} aria-label="Tool">
            <option value="">Add a tool from the asset register…</option>
            {options.map((a) => <option key={a.id} value={a.id}>{a.name} · {a.tag}</option>)}
          </select>
          <button className="btn btn-ghost" disabled={!pick || !!state.busy} onClick={() => run('add', () => api(`/maintenance/work-orders/${w.id}/tools`, { body: { assetId: pick } }))}><Plus className="h-4 w-4" /> Add</button>
        </div>
      )}
      {state.error && <div className="mt-2"><ErrorNote>{state.error}</ErrorNote></div>}
    </section>
  );
}
