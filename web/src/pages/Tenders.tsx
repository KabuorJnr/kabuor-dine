// Tenders and requests for quotations. Procurement is deciding "who should supply this, fairly and at the best value?",
// so a tender moves through plain steps: write it, publish it (a link to send to suppliers, or posted publicly), wait
// for it to close with the bids sealed, open them, check each against the requirements, and award. The comparison
// table and the ranking make the choice visible; the award and regret notices are ready to send.
import { Fragment, useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { Copy, ExternalLink, Gavel, Lock, Plus, Trash2, Trophy } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, dateTime, ksh, toCents } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageError, PageHeader, Sheet, SheetFooter, Status, askDialog, confirmDialog } from '../components/ui';

type TStatus = 'Draft' | 'Published' | 'Opened' | 'Awarded' | 'Cancelled';
type TRow = { id: string; number: string; title: string; method: string; status: TStatus; closesAt: string; publishedAt: string | null; openedAt: string | null; awardedAt: string | null; bids: number; closed: boolean; lines: number };
const statusOf = (t: { status: TStatus; closed: boolean }): [string, 'good' | 'warn' | 'bad' | 'info' | 'plain'] =>
  t.status === 'Published' ? (t.closed ? ['Closed, ready to open', 'warn'] : ['Taking bids', 'info']) : t.status === 'Opened' ? ['Evaluating', 'warn'] : t.status === 'Awarded' ? ['Awarded', 'good'] : t.status === 'Cancelled' ? ['Cancelled', 'plain'] : ['Draft', 'plain'];

const bidLink = (token: string) => `${location.origin}/bid/${token}`;

export default function Tenders() {
  const session = useSession();
  const canBuy = can(session, 'buy');
  const [params, setParams] = useSearchParams();
  const [view, setView] = useState('live');
  const { data, error, reload } = useApi<{ counts: Record<string, number>; items: TRow[] }>(`/tenders?view=${view}`);
  const [creating, setCreating] = useState(false);
  const openId = params.get('id');
  const open = (id: string | null) => setParams(id ? { id } : {}, { replace: true });
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const c = data.counts;
  return (
    <div>
      <PageHeader print
        title="Tenders & quotations"
        subtitle="Ask suppliers to bid. Bids stay sealed until the closing time, then they’re opened, compared and one is awarded."
        actions={canBuy && <button className="btn btn-primary" onClick={() => setCreating(true)}><Plus className="h-4 w-4" /> New tender</button>}
      />
      <div className="space-y-4">
        <Chips label="Show" value={view} onChange={setView} options={[['live', 'Live', c.open + c.toOpen + c.evaluating], ['drafts', 'Drafts', c.drafts], ['done', 'Awarded or cancelled', c.done], ['all', 'All']]} />
        {data.items.length === 0 ? (
          <Empty title="No tenders here" action={canBuy && <button className="btn btn-primary" onClick={() => setCreating(true)}>Start one</button>}>
            For anything worth comparing prices on: send a request for quotations to a few suppliers, or post an open tender.
          </Empty>
        ) : (
          <ul className="card divide-y divide-line">
            {data.items.map((t) => {
              const [label, tone] = statusOf(t);
              return (
                <li key={t.id}>
                  <button className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-paper" onClick={() => open(t.id)}>
                    <Gavel className="h-4 w-4 shrink-0 text-faint" />
                    <span className="min-w-0 flex-1">
                      <span className="flex items-center gap-2"><code className="text-xs font-semibold text-muted">{t.number}</code><b className="truncate text-sm">{t.title}</b></span>
                      <span className="mt-0.5 block truncate text-sm text-muted">{t.method === 'Open' ? 'Open tender' : 'Quotations'} · {t.lines} line{t.lines === 1 ? '' : 's'} · {t.bids} bid{t.bids === 1 ? '' : 's'} · {t.closed ? 'closed' : 'closes'} {dateTime(t.closesAt)}</span>
                    </span>
                    <Status tone={tone}>{label}</Status>
                  </button>
                </li>
              );
            })}
          </ul>
        )}
      </div>
      {creating && <TenderForm onClose={() => setCreating(false)} onSaved={(id) => (setCreating(false), reload(), open(id))} />}
      {openId && <TenderSheet id={openId} canBuy={canBuy} onClose={() => open(null)} onChange={reload} />}
    </div>
  );
}

