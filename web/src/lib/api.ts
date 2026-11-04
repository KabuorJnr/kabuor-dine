// Talking to the Kabuor ERP API. The sign-in token lives in localStorage; a 401 signs the person out.
import { useCallback, useEffect, useState } from 'react';

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

const KEY = 'kerp-session';

/** This tab's id, sent with every request (X-Client) so the live stream can tell this tab's own saves from everyone else's (live.ts). */
export const CLIENT_ID = (() => {
  try { return crypto.randomUUID().slice(0, 13); } catch { return Math.random().toString(36).slice(2, 15); }
})();

export type Session = {
  token: string;
  isLocal?: boolean;
  user: { id: string; name: string; email: string; role: Role; language: 'en' | 'sw'; employeeId?: string | null; mustChangePassword?: boolean; twoFactorEnabled?: boolean; isPlatformAdmin?: boolean; permissions?: Permission[] };
  company: { id: string; name: string; kraPin: string; vatRegistered: boolean; industry?: string; accountingLockDate?: string | null; awaitingFirstPayment?: boolean; ownerOversight?: boolean; product?: string };
};
export type Role = 'Owner' | 'Accountant' | 'Sales' | 'Storekeeper' | 'Viewer' | 'HR' | 'Employee' | 'Procurement' | 'Dispatcher' | 'Maintenance';

export type MockAccount = {
  id: string;
  name: string;
  email: string;
  role: Role;
  roleTitle: string;
  emoji: string;
  companyName: string;
  password?: string;
  session: Session;
};

// One-click test restaurant account for local development:
export const MOCK_ACCOUNTS: MockAccount[] = !import.meta.env.DEV ? [] : [
  {
    id: 'dine-chef',
    name: 'Chef Rooney',
    email: 'chef@kabuor.co.ke',
    role: 'Owner',
    roleTitle: 'Restaurant Owner & Chef',
    emoji: '🍽️',
    companyName: 'Ziwa Grill & Lounge',
    password: 'admin',
    session: {
      token: 'demo-dine-token',
      isLocal: false,
      user: {
        id: '22222222-2222-2222-2222-222222222222',
        name: 'Chef Rooney',
        email: 'chef@kabuor.co.ke',
        role: 'Owner',
        language: 'en',
        permissions: ['read', 'sales.view', 'sell', 'pos', 'crm', 'finance', 'reports', 'stock.view', 'stock', 'buy.view', 'buy', 'make', 'people', 'fleet.view', 'fleet', 'admin', 'verified.view', 'verified.share', 'dine', 'dine.manage'],
      },
      company: {
        id: '11111111-1111-1111-1111-111111111111',
        name: 'Ziwa Grill & Lounge',
        kraPin: 'P051234567Z',
        vatRegistered: true,
        industry: 'restaurant',
        product: 'dine',
      },
    },
  },
];

export const DEMO_OWNER: Session | undefined = MOCK_ACCOUNTS[0]?.session;
export const DEMO_SARAH: Session = {
  token: 'demo-sarah-token',
  isLocal: true,
  user: {
    id: 'usr-sarah',
    name: 'Sarah Wanjiku',
    email: 'sarah@kabuor.co.ke',
    role: 'Accountant',
    language: 'en',
    permissions: ['finance', 'sales.view'],
  },
  company: {
    id: 'comp-1',
    name: 'Kabuor Enterprises Ltd',
    kraPin: 'P051234567Z',
    vatRegistered: true,
    industry: 'distribution',
  },
};

