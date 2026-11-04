// Activity: every change anyone made, newest first (the audit trail the database writes with each save). The owner is
// asking "who changed this, and when?", so each line says who, what, which record and the fields that changed;
// it can be narrowed to one kind of record. Passwords and keys are never in it: it only says they changed.
import { useMemo, useState } from 'react';
import { useApi } from '../lib/api';
import { cn, dateTime } from '../lib/format';
import { Loading, PageError } from './ui';

type Entry = { id: number; actor: string; at: string; action: 'Added' | 'Modified' | 'Deleted'; entity: string; entityId: string; changes: string };

const NAMES: Record<string, string> = {
  SalesDoc: 'Invoice or quote', SalesLine: 'Invoice line', Payment: 'Payment', PaymentAllocation: 'Payment match', JournalEntry: 'Journal entry',
  JournalLine: 'Journal line', Customer: 'Customer', Item: 'Product', User: 'Login', Company: 'Business settings', Supplier: 'Supplier',
  PurchaseDoc: 'Purchase order or bill', PurchaseLine: 'Purchase line', SupplierPayment: 'Supplier payment', StockMove: 'Stock movement',
  StockLevel: 'Stock level', Employee: 'Employee', LeaveRequest: 'Leave request', PayrollRun: 'Payroll', Payslip: 'Payslip', Lead: 'Lead',
  DispatchTrip: 'Trip', DeliveryItem: 'Delivery', Asset: 'Asset', JobOpening: 'Job opening', Candidate: 'Applicant', BillingPayment: 'Plan payment',
  Sequence: 'Numbering', Account: 'Account', Location: 'Store',
};
const name = (e: string) => NAMES[e] ?? e.replace(/([a-z])([A-Z])/g, '$1 $2');
const VERB = { Added: 'added', Modified: 'changed', Deleted: 'deleted' } as const;
// Fields that change with every save and say nothing to a person reading the log.
const NOISE = new Set(['Id', 'CompanyId', 'CreatedAt', 'Next', 'TokenVersion', 'FailedLogins', 'LastSignInAt', 'TotpLastStep']);
// The parts that change along with a document (its lines, its journal, stock counts): shown when asked for by name.
const PARTS = new Set(['Sequence', 'SalesLine', 'PurchaseLine', 'JournalLine', 'JournalEntry', 'PaymentAllocation', 'StockLevel', 'StockMove']);

function fields(raw: string, action: Entry['action']) {
  try {
    const map = JSON.parse(raw) as Record<string, unknown>;
    return Object.entries(map)
      .filter(([k]) => !NOISE.has(k))
      .map(([k, v]) => {
        const label = k.replace(/([a-z])([A-Z])/g, '$1 $2');
        if (action === 'Modified' && v && typeof v === 'object' && 'to' in (v as object)) {
          const { from, to } = v as { from: unknown; to: unknown };
          return `${label}: ${show(from)} → ${show(to)}`;
        }
        return `${label}: ${show(v)}`;
      });
  } catch {
    return [];
  }
}
const show = (v: unknown) => (v === null || v === undefined || v === '' ? '—' : typeof v === 'object' ? JSON.stringify(v) : String(v)).slice(0, 80);

export default function ActivityLog() {
  const [entity, setEntity] = useState('');
  const { data, error } = useApi<Entry[]>(`/audit?take=300${entity ? `&entity=${entity}` : ''}`);
  const [open, setOpen] = useState<number | null>(null);
  const kinds = useMemo(() => Object.keys(NAMES).sort((a, b) => name(a).localeCompare(name(b))), []);
  if (error) return <PageError>{error}</PageError>;
  const rows = (data ?? []).filter((e) => entity || !PARTS.has(e.entity));
  let lastDay = '';
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-muted">Every change, newest first. Passwords and keys show only as “changed”. {!entity && 'Lines and journal entries show when you pick them.'}</p>
        <select className="field w-auto" value={entity} onChange={(e) => setEntity(e.target.value)} aria-label="Kind of record">
          <option value="">Everything</option>
          {kinds.map((k) => <option key={k} value={k}>{name(k)}</option>)}
        </select>
      </div>
      {!data ? <Loading /> : rows.length === 0 ? (
        <p className="card p-5 text-sm text-muted">No changes recorded{entity ? ` to ${name(entity).toLowerCase()} records` : ''} yet.</p>
      ) : (
        <ol className="card divide-y divide-line">
          {rows.map((e) => {
            const d = new Date(e.at).toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long' });
            const header = d !== lastDay;
            lastDay = d;
            const list = fields(e.changes, e.action);
            return (
              <li key={e.id}>
                {header && <p className="bg-paper px-4 py-1.5 text-2xs font-semibold tracking-wider text-faint uppercase">{d}</p>}
                <button className="flex w-full items-start gap-3 px-4 py-2.5 text-left hover:bg-paper" onClick={() => setOpen(open === e.id ? null : e.id)} aria-expanded={open === e.id}>
                  <span className={cn('mt-1.5 h-2 w-2 shrink-0 rounded-full', e.action === 'Added' ? 'bg-good' : e.action === 'Deleted' ? 'bg-bad' : 'bg-brand')} />
                  <span className="min-w-0 flex-1 text-sm">
                    <b>{e.actor || 'System'}</b> {VERB[e.action]} {name(e.entity).toLowerCase()}
                    {e.action === 'Modified' && list.length > 0 && <span className="text-muted"> · {list.slice(0, 2).map((x) => x.split(':')[0]).join(', ')}{list.length > 2 ? ` +${list.length - 2}` : ''}</span>}
                    {open === e.id && list.length > 0 && (
                      <ul className="mt-2 space-y-0.5 rounded-md bg-paper p-2 font-mono text-xs break-all text-ink-2 ring-1 ring-line">
                        {list.map((x) => <li key={x}>{x}</li>)}
                      </ul>
                    )}
                  </span>
                  <span className="shrink-0 text-xs text-muted">{dateTime(e.at).split(', ').pop()}</span>
                </button>
              </li>
            );
          })}
        </ol>
      )}
    </div>
  );
}
