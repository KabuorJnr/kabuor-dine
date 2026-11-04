// Assets: the things the business owns and uses (machines, vehicles, tools, laptops, furniture). The owner is deciding
// "where is everything, who is answerable for it, is anything missing, and what is it costing to keep running?", so
// the page leads with what's out of place (lost, under repair, not seen in 90 days), finds any item from its label in
// one scan, shows how assets fit together (a motor in a pump, a pump on a line), and prints labels for new things.
// Each asset has an asset type, a location, a custodian and a department, and its faults and work orders.
import { Fragment, useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import QRCode from 'qrcode';
import { ChevronRight, ListTree, Loader2, Pencil, Plus, Printer, ScanLine, Wrench } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, dateTime, day, dayYear, fromCents, ksh, toCents } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, SearchBox, Sheet, Status, SheetFooter, PageError, Tabs, noticeDialog, askDialog } from '../components/ui';
import { ScanButton } from '../components/Scanner';
import { FaultForm } from './Maintenance';

type AssetStatus = 'InUse' | 'InStore' | 'UnderRepair' | 'Lost' | 'Disposed';
type Criticality = 'Low' | 'Medium' | 'High';
type Asset = {
  id: string; tag: string; name: string; category: string; categoryName: string; serialNumber: string; manufacturer: string; model: string;
  locationId: string | null; location: string; employeeId: string | null; assignedTo: string; departmentId: string | null; department: string;
  parentId: string | null; parent: string | null; children: number; criticality: Criticality; warrantyUntil: string | null;
  meterUnit?: string; meterReading?: number; meterReadAt?: string | null;
  status: AssetStatus; condition: 'Good' | 'Fair' | 'Poor'; purchaseDate: string | null; cost: number; supplier: string; notes: string;
  lastSeenAt: string | null; lastSeenBy: string; createdAt: string;
};
type AssetType = { code: string; name: string; custom: boolean; count: number };
type Register = {
  counts: { all: number; inUse: number; inStore: number; underRepair: number; lost: number; disposed: number; unseen: number };
  totalCost: number; categories: AssetType[]; items: Asset[];
};
type Option = { id: string; name: string };
type Employee = { id: string; name: string; jobTitle: string; department: string };

const STATUS: Record<AssetStatus, [string, 'good' | 'info' | 'warn' | 'bad' | 'plain']> = {
  InUse: ['In use', 'good'], InStore: ['In store', 'info'], UnderRepair: ['Under repair', 'warn'], Lost: ['Lost', 'bad'], Disposed: ['Disposed', 'plain'],
};
const CRITICALITY: [Criticality, string][] = [['High', 'High: the business stops without it'], ['Medium', 'Medium'], ['Low', 'Low']];
const ago = (iso: string | null) => {
  if (!iso) return 'never seen';
  const days = Math.floor((Date.now() - new Date(iso).getTime()) / 864e5);
  return days === 0 ? 'seen today' : days === 1 ? 'seen yesterday' : `seen ${days} days ago`;
};

/** Labels for an A4 sheet of 3 × 7 (63.5 × 38.1 mm, e.g. Avery L7160): QR of the asset's link, the tag, the name. */
async function printLabels(assets: Asset[], business: string) {
  const w = window.open('', '_blank');
  if (!w) return noticeDialog({ title: 'Allow pop-ups to print labels', body: 'Your browser blocked the window the labels open in. Allow pop-ups for this site, then try again.' });
  const cells = await Promise.all(assets.map(async (a) => {
    const qr = await QRCode.toDataURL(`${location.origin}/assets?tag=${encodeURIComponent(a.tag)}`, { margin: 0, width: 220, errorCorrectionLevel: 'M' });
    const esc = (t: string) => t.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!);
    return `<div class="l"><img src="${qr}" alt=""><div><b>${esc(a.tag)}</b><span>${esc(a.name)}</span><small>${esc(business)}</small></div></div>`;
  }));
  w.document.write(`<!doctype html><title>Asset labels</title><style>
    @page{size:A4;margin:15mm 7mm}body{margin:0;font-family:Arial,sans-serif}
    .s{display:grid;grid-template-columns:repeat(3,63.5mm);grid-auto-rows:38.1mm;column-gap:2.5mm}
    .l{display:flex;align-items:center;gap:3mm;padding:2.5mm 3mm;box-sizing:border-box;overflow:hidden;break-inside:avoid}
    .l img{width:27mm;height:27mm}.l div{display:flex;flex-direction:column;gap:1mm;min-width:0}
    .l b{font-size:11pt;letter-spacing:.3px}.l span{font-size:8.5pt;line-height:1.2}.l small{font-size:7pt;color:#555}
    </style><div class="s">${cells.join('')}</div>`);
  w.document.close();
  setTimeout(() => w.print(), 300);
}

