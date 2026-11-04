// Leave: requests waiting for a decision first, then everything else. Days are counted Monday to Friday.
import { useState } from 'react';
import { Check, Loader2, Plus, X } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { day } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, Sheet, Status, SheetFooter } from '../components/ui';

const TYPES = ['Annual', 'Sick', 'Maternity', 'Paternity', 'Compassionate', 'Unpaid'];

function workingDays(from: string, to: string) {
  if (!from || !to || to < from) return 0;
  let n = 0;
  for (let d = new Date(`${from}T00:00:00`); d <= new Date(`${to}T00:00:00`); d.setDate(d.getDate() + 1)) if (d.getDay() !== 0 && d.getDay() !== 6) n++;
  return n;
}

function RequestLeave({ open, onClose, onDone }: { open: boolean; onClose: () => void; onDone: () => void }) {
  const people = useApi<any>(open ? '/employees' : null);
  const [f, setF] = useState({ employeeId: '', type: 'Annual', from: '', to: '', note: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const days = workingDays(f.from, f.to);
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      await api('/leave', { body: f });
      onDone();
      onClose();
      setF({ employeeId: '', type: 'Annual', from: '', to: '', note: '' });
      setState({ busy: false, error: '' });
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  return (
    <Sheet open={open} onClose={onClose} title="Request leave" description="Counted in working days, against the annual balance.">
      <form onSubmit={submit} className="space-y-4">
        <Field label="Who">
          <select className="field" required value={f.employeeId} onChange={(e) => setF({ ...f, employeeId: e.target.value })}>
            <option value="">Choose an employee</option>
            {(people.data?.items ?? []).map((p: any) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </Field>
        <div>
          <p className="mb-1.5 text-sm font-semibold">Kind of leave</p>
          <div className="flex flex-wrap gap-1.5">
            {TYPES.map((t) => (
              <button key={t} type="button" aria-pressed={f.type === t} onClick={() => setF({ ...f, type: t })} className={f.type === t ? 'rounded-full bg-ink px-3.5 py-1.5 text-sm font-semibold text-white' : 'rounded-full bg-white px-3.5 py-1.5 text-sm font-semibold ring-1 ring-line'}>
                {t}
              </button>
            ))}
          </div>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label="From">
            <input className="field" type="date" required value={f.from} onChange={(e) => setF({ ...f, from: e.target.value, to: f.to || e.target.value })} />
          </Field>
          <Field label="To (last day)">
            <input className="field" type="date" required value={f.to} onChange={(e) => setF({ ...f, to: e.target.value })} />
          </Field>
        </div>
        {days > 0 && <p className="text-sm font-semibold">{days} working day{days === 1 ? '' : 's'}</p>}
        <Field label="Note">
          <input className="field" value={f.note} onChange={(e) => setF({ ...f, note: e.target.value })} placeholder="Optional" />
        </Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter>
        <button className="btn btn-primary" disabled={state.busy}>
          {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Send request
        </button>
        </SheetFooter>
      </form>
    </Sheet>
  );
}

export default function Leave() {
  const [status, setStatus] = useState('pending');
  const [adding, setAdding] = useState(false);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const { data, error: loadError, reload } = useApi<any>(`/leave?status=${status}`);
  const decide = async (id: string, approve: boolean) => {
    setBusy(id);
    setError('');
    try {
      await api(`/leave/${id}/${approve ? 'approve' : 'reject'}`, { body: {} });
      reload();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy('');
    }
  };
  return (
    <>
      <PageHeader print
        title="Leave"
        subtitle="Counted in working days, Monday to Friday. Public holidays are yours to allow for."
        actions={
          <button className="btn btn-primary" onClick={() => setAdding(true)}>
            <Plus className="h-4 w-4" /> Request leave
          </button>
        }
      />
      <div className="mb-4">
        <Chips label="Filter leave" value={status} onChange={setStatus} options={[['pending', 'Waiting for you', data?.counts.pending], ['approved', 'Approved', data?.counts.approved], ['all', 'All', data?.counts.all]]} />
      </div>
      {(loadError || error) && <ErrorNote>{loadError || error}</ErrorNote>}
      {!data ? (
        <Loading />
      ) : data.items.length === 0 ? (
        <Empty title={status === 'pending' ? 'Nothing waiting' : 'No leave yet'}>{status === 'pending' ? 'Leave requests waiting for a decision show here.' : 'Approved leave shows here.'}</Empty>
      ) : (
        <ul className="card divide-y divide-line">
          {data.items.map((l: any) => (
            <li key={l.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
              <span className="min-w-0 flex-1">
                <b className="block">{l.employee}</b>
                <span className="text-sm text-muted">
                  {l.type} · {l.days} day{l.days === 1 ? '' : 's'} · {day(l.from)} – {day(l.to)}
                  {l.note && ` · “${l.note}”`}
                </span>
              </span>
              {l.status === 'Pending' ? (
                <span className="flex gap-2">
                  <button className="btn btn-ghost py-2" disabled={busy === l.id} onClick={() => decide(l.id, false)}>
                    <X className="h-4 w-4" /> Decline
                  </button>
                  <button className="btn btn-primary py-2" disabled={busy === l.id} onClick={() => decide(l.id, true)}>
                    {busy === l.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />} Approve
                  </button>
                </span>
              ) : (
                <Status tone={l.status === 'Approved' ? 'good' : 'plain'}>
                  {l.status}
                  {l.decidedBy && ` by ${l.decidedBy}`}
                </Status>
              )}
            </li>
          ))}
        </ul>
      )}
      <RequestLeave open={adding} onClose={() => setAdding(false)} onDone={reload} />
    </>
  );
}
