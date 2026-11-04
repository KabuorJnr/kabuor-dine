// The approvals register: every decision about money, in order, each sealed with a fingerprint of itself and of the
// one before it. The check walks the whole chain; the latest fingerprint can be written down or sent to an auditor, so
// even rewriting the whole register would be caught.
import { useState } from 'react';
import { Link } from 'react-router';
import { Copy, ShieldAlert, ShieldCheck } from 'lucide-react';
import { api, download, useApi } from '../lib/api';
import { cn, dateTime, ksh } from '../lib/format';
import { Chips, Empty, Loading, PageError, PageHeader, SearchBox } from '../components/ui';

type Rec = { seq: number; at: string; actor: string; subject: string; subjectId: string; reference: string; action: string; amount: number; note: string; hash: string; prevHash: string };
type Check = { intact: boolean; records: number; brokenAt: number | null; problem: string | null; fingerprint: string; checkedAt: string };

const LINK: Record<string, (id: string) => string> = {
  Request: (id) => `/procurement?id=${id}`, PurchaseOrder: (id) => `/purchases/${id}`, Tender: (id) => `/procurement/tenders?id=${id}`, WorkOrder: (id) => `/maintenance?wo=${id}`, Budget: () => '/finance/budgets',
};
const SUBJECT: Record<string, string> = { Request: 'Request', PurchaseOrder: 'Order', Tender: 'Tender', WorkOrder: 'Work order', Budget: 'Budget' };

export default function Approvals() {
  const [subject, setSubject] = useState('');
  const [q, setQ] = useState('');
  const list = useApi<Rec[]>(`/approvals?take=300${subject ? `&subject=${subject}` : ''}${q ? `&q=${encodeURIComponent(q)}` : ''}`);
  const check = useApi<Check>('/approvals/verify');
  const [copied, setCopied] = useState(false);
  if (list.error) return <PageError>{list.error}</PageError>;
  const c = check.data;
  return (
    <div className="space-y-5">
      <PageHeader print title="Approvals register" subtitle="Every decision about money, in the order it was made. Nobody can change or remove a record: each one is sealed to the one before it." />
      {c && (
        <div className={cn('card flex flex-wrap items-center gap-4 p-4', c.intact ? 'ring-1 ring-good/30' : 'ring-2 ring-bad/50')}>
          {c.intact ? <ShieldCheck className="h-8 w-8 text-good" /> : <ShieldAlert className="h-8 w-8 text-bad" />}
          <div className="min-w-0 flex-1">
            <p className={cn('font-semibold', !c.intact && 'text-bad')}>{c.intact ? `Intact: all ${c.records} records check out` : `Tampering found at record ${c.brokenAt}`}</p>
            <p className="text-sm text-muted">{c.intact ? `Checked ${dateTime(c.checkedAt)}. Write the fingerprint below in your records or send it to your auditor: if anything before it changes, it won’t match.` : c.problem}</p>
          </div>
          {c.fingerprint && (
            <button className="btn btn-ghost max-w-full font-mono text-xs" title="Copy the latest fingerprint" onClick={() => { navigator.clipboard?.writeText(c.fingerprint).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500); }).catch(() => {}); }}>
              <Copy className="h-3.5 w-3.5" /> <span className="truncate">{copied ? 'Copied' : `${c.fingerprint.slice(0, 20)}…`}</span>
            </button>
          )}
        </div>
      )}
      <KeptCheck />
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
        <Chips label="Show" value={subject} onChange={setSubject} options={[['', 'Everything'], ['Budget', 'Budgets'], ['Request', 'Requests'], ['PurchaseOrder', 'Orders'], ['Tender', 'Tenders'], ['WorkOrder', 'Work orders']]} />
        <div className="sm:ml-auto sm:w-64"><SearchBox value={q} onChange={setQ} placeholder="Number or person" /></div>
      </div>
      {!list.data ? <Loading /> : list.data.length === 0 ? (
        <Empty title="No decisions yet">Budgets, requests, orders released by finance, tender awards and closed jobs appear here as they happen.</Empty>
      ) : (
        <div className="card overflow-x-auto">
          <table className="w-full min-w-[46rem] text-sm">
            <thead className="bg-paper text-left text-xs text-muted"><tr><th className="px-4 py-2.5">#</th><th className="px-4 py-2.5">When</th><th className="px-4 py-2.5">What</th><th className="px-4 py-2.5">Decision</th><th className="px-4 py-2.5">By</th><th className="px-4 py-2.5 text-right">Amount</th><th className="px-4 py-2.5">Fingerprint</th></tr></thead>
            <tbody className="divide-y divide-line">
              {list.data.map((r) => (
                <tr key={r.seq} className={cn(c && !c.intact && c.brokenAt !== null && r.seq >= c.brokenAt && 'bg-bad-soft')}>
                  <td className="num px-4 py-2.5 text-muted">{r.seq}</td>
                  <td className="whitespace-nowrap px-4 py-2.5">{dateTime(r.at)}</td>
                  <td className="px-4 py-2.5"><span className="text-xs text-muted">{SUBJECT[r.subject] ?? r.subject}</span> <Link className="font-semibold text-brand hover:underline" to={(LINK[r.subject] ?? (() => '#'))(r.subjectId)}>{r.reference}</Link></td>
                  <td className="px-4 py-2.5"><b className={cn(/turned down|cancel/i.test(r.action) && 'text-bad', /over budget/i.test(r.action) && 'text-warn')}>{r.action}</b>{r.note && <p className="max-w-xs truncate text-xs text-muted" title={r.note}>{r.note}</p>}</td>
                  <td className="px-4 py-2.5">{r.actor}</td>
                  <td className="num px-4 py-2.5 text-right">{r.amount ? ksh(r.amount) : '—'}</td>
                  <td className="px-4 py-2.5 font-mono text-2xs text-faint" title={`${r.hash}\nafter ${r.prevHash || '(first record)'}`}>{r.hash.slice(0, 12)}…</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

/** Checking a fingerprint kept outside the system, and taking a statement to keep. */
function KeptCheck() {
  const [f, setF] = useState('');
  const [res, setRes] = useState<{ found: boolean; message: string } | null>(null);
  const [error, setError] = useState('');
  const check = async () => {
    setError(''); setRes(null);
    try { setRes(await api('/approvals/check', { body: { fingerprint: f } })); } catch (e) { setError((e as Error).message); }
  };
  const statement = () => download('/approvals/statement', `approvals-register-statement-${new Date().toISOString().slice(0, 10)}.txt`).catch((e) => setError((e as Error).message));
  return (
    <section className="card space-y-2 p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="font-semibold">Check a fingerprint you kept</p>
        <button className="btn btn-ghost" onClick={statement}>Download a statement to keep</button>
      </div>
      <p className="text-sm text-muted">Paste a fingerprint from an earlier statement (or one your auditor holds). If it’s no longer found, the register was changed after it was taken.</p>
      <div className="flex gap-2">
        <input className="field min-w-0 flex-1 font-mono text-xs" value={f} onChange={(e) => setF(e.target.value)} placeholder="e.g. 5fcc41397e9f99eb5ff3…" aria-label="Fingerprint" />
        <button className="btn btn-primary" disabled={f.trim().length < 16} onClick={check}>Check</button>
      </div>
      {res && <p className={cn('rounded-md p-2 text-sm', res.found ? 'bg-good-soft text-good' : 'bg-bad-soft text-bad')}>{res.message}</p>}
      {error && <p className="text-sm text-bad">{error}</p>}
    </section>
  );
}
