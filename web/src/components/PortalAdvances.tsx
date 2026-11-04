// The employee portal's cash advances: ask for one, see where it is, account for it with receipts, and (for a head of
// department) answer the department's.
import { useState } from 'react';
import { HandCoins } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { day, ksh } from '../lib/format';
import { ErrorNote, Status, askDialog } from './ui';
import { ADV_STATUS, AccountForm, AdvanceForm, type AdvanceRow } from '../pages/Advances';

export default function PortalAdvances() {
  const { data, reload } = useApi<{ mine: AdvanceRow[]; toApprove: AdvanceRow[]; isHead: boolean }>('/me/advances');
  const [asking, setAsking] = useState(false);
  const [accounting, setAccounting] = useState<AdvanceRow | null>(null);
  const [error, setError] = useState('');
  const run = async (fn: () => Promise<unknown>) => { setError(''); try { await fn(); reload(); } catch (e) { setError((e as Error).message); } };
  if (!data) return null;
  return (
    <section className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="font-semibold">Cash advances</p>
        <button className="btn btn-ghost" onClick={() => setAsking(true)}><HandCoins className="h-4 w-4" /> Ask for an advance</button>
      </div>
      {error && <ErrorNote>{error}</ErrorNote>}
      {data.isHead && data.toApprove.length > 0 && (
        <ul className="card divide-y divide-line text-sm">
          {data.toApprove.map((a) => (
            <li key={a.id} className="space-y-2 px-4 py-3">
              <p><b>{a.number}</b> {a.employee}: {a.purpose} · <b className="num">{ksh(a.amount)}</b></p>
              <div className="flex gap-2">
                <button className="btn btn-primary" onClick={() => run(() => api(`/me/advances/${a.id}/approve`, { body: {} }))}>Approve</button>
                <button className="btn btn-ghost" onClick={async () => { const reason = await askDialog({ title: `Turn down ${a.number}?`, label: 'Why', required: true, confirm: 'Turn down', danger: true }); if (reason) run(() => api(`/me/advances/${a.id}/reject`, { body: { reason } })); }}>Turn down</button>
              </div>
            </li>
          ))}
        </ul>
      )}
      {data.mine.length === 0 ? <p className="text-sm text-muted">Going on a trip for work? Ask for the money before you go, then bring the receipts back.</p> : (
        <ul className="card divide-y divide-line text-sm">
          {data.mine.map((a) => (
            <li key={a.id} className="px-4 py-3">
              <div className="flex items-start justify-between gap-3">
                <p className="min-w-0"><b className="whitespace-nowrap">{a.number}</b> {a.purpose}</p>
                <span className="num shrink-0 font-semibold">{ksh(a.amount)}</span>
              </div>
              <div className="mt-2 flex flex-wrap items-center gap-2">
                <Status tone={ADV_STATUS[a.status][1]}>{ADV_STATUS[a.status][0]}</Status>
                {a.status === 'PaidOut' && <span className={a.overdue ? 'text-bad' : 'text-muted'}>Receipts by {day(a.accountBy)}</span>}
                {a.status === 'PaidOut' && <button className="btn btn-primary" onClick={() => setAccounting(a)}>Account for it</button>}
                {(a.status === 'WithHead' || a.status === 'WithFinance' || a.status === 'SignedOff') && <button className="text-xs text-muted hover:text-bad" onClick={() => run(() => api(`/me/advances/${a.id}/cancel`, { body: {} }))}>Cancel</button>}
                {a.rejectReason && <span className="text-muted">{a.rejectReason}</span>}
              </div>
            </li>
          ))}
        </ul>
      )}
      {asking && <AdvanceForm portal onClose={() => setAsking(false)} onSaved={() => (setAsking(false), reload())} />}
      {accounting && <AccountForm advance={accounting} path={`/me/advances/${accounting.id}/account`} accountsPath="/me/expense-accounts" onClose={() => setAccounting(null)} onDone={() => (setAccounting(null), reload())} />}
    </section>
  );
}
