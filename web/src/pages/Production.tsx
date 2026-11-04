// Production. Deciding "what can we make today, and what does it cost us?": each blueprint shows how many
// batches the store's stock allows (and which ingredient runs out first), the cost per unit and the margin.
// Runs are planned, then finished with what actually came out.
import { useEffect, useState } from 'react';
import { Factory, Loader2, Plus, Trash2 } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { cn, day, ksh, toCents } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, Sheet, Status, SheetFooter } from '../components/ui';
import { useSearchParams } from 'react-router';
import Mrp from './Mrp';

type Item = { id: string; name: string; unit: string; trackStock: boolean; kind: string };

function RecipeEditor({ open, onClose, existing, onSaved }: { open: boolean; onClose: () => void; existing?: any; onSaved: () => void }) {
  const items = useApi<Item[]>(open ? '/items?all=true' : null);
  const tracked = (items.data ?? []).filter((i) => i.trackStock);
  const [f, setF] = useState({ name: '', outputItemId: '', outputQuantity: '1', conversion: '', notes: '' });
  const [lines, setLines] = useState<{ itemId: string; quantity: string }[]>([{ itemId: '', quantity: '' }]);
  const [state, setState] = useState({ busy: false, error: '' });
  useEffect(() => {
    if (!open) return;
    if (existing) {
      setF({ name: existing.name, outputItemId: existing.outputItemId, outputQuantity: String(existing.outputQuantity), conversion: existing.conversionCostPerBatch ? String(existing.conversionCostPerBatch / 100) : '', notes: existing.notes ?? '' });
      setLines(existing.lines.map((l: any) => ({ itemId: l.itemId, quantity: String(l.quantity) })));
    } else {
      setF({ name: '', outputItemId: '', outputQuantity: '1', conversion: '', notes: '' });
      setLines([{ itemId: '', quantity: '' }]);
    }
    setState({ busy: false, error: '' });
  }, [open, existing]);
  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      const body = { ...f, outputQuantity: Number(f.outputQuantity), conversionCostPerBatch: toCents(f.conversion), lines: lines.filter((l) => l.itemId).map((l) => ({ itemId: l.itemId, quantity: Number(l.quantity) })) };
      if (existing) await api(`/production/recipes/${existing.id}`, { method: 'PUT', body });
      else await api('/production/recipes', { body });
      onSaved();
      onClose();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  const unitOf = (id: string) => tracked.find((i) => i.id === id)?.unit ?? '';
  return (
    <Sheet open={open} onClose={onClose} title={existing ? `Edit ${existing.name}` : 'New blueprint'} wide>
      {items.data && tracked.length < 2 ? (
        <p className="text-sm text-muted">A blueprint is what one batch of a product takes (materials, packaging) and how many it makes. It uses items counted in stock. In Settings → Products & services, turn on “Count in stock” for the product you make and for its ingredients and packaging.</p>
      ) : (
        <form onSubmit={save} className="space-y-4">
          <Field label="Blueprint name">
            <input className="field" required value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. Chilli sauce, 20-jar batch" />
          </Field>
          <div className="grid gap-3 sm:grid-cols-[1fr_9rem]">
            <Field label="It makes">
              <select className="field" required value={f.outputItemId} onChange={(e) => setF({ ...f, outputItemId: e.target.value })}>
                <option value="">Choose the finished product</option>
                {tracked.map((i) => (
                  <option key={i.id} value={i.id}>
                    {i.name}
                  </option>
                ))}
              </select>
            </Field>
            <Field label={`How many a batch${f.outputItemId ? ` (${unitOf(f.outputItemId)})` : ''}`}>
              <input className="field num" inputMode="decimal" required value={f.outputQuantity} onChange={(e) => setF({ ...f, outputQuantity: e.target.value })} />
            </Field>
          </div>
          <fieldset>
            <legend className="mb-1.5 text-sm font-semibold">What one batch uses</legend>
            <ul className="space-y-2">
              {lines.map((l, i) => (
                <li key={i} className="grid grid-cols-[1fr_7rem_2.25rem] gap-2">
                  <select className="field" value={l.itemId} onChange={(e) => setLines(lines.map((x, j) => (j === i ? { ...x, itemId: e.target.value } : x)))} aria-label={`Ingredient ${i + 1}`}>
                    <option value="">Ingredient or packaging</option>
                    {tracked
                      .filter((t) => t.id !== f.outputItemId)
                      .map((t) => (
                        <option key={t.id} value={t.id}>
                          {t.name}
                        </option>
                      ))}
                  </select>
                  <input className="field num" inputMode="decimal" value={l.quantity} placeholder={unitOf(l.itemId) || 'qty'} onChange={(e) => setLines(lines.map((x, j) => (j === i ? { ...x, quantity: e.target.value } : x)))} aria-label={`Quantity ${i + 1}`} />
                  <button type="button" className="grid place-items-center rounded-full text-muted hover:bg-bad-soft hover:text-bad" onClick={() => setLines(lines.length > 1 ? lines.filter((_, j) => j !== i) : [{ itemId: '', quantity: '' }])} aria-label={`Remove ingredient ${i + 1}`}>
                    <Trash2 className="h-4 w-4" />
                  </button>
                </li>
              ))}
            </ul>
            <button type="button" className="mt-2 inline-flex items-center gap-1.5 text-sm font-semibold text-brand" onClick={() => setLines([...lines, { itemId: '', quantity: '' }])}>
              <Plus className="h-4 w-4" /> Add an ingredient
            </button>
          </fieldset>
          <Field label="Labour, gas and power per batch (KSh)" hint="Added to the product's cost so your margin is real.">
            <input className="field num" inputMode="decimal" value={f.conversion} onChange={(e) => setF({ ...f, conversion: e.target.value })} />
          </Field>
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
          <SheetFooter>
          <button className="btn btn-primary" disabled={state.busy}>
            {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Save blueprint
          </button>
          </SheetFooter>
        </form>
      )}
    </Sheet>
  );
}

function RecipeSheet({ recipeId, onClose, onPlanned, onEdit }: { recipeId: string | null; onClose: () => void; onPlanned: () => void; onEdit: (r: any) => void }) {
  const locations = useApi<any[]>(recipeId ? '/locations' : null);
  const [locationId, setLocationId] = useState('');
  const cap = useApi<any>(recipeId ? `/production/recipes/${recipeId}/capacity${locationId ? `?locationId=${locationId}` : ''}` : null);
  const recipe = useApi<any>(recipeId ? `/production/recipes/${recipeId}` : null);
  const [batches, setBatches] = useState('1');
  const [state, setState] = useState({ busy: false, error: '' });
  useEffect(() => setLocationId(''), [recipeId]);
  const plan = async () => {
    setState({ busy: true, error: '' });
    try {
      await api('/production/runs', { body: { recipeId, batches: Number(batches), locationId: cap.data?.location.id } });
      onPlanned();
      onClose();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const c = cap.data;
  const n = Number(batches) || 0;
  return (
    <Sheet open={Boolean(recipeId)} onClose={onClose} title={c?.recipe.name ?? 'Blueprint'} wide>
      {!c ? (
        <Loading />
      ) : (
        <div className="space-y-5">
          <div className="flex flex-wrap items-center gap-3">
            {(locations.data?.length ?? 0) > 1 && (
              <select className="field w-auto" value={locationId || c.location.id} onChange={(e) => setLocationId(e.target.value)} aria-label="Store">
                {locations.data!.map((l) => (
                  <option key={l.id} value={l.id}>
                    {l.name}
                  </option>
                ))}
              </select>
            )}
            {recipe.data && (
              <button className="text-sm font-semibold text-brand" onClick={() => onEdit(recipe.data)}>
                Edit blueprint
              </button>
            )}
          </div>
          <div className="grid gap-3 sm:grid-cols-3">
            <div className="card p-4">
              <p className="text-sm text-muted">Can make now</p>
              <p className="num font-display text-2xl font-semibold">{c.maxBatches} batch{c.maxBatches === 1 ? '' : 'es'}</p>
              <p className="text-xs text-muted">{c.maxBatches * c.recipe.outputQuantity} {c.recipe.unit} · limited by {c.limitedBy}</p>
            </div>
            <div className="card p-4">
              <p className="text-sm text-muted">Cost per {c.recipe.unit}</p>
              <p className="num font-display text-2xl font-semibold">{ksh(c.unitCost)}</p>
              <p className="text-xs text-muted">{ksh(c.costPerBatch)} a batch at today’s costs</p>
            </div>
            <div className="card p-4">
              <p className="text-sm text-muted">Margin</p>
              <p className={cn('num font-display text-2xl font-semibold', c.margin !== null && c.margin < 20 && 'text-bad')}>{c.margin === null ? '—' : `${c.margin}%`}</p>
              <p className="text-xs text-muted">selling at {ksh(c.price)} incl. VAT</p>
            </div>
          </div>
          <table className="w-full text-sm">
            <thead className="text-left text-xs text-muted">
              <tr>
                <th className="pb-2 font-semibold">Ingredient</th>
                <th className="pb-2 text-right font-semibold">Per batch</th>
                <th className="pb-2 text-right font-semibold">For {n || 0}</th>
                <th className="pb-2 text-right font-semibold">In {c.location.name}</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line">
              {c.lines.map((l: any) => {
                const need = l.perBatch * n;
                return (
                  <tr key={l.itemId}>
                    <td className="py-2">{l.name}</td>
                    <td className="num py-2 text-right">
                      {l.perBatch} {l.unit}
                    </td>
                    <td className="num py-2 text-right">{need}</td>
                    <td className={cn('num py-2 text-right font-semibold', l.onHand < need ? 'text-bad' : 'text-good')}>{l.onHand}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          <div className="flex flex-wrap items-end gap-3 rounded-md bg-paper p-3">
            <Field label="Batches to make">
              <input className="field num w-28" inputMode="decimal" value={batches} onChange={(e) => setBatches(e.target.value)} />
            </Field>
            <p className="pb-2.5 text-sm text-muted">
              = {n * c.recipe.outputQuantity} {c.recipe.unit}
              {n > c.maxBatches && <span className="font-semibold text-bad"> · more than the stock allows</span>}
            </p>
            <button className="btn btn-primary ml-auto" onClick={plan} disabled={state.busy || n <= 0}>
              {state.busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Factory className="h-4 w-4" />} Plan this run
            </button>
          </div>
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
        </div>
      )}
    </Sheet>
  );
}

function FinishRun({ run, onClose, onDone }: { run: any; onClose: () => void; onDone: () => void }) {
  const [actual, setActual] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });
  useEffect(() => setActual(run ? String(run.outputQuantity) : ''), [run]);
  if (!run) return null;
  const finish = async () => {
    setState({ busy: true, error: '' });
    try {
      await api(`/production/runs/${run.id}/complete`, { body: { actualOutput: Number(actual) } });
      onDone();
      onClose();
      setState({ busy: false, error: '' });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title={`Finish ${run.number}`}>
      <p className="text-sm text-muted">
        {run.recipeName} × {run.batches}. Finishing takes the ingredients out of {run.location} and adds what came out, at what it cost to make.
      </p>
      <Field label="How many came out" hint={`The blueprint says ${run.outputQuantity}. Enter the real number if some spilled or broke.`} className="mt-4">
        <input className="field num" inputMode="decimal" value={actual} onChange={(e) => setActual(e.target.value)} />
      </Field>
      {state.error && <div className="mt-3"><ErrorNote>{state.error}</ErrorNote></div>}
      <SheetFooter>
      <button className="btn btn-primary" onClick={finish} disabled={state.busy}>
        {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Finish and update stock
      </button>
      </SheetFooter>
    </Sheet>
  );
}

export default function Production() {
  const [params, setParams] = useSearchParams();
  const tab = params.get('tab') ?? 'recipes';
  const setTab = (t: string) => setParams(t === 'recipes' ? {} : { tab: t });
  const [status, setStatus] = useState('planned');
  const recipes = useApi<any[]>('/production/recipes');
  const runs = useApi<any>(`/production/runs?status=${status}`);
  const [open, setOpen] = useState<string | null>(null);
  const [editing, setEditing] = useState<{ open: boolean; existing?: any }>({ open: false });
  const [finishing, setFinishing] = useState<any>(null);
  const [error, setError] = useState('');
  const cancel = async (id: string) => {
    try {
      await api(`/production/runs/${id}/cancel`, { body: {} });
      runs.reload();
    } catch (e) {
      setError((e as Error).message);
    }
  };
  return (
    <>
      <PageHeader print
        title="Production"
        subtitle="Blueprints say what each product is made of and what it costs. From them: how many you can make with what's in store, what to make and buy next, and what each unit really costs."
        actions={
          <button className="btn btn-primary" onClick={() => setEditing({ open: true })}>
            <Plus className="h-4 w-4" /> New blueprint
          </button>
        }
      />
      <div className="mb-4">
        <Chips label="Production" value={tab} onChange={setTab} options={[['recipes', 'Blueprints', recipes.data?.length], ['runs', 'Production runs', runs.data?.counts.planned], ['plan', 'What to make & buy']]} />
      </div>
      {error && <ErrorNote>{error}</ErrorNote>}
      {tab === 'plan' ? <Mrp embedded /> : tab === 'recipes' ? (
        !recipes.data ? (
          <Loading />
        ) : recipes.data.length === 0 ? (
          <Empty title="No blueprints yet" action={<button className="btn btn-primary" onClick={() => setEditing({ open: true })}>Add your first blueprint</button>}>
            A blueprint says what one batch of a product uses (materials, packaging, labels) and how many it makes. Everything else follows from it: what each unit really costs, how many batches your stock allows, what to buy, and what you can promise customers.
          </Empty>
        ) : (
          <ul className="card divide-y divide-line">
            {recipes.data.map((r) => (
              <li key={r.id}>
                <button onClick={() => setOpen(r.id)} className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-paper">
                  <span className="grid h-10 w-10 shrink-0 place-items-center rounded-full bg-brand-soft text-brand">
                    <Factory className="h-5 w-5" />
                  </span>
                  <span className="min-w-0 flex-1">
                    <b className="block truncate">{r.name}</b>
                    <span className="text-sm text-muted">
                      Makes {r.outputQuantity} {r.unit} of {r.output} · {r.ingredients} material{r.ingredients === 1 ? '' : 's'}
                    </span>
                  </span>
                  {!r.active && <Status tone="plain">Off</Status>}
                  <span className="text-sm font-semibold text-brand">Plan →</span>
                </button>
              </li>
            ))}
          </ul>
        )
      ) : (
        <>
          <div className="mb-3">
            <Chips label="Runs" value={status} onChange={setStatus} options={[['planned', 'Planned', runs.data?.counts.planned], ['done', 'Done', runs.data?.counts.done], ['all', 'All', runs.data?.counts.all]]} />
          </div>
          {!runs.data ? (
            <Loading />
          ) : runs.data.items.length === 0 ? (
            <Empty title={status === 'planned' ? 'Nothing planned' : 'No runs yet'}>Open a blueprint and plan a run.</Empty>
          ) : (
            <ul className="card divide-y divide-line">
              {runs.data.items.map((r: any) => (
                <li key={r.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
                  <span className="min-w-0 flex-1">
                    <b className="block">
                      {r.number} · {r.recipeName}
                    </b>
                    <span className="text-sm text-muted">
                      {r.batches} batch{r.batches === 1 ? '' : 'es'} → {r.outputQuantity} · {r.location} · {r.status === 'Done' ? `made ${day(r.completedAt)} by ${r.completedBy}` : `for ${day(r.plannedFor)}`}
                    </span>
                  </span>
                  {r.status === 'Done' ? (
                    <span className="text-right text-sm">
                      <b className="num block">{ksh(r.unitCost)} each</b>
                      <span className="text-xs text-muted">{ksh(r.materialCost + r.conversionCost)} in total</span>
                    </span>
                  ) : r.status === 'Planned' ? (
                    <span className="flex gap-2">
                      <button className="btn btn-ghost py-2" onClick={() => cancel(r.id)}>
                        Cancel
                      </button>
                      <button className="btn btn-primary py-2" onClick={() => setFinishing(r)}>
                        Finish
                      </button>
                    </span>
                  ) : (
                    <Status tone="plain">Cancelled</Status>
                  )}
                </li>
              ))}
            </ul>
          )}
        </>
      )}
      <RecipeSheet recipeId={open} onClose={() => setOpen(null)} onPlanned={() => (runs.reload(), setTab('runs'), setStatus('planned'))} onEdit={(r) => (setOpen(null), setEditing({ open: true, existing: r }))} />
      <RecipeEditor open={editing.open} existing={editing.existing} onClose={() => setEditing({ open: false })} onSaved={recipes.reload} />
      <FinishRun run={finishing} onClose={() => setFinishing(null)} onDone={runs.reload} />
    </>
  );
}
