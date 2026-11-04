// Kabuor Verified: the owner's proof page. The owner is deciding "Is my business ready to borrow, and what's holding
// my figures back?", so the page leads with the one number a lender asks for (money Safaricom confirmed, a month),
// then KRA signing, then what would make the proof stronger. Facts only, never a score: lenders decide.
// The owner alone decides who sees their figures, what they see and for how long.
import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import QRCode from 'qrcode';
import { BadgeCheck, Check, CircleAlert, Copy, ExternalLink, Loader2, Share2, ShieldCheck } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { ksh } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, Sheet, Stat, confirmDialog, noticeDialog } from '../components/ui';
import { ChartCard, Columns, compact, Legend, monthName, SERIES, Table } from '../components/DashboardCharts';

type Month = { month: string; partial: boolean; confirmed: number; allMoneyIn: number; confirmedShare: number; payers: number; topFiveShare: number };
type Source = { source: string; amount: number; count: number; confirmedAmount: number };
type Fix = { code: string; text: string; link: string | null; action: string | null };
type Summary = {
  months: number; from: string; to: string;
  averageMonthlyConfirmed: number; payers: number; topFiveShare: number; monthToDateConfirmed: number;
  ownNumbers: number; notFromSafaricom: number;
  monthList: Month[]; bySource: Source[];
  etims: { required: boolean; issued: number; signed: number; testSigned: number; failedOrPending: number; signedShare: number };
  fixes: Fix[]; checkedAt: string;
};

type ShareItem = {
  id: string;
  lenderName: string;
  lenderContact: string;
  months: number;
  createdAt: string;
  expiresAt: string;
  revokedAt: string | null;
  revokedBy: string | null;
  createdBy: string;
  status: 'active' | 'expired' | 'revoked';
  viewsCount: number;
  lastViewedAt: string | null;
};

// Everything that isn't Safaricom's confirmation is shown in a neutral grey: it may be right, but it proves nothing.
const OTHER = '#b9bccf';
const SOURCE_WORDS: Record<string, string> = {
  MpesaConfirmation: 'M-Pesa confirmations', MpesaPrompt: 'M-Pesa payment prompts', Till: 'Recorded at the till',
  StatementImport: 'Statement uploads', ConnectedSystem: 'Connected apps', Manual: 'Entered by hand', Unknown: 'Source not yet known',
};
const pct = (share: number) => `${Math.round(share * 100)}%`;
const num = (n: number) => n.toLocaleString('en-KE');

export default function Verified() {
  const session = useSession();
  const canShare = can(session, 'verified.share');
  const [months, setMonths] = useState<'3' | '6' | '12'>('6');
  const { data, error, loading } = useApi<Summary>(`/verified/summary?months=${months}`);
  const { data: shares, reload: reloadShares } = useApi<ShareItem[]>('/verified/shares');
  const [shareOpen, setShareOpen] = useState(false);

  return (
    <>
      <PageHeader print
        title="Verified"
        subtitle="What Safaricom and KRA confirm about your business. Lenders trust these figures because nobody in your business typed them."
        actions={
          canShare ? (
            <button type="button" className="btn btn-primary" onClick={() => setShareOpen(true)}>
              <Share2 className="h-4 w-4 mr-1.5" /> Share with a lender
            </button>
          ) : (
            <button
              type="button"
              className="btn btn-secondary opacity-60 cursor-not-allowed"
              disabled
              title="Only the business owner can share verified figures with lenders"
            >
              <Share2 className="h-4 w-4 mr-1.5" /> Share with a lender
            </button>
          )
        }
      />
      <div className="mb-4">
        <Chips label="Period" value={months} onChange={setMonths} options={[['3', 'Last 3 months'], ['6', 'Last 6 months'], ['12', 'Last 12 months']]} />
      </div>
      {error && <ErrorNote>{error}</ErrorNote>}
      {loading && !data && <Loading />}
      {data && <Body s={data} shares={shares ?? []} onReloadShares={reloadShares} onOpenShare={() => setShareOpen(true)} canShare={canShare} />}

      {shareOpen && data && (
        <ShareSheet
          s={data}
          companyName={session?.company.name ?? ''}
          kraPin={session?.company.kraPin ?? ''}
          onClose={() => setShareOpen(false)}
          onCreated={() => {
            reloadShares();
          }}
        />
      )}
    </>
  );
}

