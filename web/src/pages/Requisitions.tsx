// Requests from departments. Anyone can ask for what their department needs. The chain: head of department →
// procurement ("should we get this, and how: from stores, a supplier or a tender?") → finance signs off the money
// against the budget it approved. Each request shows its budget position, every decision in its trail, then the ways
// to fulfil it.
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { Gavel, PackageCheck, Plus, ShoppingBag, Trash2 } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, day, dateTime, ksh, toCents } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageError, PageHeader, Sheet, SheetFooter, Status, askDialog } from '../components/ui';
import DocTrail from '../components/DocTrail';

type ReqStatus = 'Draft' | 'WithHead' | 'WithBuying' | 'WithFinance' | 'Approved' | 'Fulfilled' | 'Rejected' | 'Cancelled';
type ReqRow = {
  id: string; number: string; departmentId: string; department: string; requestedBy: string; neededBy: string | null; purpose: string; status: ReqStatus; estimatedTotal: number;
  overBudget: boolean; createdAt: string; submittedAt: string | null; approvedAt: string | null; approvedBy: string; headApprovedBy: string; rejectReason: string; lines: number; outstanding: number;
  financeSignedOffBy: string; financeSignedOffAt: string | null; financeNote: string;
};
export const REQ_STATUS: Record<ReqStatus, [string, 'good' | 'warn' | 'bad' | 'info' | 'plain']> = {
  Draft: ['Draft', 'plain'], WithHead: ['With the head of department', 'warn'], WithBuying: ['With procurement', 'warn'], WithFinance: ['With finance', 'warn'], Approved: ['Signed off', 'info'],
  Fulfilled: ['Done', 'good'], Rejected: ['Turned down', 'bad'], Cancelled: ['Cancelled', 'plain'],
};

