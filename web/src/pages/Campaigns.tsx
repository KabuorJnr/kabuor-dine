// Marketing campaigns. Deciding "who gets this, and was it worth it?": the editor shows how many people the
// chosen group reaches (and who's left out and why) plus the real message the first few will get; the
// campaign page shows delivery and what those customers bought in the 14 days after.
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { ArrowLeft, Check, Loader2, MessageCircle, MessageSquareText, Plus, Send } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { cn, dateTime, ksh, msisdn, prettyPhone } from '../lib/format';
import { Empty, ErrorNote, Field, Loading, PageHeader, Stat, Status, PageError, confirmDialog } from '../components/ui';

const AUDIENCES: { id: string; label: string; text: (v: number) => string; value?: 'days' | 'top' }[] = [
  { id: 'all', label: 'All customers', text: () => 'Everyone you have a phone number for.' },
  { id: 'recent', label: 'Bought recently', text: (v) => `Bought in the last ${v} days.`, value: 'days' },
  { id: 'lapsed', label: 'Haven’t bought lately', text: (v) => `Bought before, but not in the last ${v} days. Win them back.`, value: 'days' },
  { id: 'top', label: 'Best customers', text: (v) => `Your top ${v} by spend this year.`, value: 'top' },
  { id: 'owing', label: 'Owe you money', text: () => 'Customers with unpaid invoices.' },
  { id: 'never-bought', label: 'Never bought', text: () => 'Customers on file who haven’t bought yet.' },
  { id: 'leads', label: 'Open leads', text: () => 'People in your pipeline who aren’t customers yet.' },
];
const label = (id: string) => AUDIENCES.find((a) => a.id === id)?.label ?? id;

