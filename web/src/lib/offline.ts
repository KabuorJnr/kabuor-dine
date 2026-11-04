// The till keeps selling when the internet drops. The last catalogue is kept on the device, sales that can't
// reach the server wait in a queue (each with its own key, so a sale sent twice is kept once), and the queue
// sends itself when the connection is back. Only network failures queue: a real refusal (out of stock, bad
// M-Pesa code) is shown to the cashier like any error.
import { api, ApiError } from './api';

const CATALOGUE = 'kerp-pos-catalogue';
const QUEUE = 'kerp-pos-queue';

export type Queued = { clientRef: string; body: any; at: string; total: number; error?: string };

const read = <T,>(key: string, fallback: T): T => {
  try {
    return JSON.parse(localStorage.getItem(key) ?? 'null') ?? fallback;
  } catch {
    return fallback;
  }
};
const write = (key: string, value: unknown) => {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {}
  window.dispatchEvent(new Event('kerp-pos-queue'));
};

/** No answer at all, or the host is down: worth keeping the sale and trying later. */
export const unreachable = (e: unknown) => !(e instanceof ApiError) || e.status >= 502;

export const saveCatalogue = (data: unknown) => write(CATALOGUE, { data, at: new Date().toISOString() });
export const lastCatalogue = () => read<{ data: any; at: string } | null>(CATALOGUE, null);

export const queued = () => read<Queued[]>(QUEUE, []);
export const newRef = () => `${Date.now().toString(36)}-${crypto.getRandomValues(new Uint32Array(1))[0].toString(36)}`;

export function enqueue(q: Queued) {
  write(QUEUE, [...queued(), q]);
}

let syncing = false;
/** Sends what's waiting, oldest first. Stops at the first network failure; a refused sale is kept with its reason. */
export async function sync(): Promise<{ sent: number; left: number }> {
  if (syncing) return { sent: 0, left: queued().length };
  syncing = true;
  let sent = 0;
  try {
    for (const q of queued()) {
      if (q.error) continue;
      try {
        await api('/pos/checkout', { body: { ...q.body, clientRef: q.clientRef, soldAt: q.at } });
        write(QUEUE, queued().filter((x) => x.clientRef !== q.clientRef));
        sent++;
      } catch (e) {
        if (unreachable(e)) break;
        write(QUEUE, queued().map((x) => (x.clientRef === q.clientRef ? { ...x, error: (e as Error).message } : x)));
      }
    }
  } finally {
    syncing = false;
  }
  return { sent, left: queued().length };
}

/** Drop a refused sale after the cashier has dealt with it (e.g. recorded it by hand). */
export function discard(clientRef: string) {
  write(QUEUE, queued().filter((x) => x.clientRef !== clientRef));
}

/** Keeps the app itself loadable offline (the till page and its code). Production builds only. */
export function registerShell() {
  if (import.meta.env.PROD && 'serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(() => {});
}
