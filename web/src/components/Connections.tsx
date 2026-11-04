// Settings → Connections: letting an online shop or another system talk to Kabuor. The owner is deciding
// "what can get into my books, and is it working?", so each key says what it's for and when it was last
// used, each webhook says whether deliveries are getting through, and a new key is shown once with a copy button.
import { useState } from 'react';
import { Check, Copy, KeyRound, Loader2, Plus, Trash2, Webhook } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { dateTime } from '../lib/format';
import { Empty, ErrorNote, Field, Loading, Status, PageError, confirmDialog } from './ui';

function CopyBox({ text }: { text: string }) {
  const [done, setDone] = useState(false);
  return (
    <div className="flex items-center gap-2 rounded-md bg-paper p-2 ring-1 ring-line">
      <code className="min-w-0 flex-1 break-all text-xs">{text}</code>
      <button className="btn btn-ghost shrink-0 px-2" aria-label="Copy" onClick={async () => { try { await navigator.clipboard.writeText(text); setDone(true); setTimeout(() => setDone(false), 1500); } catch {} }}>
        {done ? <Check className="h-4 w-4 text-good" /> : <Copy className="h-4 w-4" />}
      </button>
    </div>
  );
}

/** The business's shop and customer portal on the internet. */
function PortalCard() {
  const { data, reload } = useApi<any>('/settings/portal');
  const [slug, setSlug] = useState<string | null>(null);
  const [state, setState] = useState({ busy: false, error: '' });
  if (!data) return null;
  const save = async (enabled: boolean) => {
    setState({ busy: true, error: '' });
    try {
      await api('/settings/portal', { method: 'PUT', body: { enabled, slug: slug ?? data.slug } });
      setSlug(null);
      reload();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <section className="card space-y-3 p-4 sm:p-5">
      <h2 className="font-semibold">Online shop and customer portal</h2>
      <p className="text-sm text-muted">Customers browse your products, ask questions, and, after signing in with their phone, see what they owe, pay by M-Pesa and order again. Everything lands in your inquiries, quotes and books. Sign-in codes go by SMS, so set up SMS for this to work for customers.</p>
      {data.portalEnabled && data.url && <div className="flex items-center gap-2"><a className="font-semibold text-brand underline" href={data.url} target="_blank" rel="noreferrer">{data.url}</a><CopyBox text={data.url} /></div>}
      <div className="flex flex-wrap items-end gap-2">
        <Field label="Address"><input className="field w-56" value={slug ?? data.slug ?? ''} onChange={(e) => setSlug(e.target.value)} placeholder="your-business" /></Field>
        <button className="btn btn-primary" disabled={state.busy} onClick={() => save(true)}>{state.busy && <Loader2 className="h-4 w-4 animate-spin" />} {data.portalEnabled ? 'Save' : 'Open the shop'}</button>
        {data.portalEnabled && <button className="btn btn-ghost" onClick={() => save(false)}>Close it</button>}
      </div>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
    </section>
  );
}

export default function Connections() {
  const keys = useApi<any[]>('/settings/api-keys');
  const hooks = useApi<any[]>('/settings/webhooks');
  const [name, setName] = useState('');
  const [fresh, setFresh] = useState<{ name: string; key: string } | null>(null);
  const [url, setUrl] = useState('');
  const [newHook, setNewHook] = useState<{ url: string; secret: string } | null>(null);
  const [state, setState] = useState({ busy: '', error: '' });
  if (keys.error) return <PageError>{keys.error}</PageError>;
  if (!keys.data || !hooks.data) return <Loading />;

  const run = async (busy: string, fn: () => Promise<void>) => {
    setState({ busy, error: '' });
    try {
      await fn();
      setState({ busy: '', error: '' });
    } catch (e) {
      setState({ busy: '', error: (e as Error).message });
    }
  };
  const origin = window.location.origin;

  return (
    <div className="grid gap-5 lg:grid-cols-[1fr_22rem]">
      <div className="space-y-5">
        <PortalCard />
        <section className="card space-y-4 p-4 sm:p-5">
          <div className="flex items-center gap-2"><KeyRound className="h-5 w-5 text-muted" /><h2 className="font-semibold">API keys</h2></div>
          <p className="text-sm text-muted">A key lets one system (your online shop, an app) read your products and send in orders. Make one key per system, so you can switch one off without touching the others.</p>
          {fresh && (
            <div className="space-y-2 rounded-md bg-good-soft p-3">
              <p className="text-sm font-semibold text-good">Key for {fresh.name}. Copy it now: it won’t be shown again.</p>
              <CopyBox text={fresh.key} />
            </div>
          )}
          {keys.data.length ? (
            <ul className="divide-y divide-line rounded-md ring-1 ring-line">
              {keys.data.map((k) => (
                <li key={k.id} className="flex items-center gap-3 px-3 py-2.5">
                  <span className="min-w-0 flex-1">
                    <b className="block truncate text-sm">{k.name}</b>
                    <span className="text-xs text-muted">{k.prefix}… · {k.lastUsedAt ? `last used ${dateTime(k.lastUsedAt)}` : 'not used yet'}</span>
                  </span>
                  <button className="btn btn-ghost text-bad" disabled={state.busy === k.id} onClick={async () => (await confirmDialog({ title: `Switch off the key for ${k.name}?`, body: 'Anything using it stops working at once.', confirm: 'Switch off', danger: true })) && run(k.id, async () => { await api(`/settings/api-keys/${k.id}`, { method: 'DELETE' }); keys.reload(); })}>
                    <Trash2 className="h-4 w-4" /> Switch off
                  </button>
                </li>
              ))}
            </ul>
          ) : (
            <Empty title="No keys yet">Make one when you connect your online shop.</Empty>
          )}
          <div className="flex gap-2">
            <input className="field flex-1" value={name} onChange={(e) => setName(e.target.value)} placeholder="What will use it? e.g. Online shop" />
            <button className="btn btn-primary" disabled={state.busy === 'key' || name.trim().length < 2} onClick={() => run('key', async () => { const r = await api<any>('/settings/api-keys', { body: { name } }); setFresh({ name: r.name, key: r.key }); setName(''); keys.reload(); })}>
              {state.busy === 'key' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Plus className="h-4 w-4" />} Make key
            </button>
          </div>
        </section>

        <section className="card space-y-4 p-4 sm:p-5">
          <div className="flex items-center gap-2"><Webhook className="h-5 w-5 text-muted" /><h2 className="font-semibold">Tell another system when things happen</h2></div>
          <p className="text-sm text-muted">We send a signed message to your address when a payment arrives or an invoice is paid, so your shop can mark the order paid or start delivery. Failed sends are retried for about a day.</p>
          {newHook && (
            <div className="space-y-2 rounded-md bg-good-soft p-3">
              <p className="text-sm font-semibold text-good">Signing secret for {newHook.url}. Use it to check each message really came from Kabuor.</p>
              <CopyBox text={newHook.secret} />
            </div>
          )}
          {hooks.data.length > 0 && (
            <ul className="divide-y divide-line rounded-md ring-1 ring-line">
              {hooks.data.map((h) => (
                <li key={h.id} className="flex items-center gap-3 px-3 py-2.5">
                  <span className="min-w-0 flex-1">
                    <b className="block truncate text-sm">{h.url}</b>
                    <span className="text-xs text-muted">{h.events === '*' ? 'All events' : h.events.replace(/,/g, ', ')} · {h.delivered} delivered</span>
                    {h.failing > 0 && <span className="block text-xs text-bad">{h.failing} not getting through{h.lastError ? `: ${h.lastError}` : ''}</span>}
                  </span>
                  {h.failing > 0 ? <Status tone="bad">Failing</Status> : <Status tone="good">OK</Status>}
                  <button className="btn btn-ghost px-2 text-bad" aria-label="Remove" onClick={async () => (await confirmDialog({ title: 'Stop sending events here?', body: h.url, confirm: 'Stop sending', danger: true })) && run(h.id, async () => { await api(`/settings/webhooks/${h.id}`, { method: 'DELETE' }); hooks.reload(); })}>
                    <Trash2 className="h-4 w-4" />
                  </button>
                </li>
              ))}
            </ul>
          )}
          <div className="flex gap-2">
            <input className="field flex-1" type="url" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://yourshop.co.ke/kabuor-events" />
            <button className="btn btn-primary" disabled={state.busy === 'hook' || !url.startsWith('https://')} onClick={() => run('hook', async () => { const r = await api<any>('/settings/webhooks', { body: { url } }); setNewHook({ url: r.url, secret: r.secret }); setUrl(''); hooks.reload(); })}>
              {state.busy === 'hook' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Plus className="h-4 w-4" />} Add
            </button>
          </div>
        </section>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
      </div>

      <aside className="card h-fit space-y-3 p-4 text-sm sm:p-5">
        <h2 className="font-semibold">For your developer</h2>
        <Field label="Base address"><CopyBox text={`${origin}/api/v1`} /></Field>
        <ul className="space-y-1.5 text-muted">
          <li><code className="text-ink">GET /items</code> products, prices and stock</li>
          <li><code className="text-ink">POST /orders</code> an order: <code>externalRef</code>, customer, <code>lines</code> (by <code>sku</code> or <code>itemId</code>), and <code>mpesaReceipt</code> if already paid. The same <code>externalRef</code> is only recorded once.</li>
          <li><code className="text-ink">GET /orders/{'{externalRef}'}</code> invoice number, status and balance</li>
        </ul>
        <p className="text-muted">Send the key in the <code className="text-ink">X-Api-Key</code> header. Webhooks carry <code className="text-ink">X-Kabuor-Signature: sha256=…</code>, the HMAC-SHA256 of the body with your signing secret.</p>
      </aside>
    </div>
  );
}