export function getSession(): Session | null {
  try {
    if (import.meta.env.DEV && typeof window !== 'undefined') {
      const demo = new URLSearchParams(window.location.search).get('demo');
      if (demo === 'owner' && DEMO_OWNER) {
        localStorage.setItem(KEY, JSON.stringify(DEMO_OWNER));
        return DEMO_OWNER;
      }
      if (demo === 'sarah' || demo === 'finance') {
        localStorage.setItem(KEY, JSON.stringify(DEMO_SARAH));
        return DEMO_SARAH;
      }
    }
    const s = JSON.parse(localStorage.getItem(KEY) ?? 'null') as Session | null;
    if (!s || !s.token || !s.user || !s.company) return null;
    if (!Array.isArray(s.user.permissions)) s.user.permissions = [];
    // Saved before fleet had its own permissions: every role that had stock had fleet too (Auth.cs), so add them
    // rather than hide Fleet & dispatch until the next sign-in.
    const p = s.user.permissions;
    if (p.includes('stock.view') && !p.includes('fleet.view')) p.push('fleet.view');
    if (p.includes('stock') && !p.includes('fleet')) p.push('fleet');
    return s;
  } catch {
    return null;
  }
}
export function setSession(s: Session | null) {
  try {
    if (s) {
      if (!s.token.startsWith('clerk-')) {
        s.isLocal = true;
      }
      localStorage.setItem(KEY, JSON.stringify(s));
    } else {
      localStorage.removeItem(KEY);
    }
  } catch {}
  // Answers kept for the last person (another business, after switching stores) are never shown to the next.
  apiCache.clear();
  inflight.clear();
  window.dispatchEvent(new Event('kerp-session'));
}

const apiCache = new Map<string, { data: any; timestamp: number }>();
const inflight = new Map<string, Promise<unknown>>();
const FRESH_MS = 5_000;

export function clearApiCache(prefix?: string) {
  if (!prefix) {
    apiCache.clear();
  } else {
    for (const key of apiCache.keys()) {
      if (key.startsWith(prefix)) apiCache.delete(key);
    }
  }
}

export async function api<T = any>(path: string, opts: { method?: string; body?: unknown } = {}): Promise<T> {
  const s = getSession();
  const method = opts.method ?? (opts.body !== undefined ? 'POST' : 'GET');
  const res = await fetch(`/api${path}`, {
    method,
    headers: { 'X-Client': CLIENT_ID, ...(opts.body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...(s ? { Authorization: `Bearer ${s.token}` } : {}) },
    body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
  });
  if (res.status === 401 && s) setSession(null);
  if (res.status === 204) {
    if (method !== 'GET') clearApiCache();
    return undefined as T;
  }
  const data = await res.json().catch(() => ({}));
  // The account hasn't been paid for yet (or no longer is, on a stale page): show the payment screen.
  if (res.status === 402 && data.code === 'pay-to-start' && s && !s.company.awaitingFirstPayment) setSession({ ...s, company: { ...s.company, awaitingFirstPayment: true } });
  if (!res.ok) throw new ApiError(res.status, data.error ?? (res.status === 403 ? 'You don’t have access to this.' : 'Network problem. Check your connection.'));
  // A web page where data was expected: something between here and the API answered instead (an older deployment,
  // a proxy's page). Say so rather than hand the screen an empty object to draw.
  if (res.headers.get('content-type')?.includes('text/html')) throw new ApiError(502, 'The server sent back a page instead of data. Reload; if it keeps happening, the server may be mid-update.');
  if (method !== 'GET') {
    clearApiCache();
  }
  return data as T;
}

/** Downloads a file from the API (CSV exports) with the sign-in token, under the server's file name. */
export async function download(path: string, fallbackName: string) {
  const s = getSession();
  const res = await fetch(`/api${path}`, { headers: s ? { Authorization: `Bearer ${s.token}` } : {} });
  if (!res.ok) throw new ApiError(res.status, 'Couldn’t download that file.');
  const name = /filename="?([^";]+)"?/.exec(res.headers.get('content-disposition') ?? '')?.[1] ?? fallbackName;
  const url = URL.createObjectURL(await res.blob());
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** GETs `path` once even when several screens (or a prefetch) ask at the same time, and keeps the answer. */
function load<T>(path: string): Promise<T> {
  let p = inflight.get(path) as Promise<T> | undefined;
  if (!p) {
    p = api<T>(path)
      .then((data) => {
        if (inflight.get(path) === p) apiCache.set(path, { data, timestamp: Date.now() }); // not if signed out meanwhile
        return data;
      })
      .finally(() => {
        if (inflight.get(path) === p) inflight.delete(path);
      });
    inflight.set(path, p);
  }
  return p;
}

