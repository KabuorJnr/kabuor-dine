// Kabuor Verified: public tamper-evident proof page for lenders.
// Lenders see totals and counts only. Never individual customer names, phone numbers, or invoice lines.
// Facts only: Kabuor provides verified facts, not credit decisions or credit scores.
import { useEffect, useState } from 'react';
import { useParams } from 'react-router';
import QRCode from 'qrcode';
import { BadgeCheck, Printer, ShieldCheck, XCircle } from 'lucide-react';
import { ChartCard, Columns, compact, Legend, monthName, SERIES, Table } from '../components/DashboardCharts';
import { ksh } from '../lib/format';
import { Loading } from '../components/ui';

type Month = { month: string; partial: boolean; confirmed: number; allMoneyIn: number; confirmedShare: number; payers: number; topFiveShare: number };
type Source = { source: string; amount: number; count: number; confirmedAmount: number };
type Summary = {
  months: number; from: string; to: string;
  averageMonthlyConfirmed: number; payers: number; topFiveShare: number; monthToDateConfirmed: number;
  ownNumbers: number; notFromSafaricom: number;
  monthList: Month[]; bySource: Source[];
  etims: { required: boolean; issued: number; signed: number; testSigned: number; failedOrPending: number; signedShare: number };
  checkedAt: string;
};

type VerifiedPayload = {
  company: {
    name: string;
    kraPin: string;
    county: string;
  };
  lenderName: string;
  months: number;
  expiresAt: string;
  checkedAt: string;
  summary: Summary;
  canonical: string;
  keyId: string;
  signature: string;
};

const OTHER = '#b9bccf';
const pct = (share: number) => `${Math.round(share * 100)}%`;
const num = (n: number) => n.toLocaleString('en-KE');

