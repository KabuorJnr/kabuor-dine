// Which module workspace this person is in. Each of the five modules (and Intelligence) can be worked in on its own:
// the sidebar narrows to that module's pages and its home is /w/{module}. "All modules" shows everything. The choice
// follows the page: opening a page that belongs to another module (a link from a handoff, Tell me) moves the person
// into that module's workspace, so the sidebar always matches what's on screen.
import { useEffect, useState } from 'react';
import { useLocation } from 'react-router';
import type { Session } from './api';
import { MODULE_ORDER, type ModuleKey } from './modules';
import { moduleOf, type VisibleGroup } from './nav';

const KEY = 'kerp-workspace';
export type Workspace = ModuleKey | null; // null: all modules

/** Where people in single-module roles start. Owners, accountants and viewers start with everything. */
const ROLE_HOME: Partial<Record<Session['user']['role'], ModuleKey>> = {
  Maintenance: 'operations', Dispatcher: 'operations', Procurement: 'procurement', Storekeeper: 'procurement', Sales: 'sales', HR: 'people',
};

function stored(session: Session): Workspace | undefined {
  try {
    const v = localStorage.getItem(`${KEY}:${session.user.id}`);
    if (v === 'all') return null;
    if (v && (MODULE_ORDER as string[]).includes(v)) return v as ModuleKey;
  } catch { /* storage blocked: fall back to the role's default */ }
  return undefined;
}

export function useWorkspace(session: Session, groups: VisibleGroup[]) {
  const { pathname, search } = useLocation();
  const has = new Set(groups.filter((g) => g.module).map((g) => g.module!));
  const available = MODULE_ORDER.filter((m) => has.has(m));
  const [ws, setWs] = useState<Workspace>(() => {
    const s = stored(session);
    if (s !== undefined) return s && available.includes(s) ? s : null;
    const home = ROLE_HOME[session.user.role];
    return home && available.includes(home) ? home : null;
  });

  const choose = (w: Workspace) => {
    setWs(w);
    try { localStorage.setItem(`${KEY}:${session.user.id}`, w ?? 'all'); } catch { /* not remembered: fine */ }
  };

  // Follow the page: /w/{module} picks that workspace; a page of another module moves into its workspace.
  useEffect(() => {
    const m = /^\/w\/([a-z]+)/.exec(pathname)?.[1] as ModuleKey | undefined;
    if (m && available.includes(m)) { if (m !== ws) choose(m); return; }
    if (ws === null) return;
    const owner = moduleOf(groups, pathname, search);
    if (owner && owner !== ws) choose(owner);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pathname, search]);

  return { workspace: ws && available.includes(ws) ? ws : null, choose, available };
}