type TLine = { description: string; specification: string; quantity: string; unit: string };

function TenderForm({ onClose, onSaved }: { onClose: () => void; onSaved: (id: string) => void }) {
  const departments = useApi<{ id: string; name: string }[]>('/departments');
  const inWeek = new Date(Date.now() + 7 * 864e5 + 3 * 3600e3).toISOString().slice(0, 16);
  const [f, setF] = useState({ title: '', scope: '', requirements: 'Copy of KRA PIN certificate\nValid tax compliance certificate\nCertificate of incorporation or business registration', method: 'Quotation', evaluation: 'LowestPrice', technicalWeight: '30', technicalPassMark: '70', closesAt: inWeek, standstillDays: '14', departmentId: '' });
  const [lines, setLines] = useState<TLine[]>([{ description: '', specification: '', quantity: '1', unit: '' }]);
  const [state, setState] = useState({ busy: false, error: '' });
  const set = (i: number, patch: Partial<TLine>) => setLines(lines.map((l, j) => (j === i ? { ...l, ...patch } : l)));
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      const r = await api<{ id: string }>('/tenders', { body: {
        ...f, departmentId: f.departmentId || null, technicalWeight: Number(f.technicalWeight), technicalPassMark: Number(f.technicalPassMark), standstillDays: Number(f.standstillDays),
        closesAt: new Date(f.closesAt).toISOString(), lines: lines.filter((l) => l.description.trim()).map((l) => ({ ...l, quantity: Number(l.quantity) || 0 })),
      } });
      onSaved(r.id);
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title="New tender" description="What you want, by when, and how bids will be judged. Bidders see all of this." size="lg">
      <div className="space-y-4">
        <Field label="Title"><input className="field" autoFocus value={f.title} onChange={(e) => setF({ ...f, title: e.target.value })} placeholder="Supply of office stationery, January to June 2027" /></Field>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label="How"><Chips label="Method" value={f.method} onChange={(v) => setF({ ...f, method: v })} options={[['Quotation', 'Ask chosen suppliers'], ['Open', 'Open tender']]} /></Field>
          <Field label="For department (optional)">
            <select className="field" value={f.departmentId} onChange={(e) => setF({ ...f, departmentId: e.target.value })}>
              <option value="">None</option>
              {(departments.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
          </Field>
        </div>
        <Field label="Scope and conditions"><textarea className="field min-h-20" value={f.scope} onChange={(e) => setF({ ...f, scope: e.target.value })} placeholder="Delivery to our Thika store within 14 days of the order. Prices include VAT and delivery. Payment 30 days after delivery." /></Field>
        <Field label="What bidders must have or send" hint="A bid missing any of these is not responsive and can’t win."><textarea className="field min-h-20" value={f.requirements} onChange={(e) => setF({ ...f, requirements: e.target.value })} /></Field>
        <div className="space-y-2">
          <p className="text-sm font-semibold">What to price</p>
          {lines.map((l, i) => (
            <div key={i} className="grid gap-2 rounded-md bg-paper p-2 ring-1 ring-line sm:grid-cols-[1fr_5rem_5rem_auto]">
              <div className="space-y-2">
                <input className="field" value={l.description} onChange={(e) => set(i, { description: e.target.value })} placeholder="Item, e.g. A4 printing paper, 80gsm" aria-label="Item" />
                <input className="field" value={l.specification} onChange={(e) => set(i, { specification: e.target.value })} placeholder="Specification (brand, size, standard)" aria-label="Specification" />
              </div>
              <input className="field num" inputMode="decimal" value={l.quantity} onChange={(e) => set(i, { quantity: e.target.value })} aria-label="Quantity" />
              <input className="field" value={l.unit} onChange={(e) => set(i, { unit: e.target.value })} placeholder="Unit" aria-label="Unit" />
              <button className="btn btn-ghost px-2" aria-label="Remove line" disabled={lines.length === 1} onClick={() => setLines(lines.filter((_, j) => j !== i))}><Trash2 className="h-4 w-4" /></button>
            </div>
          ))}
          <button className="btn btn-ghost" onClick={() => setLines([...lines, { description: '', specification: '', quantity: '1', unit: '' }])}><Plus className="h-4 w-4" /> Another line</button>
        </div>
        <Field label="How bids are judged">
          <Chips label="Evaluation" value={f.evaluation} onChange={(v) => setF({ ...f, evaluation: v })} options={[['LowestPrice', 'Lowest price (of responsive bids)'], ['Weighted', 'Quality and price']]} />
        </Field>
        {f.evaluation === 'Weighted' && (
          <div className="grid grid-cols-2 gap-3">
            <Field label="Weight on quality (%)" hint="The rest is price."><input className="field num" inputMode="numeric" value={f.technicalWeight} onChange={(e) => setF({ ...f, technicalWeight: e.target.value })} /></Field>
            <Field label="Quality pass mark" hint="Out of 100. Below this, price doesn’t count."><input className="field num" inputMode="numeric" value={f.technicalPassMark} onChange={(e) => setF({ ...f, technicalPassMark: e.target.value })} /></Field>
          </div>
        )}
        <div className="grid grid-cols-2 gap-3">
          <Field label="Bids close at"><input className="field" type="datetime-local" value={f.closesAt} onChange={(e) => setF({ ...f, closesAt: e.target.value })} /></Field>
          <Field label="Standstill (days)" hint="After the award, before the order goes."><input className="field num" inputMode="numeric" value={f.standstillDays} onChange={(e) => setF({ ...f, standstillDays: e.target.value })} /></Field>
        </div>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy || f.title.trim().length < 5} onClick={save}>Save as draft</button></SheetFooter>
      </div>
    </Sheet>
  );
}

