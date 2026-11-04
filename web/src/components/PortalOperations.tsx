// The employee portal's "Requests & faults": report something broken (by its label's tag, or where it is), ask for
// what the department needs, follow both, and, for a head of department, answer the department's requests.
import { useState } from 'react';
import { Plus, Wrench } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { dateTime, ksh } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageError, Sheet, SheetFooter, Status, askDialog } from './ui';
import { RequestForm, REQ_STATUS } from '../pages/Requisitions';
import PortalAdvances from './PortalAdvances';

type Req = {
  id: string; number: string; department: string; requestedBy: string; neededBy: string | null; purpose: string; status: keyof typeof REQ_STATUS; estimatedTotal: number; rejectReason: string; createdAt: string;
  lines: { description: string; quantity: number; unit: string; estimatedUnitCost: number; issuedQuantity: number; orderedQuantity: number }[];
};
type Ops = {
  departments: { id: string; name: string }[]; myDepartmentId: string | null; locations: { id: string; name: string }[];
  assets: { id: string; tag: string; name: string; status: string }[];
  faults: { id: string; number: string; title: string; status: string; rejectReason: string; createdAt: string; resolvedAt: string | null }[];
  requests: { mine: Req[]; toApprove: Req[]; isHead: boolean }; codes: string[];
};
const FAULT: Record<string, [string, 'good' | 'warn' | 'bad' | 'info' | 'plain']> = { New: ['With maintenance', 'warn'], Accepted: ['Being fixed', 'info'], Resolved: ['Fixed', 'good'], Rejected: ['Closed', 'plain'] };

