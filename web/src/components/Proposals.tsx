// What Msaidizi wants to do, waiting for a person. Nothing happens until someone taps Approve.
import { useState } from 'react';
import { Check, Loader2, MessageCircle, Sparkles, X } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { dateTime } from '../lib/format';

type Proposal = { id: string; kind: string; summary: string; createdAt: string };

export default function Proposals({ onChange, compact }: { onChange?: () => void; compact?: boolean }) {
  const { data, reload } = useApi<Proposal[]>('/assistant/proposals');
  const [busy, setBusy] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  if (!data?.length) return null;

  const decide = async (p: Proposal, approve: boolean) => {
    setBusy(p.id);
    try {
      const r = await api<{ status: string; result?: string }>(`/assistant/proposals/${p.id}/${approve ? 'approve' : 'reject'}`, { body: {} });
      if (approve && p.kind === 'send_reminder' && r.result?.startsWith('https://wa.me/')) window.open(r.result, '_blank', 'noopener');
      setNote(r.status === 'Failed' ? `Couldn’t do it: ${r.result}` : approve ? (p.kind === 'send_reminder' ? 'WhatsApp opened with the message ready. Tap send.' : 'Done.') : 'Dismissed.');
      reload();
      onChange?.();
    } catch (e) {
      setNote((e as Error).message);
    } finally {
      setBusy(null);
    }
  };

  return (
    <section className={compact ? '' : 'card p-4 sm:p-5'} aria-label="Waiting for your approval">
      <h2 className="flex items-center gap-2 text-sm font-semibold text-muted">
        <Sparkles className="h-4 w-4 text-indigo" /> Msaidizi is waiting for your OK ({data.length})
      </h2>
      <ul className="mt-3 divide-y divide-line">
        {data.map((p) => (
          <li key={p.id} className="flex flex-col gap-2 py-3 sm:flex-row sm:items-center">
            <div className="min-w-0 flex-1">
              <p className="text-sm font-medium">{p.summary}</p>
              <p className="text-xs text-muted">{dateTime(p.createdAt)}</p>
            </div>
            <div className="flex gap-2">
              <button className="btn btn-ghost py-2" disabled={busy === p.id} onClick={() => decide(p, false)}>
                <X className="h-4 w-4" /> Dismiss
              </button>
              <button className={p.kind === 'send_reminder' ? 'btn btn-mpesa py-2' : 'btn btn-primary py-2'} disabled={busy === p.id} onClick={() => decide(p, true)}>
                {busy === p.id ? <Loader2 className="h-4 w-4 animate-spin" /> : p.kind === 'send_reminder' ? <MessageCircle className="h-4 w-4" /> : <Check className="h-4 w-4" />}
                {p.kind === 'send_reminder' ? 'Approve & open WhatsApp' : 'Approve'}
              </button>
            </div>
          </li>
        ))}
      </ul>
      {note && <p className="mt-2 text-sm font-medium text-muted">{note}</p>}
    </section>
  );
}
