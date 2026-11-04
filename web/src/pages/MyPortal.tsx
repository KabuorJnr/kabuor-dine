import { useState } from 'react';
import {
  CalendarDays,
  CheckCircle2,
  FileText,
  LogOut,
  Plus,
  Printer,
} from 'lucide-react';
import { api, setSession, useApi } from '../lib/api';
import { day, ksh } from '../lib/format';
import { Empty, ErrorNote, Field, Loading, Sheet, Status, SheetFooter, PageError, confirmDialog, noticeDialog } from '../components/ui';
import PortalOperations from '../components/PortalOperations';
import { printAs } from '../components/PrintDoc';

const monthName = (iso: string) => new Date(`${iso}T00:00:00`).toLocaleDateString('en-GB', { month: 'long', year: 'numeric' });

import { MyDeductions, parseLines } from '../components/Deductions';

export default function MyPortal() {
  const { data, error, status, reload } = useApi<any>('/me');
  const [tab, setTab] = useState<'overview' | 'payslips' | 'leave' | 'work' | 'p9' | 'security'>('overview');

  // Modals
  const [requestLeaveOpen, setRequestLeaveOpen] = useState(false);
  const [selectedPayslipId, setSelectedPayslipId] = useState<string | null>(null);

  // Leave Form
  const [leaveType, setLeaveType] = useState('Annual');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [note, setNote] = useState('');
  const [leaveBusy, setLeaveBusy] = useState(false);
  const [leaveError, setLeaveError] = useState('');

  // Password Form
  const [currentPw, setCurrentPw] = useState('');
  const [newPw, setNewPw] = useState('');
  const [confirmPw, setConfirmPw] = useState('');
  const [pwBusy, setPwBusy] = useState(false);
  const [pwError, setPwError] = useState('');
  const [pwSuccess, setPwSuccess] = useState(false);

  // Payslip detail query
  const { data: payslipDetail } = useApi<any>(selectedPayslipId ? `/me/payslips/${selectedPayslipId}` : null);
  // P9 query
  const { data: p9Data } = useApi<any>(tab === 'p9' ? '/me/p9' : null);

  // An owner or manager who isn't on the payroll themselves has no portal: say so, rather than "didn't load".
  if (status === 404)
    return (
      <Empty title="This page is for people on the payroll">
        Employees see their payslips, P9 and leave here. Your login isn’t linked to an employee record, so there’s nothing to show yet.
      </Empty>
    );
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;

  const emp = data.employee;
  const balance = data.leaveBalance;
  const payslips: any[] = data.payslips || [];
  const leaves: any[] = data.leave || [];

  const handleLeaveSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setLeaveBusy(true);
    setLeaveError('');
    try {
      await api('/me/leave', {
        body: { type: leaveType, from, to, note },
      });
      setRequestLeaveOpen(false);
      setFrom('');
      setTo('');
      setNote('');
      reload();
    } catch (err) {
      setLeaveError((err as Error).message);
    } finally {
      setLeaveBusy(false);
    }
  };

  const handleCancelLeave = async (id: string) => {
    if (!(await confirmDialog({ title: 'Withdraw this leave request?', confirm: 'Withdraw', cancel: 'Keep it', danger: true }))) return;
    try {
      await api(`/me/leave/${id}/cancel`, { method: 'POST' });
      reload();
    } catch (err) {
      await noticeDialog({ title: 'The request wasn’t withdrawn', body: (err as Error).message });
    }
  };

  const handlePasswordChange = async (e: React.FormEvent) => {
    e.preventDefault();
    if (newPw !== confirmPw) {
      setPwError('New passwords do not match.');
      return;
    }
    setPwBusy(true);
    setPwError('');
    setPwSuccess(false);
    try {
      await api('/auth/password', {
        body: { current: currentPw, new: newPw },
      });
      setPwSuccess(true);
      setCurrentPw('');
      setNewPw('');
      setConfirmPw('');
    } catch (err) {
      setPwError((err as Error).message);
    } finally {
      setPwBusy(false);
    }
  };

  return (
    <div className="space-y-6">
      {/* Employee Profile Header */}
      <div className="flex flex-col gap-4 rounded-xl border border-line bg-white p-5 sm:flex-row sm:items-center sm:justify-between shadow-sm">
        <div className="flex items-center gap-4">
          <div className="grid h-14 w-14 place-items-center rounded-full bg-brand-soft text-brand text-xl font-semibold font-display">
            {emp.name.split(' ').map((n: string) => n[0]).slice(0, 2).join('')}
          </div>
          <div>
            <div className="flex items-center gap-2">
              <h1 className="font-display text-xl font-semibold text-ink">{emp.name}</h1>
              <span className="rounded bg-paper px-2 py-0.5 text-xs font-semibold text-muted border border-line">
                {emp.number}
              </span>
            </div>
            <p className="text-sm text-muted">
              {emp.jobTitle} · <span className="font-medium text-ink">{emp.department}</span>
            </p>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={() => setRequestLeaveOpen(true)}
            className="btn btn-primary py-2 text-xs font-semibold"
          >
            <Plus className="h-4 w-4" /> Request Leave
          </button>
          <button
            type="button"
            onClick={() => setSession(null)}
            className="btn btn-ghost py-2 text-xs font-semibold text-muted hover:text-bad"
            title="Sign out"
          >
            <LogOut className="h-4 w-4" /> Sign out
          </button>
        </div>
      </div>

      {/* Navigation Tabs */}
      <div className="flex border-b border-line gap-2 overflow-x-auto text-sm">
        {[
          ['overview', 'Overview'],
          ['payslips', `Payslips (${payslips.length})`],
          ['leave', `Leave (${leaves.length})`],
          ['work', 'Requests & faults'],
          ['p9', 'P9 Tax Card'],
          ['security', 'Security & Password'],
        ].map(([key, label]) => (
          <button
            key={key}
            type="button"
            onClick={() => setTab(key as any)}
            className={`px-4 py-2.5 font-semibold transition whitespace-nowrap border-b-2 -mb-px ${
              tab === key
                ? 'border-brand text-brand font-bold'
                : 'border-transparent text-muted hover:text-ink hover:border-line'
            }`}
          >
            {label}
          </button>
        ))}
      </div>

      {/* Tab: Overview */}
      {tab === 'overview' && (
        <div className="space-y-6">
          <MyDeductions deductions={data.deductions ?? []} onChange={reload} />
          {/* Quick Metrics */}
          <div className="grid gap-4 sm:grid-cols-3">
            <div className="card p-4">
              <span className="text-xs font-semibold uppercase tracking-wider text-muted">Annual Leave Balance</span>
              <p className="num mt-2 font-display text-3xl font-semibold text-brand">
                {balance ? `${balance.left} days` : '—'}
              </p>
              <span className="text-xs text-muted">
                {balance ? `${balance.taken} used of ${balance.earned} earned so far (${balance.entitlement} a year)` : ''}
              </span>
            </div>

            <div className="card p-4">
              <span className="text-xs font-semibold uppercase tracking-wider text-muted">Last payslip take-home</span>
              {payslips[0] ? (
                <>
                  <p className="num mt-2 font-display text-3xl font-semibold text-good">{ksh(payslips[0].net)}</p>
                  <span className="text-xs text-muted">{monthName(payslips[0].period)}</span>
                </>
              ) : (
                // No figure yet: say so in words rather than a big green dash that reads like an amount.
                <p className="mt-2 text-sm text-muted">Your first payslip shows here once payroll is approved.</p>
              )}
            </div>

            <div className="card p-4">
              <span className="text-xs font-semibold uppercase tracking-wider text-muted">Statutory ID Numbers</span>
              <div className="mt-2 text-xs space-y-1">
                <p>KRA PIN: {emp.kraPin ? <b className="font-mono text-ink">{emp.kraPin}</b> : <span className="text-faint">not recorded yet</span>}</p>
                <p>NSSF: {emp.nssfNumber ? <b className="font-mono text-ink">{emp.nssfNumber}</b> : <span className="text-faint">not recorded yet</span>}</p>
                <p>SHA (SHIF): {emp.shaNumber ? <b className="font-mono text-ink">{emp.shaNumber}</b> : <span className="text-faint">not recorded yet</span>}</p>
              </div>
            </div>
          </div>

          {/* Upcoming Leave */}
          <div className="card p-4 sm:p-5">
            <h2 className="text-sm font-semibold text-ink mb-3">Upcoming & Active Leave</h2>
            {data.upcoming && data.upcoming.length > 0 ? (
              <ul className="divide-y divide-line">
                {data.upcoming.map((l: any) => (
                  <li key={l.id} className="flex items-center justify-between py-2.5">
                    <div>
                      <b className="text-sm text-ink">{l.type} Leave</b>
                      <p className="text-xs text-muted">
                        {day(l.from)} to {day(l.to)}
                      </p>
                    </div>
                    <Status tone={l.status === 'Approved' ? 'good' : 'warn'}>{l.status}</Status>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="text-xs text-muted py-2">No upcoming leave scheduled.</p>
            )}
          </div>
        </div>
      )}

      {/* Tab: Payslips */}
      {tab === 'payslips' && (
        <div className="space-y-4">
          <div className="card divide-y divide-line">
            {payslips.length === 0 ? (
              <div className="p-8 text-center text-muted">
                <FileText className="h-10 w-10 text-line mx-auto mb-2" />
                <p className="text-sm font-semibold text-ink">No payslips issued yet</p>
                <p className="text-xs text-muted mt-1">Once HR completes monthly payroll, your payslips appear here.</p>
              </div>
            ) : (
              payslips.map((p) => (
                <div key={p.runId} className="flex items-center justify-between p-4 hover:bg-paper/50">
                  <div className="flex items-center gap-3">
                    <div className="grid h-10 w-10 place-items-center rounded-lg bg-brand-soft text-brand">
                      <FileText className="h-5 w-5" />
                    </div>
                    <div>
                      <b className="block text-sm text-ink">{monthName(p.period)}</b>
                      <span className="text-xs text-muted">
                        Gross {ksh(p.gross)} · take-home <b className="text-good">{ksh(p.net)}</b>
                      </span>
                    </div>
                  </div>

                  <div className="flex items-center gap-3">
                    <Status tone={p.paid ? 'good' : 'info'}>{p.paid ? 'Paid' : 'Approved'}</Status>
                    <button
                      type="button"
                      onClick={() => setSelectedPayslipId(p.runId)}
                      className="btn btn-ghost py-1.5 text-xs font-semibold"
                    >
                      View Breakdown
                    </button>
                  </div>
                </div>
              ))
            )}
          </div>
        </div>
      )}

      {/* Tab: Leave History & Requests */}
      {tab === 'leave' && (
        <div className="space-y-4">
          <div className="flex justify-between items-center">
            <h2 className="text-sm font-semibold text-ink">Leave Applications</h2>
            <button
              type="button"
              onClick={() => setRequestLeaveOpen(true)}
              className="btn btn-primary py-1.5 text-xs font-semibold"
            >
              <Plus className="h-4 w-4" /> Apply for Leave
            </button>
          </div>

          <div className="card divide-y divide-line">
            {leaves.length === 0 ? (
              <div className="p-8 text-center text-muted">
                <CalendarDays className="h-10 w-10 text-line mx-auto mb-2" />
                <p className="text-sm font-semibold text-ink">No leave applications yet</p>
                <p className="text-xs text-muted mt-1">Click "Apply for Leave" above to request time off.</p>
              </div>
            ) : (
              leaves.map((l) => (
                <div key={l.id} className="flex items-center justify-between p-4">
                  <div>
                    <div className="flex items-center gap-2">
                      <b className="text-sm text-ink">{l.type} Leave</b>
                      <Status
                        tone={
                          l.status === 'Approved' ? 'good' : l.status === 'Rejected' ? 'bad' : l.status === 'Cancelled' ? 'plain' : 'warn'
                        }
                      >
                        {l.status}
                      </Status>
                    </div>
                    <p className="text-xs text-muted mt-0.5">
                      {day(l.from)} to {day(l.to)} · {l.note || 'No note'}
                    </p>
                  </div>

                  {l.status === 'Pending' && (
                    <button
                      type="button"
                      onClick={() => handleCancelLeave(l.id)}
                      className="btn btn-ghost py-1 text-xs text-bad hover:bg-bad/10"
                    >
                      Withdraw
                    </button>
                  )}
                </div>
              ))
            )}
          </div>
        </div>
      )}

      {/* Tab: P9 Tax Card */}
      {tab === 'p9' && (
        <div className="space-y-4">
          <div className="card doc p-5">
            <div className="flex justify-between items-center mb-4 border-b border-line pb-3">
              <div>
                <h2 className="font-display text-base font-semibold text-ink">KRA P9 Tax Deduction Certificate</h2>
                <p className="text-xs text-muted">Annual tax deducted and remitted under PAYE</p>
              </div>
              <button
                type="button"
                onClick={() => printAs('Kabuor document')}
                className="btn btn-ghost py-1.5 text-xs font-semibold"
              >
                <Printer className="h-4 w-4" /> Print P9
              </button>
            </div>

            {p9Data ? (
              <div className="overflow-x-auto text-xs">
                <table className="w-full min-w-[54rem] divide-y divide-line text-left">
                  <thead>
                    <tr className="bg-paper font-semibold text-muted text-2xs">
                      <th className="p-2 whitespace-nowrap">Month</th>
                      <th className="p-2 text-right whitespace-nowrap">Basic Pay</th>
                      <th className="p-2 text-right whitespace-nowrap">Benefits</th>
                      <th className="p-2 text-right whitespace-nowrap">Gross Pay</th>
                      <th className="p-2 text-right whitespace-nowrap">NSSF / Pension</th>
                      <th className="p-2 text-right whitespace-nowrap">Taxable Pay</th>
                      <th className="p-2 text-right whitespace-nowrap">Tax Charged</th>
                      <th className="p-2 text-right whitespace-nowrap">Personal Relief</th>
                      <th className="p-2 text-right whitespace-nowrap">PAYE Deducted</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-line/60">
                    {p9Data.months?.map((m: any, i: number) => (
                      <tr key={i} className="hover:bg-paper/40">
                        <td className="p-2 font-semibold whitespace-nowrap">{m.month}</td>
                        <td className="p-2 text-right num whitespace-nowrap">{ksh(m.basicSalary)}</td>
                        <td className="p-2 text-right num whitespace-nowrap">{ksh(m.benefits)}</td>
                        <td className="p-2 text-right num font-semibold whitespace-nowrap">{ksh(m.gross)}</td>
                        <td className="p-2 text-right num whitespace-nowrap">{ksh(m.nssf)}</td>
                        <td className="p-2 text-right num font-semibold whitespace-nowrap">{ksh(m.taxablePay)}</td>
                        <td className="p-2 text-right num whitespace-nowrap">{ksh(m.taxCharged)}</td>
                        <td className="p-2 text-right num whitespace-nowrap">{ksh(m.relief)}</td>
                        <td className="p-2 text-right num font-bold text-bad whitespace-nowrap">{ksh(m.paye)}</td>
                      </tr>
                    ))}
                  </tbody>
                  {p9Data.totals && (
                    <tfoot className="border-t-2 border-line bg-paper/60 font-bold">
                      <tr>
                        <td className="p-2 whitespace-nowrap">TOTAL</td>
                        <td className="p-2 text-right num whitespace-nowrap">{ksh(p9Data.totals.basicSalary)}</td>
                        <td className="p-2 text-right num whitespace-nowrap">{ksh(p9Data.totals.benefits)}</td>
                        <td className="p-2 text-right num whitespace-nowrap">{ksh(p9Data.totals.gross)}</td>
                        <td className="p-2 text-right num whitespace-nowrap">{ksh(p9Data.totals.nssf)}</td>
                        <td className="p-2 text-right num whitespace-nowrap">{ksh(p9Data.totals.taxablePay)}</td>
                        <td className="p-2 text-right num whitespace-nowrap">{ksh(p9Data.totals.taxCharged)}</td>
                        <td className="p-2 text-right num whitespace-nowrap">{ksh(p9Data.totals.relief)}</td>
                        <td className="p-2 text-right num text-bad whitespace-nowrap">{ksh(p9Data.totals.paye)}</td>
                      </tr>
                    </tfoot>
                  )}
                </table>
              </div>
            ) : (
              <p className="text-xs text-muted py-4 text-center">Loading P9 records for this tax year...</p>
            )}
          </div>
        </div>
      )}

      {tab === 'work' && <PortalOperations />}

      {/* Tab: Security & Password */}
      {tab === 'security' && (
        <div className="card p-5 max-w-md">
          <h2 className="text-sm font-semibold text-ink mb-1">Change Sign-in Password</h2>
          <p className="text-xs text-muted mb-4">Choose a safe password with at least 8 characters.</p>

          <form onSubmit={handlePasswordChange} className="space-y-4">
            <Field label="Current password">
              <input
                type="password"
                required
                value={currentPw}
                onChange={(e) => setCurrentPw(e.target.value)}
                className="field"
              />
            </Field>

            <Field label="New password">
              <input
                type="password"
                required
                minLength={8}
                value={newPw}
                onChange={(e) => setNewPw(e.target.value)}
                className="field"
              />
            </Field>

            <Field label="Confirm new password">
              <input
                type="password"
                required
                minLength={8}
                value={confirmPw}
                onChange={(e) => setConfirmPw(e.target.value)}
                className="field"
              />
            </Field>

            {pwError && <ErrorNote>{pwError}</ErrorNote>}
            {pwSuccess && (
              <div className="rounded-md bg-good/10 p-3 text-xs font-semibold text-good flex items-center gap-2">
                <CheckCircle2 className="h-4 w-4" /> Password changed successfully.
              </div>
            )}

            <button type="submit" disabled={pwBusy} className="btn btn-primary w-full py-2.5 text-xs font-semibold">
              {pwBusy ? 'Updating...' : 'Save New Password'}
            </button>
          </form>
        </div>
      )}

      {/* Leave Request Sheet */}
      <Sheet open={requestLeaveOpen} onClose={() => setRequestLeaveOpen(false)} title="Apply for Leave">
        <form onSubmit={handleLeaveSubmit} className="space-y-4">
          <Field label="Leave type">
            <select value={leaveType} onChange={(e) => setLeaveType(e.target.value)} className="field">
              <option value="Annual">Annual Leave</option>
              <option value="Sick">Sick Leave</option>
              <option value="Maternity">Maternity Leave</option>
              <option value="Paternity">Paternity Leave</option>
              <option value="Compassionate">Compassionate Leave</option>
              <option value="Unpaid">Unpaid Leave</option>
            </select>
          </Field>

          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="From date">
              <input type="date" required value={from} onChange={(e) => setFrom(e.target.value)} className="field" />
            </Field>
            <Field label="To date">
              <input type="date" required value={to} onChange={(e) => setTo(e.target.value)} className="field" />
            </Field>
          </div>

          <Field label="Reason / Cover handover note (optional)">
            <textarea
              value={note}
              onChange={(e) => setNote(e.target.value)}
              placeholder="e.g. Taking annual leave; John will cover pending orders."
              className="field h-24"
            />
          </Field>

          {leaveError && <ErrorNote>{leaveError}</ErrorNote>}

          <SheetFooter>
          <button type="submit" disabled={leaveBusy} className="btn btn-primary">
            {leaveBusy ? 'Submitting...' : 'Submit Leave Application'}
          </button>
          </SheetFooter>
        </form>
      </Sheet>

      {/* Payslip Detail Sheet */}
      <Sheet open={Boolean(selectedPayslipId)} onClose={() => setSelectedPayslipId(null)} title="Payslip Detail" wide>
        {payslipDetail ? (
          <div className="doc space-y-5 text-xs">
            <div className="rounded-lg border border-line bg-paper p-4 text-center">
              <b className="text-base font-bold text-ink block">{emp.name}</b>
              <span className="text-muted">{emp.jobTitle} · {monthName(payslipDetail.period)}</span>
            </div>

            <div className="space-y-3">
              <div className="rounded-lg border border-line bg-white p-4">
                <span className="text-xs font-bold uppercase text-faint tracking-wider mb-2 block">Earnings</span>
                <div className="space-y-2">
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="text-muted min-w-0 flex-1">Basic Salary:</span>
                    <span className="num shrink-0 text-right font-semibold whitespace-nowrap">{ksh(payslipDetail.slip?.basic ?? 0)}</span>
                  </div>
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="text-muted min-w-0 flex-1">Taxable Allowances:</span>
                    <span className="num shrink-0 text-right font-semibold whitespace-nowrap">{ksh(payslipDetail.slip?.taxableAllowances ?? 0)}</span>
                  </div>
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="text-muted min-w-0 flex-1">Non-Taxable Allowances:</span>
                    <span className="num shrink-0 text-right font-semibold whitespace-nowrap">{ksh(payslipDetail.slip?.nonTaxableAllowances ?? 0)}</span>
                  </div>
                  <div className="flex items-baseline justify-between gap-3 border-t border-line pt-2 font-bold text-ink">
                    <span className="min-w-0 flex-1">Gross Earnings:</span>
                    <span className="num shrink-0 text-right text-brand whitespace-nowrap">{ksh(payslipDetail.slip?.gross ?? 0)}</span>
                  </div>
                </div>
              </div>

              <div className="rounded-lg border border-line bg-white p-4">
                <span className="text-xs font-bold uppercase text-faint tracking-wider mb-2 block">Deductions</span>
                <div className="space-y-2">
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="text-muted min-w-0 flex-1">PAYE Income Tax:</span>
                    <span className="num shrink-0 text-right font-semibold text-bad whitespace-nowrap">{ksh(payslipDetail.slip?.paye ?? 0)}</span>
                  </div>
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="text-muted min-w-0 flex-1">SHIF (SHA 2.75%):</span>
                    <span className="num shrink-0 text-right font-semibold text-bad whitespace-nowrap">{ksh(payslipDetail.slip?.shif ?? 0)}</span>
                  </div>
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="text-muted min-w-0 flex-1">Housing Levy (AHL 1.5%):</span>
                    <span className="num shrink-0 text-right font-semibold text-bad whitespace-nowrap">{ksh(payslipDetail.slip?.housingLevyEmployee ?? 0)}</span>
                  </div>
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="text-muted min-w-0 flex-1">NSSF Pension:</span>
                    <span className="num shrink-0 text-right font-semibold text-bad whitespace-nowrap">{ksh(payslipDetail.slip?.nssfEmployee ?? 0)}</span>
                  </div>
                  {parseLines(payslipDetail.slip).filter((l) => l.amount > 0).map((l, i) => (
                    <div key={i} className="flex items-baseline justify-between gap-3">
                      <span className="text-muted min-w-0 flex-1">
                        {l.name}{l.payTo ? ` (${l.payTo})` : ''}:
                        {l.wanted != null && <span className="block text-xs text-warn">Cut from {ksh(l.wanted)}: you always keep a third of your pay</span>}
                      </span>
                      <span className="num shrink-0 text-right font-semibold text-bad whitespace-nowrap">{ksh(l.amount)}</span>
                    </div>
                  ))}
                  <div className="flex items-baseline justify-between gap-3 border-t border-line pt-2 font-bold text-ink">
                    <span className="min-w-0 flex-1">Total Deductions:</span>
                    <span className="num shrink-0 text-right text-bad whitespace-nowrap">
                      {ksh(
                        (payslipDetail.slip?.paye ?? 0) +
                        (payslipDetail.slip?.shif ?? 0) +
                        (payslipDetail.slip?.housingLevyEmployee ?? 0) +
                        (payslipDetail.slip?.nssfEmployee ?? 0) +
                        (payslipDetail.slip?.otherDeductions ?? 0) +
                        (payslipDetail.slip?.pension ?? 0)
                      )}
                    </span>
                  </div>
                </div>
              </div>

              <div className="rounded-lg border border-good/20 bg-good/5 p-4 flex justify-between items-center gap-3">
                <div className="min-w-0 flex-1">
                  <span className="text-xs font-bold uppercase text-good tracking-wider block">Net Pay to Account</span>
                  <span className="text-xs text-muted">Paid via {emp.payMethod}</span>
                </div>
                <span className="num shrink-0 font-display text-2xl font-semibold text-good whitespace-nowrap">
                  {ksh(payslipDetail.slip?.net ?? 0)}
                </span>
              </div>
            </div>

            <button
              type="button"
              onClick={() => printAs('Kabuor document')}
              className="btn btn-ghost w-full py-2.5 text-xs font-semibold"
            >
              <Printer className="h-4 w-4" /> Print Payslip Slip
            </button>
          </div>
        ) : (
          <Loading />
        )}
      </Sheet>
    </div>
  );
}
