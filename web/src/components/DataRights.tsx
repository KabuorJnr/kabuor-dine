// The customer's rights under the Data Protection Act, for the owner: hand them a copy of everything held about
// them (due within 7 days of asking), or erase them (within 14 days). Erasure is final, so the sheet says exactly
// what goes and what tax law makes the business keep before anything happens.
import { useState } from 'react';
import { Download, Loader2, ShieldCheck, UserX } from 'lucide-react';
import { api } from '../lib/api';
import { ErrorNote, Field, Sheet, SheetFooter } from './ui';

export default function DataRights({ customer, onErased }: { customer: { id: string; number: string; name: string }; onErased: () => void }) {
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState('');
  const [typed, setTyped] = useState('');
  const [state, setState] = useState<{ busy: string; error: string; done?: string }>({ busy: '', error: '' });

  const download = async () => {
    setState({ busy: 'export', error: '' });
    try {
      const data = await api<unknown>(`/customers/${customer.id}/data-export`);
      const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
      const a = Object.assign(document.createElement('a'), { href: url, download: `${customer.number}-personal-data.json` });
      a.click();
      URL.revokeObjectURL(url);
      setState({ busy: '', error: '' });
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };
  const erase = async () => {
    setState({ busy: 'erase', error: '' });
    try {
      const r = await api<{ note: string }>(`/customers/${customer.id}/erase`, { body: { reason } });
      setState({ busy: '', error: '', done: r.note });
      onErased();
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };

  return (
    <section className="space-y-2 border-t border-line pt-4">
      <h3 className="flex items-center gap-1.5 text-sm font-semibold"><ShieldCheck className="h-4 w-4 text-muted" /> Their data</h3>
      <p className="text-xs text-muted">If {customer.name} asks, the law gives you 7 days to send them their data and 14 to erase it.</p>
      <div className="flex flex-wrap gap-2">
        <button className="btn btn-ghost" disabled={state.busy === 'export'} onClick={download}>{state.busy === 'export' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />} Download their data</button>
        <button className="btn btn-ghost text-bad" onClick={() => setOpen(true)}><UserX className="h-4 w-4" /> Erase…</button>
      </div>
      {state.error && !open && <ErrorNote>{state.error}</ErrorNote>}
      <Sheet open={open} onClose={() => setOpen(false)} title={`Erase ${customer.name}`}>
        {state.done ? (
          <p className="rounded-md bg-good-soft p-3 text-sm text-good">{state.done}</p>
        ) : (
          <div className="space-y-4 text-sm">
            <div className="space-y-1.5">
              <p className="font-semibold">What goes</p>
              <p className="text-muted">Phone, email, address and notes; their inquiries and messages; leads and follow-ups; marketing messages; M-Pesa payer names and numbers; and the old values in the audit log. They won't get marketing again.</p>
              <p className="font-semibold">What stays</p>
              <p className="text-muted">Invoices and payments, because tax law requires keeping them for five years. If they have issued invoices, their name and KRA PIN stay on those; otherwise the name goes too. Anything they still owe stays owed.</p>
            </div>
            <Field label="Why (for the audit log)"><input className="field" value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Asked by email on 21 October" /></Field>
            <Field label={`Type ${customer.number} to confirm`}><input className="field" value={typed} onChange={(e) => setTyped(e.target.value)} /></Field>
            {state.error && <ErrorNote>{state.error}</ErrorNote>}
            <SheetFooter>
            <button className="btn btn-primary bg-bad" disabled={state.busy === 'erase' || typed.trim() !== customer.number || reason.trim().length < 3} onClick={erase}>
              {state.busy === 'erase' && <Loader2 className="h-4 w-4 animate-spin" />} Erase their personal data
            </button>
            </SheetFooter>
            <p className="text-xs text-faint">This can't be undone.</p>
          </div>
        )}
      </Sheet>
    </section>
  );
}
