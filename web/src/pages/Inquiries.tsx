// The inquiries inbox. A salesperson is deciding "who do I answer first, and how do I turn this into a sale?",
// so the list puts unanswered inquiries first with how long they've waited, and the thread offers the next step:
// reply, make a quote, or close it as won or lost (a lost inquiry needs a reason: that's how the business learns
// why people ask but don't buy).
import { useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { Clock, FileText, Loader2, MessageCircle, Plus, Send, Sparkles } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, dateTime, ksh, msisdn, prettyPhone } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, Sheet, Status, SheetFooter } from '../components/ui';

const CHANNELS = ['WhatsApp', 'Call', 'WalkIn', 'Facebook', 'Instagram', 'TikTok', 'LinkedIn', 'X', 'Email', 'Sms', 'Website', 'Referral', 'Event', 'Other'];
const label = (c: string) => (c === 'WalkIn' ? 'Walk-in' : c === 'Sms' ? 'SMS' : c);

export default function Inquiries() {
  const session = useSession();
  const [params, setParams] = useSearchParams();
  const status = params.get('status') ?? 'active';
  const mine = params.get('mine') === '1';
  const { data, error, reload } = useApi<any>(`/inquiries?status=${status}${mine ? '&mine=true' : ''}`);
  const [open, setOpen] = useState<string | null>(params.get('id'));
  const [adding, setAdding] = useState(false);

  return (
    <div>
      <PageHeader title="Inquiries" subtitle="Questions from your shop, links, calls and walk-ins. Answer the oldest first."
        actions={can(session, 'sell') ? <button className="btn btn-primary" onClick={() => setAdding(true)}><Plus className="h-4 w-4" /> Log an inquiry</button> : undefined} />
      <div className="mb-4 flex flex-wrap items-center gap-3">
        <Chips label="Show" value={status} onChange={(v) => setParams({ status: v, ...(mine ? { mine: '1' } : {}) })}
          options={[['active', 'To deal with', (data?.counts.open ?? 0) + (data?.counts.answered ?? 0) + (data?.counts.quoted ?? 0)], ['open', 'Not answered', data?.counts.open], ['waiting', 'Waiting on customer', (data?.counts.answered ?? 0) + (data?.counts.quoted ?? 0)], ['won', 'Bought', data?.counts.won], ['lost', 'Lost', data?.counts.lost]]} />
        <label className="flex items-center gap-2 text-sm"><input type="checkbox" className="h-4 w-4 accent-[var(--color-brand)]" checked={mine} onChange={(e) => setParams({ status, ...(e.target.checked ? { mine: '1' } : {}) })} /> Only mine</label>
      </div>
      {error ? <ErrorNote>{error}</ErrorNote> : !data ? <Loading /> : data.items.length === 0 ? (
        <Empty title="Nothing here">{status === 'active' ? 'No inquiries waiting. Share your shop link or a tracking link to bring them in.' : 'No inquiries in this list.'}</Empty>
      ) : (
        <ul className="card divide-y divide-line">
          {data.items.map((i: any) => (
            <li key={i.id}>
              <button className="flex w-full items-start gap-3 px-4 py-3 text-left hover:bg-paper" onClick={() => setOpen(i.id)}>
                <span className="min-w-0 flex-1">
                  <span className="flex flex-wrap items-center gap-2"><b className="truncate">{i.name}</b><span className="text-xs text-muted">{i.number} · {label(i.channel)}</span>{i.broughtBy && <span className="rounded bg-brand-soft px-1.5 py-0.5 text-2xs font-medium text-brand">via {i.broughtBy}</span>}</span>
                  <span className="block truncate text-sm text-muted">{i.subject}</span>
                </span>
                <span className="shrink-0 text-right text-xs">
                  {i.waitingHours != null ? <span className={cn('inline-flex items-center gap-1 font-semibold', i.waitingHours > 4 ? 'text-bad' : 'text-warn')}><Clock className="h-3.5 w-3.5" /> {i.waitingHours < 1 ? 'Just now' : `${Math.round(i.waitingHours)} h waiting`}</span>
                    : <Status tone={i.status === 'Won' ? 'good' : i.status === 'Lost' ? 'bad' : 'plain'}>{i.status === 'Answered' ? 'Answered' : i.status === 'Quoted' ? 'Quoted' : i.status === 'Won' ? 'Bought' : i.status === 'Lost' ? `Lost: ${i.lostReason}` : i.status}</Status>}
                  <span className="block text-muted">{i.assignedName || 'Unassigned'}</span>
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
      {open && <Thread id={open} onClose={() => (setOpen(null), reload())} />}
      <NewInquiry open={adding} onClose={() => setAdding(false)} onDone={(id) => (setAdding(false), reload(), setOpen(id))} />
    </div>
  );
}


function InquiryQuoteVarianceCard({ inquiryId }: { inquiryId: string }) {
  const { data: v } = useApi<any>(`/inquiries/${inquiryId}/variance`);
  if (!v) return null;

  const isIdentical = v.status === 'Identical';
  const isDiscount = v.varianceAmount < 0;

  return (
    <div className="rounded-lg border border-line bg-gradient-to-br from-paper to-white p-3.5 text-xs shadow-sm space-y-2">
      <div className="flex items-center justify-between gap-2 border-b border-line pb-2">
        <span className="font-semibold text-ink flex items-center gap-1.5">
          <Sparkles className="h-3.5 w-3.5 text-brand" /> Quote vs Invoice Audit
        </span>
        <span className={cn('rounded-full px-2 py-0.5 font-bold text-2xs',
          isIdentical ? 'bg-good-soft text-good' : isDiscount ? 'bg-warn-soft text-warn' : 'bg-indigo-soft text-indigo'
        )}>
          {isIdentical ? 'Exact match' : `${v.varianceAmount > 0 ? '+' : ''}${ksh(v.varianceAmount)} (${v.variancePercent > 0 ? '+' : ''}${v.variancePercent}%)`}
        </span>
      </div>
      <div className="grid grid-cols-2 gap-2 text-muted">
        <div>Quoted: <b className="text-ink">{ksh(v.quotedTotal)}</b> ({v.quoteNumber})</div>
        <div>Invoiced: <b className="text-ink">{ksh(v.invoicedTotal)}</b> ({v.invoiceNumber})</div>
      </div>
      <div className="rounded bg-paper p-2 text-ink">
        <p className="font-medium text-2xs"><b className="text-brand">Agent Explanation:</b> {v.agentSummary}</p>
        {v.agentReasons?.length > 0 && (
          <ul className="mt-1 list-inside list-disc text-2xs text-muted space-y-0.5">
            {v.agentReasons.map((r: string, idx: number) => <li key={idx}>{r}</li>)}
          </ul>
        )}
      </div>
    </div>
  );
}

function Thread({ id, onClose }: { id: string; onClose: () => void }) {
  const session = useSession();
  const navigate = useNavigate();
  const { data, error, reload } = useApi<any>(`/inquiries/${id}`);
  const [text, setText] = useState('');
  const [reason, setReason] = useState('');
  const [state, setState] = useState({ busy: '', error: '' });
  const act = async (what: string, fn: () => Promise<any>) => {
    setState({ busy: what, error: '' });
    try {
      const r = await fn();
      setState({ busy: '', error: '' });
      reload();
      return r;
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };
  const wa = data && msisdn(data.phone);
  const active = data && ['Open', 'Answered', 'Quoted'].includes(data.status);
  return (
    <Sheet open onClose={onClose} title={data ? `${data.number} · ${data.name}` : 'Inquiry'}>
      {error ? <ErrorNote>{error}</ErrorNote> : !data ? <Loading /> : (
        <div className="space-y-4">
          <p className="text-sm text-muted">
            {label(data.channel)}{data.phone && <> · {prettyPhone(data.phone)}</>}{data.item && <> · asked about <b className="text-ink">{data.item}</b></>} · {data.assignedName ? `with ${data.assignedName}` : 'nobody yet'}{data.broughtBy && <> · <span className="font-semibold text-brand">Brought by: {data.broughtBy}</span></>}
          </p>
          <InquiryQuoteVarianceCard inquiryId={id} />
          <ol className="space-y-2">
            {data.messages.map((m: any, i: number) => (
              <li key={i} className={cn('max-w-[85%] rounded-lg px-3 py-2 text-sm', m.from === 'customer' ? 'bg-paper' : 'ml-auto bg-brand-soft')}>
                <p className="whitespace-pre-wrap">{m.text}</p>
                <p className="mt-1 text-2xs text-muted">{m.author} · {dateTime(m.at)}</p>
              </li>
            ))}
          </ol>
          {active && can(session, 'sell') && (
            <>
              <div className="space-y-2">
                <textarea className="field min-h-20" value={text} onChange={(e) => setText(e.target.value)} placeholder="Reply (they see it on their portal)" />
                <div className="flex flex-wrap gap-2">
                  <button className="btn btn-primary" disabled={!text.trim() || Boolean(state.busy)} onClick={() => act('reply', async () => { await api(`/inquiries/${id}/reply`, { body: { text } }); setText(''); })}>
                    {state.busy === 'reply' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />} Reply
                  </button>
                  {wa && <a className="btn btn-mpesa" target="_blank" rel="noreferrer" href={`https://wa.me/${wa}?text=${encodeURIComponent(text || `Hello ${data.name}, `)}`} onClick={() => text.trim() && api(`/inquiries/${id}/reply`, { body: { text } }).then(() => (setText(''), reload()))}><MessageCircle className="h-4 w-4" /> Reply on WhatsApp</a>}
                  {data.quoteId ? <Link className="btn btn-ghost" to={`/sales/${data.quoteId}`}><FileText className="h-4 w-4" /> Open the quote</Link>
                    : <button className="btn btn-ghost" disabled={Boolean(state.busy)} onClick={async () => { const r = await act('quote', () => api<any>(`/inquiries/${id}/quote`, { body: {} })); if (r?.quoteId) navigate(`/sales/${r.quoteId}/edit`); }}><FileText className="h-4 w-4" /> Make a quote</button>}
                </div>
              </div>
              <div className="space-y-2 border-t border-line pt-3">
                <p className="text-sm font-semibold">Didn’t buy? Say why</p>
                <div className="flex flex-wrap gap-1.5">
                  {data.lostReasons.map((r: string) => (
                    <button key={r} onClick={() => setReason(r)} className={cn('rounded-full px-3 py-1 text-xs font-semibold ring-1', reason === r ? 'bg-brand-soft text-brand ring-brand/40' : 'bg-white ring-line')}>{r}</button>
                  ))}
                </div>
                <button className="btn btn-ghost text-bad" disabled={!reason || Boolean(state.busy)} onClick={() => act('lost', () => api(`/inquiries/${id}/close`, { body: { won: false, reason } }))}>Close as lost</button>
              </div>
            </>
          )}
          {data.status === 'Won' && <p className="rounded-md bg-good-soft px-3 py-2 text-sm text-good">Bought.{data.invoiceId && <> <Link className="underline" to={`/sales/${data.invoiceId}`}>Open the invoice</Link></>}</p>}
          {data.status === 'Lost' && <p className="rounded-md bg-paper px-3 py-2 text-sm">Lost: {data.lostReason}</p>}
          {data.customerId && <Link className="text-sm font-semibold text-brand" to={`/customers/${data.customerId}`}>Customer profile and journey →</Link>}
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
        </div>
      )}
    </Sheet>
  );
}

function NewInquiry({ open, onClose, onDone }: { open: boolean; onClose: () => void; onDone: (id: string) => void }) {
  const [f, setF] = useState({ name: '', phone: '', message: '', channel: 'WhatsApp', broughtBy: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      const r = await api<any>('/inquiries', { body: f });
      setF({ name: '', phone: '', message: '', channel: 'WhatsApp', broughtBy: '' });
      setState({ busy: false, error: '' });
      onDone(r.id);
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open={open} onClose={onClose} title="Log an inquiry">
      <div className="space-y-4">
        <p className="text-sm text-muted">For questions that came by phone, WhatsApp or in person, so they’re followed up and counted.</p>
        <div>
          <p className="mb-1.5 text-sm font-semibold">How did they reach you?</p>
          <div className="flex flex-wrap gap-1.5">
            {CHANNELS.map((c) => <button key={c} onClick={() => setF({ ...f, channel: c })} className={cn('rounded-full px-3 py-1 text-xs font-semibold ring-1', f.channel === c ? 'bg-brand-soft text-brand ring-brand/40' : 'bg-white ring-line')}>{label(c)}</button>)}
          </div>
        </div>
        <Field label="Name"><input className="field" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} /></Field>
        <Field label="Phone"><input className="field" inputMode="tel" value={f.phone} onChange={(e) => setF({ ...f, phone: e.target.value })} placeholder="0712 345 678" /></Field>
        <Field label="Who brought them in?" hint="Salesperson name, tracking link, campaign, or referral"><input className="field" value={f.broughtBy} onChange={(e) => setF({ ...f, broughtBy: e.target.value })} placeholder="e.g. Kelvin, John Safari, Facebook Campaign" /></Field>
        <Field label="What they asked"><textarea className="field min-h-20" value={f.message} onChange={(e) => setF({ ...f, message: e.target.value })} /></Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter>
        <button className="btn btn-primary" disabled={state.busy || f.message.trim().length < 2} onClick={save}>{state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Save</button>
        </SheetFooter>
      </div>
    </Sheet>
  );
}
