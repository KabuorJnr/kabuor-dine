// M-Pesa prompts from an invoice. The person at the counter is deciding "did they pay?", so after sending, the
// sheet waits on Safaricom's answer and says it plainly (paid with receipt, cancelled, or no answer yet) instead
// of leaving them to refresh. Setup lives in Settings and never shows the saved secrets again.
import { useEffect, useState } from 'react';
import { Check, Loader2, Smartphone } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { ksh, toCents } from '../lib/format';
import { ErrorNote, Field, Loading, Sheet, SheetFooter, PageError } from './ui';

type Req = { id: string; status: 'Pending' | 'Paid' | 'Failed' | 'Cancelled'; receipt: string; note: string; amount: number; phone: string };

export function RequestPayment({ open, onClose, invoiceId, number, balance, phone, onPaid }: { open: boolean; onClose: () => void; invoiceId: string; number: string; balance: number; phone: string; onPaid: () => void }) {
  const [to, setTo] = useState(phone);
  const [amount, setAmount] = useState(String(balance / 100));
  const [sent, setSent] = useState<Req | null>(null);
  const [state, setState] = useState({ busy: false, error: '' });
  const [waited, setWaited] = useState(0);

  useEffect(() => {
    if (open) (setTo(phone), setAmount(String(Math.floor(balance / 100))), setSent(null), setState({ busy: false, error: '' }), setWaited(0));
  }, [open, phone, balance]);

  // Wait for Safaricom's answer: every 3 seconds for up to two minutes.
  useEffect(() => {
    if (!sent || sent.status !== 'Pending' || waited >= 40) return;
    const t = setTimeout(async () => {
      const list = await api<Req[]>(`/sales/${invoiceId}/mpesa-requests`).catch(() => []);
      const now = list.find((r) => r.id === sent.id);
      if (now) setSent(now);
      if (now?.status === 'Paid') onPaid();
      setWaited((w) => w + 1);
    }, 3000);
    return () => clearTimeout(t);
  }, [sent, waited, invoiceId, onPaid]);

  const send = async () => {
    setState({ busy: true, error: '' });
    try {
      setSent(await api<Req>(`/sales/${invoiceId}/mpesa-request`, { body: { phone: to, amount: toCents(amount) } }));
      setWaited(0);
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };

  return (
    <Sheet open={open} onClose={onClose} title={`Ask for M-Pesa · ${number}`}>
      {!sent ? (
        <div className="space-y-4">
          <p className="text-sm text-muted">The customer gets a prompt on their phone for this invoice and only has to enter their M-Pesa PIN. It’s recorded against {number} as soon as Safaricom confirms.</p>
          <Field label="Customer’s M-Pesa number">
            <input className="field" inputMode="tel" value={to} onChange={(e) => setTo(e.target.value)} placeholder="0712 345 678" />
          </Field>
          <Field label="Amount (KSh)" hint={`Balance ${ksh(balance)}. Ask for less to take part of it.`}>
            <input className="field num" inputMode="numeric" value={amount} onChange={(e) => setAmount(e.target.value.replace(/[^\d]/g, ''))} />
          </Field>
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
          <SheetFooter>
          <button className="btn btn-mpesa" disabled={state.busy || !to.trim() || !Number(amount)} onClick={send}>
            {state.busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Smartphone className="h-4 w-4" />} Send prompt for {ksh(toCents(amount))}
          </button>
          </SheetFooter>
        </div>
      ) : (
        <div className="space-y-4 text-center">
          {sent.status === 'Pending' && (
            <>
              <Loader2 className="mx-auto h-8 w-8 animate-spin text-mpesa" />
              <p className="font-semibold">Waiting for the customer to enter their PIN…</p>
              <p className="text-sm text-muted">{ksh(sent.amount)} requested from {sent.phone}. {waited >= 40 ? 'No answer from Safaricom yet. If they paid, it will still be recorded when the confirmation arrives.' : 'This usually takes under a minute.'}</p>
            </>
          )}
          {sent.status === 'Paid' && (
            <>
              <Check className="mx-auto h-10 w-10 text-good" />
              <p className="font-semibold">Paid {ksh(sent.amount)}</p>
              <p className="text-sm text-muted">M-Pesa receipt <b className="text-ink">{sent.receipt}</b>, recorded against {number}.</p>
              <SheetFooter cancel={false}>
              <button className="btn btn-primary" onClick={onClose}>Done</button>
              </SheetFooter>
            </>
          )}
          {(sent.status === 'Failed' || sent.status === 'Cancelled') && (
            <>
              <p className="font-semibold text-bad">{sent.status === 'Cancelled' ? 'The customer cancelled the prompt.' : 'Not paid.'}</p>
              {sent.note && <p className="text-sm text-muted">Safaricom said: {sent.note}</p>}
              <SheetFooter cancel="Close">
              <button className="btn btn-mpesa" onClick={() => setSent(null)}>Try again</button>
              </SheetFooter>
            </>
          )}
        </div>
      )}
    </Sheet>
  );
}

/** Settings: the business's own Daraja app, for prompts. */
export function PromptSetup() {
  const { data, error, reload } = useApi<any>('/settings/mpesa-prompt');
  const [f, setF] = useState<any>(null);
  const [state, setState] = useState({ busy: false, error: '', saved: false });
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const v = f ?? { environment: data.environment || 'sandbox', kind: data.kind, shortcode: data.shortcode, till: data.till, consumerKey: '', consumerSecret: '', passkey: '' };
  const set = (k: string) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setF({ ...v, [k]: e.target.value });
  const save = async () => {
    setState({ busy: true, error: '', saved: false });
    try {
      await api('/settings/mpesa-prompt', { method: 'PUT', body: v });
      setF(null);
      reload();
      setState({ busy: false, error: '', saved: true });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message, saved: false });
    }
  };
  const keep = data.hasKeys ? 'Saved. Leave blank to keep it.' : undefined;
  return (
    <section className="card space-y-4 p-4 sm:p-5">
      <div>
        <h2 className="font-semibold">M-Pesa prompts from invoices</h2>
        <p className="text-sm text-muted">{data.ready ? <>Ready: invoices show <b>Ask for M-Pesa</b>{data.environment === 'sandbox' && ' (sandbox: test money only)'}.</> : 'Lets you send a payment prompt to a customer’s phone from any invoice. Needs your own Daraja app (developer.safaricom.co.ke → My Apps, with “Lipa Na M-Pesa Online”) and the passkey Safaricom emails when it goes live.'}</p>
      </div>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Environment">
          <select className="field" value={v.environment} onChange={set('environment')}>
            <option value="sandbox">Sandbox (testing)</option>
            <option value="production">Live</option>
          </select>
        </Field>
        <Field label="Customers pay to a">
          <select className="field" value={v.kind} onChange={set('kind')}>
            <option value="paybill">Paybill</option>
            <option value="till">Till (Buy Goods)</option>
          </select>
        </Field>
        <Field label={v.kind === 'till' ? 'Store number' : 'Paybill number'} hint={v.kind === 'till' ? 'The head office number Safaricom gave with the till.' : undefined}>
          <input className="field num" inputMode="numeric" value={v.shortcode} onChange={set('shortcode')} placeholder={v.environment === 'sandbox' ? '174379' : ''} />
        </Field>
        {v.kind === 'till' && (
          <Field label="Till number">
            <input className="field num" inputMode="numeric" value={v.till} onChange={set('till')} />
          </Field>
        )}
        <Field label="Consumer key" hint={keep}><input className="field" autoComplete="off" value={v.consumerKey} onChange={set('consumerKey')} /></Field>
        <Field label="Consumer secret" hint={keep}><input className="field" type="password" autoComplete="new-password" value={v.consumerSecret} onChange={set('consumerSecret')} /></Field>
        <Field label="Passkey" hint={keep} className="sm:col-span-2"><input className="field" type="password" autoComplete="new-password" value={v.passkey} onChange={set('passkey')} /></Field>
      </div>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <button className="btn btn-primary" disabled={state.busy || !f} onClick={save}>
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} {state.saved && !f ? 'Saved' : 'Save'}
      </button>
    </section>
  );
}