export function CampaignList() {
  const { data, error } = useApi<any[]>('/marketing/campaigns');
  const sms = useApi<{ live: boolean }>('/marketing/sms-status');
  return (
    <>
      <PageHeader
        title="Campaigns"
        subtitle="Offers and news to the right customers, by SMS or WhatsApp."
        actions={
          <Link to="/campaigns/new" className="btn btn-primary">
            <Plus className="h-4 w-4" /> New campaign
          </Link>
        }
      />
      {sms.data && !sms.data.live && <p className="mb-4 rounded-md bg-warn-soft px-4 py-3 text-sm text-warn">SMS is in test mode: campaigns are prepared but no SMS leaves until your Africa’s Talking keys are added. WhatsApp campaigns work now.</p>}
      {error && <ErrorNote>{error}</ErrorNote>}
      {!data ? (
        <Loading />
      ) : data.length === 0 ? (
        <Empty title="No campaigns yet" action={<Link to="/campaigns/new" className="btn btn-primary">Start one</Link>}>
          Try “Haven’t bought lately”: a short offer to customers who used to buy brings back sales you were already losing.
        </Empty>
      ) : (
        <ul className="card divide-y divide-line">
          {data.map((c) => (
            <li key={c.id}>
              <Link to={`/campaigns/${c.id}`} className="flex items-center gap-3 px-4 py-3 hover:bg-paper">
                <span className={cn('grid h-10 w-10 shrink-0 place-items-center rounded-full', c.channel === 'Sms' ? 'bg-brand-soft text-brand' : 'bg-good-soft text-good')}>
                  {c.channel === 'Sms' ? <MessageSquareText className="h-5 w-5" /> : <MessageCircle className="h-5 w-5" />}
                </span>
                <span className="min-w-0 flex-1">
                  <b className="block truncate">{c.name}</b>
                  <span className="text-sm text-muted">
                    {c.channel === 'Sms' ? 'SMS' : 'WhatsApp'} · {label(c.audience)}
                    {c.sentAt && ` · ${dateTime(c.sentAt)}`}
                  </span>
                </span>
                {c.status === 'Draft' ? <Status tone="plain">Draft</Status> : <Status tone="good">{c.sent} of {c.recipients} sent{c.testMode ? ' (test)' : ''}</Status>}
              </Link>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

export function CampaignEditor() {
  const navigate = useNavigate();
  const [f, setF] = useState({ name: '', channel: 'Sms', audience: 'lapsed', days: 60, top: 20, message: 'Hi {first_name}, we miss you! Get 10% off your next order this week. ' });
  const value = AUDIENCES.find((a) => a.id === f.audience)?.value === 'top' ? f.top : f.days;
  const counts = useApi<any>(`/marketing/audiences?days=${f.days}&top=${f.top}`);
  const [preview, setPreview] = useState<any>(null);
  const [state, setState] = useState({ busy: '', error: '' });
  useEffect(() => {
    const t = setTimeout(() => api(`/marketing/preview?audience=${f.audience}&value=${value}&message=${encodeURIComponent(f.message)}`).then(setPreview).catch(() => {}), 300);
    return () => clearTimeout(t);
  }, [f.audience, value, f.message]);
  const chars = f.message.length;
  const save = async (send: boolean) => {
    setState({ busy: send ? 'send' : 'save', error: '' });
    try {
      const c = await api('/marketing/campaigns', { body: { name: f.name, channel: f.channel, audience: f.audience, audienceValue: value, message: f.message } });
      if (send) await api(`/marketing/campaigns/${c.id}/send`, { body: {} });
      navigate(`/campaigns/${c.id}`, { replace: true });
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };
  return (
    <>
      <Link to="/campaigns" className="mb-3 inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink">
        <ArrowLeft className="h-4 w-4" /> Campaigns
      </Link>
      <PageHeader title="New campaign" />
      <div className="grid gap-5 lg:grid-cols-[1fr_20rem]">
        <div className="space-y-5">
          <section className="card space-y-4 p-4 sm:p-5">
            <Field label="Name (for you)">
              <input className="field" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. October come-back offer" />
            </Field>
            <div>
              <p className="mb-1.5 text-sm font-semibold">Send by</p>
              <div className="grid grid-cols-2 gap-2 sm:max-w-sm">
                {[['Sms', 'SMS'], ['WhatsApp', 'WhatsApp']].map(([v, l]) => (
                  <button key={v} type="button" aria-pressed={f.channel === v} onClick={() => setF({ ...f, channel: v })} className={f.channel === v ? 'btn bg-brand-soft text-brand ring-1 ring-brand/40' : 'btn btn-ghost'}>
                    {l}
                  </button>
                ))}
              </div>
              <p className="mt-1.5 text-xs text-muted">{f.channel === 'Sms' ? 'Goes to everyone at once. An opt-out line is added to every SMS.' : 'You open each chat with the message ready and tap send. Best for small, personal groups.'}</p>
            </div>
          </section>
          <section className="card p-4 sm:p-5">
            <h2 className="text-sm font-semibold">Who gets it</h2>
            <ul className="mt-3 grid gap-2 sm:grid-cols-2">
              {AUDIENCES.map((a) => {
                const n = counts.data?.[a.id]?.count;
                return (
                  <li key={a.id}>
                    <button type="button" aria-pressed={f.audience === a.id} onClick={() => setF({ ...f, audience: a.id })} className={cn('flex h-full w-full items-start justify-between gap-3 rounded-md p-3 text-left ring-1', f.audience === a.id ? 'bg-brand-soft ring-2 ring-brand' : 'bg-white ring-line hover:ring-faint')}>
                      <span>
                        <b className="block text-sm">{a.label}</b>
                        <span className="text-xs text-muted">{a.text(a.value === 'top' ? f.top : f.days)}</span>
                      </span>
                      <span className="num shrink-0 text-sm font-bold">{n ?? '…'}</span>
                    </button>
                  </li>
                );
              })}
            </ul>
            {AUDIENCES.find((a) => a.id === f.audience)?.value === 'days' && (
              <div className="mt-3 flex flex-wrap items-center gap-2 text-sm">
                <span className="text-muted">Days:</span>
                {[30, 60, 90, 180].map((d) => (
                  <button key={d} type="button" aria-pressed={f.days === d} onClick={() => setF({ ...f, days: d })} className={f.days === d ? 'rounded-full bg-ink px-3 py-1 font-semibold text-white' : 'rounded-full px-3 py-1 ring-1 ring-line'}>
                    {d}
                  </button>
                ))}
              </div>
            )}
            {f.audience === 'top' && (
              <div className="mt-3 flex flex-wrap items-center gap-2 text-sm">
                <span className="text-muted">Top:</span>
                {[10, 20, 50, 100].map((d) => (
                  <button key={d} type="button" aria-pressed={f.top === d} onClick={() => setF({ ...f, top: d })} className={f.top === d ? 'rounded-full bg-ink px-3 py-1 font-semibold text-white' : 'rounded-full px-3 py-1 ring-1 ring-line'}>
                    {d}
                  </button>
                ))}
              </div>
            )}
          </section>
          <section className="card p-4 sm:p-5">
            <Field label="Message" hint={`Use {first_name}, {name} or {balance} and each person gets their own. ${f.channel === 'Sms' ? `${chars} characters · ${Math.max(1, Math.ceil(chars / 153))} SMS each (plus the opt-out line)` : ''}`}>
              <textarea className="field" rows={4} value={f.message} onChange={(e) => setF({ ...f, message: e.target.value })} />
            </Field>
          </section>
        </div>
        <aside className="h-fit space-y-4 lg:sticky lg:top-8">
          <div className="card p-4">
            <p className="text-sm text-muted">This will reach</p>
            <p className="num font-display text-3xl font-semibold">{preview?.count ?? '…'} {preview?.count === 1 ? 'person' : 'people'}</p>
            {preview?.count === 0 && <p className="mt-1 text-sm text-warn">Nobody fits this group right now. Pick another group, or change the days.</p>}
            {preview && (preview.noPhone > 0 || preview.optedOut > 0) && (
              <p className="mt-1 text-xs text-muted">
                Left out: {[preview.noPhone > 0 && `${preview.noPhone} without a phone`, preview.optedOut > 0 && `${preview.optedOut} who opted out`].filter(Boolean).join(', ')}
              </p>
            )}
            {preview?.sample?.length > 0 && (
              <div className="mt-3 space-y-2">
                {preview.sample.map((p: any) => (
                  <div key={p.phone} className="rounded-md bg-paper p-2.5 text-sm">
                    <p className="text-xs font-semibold text-muted">{p.name}</p>
                    <p className="mt-0.5 whitespace-pre-line">{p.message}</p>
                  </div>
                ))}
              </div>
            )}
          </div>
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
          <button className="btn btn-primary w-full py-3" disabled={Boolean(state.busy) || !preview?.count} onClick={async () => (await confirmDialog({ title: `${f.channel === 'Sms' ? 'Send this SMS' : 'Prepare WhatsApp messages'} to ${preview?.count} people?`, confirm: f.channel === 'Sms' ? 'Send SMS' : 'Prepare messages' })) && save(true)}>
            {state.busy === 'send' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />} {f.channel === 'Sms' ? `Send to ${preview?.count ?? 0}` : `Prepare ${preview?.count ?? 0} messages`}
          </button>
          <button className="btn btn-ghost w-full" disabled={Boolean(state.busy)} onClick={() => save(false)}>
            Save as draft
          </button>
        </aside>
      </div>
    </>
  );
}

export function CampaignView() {
  const { id } = useParams();
  const { data, error, reload } = useApi<any>(`/marketing/campaigns/${id}`);
  const [state, setState] = useState({ busy: false, error: '' });
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const c = data.campaign;
  const r = data.results;
  const send = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(`/marketing/campaigns/${c.id}/send`, { body: {} });
      reload();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const openChat = async (rec: any) => {
    const wa = msisdn(rec.phone);
    if (wa) window.open(`https://wa.me/${wa}?text=${encodeURIComponent(rec.message)}`, '_blank', 'noopener');
    await api(`/marketing/campaigns/${c.id}/recipients/${rec.id}/sent`, { body: {} });
    reload();
  };
  return (
    <>
      <Link to="/campaigns" className="mb-3 inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink">
        <ArrowLeft className="h-4 w-4" /> Campaigns
      </Link>
      <PageHeader
        title={c.name}
        subtitle={`${c.channel === 'Sms' ? 'SMS' : 'WhatsApp'} · ${label(c.audience)}${c.sentAt ? ` · sent ${dateTime(c.sentAt)} by ${c.sentBy}` : ''}`}
        actions={
          c.status === 'Draft' && (
            <button className="btn btn-primary" onClick={send} disabled={state.busy}>
              {state.busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />} {c.channel === 'Sms' ? 'Send now' : 'Prepare messages'}
            </button>
          )
        }
      />
      {c.testMode && <p className="mb-4 rounded-md bg-warn-soft px-4 py-3 text-sm text-warn">Sent in test mode: no SMS actually left. Add Africa’s Talking keys to send for real.</p>}
      {state.error && <div className="mb-4"><ErrorNote>{state.error}</ErrorNote></div>}
      {c.status === 'Sent' && (
        <div className="mb-5 grid gap-3 sm:grid-cols-3">
          <Stat label="Delivered" value={`${r.sent}`}>
            {r.failed > 0 ? <span className="text-bad">{r.failed} failed</span> : r.ready > 0 ? `${r.ready} still to send on WhatsApp` : `of ${c.recipients.length}`}
          </Stat>
          <Stat label={`Bought within ${r.windowDays} days`} value={`${r.buyers}`} tone={r.buyers > 0 ? 'good' : undefined}>
            {r.sent ? `${Math.round((100 * r.buyers) / Math.max(1, r.sent))}% of those reached` : '—'}
          </Stat>
          <Stat label="Their sales after" value={ksh(r.sales)}>
            invoices to these customers
          </Stat>
        </div>
      )}
      <div className="grid gap-5 lg:grid-cols-[1fr_20rem]">
        <section className="card overflow-hidden">
          <h2 className="border-b border-line px-4 py-3 text-sm font-semibold text-muted">{c.status === 'Draft' ? 'Not sent yet' : `${c.recipients.length} recipients`}</h2>
          {c.recipients.length > 0 && (
            <ul className="max-h-[32rem] divide-y divide-line overflow-y-auto">
              {c.recipients.map((x: any) => (
                <li key={x.id} className="flex items-center gap-3 px-4 py-2.5">
                  <span className="min-w-0 flex-1">
                    <b className="block truncate text-sm">{x.name}</b>
                    <span className="text-xs text-muted">{prettyPhone(x.phone)}{x.error && ` · ${x.error}`}</span>
                  </span>
                  {x.status === 'Ready' ? (
                    <button className="btn btn-mpesa py-1.5 text-sm" onClick={() => openChat(x)}>
                      <MessageCircle className="h-4 w-4" /> Open chat
                    </button>
                  ) : x.status === 'Sent' ? (
                    <Status tone="good">
                      <Check className="h-3 w-3" /> Sent
                    </Status>
                  ) : (
                    <Status tone="bad">Failed</Status>
                  )}
                </li>
              ))}
            </ul>
          )}
        </section>
        <aside className="card h-fit p-4">
          <p className="text-sm font-semibold text-muted">Message</p>
          <p className="mt-2 text-sm whitespace-pre-line">{c.message}</p>
        </aside>
      </div>
    </>
  );
}