export default function Requisitions() {
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const [view, setView] = useState('waiting');
  const { data, error, reload } = useApi<{ counts: Record<string, number>; all: boolean; items: ReqRow[] }>(`/requisitions?view=${view}`);
  const [asking, setAsking] = useState(false);
  const openId = params.get('id');
  const open = (id: string | null) => setParams(id ? { id } : {}, { replace: true });
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const c = data.counts;
  return (
    <div>
      <PageHeader print
        title="Requests"
        subtitle={data.all ? 'What departments have asked for: procurement approves, finance signs off the money against the budget, then it’s issued from stores, ordered or put out to tender.' : 'What you’ve asked for, and where each request is.'}
        actions={<button className="btn btn-primary" onClick={() => setAsking(true)}><Plus className="h-4 w-4" /> Ask for something</button>}
      />
      <div className="space-y-4">
        <Chips label="Show" value={view} onChange={setView} options={[['waiting', 'With procurement', c.waiting], ['finance', 'With finance', c.finance], ['approved', 'Signed off, to fulfil', c.approved], ['drafts', 'Drafts', c.drafts], ['done', 'Done', c.done], ['all', 'All']]} />
        {data.items.length === 0 ? (
          <Empty title="Nothing here" action={<button className="btn btn-primary" onClick={() => setAsking(true)}>Ask for something</button>}>
            Departments ask for what they need here (or from their portal). Their head approves it first, then procurement.
          </Empty>
        ) : (
          <ul className="card divide-y divide-line">
            {data.items.map((r) => (
              <li key={r.id}>
                <button className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-paper" onClick={() => open(r.id)}>
                  <span className="min-w-0 flex-1">
                    <span className="flex items-center gap-2"><code className="text-xs font-semibold text-muted">{r.number}</code><b className="truncate text-sm">{r.purpose || `${r.lines} item${r.lines === 1 ? '' : 's'}`}</b></span>
                    <span className="mt-0.5 block truncate text-sm text-muted">{[r.department, r.requestedBy, r.neededBy && `needed by ${day(r.neededBy)}`].filter(Boolean).join(' · ')}</span>
                  </span>
                  <span className="num hidden text-sm sm:block">{ksh(r.estimatedTotal)}</span>
                  {r.overBudget && <Status tone="bad">Over budget</Status>}
                  <Status tone={REQ_STATUS[r.status][1]}>{REQ_STATUS[r.status][0]}</Status>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
      {asking && <RequestForm onClose={() => setAsking(false)} onSaved={(id) => (setAsking(false), reload(), open(id))} />}
      {openId && <RequestSheet id={openId} canBuy={can(session, 'buy')} canIssue={can(session, 'stock')} onClose={() => open(null)} onChange={reload} />}
    </div>
  );
}

type Line = { itemId: string; description: string; quantity: string; unit: string; estimate: string };
const blank: Line = { itemId: '', description: '', quantity: '1', unit: '', estimate: '' };

/** The request form, shared with the portal: department, why, and the lines (from the catalogue or typed). */
export function RequestForm({ onClose, onSaved, portal, departments: given, defaultDepartment }: { onClose: () => void; onSaved: (id: string) => void; portal?: boolean; departments?: { id: string; name: string }[]; defaultDepartment?: string | null }) {
  const departments = useApi<{ id: string; name: string; active?: boolean }[]>(given ? null : '/departments');
  const catalogue = useApi<{ id: string; name: string; unit: string; averageCost?: number; estimate?: number }[]>(portal ? '/me/catalogue' : '/items');
  const list = given ?? (departments.data ?? []).filter((d) => d.active !== false);
  const [f, setF] = useState({ departmentId: defaultDepartment ?? '', purpose: '', neededBy: '' });
  const [lines, setLines] = useState<Line[]>([{ ...blank }]);
  const [state, setState] = useState({ busy: false, error: '' });
  const items = catalogue.data ?? [];
  const total = lines.reduce((s, l) => s + (Number(l.quantity) || 0) * toCents(l.estimate || '0'), 0);
  const set = (i: number, patch: Partial<Line>) => setLines(lines.map((l, j) => (j === i ? { ...l, ...patch } : l)));
  const save = async (submit: boolean) => {
    setState({ busy: true, error: '' });
    try {
      const body = {
        departmentId: f.departmentId, purpose: f.purpose, neededBy: f.neededBy || null, submit,
        lines: lines.filter((l) => l.itemId || l.description.trim()).map((l) => ({ itemId: l.itemId || null, description: l.description, quantity: Number(l.quantity) || 0, unit: l.unit, estimatedUnitCost: l.estimate ? toCents(l.estimate) : null })),
      };
      const r = await api<{ id: string }>(portal ? '/me/requests' : '/requisitions', { body });
      onSaved(r.id);
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title="Ask for something" description="Say what your department needs and why. It goes to your head of department, then procurement." size="md">
      <div className="space-y-4">
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="Department">
            <select className="field" value={f.departmentId} onChange={(e) => setF({ ...f, departmentId: e.target.value })}>
              <option value="">Choose…</option>
              {list.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
          </Field>
          <Field label="Needed by (optional)"><input className="field" type="date" value={f.neededBy} onChange={(e) => setF({ ...f, neededBy: e.target.value })} /></Field>
        </div>
        <Field label="What it’s for"><input className="field" value={f.purpose} onChange={(e) => setF({ ...f, purpose: e.target.value })} placeholder="Stationery for the quarter, new welding bay…" /></Field>
        <div className="space-y-2">
          <p className="text-sm font-semibold">What you need</p>
          {lines.map((l, i) => (
            <div key={i} className="grid gap-2 rounded-md bg-paper p-2 ring-1 ring-line sm:grid-cols-[1fr_5rem_7rem_auto]">
              <div className="space-y-2">
                <select className="field" value={l.itemId} aria-label="Item" onChange={(e) => {
                  const it = items.find((x) => x.id === e.target.value);
                  const cost = it ? (it.estimate ?? it.averageCost ?? 0) : 0;
                  set(i, { itemId: e.target.value, unit: it?.unit ?? l.unit, estimate: cost ? String(Math.round(cost) / 100) : l.estimate });
                }}>
                  <option value="">Something not in the list (type it)…</option>
                  {items.map((it) => <option key={it.id} value={it.id}>{it.name}</option>)}
                </select>
                {!l.itemId && <input className="field" value={l.description} onChange={(e) => set(i, { description: e.target.value })} placeholder="What, with any size, make or model" aria-label="Description" />}
              </div>
              <input className="field num" inputMode="decimal" value={l.quantity} onChange={(e) => set(i, { quantity: e.target.value })} aria-label="How many" placeholder="Qty" />
              <input className="field num" inputMode="decimal" value={l.estimate} onChange={(e) => set(i, { estimate: e.target.value })} aria-label="Price each" placeholder="KSh each" />
              <button className="btn btn-ghost px-2" aria-label="Remove line" disabled={lines.length === 1} onClick={() => setLines(lines.filter((_, j) => j !== i))}><Trash2 className="h-4 w-4" /></button>
            </div>
          ))}
          <div className="flex items-center justify-between">
            <button className="btn btn-ghost" onClick={() => setLines([...lines, { ...blank }])}><Plus className="h-4 w-4" /> Another line</button>
            <span className="text-sm text-muted">About <b className="num text-ink">{ksh(total)}</b></span>
          </div>
        </div>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter>
          <button className="btn btn-primary" disabled={state.busy || !f.departmentId} onClick={() => save(true)}>Send the request</button>
          {!portal && <button className="btn btn-ghost" disabled={state.busy || !f.departmentId} onClick={() => save(false)}>Save as draft</button>}
        </SheetFooter>
      </div>
    </Sheet>
  );
}

type ReqDetail = {
  request: ReqRow; job: { id: string; number: string; title: string } | null;
  orders: { id: string; number: string; kind: string; status: string; total: number }[]; tenders: { id: string; number: string; status: string }[];
  budget: { budget: number | null; spent: number; committed: number; available: number; hasBudget: boolean; over: boolean } | null;
  trail: { seq: number; at: string; actor: string; action: string; amount: number; note: string; hash: string }[]; canSignOff: boolean;
  lines: { id: string; itemId: string | null; description: string; unit: string; quantity: number; estimatedUnitCost: number; issuedQuantity: number; orderedQuantity: number; outstanding: number; stocked: boolean; inStock: number | null }[];
};

function RequestSheet({ id, canBuy, canIssue, onClose, onChange }: { id: string; canBuy: boolean; canIssue: boolean; onClose: () => void; onChange: () => void }) {
  const session = useSession();
  const { data, error, reload } = useApi<ReqDetail>(`/requisitions/${id}`);
  const [state, setState] = useState({ busy: '', error: '' });
  const [ordering, setOrdering] = useState<'order' | 'tender' | null>(null);
  const run = async (what: string, fn: () => Promise<unknown>) => {
    setState({ busy: what, error: '' });
    try { await fn(); reload(); onChange(); setState({ busy: '', error: '' }); } catch (e) { setState({ busy: '', error: (e as Error).message }); }
  };
  if (error) return <Sheet open onClose={onClose} title="Request"><ErrorNote>{error}</ErrorNote></Sheet>;
  if (!data) return <Sheet open onClose={onClose} title="Request"><Loading /></Sheet>;
  const r = data.request;
  const waiting = r.status === 'WithBuying' || (r.status === 'WithHead' && session?.user.role === 'Owner');
  const stockedLeft = data.lines.some((l) => l.stocked && l.outstanding > 0);
  const anyLeft = data.lines.some((l) => l.outstanding > 0);
  const b = data.budget;
  return (
    <Sheet open onClose={onClose} title={`${r.number}${r.purpose ? ` · ${r.purpose}` : ''}`} description={`${r.department} · asked by ${r.requestedBy}${r.submittedAt ? ` ${dateTime(r.submittedAt)}` : ''}`} size="lg">
      <div className="space-y-5 text-sm">
        <div className="flex flex-wrap items-center gap-2">
          <Status tone={REQ_STATUS[r.status][1]}>{REQ_STATUS[r.status][0]}</Status>
          {r.overBudget && <Status tone="bad">Approved over budget</Status>}
          {r.neededBy && <span className="text-muted">Needed by {day(r.neededBy)}</span>}
          {data.job && <Link className="text-brand hover:underline" to={`/maintenance?wo=${data.job.id}`}>For {data.job.number}: {data.job.title}</Link>}
        </div>
        {r.rejectReason && <p className="rounded-md bg-bad-soft p-3 text-bad">Turned down: {r.rejectReason}</p>}
        {r.financeNote && <p className="rounded-md bg-warn-soft p-3 text-warn">Finance: {r.financeNote}</p>}

        {b && (
          <div className={cn('rounded-md p-3 ring-1', b.over ? 'bg-bad-soft ring-bad/30' : 'bg-paper ring-line')}>
            {b.hasBudget ? (
              <>
                <p className={cn('font-semibold', b.over && 'text-bad')}>{b.over ? `Over the department’s budget by ${ksh(r.estimatedTotal - Math.max(0, b.available))}` : `Within budget: ${ksh(b.available - r.estimatedTotal)} left after this`}</p>
                <p className="mt-1 text-muted">This month: approved budget {ksh(b.budget ?? 0)} · spent {ksh(b.spent)} · already committed {ksh(b.committed)} · this request {ksh(r.estimatedTotal)}.{b.over && ' Finance can still sign it off, but has to write why.'}</p>
              </>
            ) : <p className="font-semibold text-bad">{r.department} has no budget approved by finance for this month, so this can’t be approved yet. <Link className="text-brand hover:underline" to="/finance/budgets">Prepare the budget</Link>.</p>}
          </div>
        )}

        <div className="overflow-x-auto rounded-md ring-1 ring-line">
          <table className="w-full">
            <thead className="bg-paper text-left text-xs text-muted"><tr><th className="px-3 py-2">Item</th><th className="px-3 py-2 text-right">Asked</th><th className="px-3 py-2 text-right">Given</th><th className="px-3 py-2 text-right">Ordered</th><th className="px-3 py-2 text-right">In stock</th><th className="px-3 py-2 text-right">Estimate</th></tr></thead>
            <tbody className="divide-y divide-line">
              {data.lines.map((l) => (
                <tr key={l.id}>
                  <td className="px-3 py-2">{l.description}{!l.stocked && <span className="ml-1 text-xs text-muted">(to buy)</span>}</td>
                  <td className="num px-3 py-2 text-right">{l.quantity} {l.unit}</td>
                  <td className="num px-3 py-2 text-right">{l.issuedQuantity || '—'}</td>
                  <td className="num px-3 py-2 text-right">{l.orderedQuantity || '—'}</td>
                  <td className={cn('num px-3 py-2 text-right', l.inStock !== null && l.inStock < l.outstanding && 'text-bad')}>{l.inStock ?? '—'}</td>
                  <td className="num px-3 py-2 text-right">{ksh(Math.round(l.quantity * l.estimatedUnitCost))}</td>
                </tr>
              ))}
            </tbody>
            <tfoot><tr className="border-t border-line font-semibold"><td className="px-3 py-2" colSpan={5}>Estimated total</td><td className="num px-3 py-2 text-right">{ksh(r.estimatedTotal)}</td></tr></tfoot>
          </table>
        </div>

        {canBuy && waiting && (
          <div className="flex flex-wrap gap-2">
            <button className="btn btn-primary" disabled={!!state.busy} onClick={() => run('approve', () => api(`/requisitions/${id}/approve`, { body: {} }))}>Approve</button>
            <button className="btn btn-ghost text-bad" disabled={!!state.busy} onClick={async () => { const reason = await askDialog({ title: `Turn down ${r.number}?`, label: 'Why (they see this)', required: true, confirm: 'Turn down', danger: true }); if (reason) run('reject', () => api(`/requisitions/${id}/reject`, { body: { reason } })); }}>Turn down</button>
          </div>
        )}
        {data.canSignOff && (
          <div className="flex flex-wrap gap-2">
            <button className="btn btn-primary" disabled={!!state.busy} onClick={async () => {
              let reason = '';
              if (b?.over) { reason = (await askDialog({ title: `Sign off ${r.number} over budget?`, body: 'It stays in the approvals register for good.', label: 'Why you’re signing it off anyway', required: true, confirm: 'Sign off' })) ?? ''; if (!reason) return; }
              run('signoff', () => api(`/requisitions/${id}/sign-off`, { body: { reason } }));
            }}>Sign off the money</button>
            <button className="btn btn-ghost text-bad" disabled={!!state.busy} onClick={async () => { const reason = await askDialog({ title: `Turn down ${r.number}?`, label: 'Why (they see this)', required: true, confirm: 'Turn down', danger: true }); if (reason) run('reject', () => api(`/requisitions/${id}/reject`, { body: { reason } })); }}>Turn down</button>
          </div>
        )}
        {r.status === 'Approved' && (
          <div className="flex flex-wrap gap-2">
            {canIssue && stockedLeft && <button className="btn btn-primary" disabled={!!state.busy} onClick={() => run('issue', () => api(`/requisitions/${id}/issue`, { body: {} }))}><PackageCheck className="h-4 w-4" /> Issue what’s in the store</button>}
            {canBuy && anyLeft && <button className="btn btn-ghost" onClick={() => setOrdering('order')}><ShoppingBag className="h-4 w-4" /> Order from a supplier</button>}
            {canBuy && anyLeft && <button className="btn btn-ghost" onClick={() => setOrdering('tender')}><Gavel className="h-4 w-4" /> Put out to tender</button>}
          </div>
        )}
        {(['Draft', 'WithHead', 'WithBuying', 'WithFinance'] as ReqStatus[]).includes(r.status) && (
          <button className="btn btn-ghost" disabled={!!state.busy} onClick={() => run('cancel', () => api(`/requisitions/${id}/cancel`, { body: {} }))}>Cancel request</button>
        )}
        {r.status === 'Draft' && <button className="btn btn-primary ml-2" disabled={!!state.busy} onClick={() => run('submit', () => api(`/requisitions/${id}/submit`, { body: {} }))}>Send it</button>}
        {state.error && <ErrorNote>{state.error}</ErrorNote>}

        <DocTrail kind="request" id={id} />
        {data.trail.length > 0 && <Trail rows={data.trail} />}

        {(data.orders.length > 0 || data.tenders.length > 0) && (
          <section>
            <p className="mb-2 font-semibold">Orders and tenders from this request</p>
            <ul className="divide-y divide-line rounded-md ring-1 ring-line">
              {data.orders.map((o) => <li key={o.id} className="flex items-center gap-3 px-3 py-2"><Link className="font-semibold text-brand hover:underline" to={`/purchases/${o.id}`}>{o.number}</Link><span className="flex-1 text-muted">{o.kind}</span><span className="num">{ksh(o.total)}</span><Status tone="info">{o.status}</Status></li>)}
              {data.tenders.map((t) => <li key={t.id} className="flex items-center gap-3 px-3 py-2"><Link className="font-semibold text-brand hover:underline" to={`/procurement/tenders?id=${t.id}`}>{t.number}</Link><span className="flex-1 text-muted">Tender</span><Status tone="info">{t.status}</Status></li>)}
            </ul>
          </section>
        )}
      </div>
      {ordering && <OrderSheet mode={ordering} request={data} onClose={() => setOrdering(null)} onDone={() => (setOrdering(null), reload(), onChange())} />}
    </Sheet>
  );
}

function OrderSheet({ mode, request, onClose, onDone }: { mode: 'order' | 'tender'; request: ReqDetail; onClose: () => void; onDone: () => void }) {
  const suppliers = useApi<{ id: string; name: string }[]>(mode === 'order' ? '/directory/suppliers' : null);
  const left = request.lines.filter((l) => l.outstanding > 0);
  const [picked, setPicked] = useState<Set<string>>(new Set(left.map((l) => l.id)));
  const inWeek = new Date(Date.now() + 7 * 864e5 + 3 * 3600e3).toISOString().slice(0, 16);
  const [f, setF] = useState({ supplierId: '', method: 'Quotation', closesAt: inWeek, title: '' });
  const [state, setState] = useState<{ busy: boolean; error: string; made?: { id: string; label: string; to: string } }>({ busy: false, error: '' });
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      if (mode === 'order') {
        const po = await api<{ id: string; number: string }>(`/requisitions/${request.request.id}/order`, { body: { supplierId: f.supplierId, lineIds: [...picked] } });
        setState({ busy: false, error: '', made: { id: po.id, label: po.number, to: `/purchases/${po.id}` } });
      } else {
        const t = await api<{ id: string }>('/tenders/from-request', { body: { requisitionId: request.request.id, title: f.title, method: f.method, closesAt: new Date(f.closesAt).toISOString(), lineIds: [...picked] } });
        setState({ busy: false, error: '', made: { id: t.id, label: 'the tender', to: `/procurement/tenders?id=${t.id}` } });
      }
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  if (state.made)
    return (
      <Sheet open onClose={onDone} title={mode === 'order' ? 'Order drafted' : 'Tender drafted'}>
        <p className="rounded-md bg-good-soft p-3 text-sm text-good">{mode === 'order' ? <>Purchase order <b>{state.made.label}</b> is a draft: check the prices and send it.</> : 'The tender is a draft: check it, then publish it to take bids.'}</p>
        <SheetFooter cancel="Close"><Link className="btn btn-primary" to={state.made.to}>Open {state.made.label}</Link></SheetFooter>
      </Sheet>
    );
  return (
    <Sheet open onClose={onClose} title={mode === 'order' ? 'Order from a supplier' : 'Put out to tender'} description={mode === 'order' ? 'A draft purchase order, charged to the department.' : 'Ask several suppliers to quote; bids stay sealed until it closes.'}>
      <div className="space-y-4 text-sm">
        <div className="space-y-1.5">
          {left.map((l) => (
            <label key={l.id} className="flex items-center gap-2.5">
              <input type="checkbox" className="h-4 w-4" checked={picked.has(l.id)} onChange={(e) => { const n = new Set(picked); if (e.target.checked) n.add(l.id); else n.delete(l.id); setPicked(n); }} />
              <span className="flex-1">{l.outstanding} {l.unit} {l.description}</span>
              <span className="num text-muted">{ksh(Math.round(l.outstanding * l.estimatedUnitCost))}</span>
            </label>
          ))}
        </div>
        {mode === 'order' ? (
          <Field label="Supplier">
            <select className="field" value={f.supplierId} onChange={(e) => setF({ ...f, supplierId: e.target.value })}>
              <option value="">Choose…</option>
              {(suppliers.data ?? []).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </select>
          </Field>
        ) : (
          <>
            <Field label="Title (optional)"><input className="field" value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} placeholder={`Supply for request ${request.request.number}`} /></Field>
            <Field label="How"><Chips label="Method" value={f.method} onChange={(v) => setF({ ...f, method: v })} options={[['Quotation', 'Ask chosen suppliers'], ['Open', 'Open tender (posted publicly)']]} /></Field>
            <Field label="Bids close at"><input className="field" type="datetime-local" value={f.closesAt} onChange={(e) => setF({ ...f, closesAt: e.target.value })} /></Field>
          </>
        )}
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy || picked.size === 0 || (mode === 'order' && !f.supplierId)} onClick={save}>{mode === 'order' ? 'Draft the order' : 'Draft the tender'}</button></SheetFooter>
      </div>
    </Sheet>
  );
}

/** A document's decisions as the approvals register holds them, oldest first, each with the start of its fingerprint. */
export function Trail({ rows }: { rows: { seq: number; at: string; actor: string; action: string; amount: number; note: string; hash: string }[] }) {
  return (
    <section>
      <p className="mb-2 font-semibold">Decisions <span className="font-normal text-muted">· from the approvals register, which can’t be changed</span></p>
      <ol className="relative space-y-3 border-l border-line pl-4">
        {rows.map((t) => (
          <li key={t.seq} className="relative">
            <span className={cn('absolute -left-[1.3rem] top-1 h-2.5 w-2.5 rounded-full ring-2 ring-card', /turned down|cancel/i.test(t.action) ? 'bg-bad' : /over budget/i.test(t.action) ? 'bg-warn' : 'bg-good')} />
            <p><b>{t.action}</b> <span className="text-muted">· {t.actor} · {dateTime(t.at)}</span></p>
            {t.note && <p className="text-muted">“{t.note}”</p>}
            <p className="font-mono text-2xs text-faint" title={t.hash}>#{t.seq} · {t.hash.slice(0, 16)}…</p>
          </li>
        ))}
      </ol>
    </section>
  );
}
