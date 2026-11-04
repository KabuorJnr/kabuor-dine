// Moving in from Excel, QuickBooks, Sage or Zoho. The owner is deciding "is this what I want brought in?", so a
// file is always checked first: how many will be added, how many skipped and why, and the opening balances in
// shillings. Nothing is saved until they confirm.
import { useRef, useState } from 'react';
import { FileUp, Loader2 } from 'lucide-react';
import { api } from '../lib/api';
import { cn, ksh } from '../lib/format';
import { ErrorNote, Field } from './ui';

const KINDS = [
  ['customers', 'Customers', 'Name, phone, email, KRA PIN and what each owes you today. From QuickBooks: Customers → Export. From Sage: Customer listing to Excel.'],
  ['suppliers', 'Suppliers', 'Name, phone, email, KRA PIN and what you owe each today.'],
  ['items', 'Products & stock', 'Name, code, selling price, cost, quantity on hand and VAT (16%, 8%, exempt). Stock comes in at its cost.'],
] as const;

export default function MoveIn() {
  const [kind, setKind] = useState<(typeof KINDS)[number][0]>('customers');
  const [asAt, setAsAt] = useState(new Date(Date.now() + 3 * 3600e3).toISOString().slice(0, 10));
  const [file, setFile] = useState<{ name: string; csv: string } | null>(null);
  const [preview, setPreview] = useState<any>(null);
  const [state, setState] = useState({ busy: false, error: '', done: '' });
  const input = useRef<HTMLInputElement>(null);

  const check = async (f: { name: string; csv: string }, commit: boolean) => {
    setState({ busy: true, error: '', done: '' });
    try {
      const r = await api<any>(`/import/${kind}`, { body: { csv: f.csv, commit, asAt } });
      if (commit) {
        setState({ busy: false, error: '', done: `Brought in ${r.adding} ${KINDS.find((k) => k[0] === kind)![1].toLowerCase()}${r.openingBalances ? ` with ${ksh(r.openingBalances)} in opening balances` : ''}${r.stockValue ? ` and ${ksh(r.stockValue)} of stock` : ''}.` });
        setPreview(null);
        setFile(null);
      } else {
        setPreview(r);
        setState({ busy: false, error: '', done: '' });
      }
    } catch (e) {
      setState({ busy: false, error: (e as Error).message, done: '' });
    }
  };

  return (
    <section className="card space-y-4 p-5">
      <div className="grid gap-2 sm:grid-cols-3">
        {KINDS.map(([id, label, hint]) => (
          <button key={id} onClick={() => (setKind(id), setPreview(null), setFile(null))} className={cn('rounded-md p-3 text-left ring-1', kind === id ? 'bg-brand-soft ring-2 ring-brand' : 'bg-white ring-line')}>
            <b className="block text-sm">{label}</b>
            <span className="text-xs text-muted">{hint}</span>
          </button>
        ))}
      </div>
      {kind !== 'items' && (
        <Field label="Balances are as at" hint="The day your old system’s figures are from; they’re booked as opening balances, not as this year’s sales.">
          <input className="field max-w-xs" type="date" value={asAt} onChange={(e) => setAsAt(e.target.value)} />
        </Field>
      )}
      <input ref={input} type="file" accept=".csv,text/csv" className="hidden" onChange={async (e) => { const f = e.target.files?.[0]; if (!f) return; const x = { name: f.name, csv: await f.text() }; setFile(x); check(x, false); if (input.current) input.current.value = ''; }} />
      <button className="btn btn-primary" disabled={state.busy} onClick={() => input.current?.click()}>
        {state.busy && !preview ? <Loader2 className="h-4 w-4 animate-spin" /> : <FileUp className="h-4 w-4" />} Choose a CSV file
      </button>
      <p className="text-xs text-muted">Excel: File → Save As → CSV. Column titles are recognised whether they come from QuickBooks, Sage, Zoho or your own sheet.</p>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      {state.done && <p className="rounded-md bg-good-soft px-4 py-2.5 text-sm font-medium text-good">{state.done}</p>}
      {preview && file && (
        <div className="space-y-3 rounded-md bg-paper p-4">
          <p className="text-sm">
            <b>{file.name}</b>: <b className="text-good">{preview.adding} to bring in</b>
            {preview.skipping > 0 && <>, <b className="text-warn">{preview.skipping} skipped</b></>}
            {preview.openingBalances > 0 && <>, {ksh(preview.openingBalances)} in opening balances</>}
            {preview.stockValue > 0 && <>, {ksh(preview.stockValue)} of stock at cost</>}.
          </p>
          <ul className="max-h-64 divide-y divide-line overflow-y-auto rounded-md bg-white text-sm ring-1 ring-line">
            {preview.rows.map((r: any) => (
              <li key={r.line} className="flex justify-between gap-3 px-3 py-1.5">
                <span className="min-w-0 truncate">{r.name}</span>
                <span className={cn('shrink-0 text-xs', r.status === 'add' ? (r.problem ? 'text-warn' : 'text-good') : 'text-muted')}>{r.problem ?? 'Will be added'}</span>
              </li>
            ))}
          </ul>
          <div className="flex gap-2">
            <button className="btn btn-primary" disabled={state.busy || preview.adding === 0} onClick={() => check(file, true)}>
              {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Bring in {preview.adding}
            </button>
            <button className="btn btn-ghost" onClick={() => (setPreview(null), setFile(null))}>Cancel</button>
          </div>
        </div>
      )}
    </section>
  );
}