type Bid = {
  id: string; bidderName: string; kraPin: string; contactName: string; phone: string; email: string; deliveryDays: number; validityDays: number; notes: string; total: number; receipt: string; submittedAt: string;
  responsive: boolean | null; technicalScore: number | null; evaluationNote: string; financialScore: number | null; combinedScore: number | null; rank: number | null; qualifies: boolean; why: string; awarded: boolean;
  lines: { tenderLineId: string; unitPrice: number; total: number; note: string }[];
};
type TDetail = {
  id: string; number: string; title: string; scope: string; requirements: string; method: string; status: TStatus; evaluation: string; technicalWeight: number; technicalPassMark: number;
  closesAt: string; standstillDays: number; publishedAt: string | null; openedAt: string | null; openedBy: string; awardedAt: string | null; awardedBy: string; awardReason: string; cancelReason: string;
  standstillEnds: string | null; token: string; department: string; request: { id: string; number: string } | null; purchaseOrder: { id: string; number: string; status: string; total: number } | null; closed: boolean;
  companySlug: string | null; lines: { id: string; description: string; specification: string; unit: string; quantity: number }[]; bidCount: number; bids: Bid[] | null;
  notices: { award: string; regret: string; others: { bidderName: string; phone: string; email: string }[]; winner: { bidderName: string; phone: string; email: string } } | null;
};

