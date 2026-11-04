// Building an agent (like Oracle's AI Agent Studio or SAP's Joule Studio, sized for a Kenyan business owner).
// The owner is deciding "what should this agent do, and what may it touch?", so: start from a template or a blank
// page, say the job in plain words, tick the skills (what it can look at, and what it can only *propose*), and
// choose how often. The sheet says up front that it can't change anything without a person.
import { useEffect, useState } from 'react';
import { Cpu, Eye, Hand, Loader2, Send, ShieldCheck, Sparkles, Trash2 } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { cn, ksh } from '../lib/format';
import { Chips, ErrorNote, Field, Loading, Sheet, confirmDialog } from './ui';

type Skill = { name: string; description: string; policy: string; acts: boolean };
type Studio = { skills: Skill[]; templates: { name: string; every: string; skills: string[]; instructions: string }[]; cadence: { id: string; text: string }[]; aiReady: boolean };
export type CustomDraft = {
  key?: string;
  name: string;
  instructions: string;
  skills: string[];
  every: string;
  model?: string;
  maxThinkingSteps?: number;
  autonomousLimit?: number;
  maskPii?: boolean;
  groundWithRag?: boolean;
};

const plain = (s: string) => s.replace('propose_', '').replace(/_/g, ' ');


const ENTERPRISE_TEMPLATES = [
  {
    name: 'Dispute & billing discrepancy resolution',
    every: 'daily',
    skills: ['list_invoices', 'unmatched_payments', 'find_customers', 'propose_reminder'],
    instructions: 'Intercept billing disputes and scan invoices against agreed quote terms and M-Pesa receipts. Calculate exact overcharges, draft credit note proposals with line-item justifications, and notify customers.',
  },
  {
    name: 'Price indexation & contract audit',
    every: 'weekly',
    skills: ['list_invoices', 'sales_report'],
    instructions: 'Audit sales conversions line-by-line to verify contractual pricing and indexation terms. Flag any unauthorized discount or price alteration exceeding 5% for manager review.',
  },
  {
    name: 'Dead stock liquidation',
    every: 'weekly',
    skills: ['stock_levels', 'find_customers', 'sales_report'],
    instructions: 'Identify inventory sitting on shelves with zero sales over 45 days. Formulate targeted promotional bundles and draft outreach for customers who bought related items.',
  },
];