function Body({
  s,
  shares,
  onReloadShares,
  onOpenShare,
  canShare,
}: {
  s: Summary;
  shares: ShareItem[];
  onReloadShares: () => void;
  onOpenShare: () => void;
  canShare: boolean;
}) {
  const nothingYet = s.averageMonthlyConfirmed === 0 && s.monthToDateConfirmed === 0;
  const whole = s.monthList.filter((m) => !m.partial);
  const confirmedTotal = whole.reduce((t, m) => t + m.confirmed, 0);
  const allTotal = whole.reduce((t, m) => t + m.allMoneyIn, 0);
  const labels = s.monthList.map((m) => monthName(`${m.month}-01`) + (m.partial ? '*' : ''));
  // The chart's line covers every bar it shows, this month included; the headline sticks to whole months.
  const shownConfirmed = s.monthList.reduce((t, m) => t + m.confirmed, 0);
  const shownAll = s.monthList.reduce((t, m) => t + m.allMoneyIn, 0);

  return (
    <div className="flex flex-col gap-4">
      {nothingYet ? (
        <Empty title="No money confirmed by Safaricom yet">
          <ol className="mx-auto mt-2 max-w-md list-decimal space-y-1.5 pl-5 text-left text-sm">
            <li>Open <Link to="/settings" className="font-semibold text-brand hover:underline">Settings → Business &amp; M-Pesa</Link> and copy your confirmation address.</li>
            <li>Paste it into your Paybill or Till settings on Safaricom’s M-Pesa portal (or ask Safaricom to register it).</li>
            <li>From then on, every customer payment arrives straight from Safaricom and counts here.</li>
          </ol>
        </Empty>
      ) : s.averageMonthlyConfirmed === 0 ? (
        // Confirmations only started this month: lead with what's there, and say what a lender will look for.
        <section className="card p-5 sm:p-6">
          <p className="flex items-center gap-1.5 text-sm font-semibold text-good"><BadgeCheck className="h-4 w-4" /> Confirmed by Safaricom</p>
          <p className="num mt-1 font-display text-3xl leading-tight font-semibold tracking-tight sm:text-4xl">{ksh(s.monthToDateConfirmed)} <span className="whitespace-nowrap text-lg font-semibold text-muted">so far this month</span></p>
          <p className="mt-1 text-sm text-ink-2">Your first whole month of confirmed money ends on the last day of this month. Lenders usually look at three to six whole months.</p>
        </section>
      ) : (
        <section className="card p-5 sm:p-6">
          <p className="flex items-center gap-1.5 text-sm font-semibold text-good"><BadgeCheck className="h-4 w-4" /> Confirmed by Safaricom</p>
          <p className="num mt-1 font-display text-3xl leading-tight font-semibold tracking-tight sm:text-4xl">{ksh(Math.round(s.averageMonthlyConfirmed / 100) * 100)} <span className="text-lg font-semibold text-muted">a month</span></p>
          <p className="mt-1 text-sm text-ink-2">
            from <b>{num(s.payers)}</b> different paying numbers, on average over the last {s.months} whole months
            {allTotal > 0 && <> · {pct(confirmedTotal / allTotal)} of all money in</>}
          </p>
          <p className="mt-3 text-sm text-muted">This month so far: <span className="num font-semibold text-ink">{ksh(s.monthToDateConfirmed)}</span></p>
        </section>
      )}

      <div className="grid gap-4 lg:grid-cols-3">
        <ChartCard
          className="lg:col-span-2"
          title="Confirmed money by month"
          answer={shownAll > 0 ? `${pct(shownConfirmed / shownAll)} of money in since ${monthName(s.from.slice(0, 10), true)} was confirmed by Safaricom` : 'No money in yet in this period'}
          legend={<Legend items={[['Confirmed by Safaricom', SERIES[0]], ['All money in', OTHER]]} />}
          table={<Table head={['Month', 'Confirmed', 'All money in', 'Paying numbers']} rows={s.monthList.map((m, i) => [labels[i], ksh(m.confirmed), ksh(m.allMoneyIn), num(m.payers)])} />}
        >
          <Columns
            labels={labels}
            series={[{ name: 'Confirmed by Safaricom', color: SERIES[0], values: s.monthList.map((m) => m.confirmed) }, { name: 'All money in', color: OTHER, values: s.monthList.map((m) => m.allMoneyIn) }]}
            fmt={compact}
            tip={(i) => {
              const m = s.monthList[i];
              return (
                <>
                  <p className="font-semibold">{monthName(`${m.month}-01`, true)}{m.partial && ' (so far)'}</p>
                  <p className="mt-1">Confirmed: <b className="num">{ksh(m.confirmed)}</b></p>
                  <p>All money in: <span className="num">{ksh(m.allMoneyIn)}</span></p>
                  <p className="text-muted">{num(m.payers)} paying numbers{m.allMoneyIn > 0 && ` · ${pct(m.confirmedShare)} confirmed`}</p>
                </>
              );
            }}
          />
          {s.monthList.some((m) => m.partial) && <p className="mt-1 text-2xs text-faint">* this month so far</p>}
        </ChartCard>

        <EtimsTile e={s.etims} />
      </div>

      <section className="card p-5">
        <h2 className="flex items-center gap-2 font-semibold"><ShieldCheck className="h-4 w-4 text-brand" /> Strength of your proof</h2>
        {s.fixes.length === 0 ? (
          <p className="mt-2 text-sm text-ink-2">Nothing to fix: your money in comes from Safaricom and your invoices are signed by KRA.</p>
        ) : (
          <ul className="mt-3 divide-y divide-line">
            {s.fixes.map((f) => (
              <li key={f.code} className="flex flex-wrap items-start gap-3 py-3 first:pt-0 last:pb-0">
                <CircleAlert className="mt-0.5 h-4 w-4 shrink-0 text-warn" />
                <p className="min-w-0 flex-1 text-sm">{f.text}</p>
                {f.link && f.action && <Link to={f.link} className="btn btn-ghost shrink-0 py-1.5 text-sm">{f.action}</Link>}
              </li>
            ))}
          </ul>
        )}
      </section>

      {s.bySource.length > 0 && (
        <section className="card p-5">
          <h2 className="font-semibold">Where money in came from</h2>
          <p className="mt-0.5 text-sm text-muted">Only money Safaricom’s servers confirmed counts as proof. The rest may be right, but a lender can’t check it.</p>
          <div className="mt-3">
            <Table
              head={['Source', 'Payments', 'Amount', 'Counts as proof']}
              rows={s.bySource.map((r) => [SOURCE_WORDS[r.source] ?? r.source, num(r.count), ksh(r.amount), r.confirmedAmount > 0 ? <span className="font-semibold text-good">{ksh(r.confirmedAmount)}</span> : <span className="text-faint">None</span>])}
            />
          </div>
        </section>
      )}

      {/* PR 3: Who can see your figures */}
      <SharesList shares={shares} onReload={onReloadShares} onOpenShare={onOpenShare} canShare={canShare} />

      <p className="text-xs text-faint">
        Checked {new Date(s.checkedAt).toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short' })}. Kabuor shows facts, not a credit score: lenders make their own decisions.
        No customer names or phone numbers are part of these figures.
      </p>
    </div>
  );
}

function SharesList({
  shares,
  onReload,
  onOpenShare,
  canShare,
}: {
  shares: ShareItem[];
  onReload: () => void;
  onOpenShare: () => void;
  canShare: boolean;
}) {
  const [revoking, setRevoking] = useState<string | null>(null);

  async function handleRevoke(id: string, lender: string) {
    if (!(await confirmDialog({ title: `Stop sharing with ${lender}?`, body: 'Their link stops working straight away.', confirm: 'Stop sharing', danger: true }))) return;
    setRevoking(id);
    try {
      await api(`/verified/shares/${id}/revoke`, { method: 'POST' });
      onReload();
    } catch (err: any) {
      await noticeDialog({ title: 'Sharing wasn’t stopped', body: err?.message || 'Please try again.' });
    } finally {
      setRevoking(null);
    }
  }

  const active = shares.filter((s) => s.status === 'active');
  const past = shares.filter((s) => s.status !== 'active');

  return (
    <section className="card p-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h2 className="flex items-center gap-2 font-semibold">
            <Share2 className="h-4 w-4 text-brand" /> Who can see your figures
          </h2>
          <p className="mt-0.5 text-sm text-muted">
            Every share is explicit, purpose-limited, time-limited, and recorded as consent under Kenyan data protection law.
          </p>
        </div>
        {canShare && (
          <button type="button" onClick={onOpenShare} className="btn btn-ghost text-sm">
            + New share
          </button>
        )}
      </div>

      {shares.length === 0 ? (
        <p className="mt-4 text-sm text-muted">You haven't shared your figures with any lenders yet.</p>
      ) : (
        <div className="mt-4 space-y-4">
          {active.length > 0 && (
            <div className="space-y-2">
              <p className="text-xs font-semibold tracking-wider text-muted uppercase">Active shares</p>
              <div className="divide-y divide-line rounded-lg border border-line bg-paper">
                {active.map((sh) => (
                  <div key={sh.id} className="flex flex-wrap items-center justify-between gap-3 p-3 sm:p-4">
                    <div className="min-w-0 flex-1">
                      <p className="font-semibold text-ink">{sh.lenderName}</p>
                      <p className="text-xs text-muted">
                        Valid until {new Date(sh.expiresAt).toLocaleDateString('en-GB', { dateStyle: 'medium' })} ·{' '}
                        {sh.viewsCount === 0 ? 'Not opened yet' : `Opened ${num(sh.viewsCount)} times`}{' '}
                        {sh.lastViewedAt && `(last ${new Date(sh.lastViewedAt).toLocaleDateString('en-GB', { dateStyle: 'short' })})`} · Shared by {sh.createdBy}
                      </p>
                    </div>
                    {canShare && (
                      <button
                        type="button"
                        disabled={revoking === sh.id}
                        onClick={() => handleRevoke(sh.id, sh.lenderName)}
                        className="btn btn-ghost text-xs text-bad hover:bg-bad/10"
                      >
                        {revoking === sh.id ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : 'Stop sharing'}
                      </button>
                    )}
                  </div>
                ))}
              </div>
            </div>
          )}

          {past.length > 0 && (
            <div className="space-y-2">
              <p className="text-xs font-semibold tracking-wider text-muted uppercase">Past shares (ended or revoked)</p>
              <div className="divide-y divide-line rounded-lg border border-line bg-surface/40 opacity-75">
                {past.map((sh) => (
                  <div key={sh.id} className="flex flex-wrap items-center justify-between gap-3 p-3 text-sm">
                    <div className="min-w-0">
                      <p className="font-medium text-ink-2">{sh.lenderName}</p>
                      <p className="text-xs text-faint">
                        {sh.status === 'revoked' ? (
                          <>Revoked by {sh.revokedBy ?? 'owner'} on {new Date(sh.revokedAt!).toLocaleDateString('en-GB', { dateStyle: 'medium' })}</>
                        ) : (
                          <>Expired on {new Date(sh.expiresAt).toLocaleDateString('en-GB', { dateStyle: 'medium' })}</>
                        )}
                        {' · '}Total views: {sh.viewsCount}
                      </p>
                    </div>
                    <span className="text-xs font-semibold uppercase text-faint">{sh.status}</span>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </section>
  );
}

function ShareSheet({
  s,
  companyName,
  kraPin,
  onClose,
  onCreated,
}: {
  s: Summary;
  companyName: string;
  kraPin: string;
  onClose: () => void;
  onCreated: () => void;
}) {
  const session = useSession();
  const [lenderName, setLenderName] = useState('');
  const [lenderContact, setLenderContact] = useState('');
  const [periodMonths, setPeriodMonths] = useState<'3' | '6' | '12'>('6');
  const [days, setDays] = useState<'7' | '30' | '90'>('30');
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState<{ url: string; token: string; lender: string } | null>(null);
  const [qrUrl, setQrUrl] = useState('');
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (result?.url) {
      QRCode.toDataURL(result.url, { margin: 1, width: 220 })
        .then(setQrUrl)
        .catch(() => {});
    }
  }, [result?.url]);

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    setBusy(true);
    try {
      const res = await api<{ url: string; token: string }>('/verified/shares', {
        method: 'POST',
        body: {
          lenderName,
          lenderContact: lenderContact || undefined,
          months: Number(periodMonths),
          days: Number(days),
          password: password || undefined,
          code: code || undefined,
        },
      });
      setResult({ url: res.url, token: res.token, lender: lenderName });
      onCreated();
    } catch (err: any) {
      setError(err?.message || 'Could not create share link.');
    } finally {
      setBusy(false);
    }
  }

  const averageKsh = Math.round(s.averageMonthlyConfirmed / 100) * 100;

  return (
    <Sheet open onClose={onClose} title="Share with a lender">
      {result ? (
        <div className="space-y-5">
          <div className="rounded-lg border border-good/30 bg-good/5 p-4 text-good">
            <p className="font-semibold flex items-center gap-1.5"><BadgeCheck className="h-4 w-4" /> Link created for {result.lender}</p>
            <p className="mt-1 text-xs text-ink-2">The lender can view this report until it expires. You can stop sharing at any moment.</p>
          </div>

          <Field label="Live report address">
            <div className="flex items-center gap-2">
              <input type="text" readOnly value={result.url} className="input flex-1 font-mono text-xs select-all" />
              <button
                type="button"
                className="btn btn-secondary px-3"
                onClick={async () => {
                  try {
                    await navigator.clipboard.writeText(result.url);
                    setCopied(true);
                    setTimeout(() => setCopied(false), 2000);
                  } catch {}
                }}
              >
                {copied ? <Check className="h-4 w-4 text-good" /> : <Copy className="h-4 w-4" />}
              </button>
            </div>
          </Field>

          <div className="flex flex-wrap gap-2">
            <a
              href={`https://wa.me/?text=${encodeURIComponent(`Here is the verified revenue report for ${companyName}: ${result.url}`)}`}
              target="_blank"
              rel="noreferrer"
              className="btn btn-primary text-xs"
            >
              Share via WhatsApp
            </a>
            <a href={result.url} target="_blank" rel="noreferrer" className="btn btn-secondary text-xs inline-flex items-center gap-1">
              <ExternalLink className="h-3.5 w-3.5" /> Preview live page
            </a>
          </div>

          {qrUrl && (
            <div className="flex flex-col items-center justify-center rounded-lg border border-line bg-surface/50 p-4">
              <p className="mb-2 text-xs font-semibold text-muted">Scan QR with phone or download for application file</p>
              <img src={qrUrl} alt="QR Code" className="h-44 w-44 rounded border border-line bg-white p-2" />
              <a href={qrUrl} download={`kabuor-verified-${result.lender.toLowerCase().replace(/\s+/g, '-')}.png`} className="mt-2 text-xs text-brand hover:underline">
                Download QR Code PNG
              </a>
            </div>
          )}

          <div className="border-t border-line pt-3 flex justify-end">
            <button type="button" onClick={onClose} className="btn btn-ghost text-sm">
              Done
            </button>
          </div>
        </div>
      ) : (
        <form onSubmit={handleCreate} className="space-y-4">
          <p className="text-xs text-muted">
            Lenders see totals and counts only. They will never see customer names, phone numbers, or itemised invoice lines.
          </p>

          <Field label="Lender name (e.g. Equity Bank, KCB, NCBA)">
            <input
              type="text"
              required
              placeholder="Equity Bank"
              value={lenderName}
              onChange={(e) => setLenderName(e.target.value)}
              className="input w-full"
            />
          </Field>

          <Field label="Lender loan officer contact (optional, for your records)">
            <input
              type="text"
              placeholder="loan.officer@bank.co.ke"
              value={lenderContact}
              onChange={(e) => setLenderContact(e.target.value)}
              className="input w-full"
            />
          </Field>

          <div>
            <Chips
              label="Months of proof"
              value={periodMonths}
              onChange={setPeriodMonths}
              options={[['3', '3 months'], ['6', '6 months'], ['12', '12 months']]}
            />
          </div>

          <div>
            <Chips
              label="Link valid for"
              value={days}
              onChange={setDays}
              options={[['7', '7 days'], ['30', '30 days'], ['90', '90 days']]}
            />
          </div>

          {/* Live Preview */}
          <div className="rounded-lg border border-line bg-surface/50 p-3.5 text-xs space-y-1.5">
            <p className="font-semibold text-ink flex items-center gap-1.5">
              <ShieldCheck className="h-3.5 w-3.5 text-good" /> Preview of what {lenderName || 'the lender'} will see:
            </p>
            <div className="grid grid-cols-2 gap-2 pt-1 text-ink-2">
              <div>Business: <b>{companyName}</b></div>
              <div>KRA PIN: <b>{kraPin || 'Not set'}</b></div>
              <div>Monthly confirmed: <b>{ksh(averageKsh)}</b></div>
              <div>Paying numbers: <b>{num(s.payers)}</b></div>
              <div>eTIMS signed: <b>{s.etims.required ? pct(s.etims.signedShare) : 'N/A'}</b></div>
              <div>Period: <b>Last {periodMonths} months</b></div>
            </div>
            <p className="pt-1 text-2xs text-faint">Totals and counts only. No individual transactions or PII.</p>
          </div>

          {/* Owner Re-auth */}
          <div className="rounded-lg border border-line bg-paper p-3.5 space-y-2">
            <p className="text-xs font-semibold text-ink">Confirm owner identity</p>
            {session?.user.twoFactorEnabled ? (
              <Field label="Enter 6-digit authenticator code">
                <input
                  type="text"
                  required
                  autoFocus
                  maxLength={6}
                  placeholder="000000"
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  className="input w-full text-center tracking-widest font-mono text-base"
                />
              </Field>
            ) : (
              <Field label="Confirm account password">
                <input
                  type="password"
                  required
                  autoFocus
                  placeholder="Your password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="input w-full"
                />
              </Field>
            )}
          </div>

          {error && <ErrorNote>{error}</ErrorNote>}

          <div className="flex justify-end gap-2 pt-2">
            <button type="button" onClick={onClose} className="btn btn-ghost text-sm">
              Cancel
            </button>
            <button type="submit" disabled={busy} className="btn btn-primary text-sm">
              {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : 'Create link for lender'}
            </button>
          </div>
        </form>
      )}
    </Sheet>
  );
}

function EtimsTile({ e }: { e: Summary['etims'] }) {
  if (!e.required)
    return <Stat label="KRA eTIMS" value="Not needed">Your business isn’t registered for VAT, so invoices don’t need KRA’s signature.</Stat>;
  if (e.issued === 0) return <Stat label="KRA eTIMS" value="No invoices yet">Issued invoices are signed by KRA as you send them.</Stat>;
  return (
    <Stat label="Invoices signed by KRA" value={pct(e.signedShare)} tone={e.signedShare >= 0.95 ? 'good' : undefined}>
      <span className="num">{num(e.signed)} of {num(e.issued)}</span> issued invoices in this period.
      {e.testSigned > 0 && <> {num(e.testSigned)} signed in test mode until KRA certifies Kabuor ERP.</>}
      {e.failedOrPending > 0 && <> {num(e.failedOrPending)} waiting to be signed.</>}
    </Stat>
  );
}