export default function PortalOperations() {
  const { data, error, reload } = useApi<Ops>('/me/operations');
  const [asking, setAsking] = useState(false);
  const [reporting, setReporting] = useState(false);
  const [state, setState] = useState({ busy: '', error: '' });
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const decide = async (r: Req, approve: boolean) => {
    const reason = approve ? '' : await askDialog({ title: `Turn down ${r.number}?`, label: `Why (${r.requestedBy} sees this)`, required: true, confirm: 'Turn down', danger: true });
    if (!approve && !reason) return;
    setState({ busy: r.id, error: '' });
    try {
      await api(`/me/requests/${r.id}/${approve ? 'approve' : 'reject'}`, { body: approve ? {} : { reason } });
      reload();
      setState({ busy: '', error: '' });
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };
  return (
    <div className="space-y-6">
      <div className="flex flex-wrap gap-2">
        <button className="btn btn-primary" onClick={() => setReporting(true)}><Wrench className="h-4 w-4" /> Report something broken</button>
        <button className="btn btn-ghost" onClick={() => setAsking(true)}><Plus className="h-4 w-4" /> Ask for something</button>
      </div>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}

      {data.requests.isHead && (
        <section className="card overflow-hidden">
          <p className="border-b border-line px-4 py-3 font-semibold">Waiting for your approval ({data.requests.toApprove.length})</p>
          {data.requests.toApprove.length === 0 ? <p className="p-4 text-sm text-muted">Nothing is waiting for you.</p> : (
            <ul className="divide-y divide-line">
              {data.requests.toApprove.map((r) => (
                <li key={r.id} className="space-y-2 px-4 py-3 text-sm">
                  <div className="flex flex-wrap items-center gap-2"><b>{r.number}</b><span className="text-muted">{r.requestedBy} · {r.department}</span><span className="flex-1" /><span className="num font-semibold">{ksh(r.estimatedTotal)}</span></div>
                  {r.purpose && <p>{r.purpose}</p>}
                  <ul className="text-muted">{r.lines.map((l, i) => <li key={i}>{l.quantity} {l.unit} {l.description}{l.estimatedUnitCost > 0 && ` · ${ksh(Math.round(l.quantity * l.estimatedUnitCost))}`}</li>)}</ul>
                  <div className="flex gap-2">
                    <button className="btn btn-primary" disabled={!!state.busy} onClick={() => decide(r, true)}>Approve</button>
                    <button className="btn btn-ghost" disabled={!!state.busy} onClick={() => decide(r, false)}>Turn down</button>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>
      )}

      <section>
        <p className="mb-2 font-semibold">What you’ve asked for</p>
        {data.requests.mine.length === 0 ? <Empty title="No requests yet">Ask for what your department needs: stationery, tools, protective gear. Your head of department approves it, then procurement.</Empty> : (
          <ul className="card divide-y divide-line">
            {data.requests.mine.map((r) => (
              <li key={r.id} className="px-4 py-3 text-sm">
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <p><b className="whitespace-nowrap">{r.number}</b> {r.purpose || r.lines.map((l) => l.description).join(', ')}</p>
                    <p className="text-muted">{r.department} · {dateTime(r.createdAt)}{r.rejectReason && ` · ${r.rejectReason}`}</p>
                  </div>
                  <span className="num shrink-0 font-semibold">{ksh(r.estimatedTotal)}</span>
                </div>
                <div className="mt-2 flex items-center gap-3">
                  <Status tone={REQ_STATUS[r.status][1]}>{REQ_STATUS[r.status][0]}</Status>
                  {(r.status === 'WithHead' || r.status === 'WithBuying' || r.status === 'WithFinance') && <button className="text-xs text-muted hover:text-bad" onClick={() => api(`/me/requests/${r.id}/cancel`, { body: {} }).then(reload)}>Cancel request</button>}
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>

      <PortalAdvances />

      <section>
        <p className="mb-2 font-semibold">What you’ve reported</p>
        {data.faults.length === 0 ? <p className="text-sm text-muted">Nothing reported. If something’s broken, report it: it goes straight to maintenance.</p> : (
          <ul className="card divide-y divide-line">
            {data.faults.map((f) => (
              <li key={f.id} className="px-4 py-3 text-sm">
                <p><b className="whitespace-nowrap">{f.number}</b> {f.title}</p>
                <p className="text-muted">{dateTime(f.createdAt)}{f.rejectReason && ` · ${f.rejectReason}`}</p>
                <div className="mt-2"><Status tone={FAULT[f.status]?.[1] ?? 'plain'}>{FAULT[f.status]?.[0] ?? f.status}</Status></div>
              </li>
            ))}
          </ul>
        )}
      </section>

      {data.assets.length > 0 && (
        <section>
          <p className="mb-2 font-semibold">In your care</p>
          <ul className="card divide-y divide-line text-sm">
            {data.assets.map((a) => <li key={a.id} className="flex items-center gap-3 px-4 py-2.5"><code className="text-xs text-muted">{a.tag}</code><span className="flex-1">{a.name}</span></li>)}
          </ul>
        </section>
      )}

      {asking && <RequestForm portal departments={data.departments} defaultDepartment={data.myDepartmentId} onClose={() => setAsking(false)} onSaved={() => (setAsking(false), reload())} />}
      {reporting && <ReportSheet ops={data} onClose={() => setReporting(false)} onSaved={() => (setReporting(false), reload())} />}
    </div>
  );
}

function ReportSheet({ ops, onClose, onSaved }: { ops: Ops; onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState({ assetTag: '', locationId: '', title: '', description: '', symptom: '', priority: 'Normal', assetDown: false });
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      await api('/me/faults', { body: { ...f, locationId: f.locationId || null } });
      onSaved();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title="Report something broken" description="It goes straight to maintenance. You’ll see here when it’s being fixed.">
      <div className="space-y-4">
        <Field label="Its tag" hint="On the label, e.g. AST-EQP-0004. Or choose where it is below.">
          <div className="space-y-2">
            <input className="field uppercase" value={f.assetTag} onChange={(e) => setF({ ...f, assetTag: e.target.value })} placeholder="AST-…" />
            {ops.assets.length > 0 && (
              <select className="field" value="" onChange={(e) => e.target.value && setF({ ...f, assetTag: e.target.value })} aria-label="One of yours">
                <option value="">Or one in your care…</option>
                {ops.assets.map((a) => <option key={a.id} value={a.tag}>{a.tag} · {a.name}</option>)}
              </select>
            )}
          </div>
        </Field>
        {!f.assetTag && (
          <Field label="Where">
            <select className="field" value={f.locationId} onChange={(e) => setF({ ...f, locationId: e.target.value })}>
              <option value="">Choose a place…</option>
              {ops.locations.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </select>
          </Field>
        )}
        <Field label="What’s wrong"><input className="field" value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} placeholder="The printer jams on every page" /></Field>
        <Field label="What it’s doing">
          <select className="field" value={f.symptom} onChange={(e) => setF({ ...f, symptom: e.target.value })}>
            <option value="">Choose…</option>
            {ops.codes.map((c) => <option key={c}>{c}</option>)}
          </select>
        </Field>
        <Field label="More detail (optional)"><textarea className="field min-h-20" value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} /></Field>
        <label className="flex items-center gap-2.5 text-sm"><input type="checkbox" className="h-4 w-4" checked={f.assetDown} onChange={(e) => setF({ ...f, assetDown: e.target.checked })} /> It can’t be used at all</label>
        <Field label="How urgent"><Chips label="Priority" value={f.priority} onChange={(v) => setF({ ...f, priority: v })} options={[['Emergency', 'Emergency'], ['High', 'High'], ['Normal', 'Normal'], ['Low', 'Low']]} /></Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy || f.title.trim().length < 3 || (!f.assetTag.trim() && !f.locationId)} onClick={save}>Report it</button></SheetFooter>
      </div>
    </Sheet>
  );
}