/** Starts loading what a screen is about to show, so it arrives together with the rest instead of after it. */
export function prefetch(...paths: string[]) {
  for (const path of paths) if (!apiCache.has(path)) load(path).catch(() => {});
}

/** Loads `path` and reloads when it changes. SWR caching renders instantaneously if cached; an answer only seconds
 * old (a prefetch, another panel) isn't asked for again. `reload()` forces a refetch. */
export function useApi<T = any>(path: string | null) {
  const cached = path ? apiCache.get(path) : undefined;
  // status: the HTTP status of a failed load (404, 403…), so a page can tell "not there" from "didn't load".
  const [state, setState] = useState<{ data: T | null; error: string | null; status: number | null; loading: boolean }>({
    data: cached ? (cached.data as T) : null,
    error: null,
    status: null,
    loading: Boolean(path) && !cached,
  });
  const [tick, setTick] = useState(0);

  useEffect(() => {
    if (!path) return;
    let live = true;
    const currentCached = apiCache.get(path);
    if (!currentCached) {
      setState((s) => ({ ...s, loading: true }));
    } else if (Date.now() - currentCached.timestamp < FRESH_MS) {
      setState({ data: currentCached.data as T, error: null, status: null, loading: false });
      return;
    }
    load<T>(path)
      .then((data) => {
        if (live) setState({ data, error: null, status: null, loading: false });
      })
      .catch((e: Error) => {
        if (live) setState((s) => ({ ...s, error: e.message, status: e instanceof ApiError ? e.status : null, loading: false }));
      });
    return () => {
      live = false;
    };
  }, [path, tick]);

  const reload = useCallback(() => {
    if (path) apiCache.delete(path);
    setTick((t) => t + 1);
  }, [path]);

  // Someone else saved something (live.ts): ask again in the background and swap the answer in, without a loading
  // state, so the screen updates in place instead of flashing.
  useEffect(() => {
    if (!path) return;
    let live = true;
    const on = () => {
      apiCache.delete(path);
      load<T>(path).then((data) => { if (live) setState({ data, error: null, status: null, loading: false }); }).catch(() => {});
    };
    window.addEventListener('kerp-sync', on);
    return () => { live = false; window.removeEventListener('kerp-sync', on); };
  }, [path]);

  return { ...state, reload };
}

export function useSession() {
  const [s, set] = useState(getSession);
  useEffect(() => {
    const on = () => set(getSession());
    window.addEventListener('kerp-session', on);
    window.addEventListener('storage', on);
    return () => {
      window.removeEventListener('kerp-session', on);
      window.removeEventListener('storage', on);
    };
  }, []);
  return s;
}

/** What each part of the app needs. The server decides who has which (Auth.cs, Policies) and sends the list with
 * the sign-in, so the menu, pages, tiles and buttons always agree with what the API will allow. */
export type Permission = 'read' | 'sales.view' | 'sell' | 'pos' | 'crm' | 'finance' | 'reports' | 'stock.view' | 'stock' | 'buy.view' | 'buy' | 'make' | 'people' | 'fleet.view' | 'fleet' | 'admin' | 'verified.view' | 'verified.share' | 'assets' | 'maintain.view' | 'maintain' | 'dine' | 'dine.manage';
/** The owner oversees (Settings → Business): sees every module, approves and signs off, but leaves the daily work to the team. */
export const oversees = (s: Session | null) => Boolean(s && s.user.role === 'Owner' && s.company.ownerOversight && !s.user.isPlatformAdmin);
/** The daily-work permissions an overseeing owner hands to the team: their buttons and forms disappear. Reading stays. */
const DOING: Permission[] = ['sell', 'pos', 'stock', 'buy', 'make', 'maintain', 'fleet', 'assets'];
export const can = (s: Session | null, p: Permission) => {
  if (oversees(s) && DOING.includes(p)) return false;
  return Boolean(s?.user?.isPlatformAdmin || s?.user?.role === 'Owner' || s?.user?.permissions?.includes(p));
};
/** Pages of the daily work an overseeing owner can still look at (production plans, the till's records). */
export const canView = (s: Session | null, p: Permission) => can(s, p) || oversees(s);