/** Assets in hierarchy order (each followed by its parts), with how deep each sits. Parts whose parent is filtered out
 * show at the top level. */
function tree(items: Asset[]): { asset: Asset; depth: number }[] {
  const ids = new Set(items.map((a) => a.id));
  const kids = new Map<string, Asset[]>();
  for (const a of items) if (a.parentId && ids.has(a.parentId)) kids.set(a.parentId, [...(kids.get(a.parentId) ?? []), a]);
  const out: { asset: Asset; depth: number }[] = [];
  const walk = (a: Asset, depth: number) => { out.push({ asset: a, depth }); for (const k of kids.get(a.id) ?? []) walk(k, depth + 1); };
  for (const a of items.filter((x) => !x.parentId || !ids.has(x.parentId))) walk(a, 0);
  return out;
}

export default function Assets() {
  const session = useSession();
  const canEdit = can(session, 'assets');
  const [params, setParams] = useSearchParams();
  const [q, setQ] = useState('');
  const [status, setStatus] = useState<'all' | 'unseen' | AssetStatus>('all');
  const [category, setCategory] = useState('');
  const [view, setView] = useState<'list' | 'tree'>('list');
  const { data, error, reload } = useApi<Register>(`/assets?q=${encodeURIComponent(q)}&status=${status}&category=${category}`);
  const locations = useApi<Option[]>('/locations');
  const [openId, setOpenId] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [scanError, setScanError] = useState('');

  // A scanned label (camera, hardware scanner, or a phone opening the label's link): find it and record the sighting.
  const scan = async (code: string) => {
    setScanError('');
    try {
      const r = await api<{ asset: Asset }>(`/assets/scan?code=${encodeURIComponent(code)}&seen=${canEdit}`);
      setOpenId(r.asset.id);
      reload();
    } catch (e) {
      setScanError((e as Error).message);
    }
  };
  useEffect(() => {
    const tag = params.get('tag');
    const id = params.get('id');
    if (tag) scan(tag);
    if (id) setOpenId(id);
    if (tag || id) setParams({}, { replace: true });
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const shown = useMemo(() => data?.items ?? [], [data]);
  const rows = useMemo(() => (view === 'tree' ? tree(shown) : shown.map((a) => ({ asset: a, depth: 0 }))), [shown, view]);
  const pickedAssets = useMemo(() => shown.filter((a) => picked.has(a.id)), [shown, picked]);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const c = data.counts;
  const business = session?.company?.name ?? '';

  return (
    <div>
      <PageHeader print
        title="Assets"
        subtitle={<>{c.all} assets · {ksh(data.totalCost)} at cost{c.underRepair > 0 && <> · <span className="text-warn">{c.underRepair} under repair</span></>}{c.unseen > 0 && <> · <span className="text-warn">{c.unseen} not seen in 90 days</span></>}</>}
        actions={canEdit && <button className="btn btn-primary" onClick={() => setAdding(true)}><Plus className="h-4 w-4" /> Add assets</button>}
      />
      <div className="space-y-4">
        <div className="flex flex-wrap items-center gap-2">
          <div className="min-w-0 flex-1">
            <SearchBox value={q} onChange={setQ} placeholder="Search tag, name, serial number or custodian" />
          </div>
          <ScanButton onCode={scan} title="Scan an asset label" />
        </div>
        {scanError && <ErrorNote>{scanError}</ErrorNote>}
        <div className="flex flex-wrap items-center justify-between gap-2">
          <Chips label="Status" value={status} onChange={setStatus} options={[
            ['all', 'All', c.all], ['InUse', 'In use', c.inUse], ['InStore', 'In store', c.inStore], ['UnderRepair', 'Under repair', c.underRepair],
            ['Lost', 'Lost', c.lost], ['unseen', 'Not seen 90 days', c.unseen], ['Disposed', 'Disposed', c.disposed],
          ]} />
          <Tabs label="View" value={view} onChange={setView} options={[['list', 'List'], ['tree', 'Hierarchy']]} />
        </div>
        <Chips label="Asset type" value={category} onChange={setCategory} options={[['', 'All types'], ...data.categories.filter((k) => k.count > 0).map((k) => [k.code, k.name, k.count] as [string, string, number])]} />

        {pickedAssets.length > 0 && (
          <div className="flex items-center gap-3 rounded-md bg-brand-soft px-4 py-2.5 text-sm">
            <span className="flex-1"><b>{pickedAssets.length}</b> selected</span>
            <button className="btn btn-primary" onClick={() => printLabels(pickedAssets, business)}><Printer className="h-4 w-4" /> Print labels</button>
            <button className="btn btn-ghost" onClick={() => setPicked(new Set())}>Clear</button>
          </div>
        )}

        {shown.length === 0 ? (
          <Empty title={c.all === 0 ? 'No assets yet' : 'Nothing matches'} action={c.all === 0 && canEdit && <button className="btn btn-primary" onClick={() => setAdding(true)}>Add your first assets</button>}>
            {c.all === 0 ? 'Add the machines, vehicles, tools, laptops and furniture the business owns. Each gets a tag and a QR label, so a count is a walk round with a phone.' : 'Try another search or filter.'}
          </Empty>
        ) : (
          <ul className="card divide-y divide-line">
            {rows.map(({ asset: a, depth }) => (
              <li key={a.id} className="flex items-center gap-3 px-4 py-3" style={depth ? { paddingLeft: `${1 + depth * 1.5}rem` } : undefined}>
                <input type="checkbox" className="h-4 w-4 shrink-0" aria-label={`Select ${a.tag}`} checked={picked.has(a.id)} onChange={(e) => {
                  const next = new Set(picked);
                  if (e.target.checked) next.add(a.id); else next.delete(a.id);
                  setPicked(next);
                }} />
                <button className="flex min-w-0 flex-1 items-center gap-3 text-left" onClick={() => setOpenId(a.id)}>
                  {depth > 0 && <span className="text-faint">└</span>}
                  <span className="min-w-0 flex-1">
                    <span className="flex items-center gap-2">
                      <code className="shrink-0 rounded bg-paper px-1.5 py-0.5 text-xs font-semibold ring-1 ring-line">{a.tag}</code>
                      <b className="truncate text-sm">{a.name}</b>
                      {a.criticality === 'High' && <span className="shrink-0 rounded-full bg-warn-soft px-1.5 text-[11px] font-semibold text-warn">Critical</span>}
                      {view === 'tree' && a.children > 0 && <span className="shrink-0 text-xs text-muted">{a.children} part{a.children === 1 ? '' : 's'}</span>}
                    </span>
                    <span className="mt-0.5 block truncate text-sm text-muted">
                      {[a.categoryName, a.location, a.assignedTo && `custodian ${a.assignedTo}`, view === 'list' && a.parent && `part of ${a.parent}`].filter(Boolean).join(' · ')}
                    </span>
                  </span>
                  <span className="hidden shrink-0 text-right text-xs text-muted sm:block">{ago(a.lastSeenAt)}</span>
                  <Status tone={STATUS[a.status][1]}>{STATUS[a.status][0]}</Status>
                </button>
              </li>
            ))}
          </ul>
        )}
        {shown.length > 1 && pickedAssets.length === 0 && (
          <button className="btn btn-ghost" onClick={() => printLabels(shown, business)}><Printer className="h-4 w-4" /> Print labels for all {shown.length} shown</button>
        )}
      </div>

      {adding && <AssetForm types={data.categories} locations={locations.data ?? []} business={business} onClose={() => setAdding(false)} onSaved={() => reload()} />}
      {openId && <Detail key={openId} id={openId} canEdit={canEdit} types={data.categories} locations={locations.data ?? []} business={business} onClose={() => setOpenId(null)} onOpen={setOpenId} onChange={reload} />}
    </div>
  );
}

function usePickers() {
  const departments = useApi<Option[]>('/departments');
  const employees = useApi<Employee[]>('/directory/employees');
  const assets = useApi<{ items: Asset[] }>('/assets?status=all');
  return { departments: departments.data ?? [], employees: employees.data ?? [], assets: assets.data?.items ?? [] };
}

/** Adds assets, or changes one: what it is, its type, where it sits in the hierarchy, its location, custodian and department. */
function AssetForm({ existing, types, locations, business, onClose, onSaved }: { existing?: Asset; types: AssetType[]; locations: Option[]; business: string; onClose: () => void; onSaved: () => void }) {
  const { departments, employees, assets } = usePickers();
  const [typeList, setTypeList] = useState(types);
  const [f, setF] = useState({
    name: existing?.name ?? '', category: existing?.category ?? 'FUR', quantity: '1', serialNumber: existing?.serialNumber ?? '', manufacturer: existing?.manufacturer ?? '', model: existing?.model ?? '',
    parentId: existing?.parentId ?? '', locationId: existing?.locationId ?? '', employeeId: existing?.employeeId ?? '', assignedTo: existing?.assignedTo ?? '', departmentId: existing?.departmentId ?? '',
    criticality: existing?.criticality ?? 'Medium', warrantyUntil: existing?.warrantyUntil ?? '', meterUnit: existing?.meterUnit ?? '', condition: existing?.condition ?? 'Good', purchaseDate: existing?.purchaseDate ?? '',
    cost: existing?.cost ? fromCents(existing.cost) : '', supplier: existing?.supplier ?? '', notes: existing?.notes ?? '', inUse: true,
  });
  const [state, setState] = useState<{ busy: boolean; error: string; added?: Asset[] }>({ busy: false, error: '' });
  const qty = existing ? 1 : Math.max(1, Math.min(200, Number(f.quantity) || 1));
  const addType = async () => {
    const name = await askDialog({ title: 'Add an asset type', label: 'Name', placeholder: 'Generators', required: true, confirm: 'Next' });
    if (!name) return;
    const code = await askDialog({ title: `Code for ${name}`, body: 'Three letters, used in the tag: AST-GEN-0001.', label: 'Code', placeholder: name.slice(0, 3).toUpperCase(), required: true, confirm: 'Add type' });
    if (!code) return;
    try {
      const t = await api<{ code: string; name: string }>('/assets/types', { body: { code, name } });
      setTypeList([...typeList, { ...t, custom: true, count: 0 }]);
      setF({ ...f, category: t.code });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      const body = {
        name: f.name, category: f.category, quantity: qty, serialNumber: qty === 1 ? f.serialNumber : '', manufacturer: f.manufacturer, model: f.model, parentId: f.parentId || null,
        locationId: f.locationId || null, employeeId: f.employeeId || null, assignedTo: f.employeeId ? '' : f.assignedTo, departmentId: f.departmentId || null, criticality: f.criticality,
        warrantyUntil: f.warrantyUntil || null, meterUnit: f.meterUnit, condition: f.condition, status: existing ? undefined : f.inUse ? 'InUse' : 'InStore', purchaseDate: f.purchaseDate || null, cost: f.cost ? toCents(f.cost) : 0, supplier: f.supplier, notes: f.notes,
      };
      if (existing) {
        await api(`/assets/${existing.id}`, { method: 'PUT', body });
        onSaved();
        onClose();
        return;
      }
      const r = await api<{ items: Asset[] }>('/assets', { body });
      setState({ busy: false, error: '', added: r.items });
      onSaved();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  if (state.added)
    return (
      <Sheet open onClose={onClose} title="Added">
        <div className="space-y-4 text-sm">
          <p className="rounded-md bg-good-soft p-3 text-good"><b>{state.added.length}</b> added: {state.added[0].tag}{state.added.length > 1 && ` to ${state.added[state.added.length - 1].tag}`}.</p>
          <p className="text-muted">Print the labels and stick each on its item, so anyone can scan it.</p>
          <SheetFooter cancel="Close">
            <button className="btn btn-primary" onClick={() => printLabels(state.added!, business)}><Printer className="h-4 w-4" /> Print {state.added.length} label{state.added.length === 1 ? '' : 's'}</button>
            <button className="btn btn-ghost" onClick={() => setState({ busy: false, error: '' })}>Add more</button>
          </SheetFooter>
        </div>
      </Sheet>
    );
  return (
    <Sheet open onClose={onClose} title={existing ? `Change ${existing.tag}` : 'Add assets'} size="md">
      <div className="space-y-4">
        <Field label="Name"><input className="field" autoFocus value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="Standby generator 60 kVA, Bosch drill, HP laptop…" /></Field>
        {!existing && (
          <Field label="Asset type" hint="Part of the tag: AST-EQP-0001 for machines, AST-VEH-0001 for vehicles.">
            <div className="flex flex-wrap items-center gap-2">
              <Chips label="Asset type" value={f.category} onChange={(v) => setF({ ...f, category: v })} options={typeList.map((t) => [t.code, t.name])} />
              <button type="button" className="text-sm font-semibold text-brand hover:underline" onClick={addType}>+ New type</button>
            </div>
          </Field>
        )}
        <Field label="Part of" hint="The asset it’s fitted in or belongs to: a motor in a pump, a pump on a production line.">
          <select className="field" value={f.parentId} onChange={(e) => setF({ ...f, parentId: e.target.value })}>
            <option value="">Nothing (it stands alone)</option>
            {assets.filter((a) => a.id !== existing?.id && a.status !== 'Disposed').map((a) => <option key={a.id} value={a.id}>{a.tag} · {a.name}</option>)}
          </select>
        </Field>
        <div className="grid grid-cols-2 gap-3">
          {!existing && <Field label="How many" hint="Each gets its own tag."><input className="field num" inputMode="numeric" value={f.quantity} onChange={(e) => setF({ ...f, quantity: e.target.value.replace(/\D/g, '') })} /></Field>}
          <Field label="Serial number" hint={qty > 1 ? 'Add one at a time to record serials.' : 'From the maker’s plate.'}>
            <input className="field" disabled={qty > 1} value={qty > 1 ? '' : f.serialNumber} onChange={(e) => setF({ ...f, serialNumber: e.target.value })} />
          </Field>
          <Field label="Make"><input className="field" value={f.manufacturer} onChange={(e) => setF({ ...f, manufacturer: e.target.value })} placeholder="Perkins" /></Field>
          <Field label="Model"><input className="field" value={f.model} onChange={(e) => setF({ ...f, model: e.target.value })} placeholder="1104A-44TG2" /></Field>
        </div>
        {!existing && (
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Location">
              <select className="field" value={f.locationId} onChange={(e) => setF({ ...f, locationId: e.target.value })}>
                <option value="">Not set</option>
                {locations.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </select>
            </Field>
            <Field label="Custodian" hint="The person answerable for it. Empty if it’s kept in a store.">
              <select className="field" value={f.employeeId} onChange={(e) => setF({ ...f, employeeId: e.target.value })}>
                <option value="">No one / someone not on the staff list</option>
                {employees.map((e) => <option key={e.id} value={e.id}>{e.name}</option>)}
              </select>
            </Field>
            {!f.employeeId && <Field label="Custodian’s name (not on the staff list)" className="sm:col-span-2"><input className="field" value={f.assignedTo} onChange={(e) => setF({ ...f, assignedTo: e.target.value })} /></Field>}
          </div>
        )}
        {!existing && <label className="flex items-center gap-2.5 text-sm"><input type="checkbox" className="h-4 w-4" checked={f.inUse} onChange={(e) => setF({ ...f, inUse: e.target.checked })} /> In use (installed or working); untick if it’s kept in a store</label>}
        <Field label="Department" hint="Uses it and carries its running costs.">
          <select className="field" value={f.departmentId} onChange={(e) => setF({ ...f, departmentId: e.target.value })}>
            <option value="">Not set</option>
            {departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </select>
        </Field>
        <Field label="How critical" hint="Critical assets go first, and their repairs must record why they failed.">
          <Chips label="Criticality" value={f.criticality} onChange={(v) => setF({ ...f, criticality: v as Criticality })} options={CRITICALITY} />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label={existing ? 'Cost (KSh)' : 'Cost each (KSh)'}><input className="field num" inputMode="decimal" value={f.cost} onChange={(e) => setF({ ...f, cost: e.target.value })} /></Field>
          <Field label="Bought on"><input className="field" type="date" value={f.purchaseDate} onChange={(e) => setF({ ...f, purchaseDate: e.target.value })} /></Field>
          <Field label="Bought from"><input className="field" value={f.supplier} onChange={(e) => setF({ ...f, supplier: e.target.value })} /></Field>
          <Field label="Warranty until"><input className="field" type="date" value={f.warrantyUntil} onChange={(e) => setF({ ...f, warrantyUntil: e.target.value })} /></Field>
          <Field label="Meter (optional)" hint="Service it by use as well as by date.">
            <select className="field" value={f.meterUnit} onChange={(e) => setF({ ...f, meterUnit: e.target.value })}>
              <option value="">No meter</option>
              {['hours', 'cycles', 'km', 'landings', 'starts'].map((u) => <option key={u} value={u}>Measured in {u}</option>)}
            </select>
          </Field>
        </div>
        <Field label="Notes"><input className="field" value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} /></Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter>
          <button className="btn btn-primary" disabled={state.busy || f.name.trim().length < 2} onClick={save}>
            {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} {existing ? 'Save changes' : qty > 1 ? `Add ${qty} and make ${qty} tags` : 'Add and make its tag'}
          </button>
        </SheetFooter>
      </div>
    </Sheet>
  );
}

type DetailData = {
  asset: Asset; history: { kind: string; text: string; actor: string; at: string }[];
  path: { id: string; tag: string; name: string }[]; children: { id: string; tag: string; name: string; status: AssetStatus }[];
  workOrders: { id: string; number: string; title: string; type: string; status: string; createdAt: string; completedAt: string | null }[];
  plans: { id: string; name: string; nextDue: string; every: number; unit: string }[]; openFaults: number;
  maintenance: { cost: number; breakdowns: number; open: number };
};

function Detail({ id, canEdit, types, locations, business, onClose, onOpen, onChange }: { id: string; canEdit: boolean; types: AssetType[]; locations: Option[]; business: string; onClose: () => void; onOpen: (id: string) => void; onChange: () => void }) {
  const { data, error, reload } = useApi<DetailData>(`/assets/${id}`);
  const employees = useApi<Employee[]>(canEdit ? '/directory/employees' : null);
  const [move, setMove] = useState('');
  const [who, setWho] = useState('');
  const [note, setNote] = useState('');
  const [state, setState] = useState({ busy: '', error: '' });
  const [editing, setEditing] = useState(false);
  const [reporting, setReporting] = useState(false);
  if (error) return <Sheet open onClose={onClose} title="Asset"><ErrorNote>{error}</ErrorNote></Sheet>;
  if (!data) return <Sheet open onClose={onClose} title="Asset"><Loading /></Sheet>;
  const a = data.asset;
  const onMeter = () => (reload(), onChange());
  const act = async (kind: string, extra: Record<string, unknown> = {}) => {
    setState({ busy: kind, error: '' });
    try {
      await api(`/assets/${a.id}/actions`, { body: { kind, note, ...extra } });
      setNote('');
      reload();
      onChange();
      setState({ busy: '', error: '' });
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };
  const facts: [string, React.ReactNode][] = [
    ['Asset type', a.categoryName], ['Location', a.location || 'Not set'], ['Custodian', a.assignedTo || 'No one'], ['Department', a.department || 'Not set'],
    ['Criticality', a.criticality], ['Condition', a.condition], ['Make and model', [a.manufacturer, a.model].filter(Boolean).join(' ') || '—'], ['Serial number', a.serialNumber || '—'],
    ['Cost', a.cost ? ksh(a.cost) : '—'], ['Bought', [a.purchaseDate && dayYear(a.purchaseDate), a.supplier].filter(Boolean).join(' from ') || '—'],
    ['Warranty', a.warrantyUntil ? <span className={cn(a.warrantyUntil < new Date().toISOString().slice(0, 10) && 'text-muted line-through')}>until {dayYear(a.warrantyUntil)}</span> : '—'],
    ['Last seen', a.lastSeenAt ? `${dateTime(a.lastSeenAt)} by ${a.lastSeenBy}` : 'Never scanned'],
    ...(a.meterUnit ? [['Meter', <MeterCell key="m" asset={a} onChange={onMeter} />] as [string, React.ReactNode]] : []),
  ];
  const staff = employees.data ?? [];
  return (
    <Sheet open onClose={onClose} title={`${a.tag} · ${a.name}`} size="md">
      <div className="space-y-5 text-sm">
        {data.path.length > 0 && (
          <nav aria-label="Part of" className="flex flex-wrap items-center gap-1 text-muted">
            {data.path.map((p, i) => (
              <Fragment key={p.id}>{i > 0 && <ChevronRight className="h-3.5 w-3.5" />}<button className="hover:text-brand hover:underline" onClick={() => onOpen(p.id)}>{p.tag} {p.name}</button></Fragment>
            ))}
            <ChevronRight className="h-3.5 w-3.5" /><span className="text-ink">{a.tag}</span>
          </nav>
        )}
        <div className="flex flex-wrap items-center gap-2">
          <Status tone={STATUS[a.status][1]}>{STATUS[a.status][0]}</Status>
          {a.criticality === 'High' && <Status tone="warn">Critical</Status>}
          <span className="text-muted">{ago(a.lastSeenAt)}</span>
          <span className="flex-1" />
          <button className="btn btn-ghost" onClick={() => setReporting(true)}><Wrench className="h-4 w-4" /> Report a fault</button>
          {canEdit && <button className="btn btn-ghost" onClick={() => setEditing(true)}><Pencil className="h-4 w-4" /> Change</button>}
          <button className="btn btn-ghost" onClick={() => printLabels([a], business)}><Printer className="h-4 w-4" /> Label</button>
        </div>
        <dl className="grid grid-cols-[8rem_1fr] gap-x-3 gap-y-1.5">
          {facts.map(([k, v]) => <Fragment key={k}><dt className="text-muted">{k}</dt><dd>{v}</dd></Fragment>)}
          {a.notes && <><dt className="text-muted">Notes</dt><dd>{a.notes}</dd></>}
        </dl>

        <section className="grid grid-cols-3 divide-x divide-line rounded-md ring-1 ring-line">
          <div className="px-3 py-2"><p className="text-xs text-muted">Maintenance cost</p><p className="num font-semibold">{ksh(data.maintenance.cost)}</p></div>
          <div className="px-3 py-2"><p className="text-xs text-muted">Breakdowns</p><p className="num font-semibold">{data.maintenance.breakdowns}</p></div>
          <div className="px-3 py-2"><p className="text-xs text-muted">Open jobs · faults</p><p className="num font-semibold">{data.maintenance.open} · {data.openFaults}</p></div>
        </section>

        {data.children.length > 0 && (
          <section>
            <p className="mb-2 flex items-center gap-2 font-semibold"><ListTree className="h-4 w-4 text-muted" /> Parts of it</p>
            <ul className="divide-y divide-line rounded-md ring-1 ring-line">
              {data.children.map((k) => <li key={k.id}><button className="flex w-full items-center gap-3 px-3 py-2 text-left hover:bg-paper" onClick={() => onOpen(k.id)}><code className="text-xs text-muted">{k.tag}</code><span className="flex-1">{k.name}</span><Status tone={STATUS[k.status][1]}>{STATUS[k.status][0]}</Status></button></li>)}
            </ul>
          </section>
        )}

        {(data.workOrders.length > 0 || data.plans.length > 0) && (
          <section>
            <p className="mb-2 font-semibold">Maintenance</p>
            <ul className="divide-y divide-line rounded-md ring-1 ring-line">
              {data.plans.map((p) => <li key={p.id} className="flex items-center gap-3 px-3 py-2"><span className="flex-1">{p.name} <span className="text-muted">· every {p.every} {p.unit.toLowerCase()}</span></span><span className="text-muted">next {day(p.nextDue)}</span></li>)}
              {data.workOrders.map((w) => (
                <li key={w.id}><Link className="flex items-center gap-3 px-3 py-2 hover:bg-paper" to={`/maintenance?wo=${w.id}`}><code className="text-xs text-muted">{w.number}</code><span className="min-w-0 flex-1 truncate">{w.title}</span><span className="text-xs text-muted">{day(w.completedAt ?? w.createdAt)}</span><Status tone={['Completed', 'Closed'].includes(w.status) ? 'good' : w.status === 'Cancelled' ? 'plain' : 'warn'}>{w.status === 'InProgress' ? 'In progress' : w.status === 'OnHold' ? 'On hold' : w.status}</Status></Link></li>
              ))}
            </ul>
          </section>
        )}

        {canEdit && a.status !== 'Disposed' && (
          <div className="space-y-3 border-t border-line pt-4">
            <button className="btn btn-primary w-full" disabled={!!state.busy} onClick={() => act('seen')}>
              {state.busy === 'seen' ? <Loader2 className="h-4 w-4 animate-spin" /> : <ScanLine className="h-4 w-4" />} I can see it here (mark seen)
            </button>
            <div className="flex gap-2">
              <select className="field flex-1" value={move} onChange={(e) => setMove(e.target.value)} aria-label="Move to location">
                <option value="">Move to another location…</option>
                {locations.filter((l) => l.id !== a.locationId).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </select>
              <button className="btn btn-ghost" disabled={!move || !!state.busy} onClick={() => act('move', { locationId: move }).then(() => setMove(''))}>Move</button>
            </div>
            <div className="flex gap-2">
              <select className="field flex-1" value={who} onChange={(e) => setWho(e.target.value)} aria-label="New custodian">
                <option value="">{a.assignedTo ? `Custodian now: ${a.assignedTo}. Hand over to…` : 'Give to a custodian…'}</option>
                {staff.filter((e) => e.id !== a.employeeId).map((e) => <option key={e.id} value={e.id}>{e.name}</option>)}
              </select>
              <button className="btn btn-ghost" disabled={!who || !!state.busy} onClick={() => act('assign', { employeeId: who }).then(() => setWho(''))}>Hand over</button>
              {a.assignedTo && <button className="btn btn-ghost" disabled={!!state.busy} onClick={() => act('assign', { assignedTo: '' })}>Returned</button>}
            </div>
            <Field label="Note (needed to mark lost or disposed)"><input className="field" value={note} onChange={(e) => setNote(e.target.value)} placeholder="What happened" /></Field>
            <div className="flex flex-wrap gap-2">
              {(['InUse', 'InStore', 'UnderRepair', 'Lost', 'Disposed'] as AssetStatus[]).filter((s) => s !== a.status).map((s) => (
                <button key={s} className={cn('btn btn-ghost', (s === 'Lost' || s === 'Disposed') && 'text-bad')} disabled={!!state.busy} onClick={() => act('status', { status: s })}>Mark {STATUS[s][0].toLowerCase()}</button>
              ))}
              {(['Good', 'Fair', 'Poor'] as const).filter((x) => x !== a.condition).map((x) => (
                <button key={x} className="btn btn-ghost" disabled={!!state.busy} onClick={() => act('condition', { condition: x })}>Condition: {x.toLowerCase()}</button>
              ))}
            </div>
            {state.error && <ErrorNote>{state.error}</ErrorNote>}
          </div>
        )}

        <div className="border-t border-line pt-4">
          <p className="mb-2 font-semibold">History</p>
          <ul className="space-y-1.5">
            {data.history.map((h, i) => <li key={i} className="flex gap-3"><span className="w-36 shrink-0 text-muted">{dateTime(h.at)}</span><span>{h.text} <span className="text-faint">· {h.actor}</span></span></li>)}
          </ul>
        </div>
      </div>
      {editing && <AssetForm existing={a} types={types} locations={locations} business={business} onClose={() => setEditing(false)} onSaved={() => (reload(), onChange())} />}
      {reporting && <FaultForm assetId={a.id} onClose={() => setReporting(false)} onSaved={() => (setReporting(false), reload())} />}
    </Sheet>
  );
}

/** An asset's meter: the latest reading, and recording a new one (which can make a service fall due). */
function MeterCell({ asset, onChange }: { asset: { id: string; meterUnit?: string; meterReading?: number; meterReadAt?: string | null }; onChange: () => void }) {
  const [msg, setMsg] = useState('');
  const record = async () => {
    const v = await askDialog({ title: 'New meter reading', body: `Last: ${asset.meterReading ?? 0} ${asset.meterUnit}.`, label: `Reading (${asset.meterUnit})`, required: true, confirm: 'Record' });
    if (!v) return;
    try {
      const r = await api<{ made: { made: number; numbers: string[] } }>(`/maintenance/meters/${asset.id}`, { body: { reading: Number(v) } });
      setMsg(r.made.made > 0 ? `Service due: ${r.made.numbers.join(', ')} made.` : 'Recorded.');
      onChange();
    } catch (e) { setMsg((e as Error).message); }
  };
  return (
    <span className="flex flex-wrap items-center gap-2">
      <b className="num">{asset.meterReading ?? 0} {asset.meterUnit}</b>
      {asset.meterReadAt && <span className="text-muted">({dateTime(asset.meterReadAt)})</span>}
      <button className="text-brand hover:underline" onClick={record}>Record reading</button>
      {msg && <span className="w-full text-xs text-muted">{msg}</span>}
    </span>
  );
}