export default function VerifiedView() {
  const { token } = useParams<{ token: string }>();
  const [data, setData] = useState<VerifiedPayload | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [qrUrl, setQrUrl] = useState('');

  useEffect(() => {
    if (!token) return;
    setLoading(true);
    fetch(`/api/verified/s/${token}`)
      .then(async (res) => {
        if (!res.ok) {
          const body = await res.json().catch(() => ({}));
          throw new Error(body.error || 'This link has ended. Ask the business for a new one.');
        }
        return res.json();
      })
      .then((json: VerifiedPayload) => {
        setData(json);
        QRCode.toDataURL(window.location.href, { margin: 1, width: 180 })
          .then(setQrUrl)
          .catch(() => {});
      })
      .catch((err: Error) => setError(err.message))
      .finally(() => setLoading(false));
  }, [token]);

  if (loading) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-paper">
        <Loading />
      </div>
    );
  }

  if (error || !data) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-paper px-4">
        <div className="card max-w-md p-8 text-center">
          <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-surface text-muted">
            <XCircle className="h-6 w-6" />
          </div>
          <h1 className="mt-4 text-xl font-bold text-ink">Link Ended</h1>
          <p className="mt-2 text-sm text-ink-2">
            {error || 'This link has ended. Ask the business for a new one.'}
          </p>
        </div>
      </div>
    );
  }

  const { company, lenderName, months, expiresAt, checkedAt, summary, keyId, signature } = data;
  const labels = summary.monthList.map((m) => monthName(`${m.month}-01`) + (m.partial ? '*' : ''));
  const shownConfirmed = summary.monthList.reduce((t, m) => t + m.confirmed, 0);
  const shownAll = summary.monthList.reduce((t, m) => t + m.allMoneyIn, 0);

  return (
    <div className="min-h-screen bg-surface/30 text-ink antialiased">
      {/* Top Banner */}
      <div className="border-b border-line bg-paper px-4 py-3 print:hidden">
        <div className="mx-auto flex max-w-5xl flex-wrap items-center justify-between gap-3 text-xs">
          <div className="flex items-center gap-2">
            <span className="inline-flex items-center gap-1.5 rounded-full bg-good/10 px-2.5 py-0.5 font-semibold text-good">
              <BadgeCheck className="h-3.5 w-3.5" /> Kabuor Verified Report
            </span>
            <span className="text-muted">·</span>
            <span className="font-medium text-ink-2">
              Prepared for <b>{lenderName}</b> · valid until {new Date(expiresAt).toLocaleDateString('en-GB', { dateStyle: 'medium' })}
            </span>
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => window.print()}
              className="btn btn-secondary py-1 text-xs inline-flex items-center gap-1.5"
            >
              <Printer className="h-3.5 w-3.5" /> Print / Save PDF
            </button>
          </div>
        </div>
      </div>

      <main className="mx-auto max-w-5xl px-4 py-8 sm:px-6">
        {/* Business Header */}
        <div className="card p-6 sm:p-8">
          <div className="flex flex-wrap items-start justify-between gap-4">
            <div>
              <p className="text-xs font-semibold uppercase tracking-wider text-muted">Merchant Information</p>
              <h1 className="mt-1 font-display text-3xl font-semibold tracking-tight text-ink sm:text-4xl">
                {company.name}
              </h1>
              <div className="mt-3 flex flex-wrap items-center gap-3 text-sm text-ink-2">
                <span>KRA PIN: <b className="font-mono">{company.kraPin || 'Not registered'}</b></span>
                {company.county && <><span>·</span><span>County: <b>{company.county}</b></span></>}
                <span>·</span>
                <span>Report period: <b>Last {months} months</b></span>
              </div>
            </div>

            {qrUrl && (
              <div className="hidden sm:flex flex-col items-center rounded-lg border border-line bg-paper p-2 text-center">
                <img src={qrUrl} alt="Live Report QR" className="h-24 w-24" />
                <span className="mt-1 text-2xs text-faint">Live link</span>
              </div>
            )}
          </div>

          <div className="mt-6 grid gap-4 border-t border-line pt-6 sm:grid-cols-3">
            <div className="rounded-lg bg-surface/50 p-4">
              <p className="text-xs font-semibold text-muted">Confirmed Monthly Revenue</p>
              <p className="num mt-1 font-display text-2xl font-semibold text-good sm:text-3xl">
                {ksh(Math.round(summary.averageMonthlyConfirmed / 100) * 100)}
              </p>
              <p className="mt-1 text-xs text-ink-2">
                Averaged over {months} whole months from {num(summary.payers)} paying numbers
              </p>
            </div>

            <div className="rounded-lg bg-surface/50 p-4">
              <p className="text-xs font-semibold text-muted">KRA eTIMS Signed Share</p>
              <p className="num mt-1 font-display text-2xl font-semibold text-ink sm:text-3xl">
                {summary.etims.required ? pct(summary.etims.signedShare) : 'Exempt'}
              </p>
              <p className="mt-1 text-xs text-ink-2">
                {summary.etims.required
                  ? `${num(summary.etims.signed)} of ${num(summary.etims.issued)} invoices signed with KRA`
                  : 'Business is not VAT registered'}
              </p>
            </div>

            <div className="rounded-lg bg-surface/50 p-4">
              <p className="text-xs font-semibold text-muted">This Month (Month-To-Date)</p>
              <p className="num mt-1 font-display text-2xl font-semibold text-ink sm:text-3xl">
                {ksh(summary.monthToDateConfirmed)}
              </p>
              <p className="mt-1 text-xs text-ink-2">
                Confirmed so far this month
              </p>
            </div>
          </div>
        </div>

        {/* Charts & Breakdown */}
        <div className="mt-6 grid gap-6">
          <ChartCard
            title="Confirmed money by month"
            answer={shownAll > 0 ? `${pct(shownConfirmed / shownAll)} of money in since ${monthName(summary.from.slice(0, 10), true)} was confirmed directly by Safaricom's servers` : 'No money in recorded in this period'}
            legend={<Legend items={[['Confirmed by Safaricom', SERIES[0]], ['All money in', OTHER]]} />}
            table={<Table head={['Month', 'Confirmed', 'All money in', 'Paying numbers']} rows={summary.monthList.map((m, i) => [labels[i], ksh(m.confirmed), ksh(m.allMoneyIn), num(m.payers)])} />}
          >
            <Columns
              labels={labels}
              series={[{ name: 'Confirmed by Safaricom', color: SERIES[0], values: summary.monthList.map((m) => m.confirmed) }, { name: 'All money in', color: OTHER, values: summary.monthList.map((m) => m.allMoneyIn) }]}
              fmt={compact}
              tip={(i) => {
                const m = summary.monthList[i];
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
            {summary.monthList.some((m) => m.partial) && <p className="mt-1 text-2xs text-faint">* this month so far</p>}
          </ChartCard>

          {/* Definitions */}
          <section className="card p-5">
            <h2 className="font-semibold text-ink">How figures are verified</h2>
            <div className="mt-3 grid gap-3 text-xs sm:grid-cols-2">
              <div className="rounded-lg border border-line bg-surface/30 p-3">
                <p className="font-semibold text-ink">Confirmed by Safaricom</p>
                <p className="mt-1 text-ink-2">
                  Payments Safaricom's servers delivered to Kabuor ERP directly, with caller IP verified against Safaricom's published gateway addresses. Payments typed in by the business are excluded.
                </p>
              </div>

              <div className="rounded-lg border border-line bg-surface/30 p-3">
                <p className="font-semibold text-ink">KRA eTIMS Compliance</p>
                <p className="mt-1 text-ink-2">
                  Invoices transmitted and cryptographically signed by Kenya Revenue Authority. Test-mode signatures and unsigned documents are counted separately and excluded from compliance totals.
                </p>
              </div>

              <div className="rounded-lg border border-line bg-surface/30 p-3">
                <p className="font-semibold text-ink">Paying Numbers &amp; Concentration</p>
                <p className="mt-1 text-ink-2">
                  Unique customer MSISDNs per month. The top 5 customers accounted for {pct(summary.topFiveShare)} of confirmed volume. Staff and company phones are flagged.
                </p>
              </div>

              <div className="rounded-lg border border-line bg-surface/30 p-3">
                <p className="font-semibold text-ink">Data Minimisation &amp; Privacy</p>
                <p className="mt-1 text-ink-2">
                  Under the Kenya Data Protection Act 2019, customer phone numbers, names, and line item contents are never revealed. Aggregated figures and customer counts are provided.
                </p>
              </div>
            </div>
          </section>

          {/* Tamper Evidence & Cryptographic Verification */}
          <section className="card p-5">
            <div className="flex flex-wrap items-center justify-between gap-3 text-xs">
              <div className="flex items-center gap-2">
                <ShieldCheck className="h-5 w-5 text-good" />
                <div>
                  <p className="font-semibold text-ink">Tamper-Evident Digital Signature</p>
                  <p className="text-muted">
                    Signed by Kabuor ERP at {new Date(checkedAt).toLocaleString('en-GB', { dateStyle: 'medium', timeStyle: 'short' })} ({keyId})
                  </p>
                </div>
              </div>
              <div className="font-mono text-2xs text-muted max-w-xs truncate" title={signature}>
                Sig: {signature}
              </div>
            </div>
          </section>

          {/* Regulatory Notice */}
          <div className="text-center text-xs text-faint space-y-1">
            <p>
              <b>Important regulatory notice:</b> Kabuor provides verified facts and figures, not a credit score or credit decision.
              Scoring belongs to licensed credit reference bureaus and financial institutions.
            </p>
            <p>
              Report generated for {lenderName}. Link token expires {new Date(expiresAt).toLocaleDateString('en-GB', { dateStyle: 'long' })}.
            </p>
          </div>
        </div>
      </main>
    </div>
  );
}