export function StudioSheet({ open, onClose, editing, onSaved }: { open: boolean; onClose: () => void; editing: CustomDraft | null; onSaved: () => void }) {
  const studio = useApi<Studio>(open ? '/agents/studio' : null);
  const [f, setF] = useState<CustomDraft>({
    name: '', instructions: '', skills: [], every: 'daily',
    model: 'claude-3-5-sonnet', maxThinkingSteps: 5, autonomousLimit: 2500, maskPii: true, groundWithRag: true
  });
  const [state, setState] = useState({ busy: false, error: '' });
  useEffect(() => {
    if (open) (setF(editing ?? {
    name: '', instructions: '', skills: [], every: 'daily',
    model: 'claude-3-5-sonnet', maxThinkingSteps: 5, autonomousLimit: 2500, maskPii: true, groundWithRag: true
  }), setState({ busy: false, error: '' }));
  }, [open, editing]);

  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      const body = {
        name: f.name, instructions: f.instructions, skills: f.skills, every: f.every,
        model: f.model, maxThinkingSteps: f.maxThinkingSteps, autonomousLimit: f.autonomousLimit,
        maskPii: f.maskPii, groundWithRag: f.groundWithRag
      };
      await (f.key ? api(`/agents/custom/${encodeURIComponent(f.key)}`, { method: 'PUT', body }) : api('/agents/custom', { body }));
      onSaved();
      onClose();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const remove = async () => {
    if (!f.key || !(await confirmDialog({ title: `Delete ${f.name}?`, body: 'Its history stays.', confirm: 'Delete', danger: true }))) return;
    await api(`/agents/custom/${encodeURIComponent(f.key)}`, { method: 'DELETE' });
    onSaved();
    onClose();
  };
  const toggle = (name: string) => setF({ ...f, skills: f.skills.includes(name) ? f.skills.filter((s) => s !== name) : [...f.skills, name] });

  return (
    <Sheet open={open} onClose={onClose} title={f.key ? `Edit ${f.name}` : 'Build an agent'}>
      {!studio.data ? (
        studio.error ? <ErrorNote>{studio.error}</ErrorNote> : <Loading />
      ) : (
        <div className="space-y-5">
          {!studio.data.aiReady && <p className="rounded-md bg-warn-soft px-3 py-2 text-sm text-warn">Agents you build need the AI switched on for this server (ANTHROPIC__APIKEY). You can build it now; it runs once that's set.</p>}
          {!f.key && (
            <div>
              <p className="mb-1.5 text-sm font-semibold">Start from</p>
              <div className="grid gap-2 sm:grid-cols-2">
                {[...ENTERPRISE_TEMPLATES, ...studio.data.templates].map((t) => (
                    <button key={t.name} type="button" onClick={() => setF((prev) => ({ ...prev, name: t.name, instructions: t.instructions, skills: t.skills, every: t.every }))} className={cn('rounded-md p-3 text-left text-sm ring-1 transition', f.name === t.name ? 'bg-brand-soft ring-2 ring-brand' : 'bg-white ring-line hover:ring-faint')}>
                      <b className="block text-xs font-bold text-ink">{t.name}</b>
                      <span className="line-clamp-2 text-2xs text-muted mt-0.5">{t.instructions}</span>
                    </button>
                  ))}
              </div>
            </div>
          )}
          <Field label="Name"><input className="field" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. Big debtors watch" maxLength={60} /></Field>
          <Field label="Its job, in your words" hint="What to look at, what counts as worth telling you, and what to propose. Be specific about amounts and days.">
            <textarea className="field min-h-28" value={f.instructions} onChange={(e) => setF({ ...f, instructions: e.target.value })} maxLength={2000}
              placeholder="e.g. Find customers who owe more than KSh 50,000 or are over 30 days late and propose a polite reminder for each." />
          </Field>
          <div>
            <p className="mb-1.5 text-sm font-semibold">Skills</p>
            <div className="grid gap-4 sm:grid-cols-2">
              {[false, true].map((acts) => (
                <div key={String(acts)}>
                  <p className="mb-1 flex items-center gap-1.5 text-xs font-semibold tracking-wide text-faint uppercase">{acts ? <><Hand className="h-3.5 w-3.5" /> Can propose (you approve)</> : <><Eye className="h-3.5 w-3.5" /> Can look at</>}</p>
                  <ul className="space-y-1">
                    {studio.data!.skills.filter((s) => s.acts === acts).map((s) => (
                      <li key={s.name}>
                        <label className="flex cursor-pointer items-start gap-2 text-sm" title={s.description}>
                          <input type="checkbox" className="mt-0.5 h-4 w-4 accent-[var(--color-brand)]" checked={f.skills.includes(s.name)} onChange={() => toggle(s.name)} />
                          <span className="capitalize">{plain(s.name)}</span>
                        </label>
                      </li>
                    ))}
                  </ul>
                </div>
              ))}
            </div>
          </div>

          {/* REQ-01: Vendor Diversity & Foundation Model Selector */}
          <div>
            <p className="mb-1.5 flex items-center gap-1.5 text-xs font-semibold tracking-wide text-faint uppercase">
              <Cpu className="h-3.5 w-3.5" /> Generative AI Model (Vendor Diversity)
            </p>
            <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
              {[
                ['claude-3-5-sonnet', 'Claude 3.5 Sonnet', 'High Reasoning'],
                ['gpt-4o', 'GPT-4o Enterprise', 'Multipurpose'],
                ['gemini-2-5-flash', 'Gemini 2.5 Flash', 'Tier 1 (<150ms)'],
                ['deepseek-r1', 'DeepSeek R1', 'Math & Audit'],
              ].map(([id, label, hint]) => (
                <button
                  key={id}
                  type="button"
                  onClick={() => setF({ ...f, model: id })}
                  className={cn(
                    'rounded-lg p-2.5 text-left border text-xs transition',
                    (f.model || 'claude-3-5-sonnet') === id
                      ? 'border-indigo bg-indigo/5 ring-1 ring-indigo text-indigo font-semibold'
                      : 'border-line bg-white hover:border-faint text-ink'
                  )}
                >
                  <span className="block font-semibold truncate">{label}</span>
                  <span className="block text-2xs text-muted">{hint}</span>
                </button>
              ))}
            </div>
          </div>

          {/* REQ-14 & REQ-20: Reasoning Steps & Autonomous Limit Constraints */}
          <div className="grid gap-4 sm:grid-cols-2">
            <div>
              <p className="mb-1 text-xs font-semibold tracking-wide text-faint uppercase">
                Max Thinking Steps (Figure 7)
              </p>
              <Chips
                label="Max Thinking Steps"
                value={String(f.maxThinkingSteps || 5)}
                onChange={(v) => setF({ ...f, maxThinkingSteps: Number(v) })}
                options={[
                  ['3', '3 steps'],
                  ['5', '5 steps'],
                  ['8', '8 steps'],
                  ['12', '12 steps'],
                ]}
              />
            </div>
            <div>
              <p className="mb-1 text-xs font-semibold tracking-wide text-faint uppercase">
                Autonomous Limit (KSh)
              </p>
              <Chips
                label="Autonomous Limit"
                value={String(f.autonomousLimit ?? 2500)}
                onChange={(v) => setF({ ...f, autonomousLimit: Number(v) })}
                options={[
                  ['0', 'Ask every time'],
                  ['2500', '≤ KSh 2,500'],
                  ['5000', '≤ KSh 5,000'],
                  ['10000', '≤ KSh 10,000'],
                ]}
              />
            </div>
          </div>

          {/* REQ-06 & REQ-07: Orchestration Guardrails Pipeline (Figure 6) */}
          <div className="rounded-lg border border-line bg-paper p-3.5">
            <p className="mb-2 flex items-center gap-1.5 text-xs font-semibold tracking-wide text-faint uppercase">
              <ShieldCheck className="h-3.5 w-3.5 text-good" /> Orchestration Safeguards Pipeline
            </p>
            <div className="grid gap-2 sm:grid-cols-2">
              <label className="flex items-center gap-2 text-xs font-semibold text-ink cursor-pointer">
                <input
                  type="checkbox"
                  className="h-4 w-4 rounded accent-indigo"
                  checked={f.maskPii !== false}
                  onChange={(e) => setF({ ...f, maskPii: e.target.checked })}
                />
                <span>PII & Financial Data Masking (GDPR)</span>
              </label>
              <label className="flex items-center gap-2 text-xs font-semibold text-ink cursor-pointer">
                <input
                  type="checkbox"
                  className="h-4 w-4 rounded accent-indigo"
                  checked={f.groundWithRag !== false}
                  onChange={(e) => setF({ ...f, groundWithRag: e.target.checked })}
                />
                <span>ERP Master Data Grounding (RAG)</span>
              </label>
            </div>
          </div>
          <div>
            <p className="mb-1.5 text-sm font-semibold">How often</p>
            <Chips label="How often" value={f.every} onChange={(v) => setF({ ...f, every: v })} options={studio.data.cadence.map((c) => [c.id, c.text] as [string, string])} />
          </div>
          <p className="rounded-md bg-paper px-3 py-2 text-xs text-muted">It works with the rights of whoever switches it on, only with the skills ticked here. It can’t change anything: what it finds shows up under <b>To review</b>, and anything it proposes waits for a person. Changing its job switches it off until you switch it on again.</p>
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
          <div className="flex gap-2">
            <button className="btn btn-primary flex-1 py-3" disabled={state.busy || f.name.trim().length < 3 || f.instructions.trim().length < 20 || f.skills.length === 0} onClick={save}>
              {state.busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Sparkles className="h-4 w-4" />} {f.key ? 'Save' : 'Create agent'}
            </button>
            {f.key && <button className="btn btn-ghost text-bad" onClick={remove}><Trash2 className="h-4 w-4" /> Delete</button>}
          </div>
        </div>
      )}
    </Sheet>
  );
}

/** Handing the order taking agent a customer's message: pick who it's from, paste, and get a draft quote. */
export function OrderIntake({ onDone }: { onDone: () => void }) {
  const [q, setQ] = useState('');
  const [customer, setCustomer] = useState<{ id: string; name: string } | null>(null);
  const found = useApi<{ page: { items: { id: string; name: string; phone: string }[] } }>(!customer && q.trim().length > 1 ? `/customers?q=${encodeURIComponent(q)}&take=5` : null);
  const [text, setText] = useState('');
  const [state, setState] = useState<{ busy: boolean; error: string; result: any }>({ busy: false, error: '', result: null });

  const send = async () => {
    setState({ busy: true, error: '', result: null });
    try {
      const r = await api<any>('/agents/orders/intake', { body: { text, customerId: customer?.id } });
      setState({ busy: false, error: '', result: r });
      setText('');
      onDone();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message, result: null });
    }
  };

  return (
    <div className="mt-4 space-y-2 rounded-md bg-paper p-3">
      <p className="text-sm font-semibold">Hand it an order</p>
      {customer ? (
        <p className="flex items-center justify-between rounded-md bg-white px-3 py-2 text-sm ring-1 ring-line">
          <b>{customer.name}</b> <button className="text-xs font-semibold text-brand" onClick={() => setCustomer(null)}>Change</button>
        </p>
      ) : (
        <div className="relative">
          <input className="field" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Who is it from? Name or phone" aria-label="Customer" />
          {found.data?.page.items.length ? (
            <ul className="absolute z-10 mt-1 w-full overflow-hidden rounded-md bg-white shadow-lg ring-1 ring-line">
              {found.data.page.items.map((c) => (
                <li key={c.id}><button className="w-full px-3 py-2 text-left text-sm hover:bg-paper" onClick={() => (setCustomer({ id: c.id, name: c.name }), setQ(''))}>{c.name} <span className="text-muted">{c.phone}</span></button></li>
              ))}
            </ul>
          ) : null}
        </div>
      )}
      <textarea className="field min-h-20" value={text} onChange={(e) => setText(e.target.value)} placeholder="Paste their WhatsApp, SMS or email, e.g. “Habari, naomba 2 crates soda na 5 milk”" maxLength={4000} />
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      {state.result && (
        <p className="rounded-md bg-good-soft px-3 py-2 text-sm text-good">
          {state.result.quote ? <>Drafted {state.result.quote.number} for {ksh(state.result.quote.total)}. It’s under To review.</> : <>Couldn’t match any product; it’s under To review.</>}
          {state.result.unmatched?.length > 0 && <span className="block text-warn">Not matched: {state.result.unmatched.join('; ')}</span>}
        </p>
      )}
      <button className="btn btn-primary" disabled={state.busy || !customer || text.trim().length < 3} onClick={send}>
        {state.busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />} Draft the quote
      </button>
    </div>
  );
}