function TenderSheet({ id, canBuy, onClose, onChange }: { id: string; canBuy: boolean; onClose: () => void; onChange: () => void }) {
  const { data: t, error, reload } = useApi<TDetail>(`/tenders/${id}`);
  const [state, setState] = useState({ busy: '', error: '' });
  const [copied, setCopied] = useState('');
  const [scoring, setScoring] = useState<Bid | null>(null);
  const run = async (what: string, fn: () => Promise<unknown>) => {
    setState({ busy: what, error: '' });
    try { await fn(); reload(); onChange(); setState({ busy: '', error: '' }); } catch (e) { setState({ busy: '', error: (e as Error).message }); }
  };
  const copy = async (text: string, what: string) => { await navigator.clipboard.writeText(text); setCopied(what); setTimeout(() => setCopied(''), 1500); };
  if (error) return <Sheet open onClose={onClose} title="Tender"><ErrorNote>{error}</ErrorNote></Sheet>;
  if (!t) return <Sheet open onClose={onClose} title="Tender"><Loading /></Sheet>;
  const [label, tone] = statusOf(t);
  const award = async (b: Bid) => {
    const top = b.rank === 1;
    if (top) {
      if (await confirmDialog({ title: `Award to ${b.bidderName}?`, body: `${ksh(b.total)}. A draft purchase order is made; it can go to them after the ${t.standstillDays}-day standstill.`, confirm: 'Award' })) run('award', () => api(`/tenders/${id}/award`, { body: { bidId: b.id } }));
    } else {
      const reason = await askDialog({ title: `Award to ${b.bidderName}?`, body: 'This isn’t the top-ranked bid. The reason is kept on record.', label: 'Why this bid', required: true, confirm: 'Award' });
      if (reason) run('award', () => api(`/tenders/${id}/award`, { body: { bidId: b.id, reason } }));
    }
  };
  return (
    <Sheet open onClose={onClose} title={`${t.number} · ${t.title}`} description={`${t.method === 'Open' ? 'Open tender' : 'Request for quotations'}${t.department ? ` · ${t.department}` : ''}${t.request ? ` · from request ${t.request.number}` : ''}`} size="lg">
      <div className="space-y-5 text-sm">
        <div className="flex flex-wrap items-center gap-2">
          <Status tone={tone}>{label}</Status>
          <span className="text-muted">{t.closed ? 'Closed' : 'Closes'} {dateTime(t.closesAt)} · {t.bidCount} bid{t.bidCount === 1 ? '' : 's'} in</span>
        </div>
        {t.cancelReason && <p className="rounded-md bg-paper p-3 ring-1 ring-line">Cancelled: {t.cancelReason}</p>}

        {canBuy && t.status === 'Draft' && (
          <div className="flex flex-wrap gap-2">
            <button className="btn btn-primary" disabled={!!state.busy} onClick={() => run('publish', () => api(`/tenders/${id}/publish`, { body: {} }))}>Publish and take bids</button>
            <button className="btn btn-ghost text-bad" disabled={!!state.busy} onClick={async () => { const reason = await askDialog({ title: 'Cancel this tender?', label: 'Why', required: true, confirm: 'Cancel it', danger: true }); if (reason) run('cancel', () => api(`/tenders/${id}/cancel`, { body: { reason } })); }}>Cancel</button>
          </div>
        )}
        {t.status === 'Published' && (
          <div className="space-y-2 rounded-md bg-brand-soft p-3">
            <p className="font-semibold text-brand">{t.method === 'Open' && t.companySlug ? 'Posted on your public tenders page. Share the link too:' : t.method === 'Open' ? 'Open to anyone with this link: share it widely (newspaper, website, WhatsApp groups)' : 'Send this link to the suppliers you want to quote'}</p>
            <div className="flex flex-wrap gap-2">
              <code className="min-w-0 flex-1 truncate rounded bg-white px-2 py-1.5 text-xs ring-1 ring-line">{bidLink(t.token)}</code>
              <button className="btn btn-ghost" onClick={() => copy(bidLink(t.token), 'link')}><Copy className="h-4 w-4" /> {copied === 'link' ? 'Copied' : 'Copy'}</button>
              <a className="btn btn-ghost" href={`https://wa.me/?text=${encodeURIComponent(`Please quote for ${t.title} (${t.number}). Bids close ${dateTime(t.closesAt)}: ${bidLink(t.token)}`)}`} target="_blank" rel="noreferrer">WhatsApp</a>
            </div>
            {t.method === 'Open' && t.companySlug && <p className="text-xs text-muted">All your open tenders: <a className="underline" href={`/tenders/${t.companySlug}`} target="_blank" rel="noreferrer">{location.origin}/tenders/{t.companySlug}</a></p>}
            <p className="flex items-center gap-1.5 text-xs text-muted"><Lock className="h-3.5 w-3.5" /> Bids are sealed: nobody here can see who bid or at what price until it closes and is opened.</p>
            {canBuy && t.closed && <button className="btn btn-primary mt-1" disabled={!!state.busy} onClick={() => run('open', () => api(`/tenders/${id}/open`, { body: {} }))}>Open the {t.bidCount} bid{t.bidCount === 1 ? '' : 's'}</button>}
          </div>
        )}
        {state.error && <ErrorNote>{state.error}</ErrorNote>}

        <section>
          <p className="mb-2 font-semibold">What’s being priced</p>
          <ul className="divide-y divide-line rounded-md ring-1 ring-line">
            {t.lines.map((l) => <li key={l.id} className="flex gap-3 px-3 py-2"><span className="flex-1">{l.description}{l.specification && <span className="block text-xs text-muted">{l.specification}</span>}</span><span className="num">{l.quantity} {l.unit}</span></li>)}
          </ul>
          {t.requirements && <p className="mt-2 whitespace-pre-line text-muted"><b className="text-ink">Requirements:</b> {t.requirements}</p>}
          {t.evaluation === 'Weighted' && <p className="mt-1 text-muted">Judged on quality ({t.technicalWeight}%, pass mark {t.technicalPassMark}) and price ({100 - t.technicalWeight}%).</p>}
        </section>

        {t.bids && (
          <section>
            <p className="mb-2 font-semibold">Bids, best first{t.openedAt && <span className="font-normal text-muted"> · opened {dateTime(t.openedAt)} by {t.openedBy}</span>}</p>
            {t.bids.length === 0 ? <p className="text-muted">No bids came in. Cancel it and ask again, perhaps with more time or more suppliers.</p> : (
              <div className="overflow-x-auto rounded-md ring-1 ring-line">
                <table className="w-full min-w-[40rem]">
                  <thead className="bg-paper text-left text-xs text-muted">
                    <tr><th className="px-3 py-2">#</th><th className="px-3 py-2">Bidder</th>{t.lines.map((l) => <th key={l.id} className="px-3 py-2 text-right">{l.description}</th>)}<th className="px-3 py-2 text-right">Total</th><th className="px-3 py-2">Delivery</th><th className="px-3 py-2">Evaluation</th><th /></tr>
                  </thead>
                  <tbody className="divide-y divide-line">
                    {t.bids.map((b) => (
                      <tr key={b.id} className={cn(b.awarded && 'bg-good-soft')}>
                        <td className="num px-3 py-2 font-semibold">{b.rank ?? '—'}</td>
                        <td className="px-3 py-2"><b>{b.bidderName}</b><span className="block text-xs text-muted">{[b.kraPin, b.phone].filter(Boolean).join(' · ')}</span></td>
                        {t.lines.map((l) => { const p = b.lines.find((x) => x.tenderLineId === l.id); return <td key={l.id} className="num px-3 py-2 text-right">{p ? ksh(p.unitPrice) : '—'}</td>; })}
                        <td className="num px-3 py-2 text-right font-semibold">{ksh(b.total)}</td>
                        <td className="px-3 py-2">{b.deliveryDays ? `${b.deliveryDays} days` : '—'}</td>
                        <td className="px-3 py-2">
                          {b.responsive === null ? <span className="text-muted">Not checked</span> : b.qualifies ? <span className="text-good">Responsive{b.technicalScore !== null && ` · quality ${b.technicalScore}`}{b.combinedScore !== null && ` · score ${b.combinedScore}`}</span> : <span className="text-bad">{b.why}</span>}
                          {b.evaluationNote && <span className="block text-xs text-muted">{b.evaluationNote}</span>}
                        </td>
                        <td className="px-2 py-1.5 text-right whitespace-nowrap">
                          {canBuy && t.status === 'Opened' && <button className="btn btn-ghost px-2 py-1 text-xs" onClick={() => setScoring(b)}>Evaluate</button>}
                          {canBuy && t.status === 'Opened' && b.qualifies && <button className="btn btn-primary ml-1 px-2 py-1 text-xs" disabled={!!state.busy} onClick={() => award(b)}><Trophy className="h-3.5 w-3.5" /> Award</button>}
                          {b.awarded && <Status tone="good">Awarded</Status>}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {t.status === 'Opened' && t.bids.some((b) => b.responsive === null) && <p className="mt-2 text-muted">Check each bid against the requirements (Evaluate) before awarding. Only responsive bids are ranked.</p>}
          </section>
        )}

        {t.status === 'Awarded' && t.notices && (
          <section className="space-y-3">
            <div className="rounded-md bg-good-soft p-3 text-good">
              Awarded {t.awardedAt && dateTime(t.awardedAt)} by {t.awardedBy}{t.awardReason && `: ${t.awardReason}`}.
              {t.purchaseOrder && <> Order <Link className="font-semibold underline" to={`/purchases/${t.purchaseOrder.id}`}>{t.purchaseOrder.number}</Link> ({ksh(t.purchaseOrder.total)}) can go to them after {t.standstillEnds && dateTime(t.standstillEnds)}.</>}
            </div>
            {[['Award notice', t.notices.award, [t.notices.winner]], ['Regret notice (to the others)', t.notices.regret, t.notices.others]].map(([title, text, to]) => (
              <div key={title as string} className="rounded-md ring-1 ring-line">
                <div className="flex items-center justify-between border-b border-line px-3 py-2"><b>{title as string}</b><button className="btn btn-ghost px-2 py-1 text-xs" onClick={() => copy(text as string, title as string)}><Copy className="h-3.5 w-3.5" /> {copied === title ? 'Copied' : 'Copy'}</button></div>
                <p className="px-3 py-2">{text as string}</p>
                <p className="px-3 pb-2 text-xs text-muted">To: {(to as { bidderName: string; phone: string }[]).map((x) => `${x.bidderName} (${x.phone})`).join(', ') || 'no one'}</p>
              </div>
            ))}
          </section>
        )}
        {t.status !== 'Draft' && t.status !== 'Awarded' && t.status !== 'Cancelled' && canBuy && (
          <button className="btn btn-ghost text-bad" disabled={!!state.busy} onClick={async () => { const reason = await askDialog({ title: `Cancel ${t.number}?`, body: 'Bidders are told it’s cancelled.', label: 'Why', required: true, confirm: 'Cancel the tender', danger: true }); if (reason) run('cancel', () => api(`/tenders/${id}/cancel`, { body: { reason } })); }}>Cancel tender</button>
        )}
        {t.status === 'Published' && <a className="inline-flex items-center gap-1.5 text-brand hover:underline" href={bidLink(t.token)} target="_blank" rel="noreferrer"><ExternalLink className="h-4 w-4" /> See it as bidders do</a>}
      </div>
      {scoring && <EvaluateSheet tender={t} bid={scoring} onClose={() => setScoring(null)} onDone={() => (setScoring(null), reload())} />}
    </Sheet>
  );
}

function EvaluateSheet({ tender, bid, onClose, onDone }: { tender: TDetail; bid: Bid; onClose: () => void; onDone: () => void }) {
  const weighted = tender.evaluation === 'Weighted';
  const [f, setF] = useState({ responsive: bid.responsive ?? true, score: bid.technicalScore?.toString() ?? '', note: bid.evaluationNote });
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(`/tenders/${tender.id}/bids/${bid.id}/evaluate`, { body: { responsive: f.responsive, technicalScore: weighted && f.score ? Number(f.score) : null, note: f.note } });
      onDone();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title={`Evaluate ${bid.bidderName}`} description={`${ksh(bid.total)} · delivery ${bid.deliveryDays || '?'} days · valid ${bid.validityDays} days · receipt ${bid.receipt}`}>
      <div className="space-y-4 text-sm">
        {tender.requirements && <div className="rounded-md bg-paper p-3 ring-1 ring-line"><p className="font-semibold">Requirements</p><p className="whitespace-pre-line text-muted">{tender.requirements}</p></div>}
        {bid.notes && <p><b>Their note:</b> {bid.notes}</p>}
        <Field label="Does it meet every requirement?"><Chips label="Responsive" value={f.responsive ? 'yes' : 'no'} onChange={(v) => setF({ ...f, responsive: v === 'yes' })} options={[['yes', 'Yes, responsive'], ['no', 'No']]} /></Field>
        {weighted && f.responsive && <Field label="Quality score (0-100)" hint={`Pass mark ${tender.technicalPassMark}.`}><input className="field num" inputMode="numeric" value={f.score} onChange={(e) => setF({ ...f, score: e.target.value })} /></Field>}
        <Field label={f.responsive ? 'Notes (optional)' : 'Which requirement it missed'}><textarea className="field min-h-20" value={f.note} onChange={(e) => setF({ ...f, note: e.target.value })} /></Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy} onClick={save}>Save evaluation</button></SheetFooter>
      </div>
    </Sheet>
  );
}

// ---------------------------------------------------------------------------------------------- public pages

type PublicTender = {
  company: { name: string; phone: string; email: string; county: string }; number: string; title: string; scope: string; requirements: string; method: string; evaluation: string;
  technicalWeight: number; technicalPassMark: number; closesAt: string; status: string; taking: boolean; lines: { id: string; description: string; specification: string; unit: string; quantity: number }[];
};

async function publicGet<T>(path: string): Promise<T> {
  const r = await fetch(`/api${path}`);
  const body = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(body.error ?? 'This page couldn’t load.');
  return body as T;
}

function PublicFrame({ children, company }: { children: React.ReactNode; company?: string }) {
  return (
    <div className="min-h-screen bg-paper px-4 py-8">
      <div className="mx-auto max-w-3xl">
        {company && <p className="mb-1 text-sm font-semibold text-muted">{company}</p>}
        {children}
        <p className="mt-8 text-center text-xs text-faint">Tenders run on Kabuor ERP</p>
      </div>
    </div>
  );
}

/** The page a supplier opens from the tender's link: what's wanted, and the bid form. No sign-in. */
export function TenderBid() {
  const token = location.pathname.split('/').pop() ?? '';
  const [data, setData] = useState<PublicTender | null>(null);
  const [error, setError] = useState('');
  const [prices, setPrices] = useState<Record<string, string>>({});
  const [f, setF] = useState({ bidderName: '', kraPin: '', contactName: '', phone: '', email: '', deliveryDays: '', validityDays: '90', notes: '' });
  const [state, setState] = useState<{ busy: boolean; error: string; receipt?: { receipt: string; total: number; at: string } }>({ busy: false, error: '' });
  useEffect(() => void publicGet<PublicTender>(`/bid/${token}`).then(setData, (e) => setError(e.message)), [token]);
  if (error) return <PublicFrame><div className="card p-6"><p className="font-semibold">This tender couldn’t be found</p><p className="mt-1 text-sm text-muted">{error}</p></div></PublicFrame>;
  if (!data) return <PublicFrame><Loading /></PublicFrame>;
  const total = data.lines.reduce((s, l) => s + toCents(prices[l.id] || '0') * l.quantity, 0);
  const submit = async () => {
    setState({ busy: true, error: '' });
    try {
      const r = await fetch(`/api/bid/${token}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({
        ...f, deliveryDays: Number(f.deliveryDays) || 0, validityDays: Number(f.validityDays) || 90, lines: data.lines.map((l) => ({ tenderLineId: l.id, unitPrice: toCents(prices[l.id] || '0') })),
      }) });
      const body = await r.json().catch(() => ({}));
      if (!r.ok) throw new Error(body.error ?? 'Your bid didn’t go through. Try again.');
      setState({ busy: false, error: '', receipt: body });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <PublicFrame company={data.company.name}>
      <h1 className="text-2xl font-semibold tracking-tight">{data.title}</h1>
      <p className="mt-1 text-sm text-muted">{data.number} · {data.method === 'Open' ? 'Open tender' : 'Request for quotations'} · bids close <b className="text-ink">{dateTime(data.closesAt)}</b></p>
      <div className="mt-5 space-y-4">
        {data.scope && <section className="card p-4 text-sm"><p className="mb-1 font-semibold">Scope and conditions</p><p className="whitespace-pre-line">{data.scope}</p></section>}
        {data.requirements && <section className="card p-4 text-sm"><p className="mb-1 font-semibold">You must have or send</p><p className="whitespace-pre-line">{data.requirements}</p><p className="mt-2 text-muted">Send the documents to {data.company.email || data.company.phone} before the closing time, quoting {data.number}.</p></section>}
        {data.evaluation === 'Weighted' && <p className="text-sm text-muted">Bids are judged on quality ({data.technicalWeight}%, pass mark {data.technicalPassMark}) and price ({100 - data.technicalWeight}%).</p>}
        {state.receipt ? (
          <section className="card space-y-2 p-5 text-sm">
            <p className="text-base font-semibold text-good">Your bid is in.</p>
            <p>Receipt <b className="num">{state.receipt.receipt}</b> · {ksh(state.receipt.total)} · {dateTime(state.receipt.at)}</p>
            <p className="text-muted">Keep the receipt. Bids stay sealed until the closing time; you’ll be told the outcome.</p>
          </section>
        ) : !data.taking ? (
          <section className="card p-5 text-sm"><p className="font-semibold">This tender is no longer taking bids.</p></section>
        ) : (
          <section className="card space-y-4 p-5">
            <p className="font-semibold">Your prices (each, VAT inclusive)</p>
            <div className="space-y-2">
              {data.lines.map((l) => (
                <div key={l.id} className="grid items-center gap-2 sm:grid-cols-[1fr_9rem]">
                  <div className="text-sm"><b>{l.description}</b> · {l.quantity} {l.unit}{l.specification && <span className="block text-xs text-muted">{l.specification}</span>}</div>
                  <input className="field num" inputMode="decimal" value={prices[l.id] ?? ''} onChange={(e) => setPrices({ ...prices, [l.id]: e.target.value })} placeholder="KSh each" aria-label={`Price for ${l.description}`} />
                </div>
              ))}
              <p className="text-right text-sm">Total <b className="num">{ksh(total)}</b></p>
            </div>
            <div className="grid gap-3 sm:grid-cols-2">
              <Field label="Company name"><input className="field" value={f.bidderName} onChange={(e) => setF({ ...f, bidderName: e.target.value })} /></Field>
              <Field label="KRA PIN"><input className="field" value={f.kraPin} onChange={(e) => setF({ ...f, kraPin: e.target.value.toUpperCase() })} placeholder="P051234567X" /></Field>
              <Field label="Contact person"><input className="field" value={f.contactName} onChange={(e) => setF({ ...f, contactName: e.target.value })} /></Field>
              <Field label="Phone"><input className="field" type="tel" value={f.phone} onChange={(e) => setF({ ...f, phone: e.target.value })} /></Field>
              <Field label="Email"><input className="field" type="email" value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} /></Field>
              <Field label="Delivery (days after the order)"><input className="field num" inputMode="numeric" value={f.deliveryDays} onChange={(e) => setF({ ...f, deliveryDays: e.target.value })} /></Field>
            </div>
            <Field label="Notes (optional)"><textarea className="field min-h-16" value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} placeholder="Brands offered, warranty, anything the buyer should know" /></Field>
            {state.error && <ErrorNote>{state.error}</ErrorNote>}
            <button className="btn btn-primary w-full py-3" disabled={state.busy || !f.bidderName.trim() || !f.phone.trim() || data.lines.some((l) => !toCents(prices[l.id] || '0'))} onClick={submit}>Send my bid of {ksh(total)}</button>
            <p className="text-xs text-muted">One bid per company. It can’t be seen by the buyer until the closing time.</p>
          </section>
        )}
      </div>
    </PublicFrame>
  );
}

/** A business's open tenders, for anyone to see. */
export function TenderBoard() {
  const slug = location.pathname.split('/').pop() ?? '';
  const [data, setData] = useState<{ company: { name: string; county: string }; tenders: { number: string; title: string; closesAt: string; token: string }[] } | null>(null);
  const [error, setError] = useState('');
  useEffect(() => void publicGet<NonNullable<typeof data>>(`/tender-board/${slug}`).then(setData, (e) => setError(e.message)), [slug]);
  if (error) return <PublicFrame><div className="card p-6 text-sm">{error}</div></PublicFrame>;
  if (!data) return <PublicFrame><Loading /></PublicFrame>;
  return (
    <PublicFrame>
      <h1 className="text-2xl font-semibold tracking-tight">{data.company.name}: open tenders</h1>
      <div className="mt-5">
        {data.tenders.length === 0 ? <div className="card p-6 text-sm text-muted">No tenders are open right now.</div> : (
          <ul className="card divide-y divide-line">
            {data.tenders.map((t) => (
              <Fragment key={t.token}>
                <li><a className="flex items-center gap-3 px-4 py-3 hover:bg-paper" href={`/bid/${t.token}`}><code className="text-xs text-muted">{t.number}</code><b className="flex-1 text-sm">{t.title}</b><span className="text-sm text-muted">closes {dateTime(t.closesAt)}</span></a></li>
              </Fragment>
            ))}
          </ul>
        )}
      </div>
    </PublicFrame>
  );
}
