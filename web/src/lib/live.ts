// Live sync: one stream per open tab from /api/live (Server-Sent Events, read with fetch so the sign-in token goes in
// a header, not the address). When someone else saves anything in this business, the cached answers are dropped and
// every screen on show asks the API again (useApi listens for "kerp-sync"), so the module workspaces never disagree.
// Saves made from this tab are skipped: the tab already refreshed after its own change.
import { useEffect, useState } from 'react';
import { CLIENT_ID, clearApiCache, getSession } from './api';

export type LiveChange = { entity: string; action: 'Added' | 'Modified' | 'Deleted'; id: string; module: string; what: string };
export type LiveEvent = { company: string; at: string; actor: string; userId: string | null; client: string | null; modules: string[]; changes: LiveChange[]; total: number };
export type LiveStatus = 'off' | 'connecting' | 'live' | 'retrying';

export { CLIENT_ID };

let status: LiveStatus = 'off';
const statusListeners = new Set<(s: LiveStatus) => void>();
const setStatus = (s: LiveStatus) => { status = s; statusListeners.forEach((f) => f(s)); };

let abort: AbortController | null = null;
let token: string | null = null;
let retry = 0;
let syncTimer: ReturnType<typeof setTimeout> | null = null;

/** Many saves in a burst (an invoice with its lines, postings and stock) become one refresh. */
function scheduleSync(modules: string[]) {
  clearApiCache();
  if (syncTimer) clearTimeout(syncTimer);
  syncTimer = setTimeout(() => window.dispatchEvent(new CustomEvent('kerp-sync', { detail: { modules } })), 350);
}

function handle(e: LiveEvent) {
  if (e.client === CLIENT_ID) return;
  scheduleSync(e.modules);
  window.dispatchEvent(new CustomEvent<LiveEvent>('kerp-live', { detail: e }));
}

async function run(t: string, signal: AbortSignal) {
  setStatus(retry ? 'retrying' : 'connecting');
  const res = await fetch('/api/live', { headers: { Authorization: `Bearer ${t}`, 'X-Client': CLIENT_ID, Accept: 'text/event-stream' }, signal, cache: 'no-store' });
  if (res.status === 401 || res.status === 403 || res.status === 402) { setStatus('off'); return 'stop'; }
  if (!res.ok || !res.body || !res.headers.get('content-type')?.includes('text/event-stream')) throw new Error(`live ${res.status}`);
  // Back on line after a drop: anything saved meanwhile was missed, so refresh once.
  if (retry > 0) scheduleSync([]);
  retry = 0;
  setStatus('live');
  const reader = res.body.pipeThrough(new TextDecoderStream()).getReader();
  let buf = '';
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    buf += value;
    let i;
    while ((i = buf.indexOf('\n\n')) >= 0) {
      const block = buf.slice(0, i);
      buf = buf.slice(i + 2);
      const data = block.split('\n').filter((l) => l.startsWith('data: ')).map((l) => l.slice(6)).join('\n');
      if (!data) continue;
      try { handle(JSON.parse(data) as LiveEvent); } catch { /* a malformed line: skip it */ }
    }
  }
  return 'closed';
}

async function loop(t: string, ctrl: AbortController) {
  while (!ctrl.signal.aborted && token === t) {
    try {
      if ((await run(t, ctrl.signal)) === 'stop') return;
    } catch {
      if (ctrl.signal.aborted) return;
    }
    if (ctrl.signal.aborted || token !== t) return;
    retry++;
    setStatus('retrying');
    // 1 s, 2 s, 4 s … up to 30 s, with a little jitter so a server restart isn't met by every tab at once.
    await new Promise((r) => setTimeout(r, Math.min(30_000, 1000 * 2 ** Math.min(retry - 1, 5)) + Math.random() * 500));
  }
}

/** Opens (or closes) the stream for whoever is signed in now. Safe to call repeatedly. */
export function connectLive() {
  const s = getSession();
  const t = s && !s.token.startsWith('demo-') && !s.company.awaitingFirstPayment && s.user.role !== 'Employee' ? s.token : null;
  if (t === token) return;
  abort?.abort();
  abort = null;
  token = t;
  retry = 0;
  if (!t) { setStatus('off'); return; }
  const ctrl = new AbortController();
  abort = ctrl;
  void loop(t, ctrl);
}

/** Keeps the stream open while mounted (the signed-in frame), following sign-in, sign-out and the tab coming back. */
export function useLiveConnection() {
  useEffect(() => {
    connectLive();
    const onSession = () => connectLive();
    const onVisible = () => {
      if (document.visibilityState !== 'visible') return;
      // A laptop waking up may hold a dead line that hasn't noticed yet: reconnect if it isn't live.
      if (status !== 'live') { token = null; connectLive(); }
    };
    window.addEventListener('kerp-session', onSession);
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      window.removeEventListener('kerp-session', onSession);
      document.removeEventListener('visibilitychange', onVisible);
      abort?.abort();
      abort = null;
      token = null;
      setStatus('off');
    };
  }, []);
}

export function useLiveStatus() {
  const [s, set] = useState(status);
  useEffect(() => {
    statusListeners.add(set);
    set(status);
    return () => { statusListeners.delete(set); };
  }, []);
  return s;
}

/** Calls `on` for each save someone else made in this business while mounted. */
export function useLiveEvents(on: (e: LiveEvent) => void) {
  useEffect(() => {
    const f = (ev: Event) => on((ev as CustomEvent<LiveEvent>).detail);
    window.addEventListener('kerp-live', f);
    return () => window.removeEventListener('kerp-live', f);
  }, [on]);
}
