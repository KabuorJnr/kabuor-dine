// Msaidizi: ask in English, Kiswahili or Sheng. Answers come from the business's own data; anything it wants
// to do comes back as a proposal you approve (shown right under the answer).
import { useEffect, useRef, useState } from 'react';
import { Loader2, Send, Sparkles } from 'lucide-react';
import { api } from '../lib/api';
import { cn } from '../lib/format';
import { useLang } from '../lib/i18n';
import { Sheet } from './ui';
import Proposals from './Proposals';

type Turn = { role: 'user' | 'assistant'; text: string };

const STARTERS = {
  en: ['Who hasn’t paid this month?', 'How much came in this week?', 'Match my unmatched M-Pesa payments', 'Remind the most overdue customer'],
  sw: ['Nani hajalipa mwezi huu?', 'Pesa ngapi imeingia wiki hii?', 'Linganisha malipo ya M-Pesa', 'Mkumbushe mteja aliyechelewa zaidi'],
};

export default function AssistantPanel({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { lang, t } = useLang();
  const [turns, setTurns] = useState<Turn[]>([]);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [proposalTick, setProposalTick] = useState(0);
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => end.current?.scrollIntoView({ behavior: 'smooth' }), [turns, busy]);

  const ask = async (message: string) => {
    if (!message.trim() || busy) return;
    setError('');
    setTurns((ts) => [...ts, { role: 'user', text: message }]);
    setText('');
    setBusy(true);
    try {
      const r = await api<{ answer: string; proposals: unknown[] }>('/assistant/ask', { body: { message, history: turns.slice(-10) } });
      setTurns((ts) => [...ts, { role: 'assistant', text: r.answer }]);
      if (r.proposals.length) setProposalTick((n) => n + 1);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Sheet open={open} onClose={onClose} title={t('Assistant')}>
      <div className="flex min-h-full flex-col">
        <div className="flex-1 space-y-3">
          {turns.length === 0 && (
            <div className="rounded-lg bg-ink p-5 text-white">
              <Sparkles className="h-6 w-6 text-gold" />
              <p className="mt-3 font-display text-xl font-semibold">{lang === 'sw' ? 'Habari! Nikusaidie na nini leo?' : 'Hi! What can I help with today?'}</p>
              <p className="mt-1 text-sm text-white/70">{lang === 'sw' ? 'Uliza kwa Kiswahili, Kiingereza au Sheng. Sitabadilisha kitu bila ruhusa yako.' : 'Ask in English, Kiswahili or Sheng. I never change anything without your OK.'}</p>
            </div>
          )}
          {turns.length === 0 && (
            <div className="flex flex-wrap gap-2">
              {STARTERS[lang].map((s) => (
                <button key={s} onClick={() => ask(s)} className="rounded-full bg-white px-3.5 py-2 text-left text-sm font-medium ring-1 ring-line hover:ring-brand">
                  {s}
                </button>
              ))}
            </div>
          )}
          {turns.map((tn, i) => (
            <div key={i} className={cn('max-w-[85%] rounded-lg px-4 py-2.5 text-sm whitespace-pre-line', tn.role === 'user' ? 'ml-auto bg-brand text-white' : 'bg-white ring-1 ring-line')}>
              {tn.text}
            </div>
          ))}
          {busy && (
            <div className="flex items-center gap-2 text-sm text-muted">
              <Loader2 className="h-4 w-4 animate-spin" /> {lang === 'sw' ? 'Ninaangalia…' : 'Looking into it…'}
            </div>
          )}
          {error && <p className="rounded-md bg-bad-soft px-3 py-2 text-sm text-bad">{error}</p>}
          <div key={proposalTick}>
            <Proposals compact />
          </div>
          <div ref={end} />
        </div>
        <form onSubmit={(e) => (e.preventDefault(), ask(text))} className="sticky bottom-0 mt-4 flex gap-2 bg-paper pt-2">
          <input className="field" value={text} onChange={(e) => setText(e.target.value)} placeholder={lang === 'sw' ? 'Andika swali lako…' : 'Ask anything about your business…'} aria-label="Message" />
          <button className="btn btn-primary px-3.5" disabled={busy || !text.trim()} aria-label="Send">
            <Send className="h-4 w-4" />
          </button>
        </form>
      </div>
    </Sheet>
  );
}
