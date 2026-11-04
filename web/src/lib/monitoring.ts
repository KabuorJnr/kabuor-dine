// Browser error reports to Sentry, only when the server has a DSN set (GET /api/client-config). Loaded lazily so
// it costs nothing when it's off. Reports carry the page and the company, never names, phones or amounts typed.
import { getSession } from './api';

type Reporter = { captureException: (e: unknown) => void };
let reporter: Reporter | null = null;

export async function startMonitoring() {
  try {
    const cfg = await fetch('/api/client-config').then((r) => r.json());
    if (!cfg?.sentryDsn) return;
    const Sentry = await import('@sentry/react');
    Sentry.init({
      dsn: cfg.sentryDsn,
      environment: cfg.environment,
      sendDefaultPii: false,
      tracesSampleRate: 0,
      beforeSend(event) {
        // Strip anything a person typed; keep only where it happened.
        if (event.request) delete event.request.data;
        const s = getSession();
        event.tags = { ...event.tags, company: s?.company.id ?? 'signed-out', role: s?.user.role ?? 'none' };
        return event;
      },
    });
    reporter = Sentry;
  } catch {
    // Monitoring must never stop the app from working.
  }
}

export const reportError = (e: unknown) => reporter?.captureException(e);
