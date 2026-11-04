// When a screen fails while drawing, say so and offer a way on, instead of leaving a blank page. Around each page
// (inside the frame, so the menu still works and moving to another page clears it) and once around the whole app as
// the last resort. Errors caught here reach monitoring through createRoot's onCaughtError (main.tsx).
import { Component, type ReactNode } from 'react';
import { AlertTriangle, RotateCw } from 'lucide-react';

type Props = { children: ReactNode; resetKey?: unknown; whole?: boolean };

export default class ErrorBoundary extends Component<Props, { error: Error | null }> {
  state = { error: null as Error | null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidUpdate(prev: Props) {
    if (this.state.error && prev.resetKey !== this.props.resetKey) this.setState({ error: null });
  }

  render() {
    const { error } = this.state;
    if (!error) return this.props.children;
    const { whole } = this.props;
    return (
      <div className={whole ? 'grid min-h-screen place-items-center bg-paper px-4' : 'pt-6'} role="alert">
        <div className="card mx-auto max-w-md px-6 py-8 text-center">
          <span className="mx-auto grid h-11 w-11 place-items-center rounded-full bg-bad-soft text-bad"><AlertTriangle className="h-5 w-5" /></span>
          <p className="mt-3 text-lg font-semibold">{whole ? 'Kabuor ERP hit a problem' : 'This page couldn’t be shown'}</p>
          <p className="mt-1 text-sm text-muted">
            Something went wrong while drawing the screen. Anything already saved is safe.
            {!whole && ' The menu still works if you need another page.'}
          </p>
          <div className="mt-5 flex flex-wrap justify-center gap-2">
            {whole ? (
              <a href="/" className="btn btn-ghost">Go to Today</a>
            ) : (
              <button className="btn btn-ghost" onClick={() => this.setState({ error: null })}>Try again</button>
            )}
            <button className="btn btn-primary" onClick={() => window.location.reload()}><RotateCw className="h-4 w-4" /> Reload the page</button>
          </div>
          {error.message && (
            <details className="mt-5 text-left text-xs text-muted">
              <summary className="cursor-pointer text-center font-semibold">Details for support</summary>
              <code className="mt-2 block rounded-md bg-paper px-3 py-2 break-words whitespace-pre-wrap">{error.message}</code>
            </details>
          )}
        </div>
      </div>
    );
  }
}
