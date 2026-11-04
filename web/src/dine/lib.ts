// Kabuor Dine on the web: shared types and small helpers for the floor, kitchen, menu and the guest's QR menu.
import { useCallback, useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../lib/api';

export type OptionGroup = { name: string; choices: string[] };
export type Dish = {
  id: string; name: string; price: number; unit: string; trackStock: boolean; onHand: number | null; onMenu: boolean;
  category: string; station: string; description: string; options: OptionGroup[]; optionsText: string;
  soldOut: boolean; markedSoldOut: boolean; hidden: boolean; popular: boolean; sort: number; suggest: { id: string; name: string } | null;
};
export type OrderStatus = 'AwaitingApproval' | 'AwaitingPayment' | 'New' | 'Preparing' | 'Ready' | 'Served' | 'Cancelled';
export type VisitOrder = { id: string; number: string; status: OrderStatus; guestName: string; note: string; placedAt: string; total: number; lines: { name: string; quantity: number; unitPrice: number; options: string; note: string }[] };
export type Visit = {
  id: string; number: string; status: 'Open' | 'Paid' | 'Closed'; approved: boolean; calledWaiter: boolean; askedForBill: boolean; rating: number;
  total: number; paid: number; due: number; paying: boolean; orders: VisitOrder[];
  receipts: { id: string; number: string; total: number; paid: number; etimsInvoiceNo: string; etimsControlCode: string; etimsQrUrl: string; status: string }[];
};
/** A dish in a basket, with the guest's choices. */
export type CartLine = { key: string; dish: Dish; quantity: number; choices: Record<string, string>; note: string };

export const optionText = (choices: Record<string, string>) => Object.entries(choices).filter(([, v]) => v).map(([k, v]) => `${k}: ${v}`).join(' · ');

/** Where an order has got to, said the way a guest and a waiter would say it. */
export const STATUS: Record<OrderStatus, { label: string; tone: 'warn' | 'info' | 'good' | 'plain' | 'bad'; guest: string }> = {
  AwaitingApproval: { label: 'Confirm table', tone: 'warn', guest: 'A waiter is coming to confirm your table' },
  AwaitingPayment: { label: 'Waiting for payment', tone: 'warn', guest: 'Pay to send it to the kitchen' },
  New: { label: 'New', tone: 'info', guest: 'Sent to the kitchen' },
  Preparing: { label: 'Preparing', tone: 'info', guest: 'Being prepared' },
  Ready: { label: 'Ready to serve', tone: 'good', guest: 'Ready, on its way to you' },
  Served: { label: 'Served', tone: 'plain', guest: 'Served. Enjoy!' },
  Cancelled: { label: 'Cancelled', tone: 'bad', guest: 'Cancelled' },
};

/** Loads `path` now and again every `ms` while the tab is visible, without a loading flash. For the screens that
 * stay open all service (the floor, the kitchen). */
export function usePoll<T>(path: string | null, ms = 4000) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<number | null>(null);
  const tick = useRef(0);
  const load = useCallback(async () => {
    if (!path) return;
    const mine = ++tick.current;
    try {
      const d = await api<T>(path);
      if (mine === tick.current) { setData(d); setError(null); setStatus(null); }
    } catch (e) {
      if (mine === tick.current) { setError((e as Error).message); setStatus(e instanceof ApiError ? e.status : null); }
    }
  }, [path]);
  useEffect(() => {
    setData(null);
    void load();
    if (!path) return;
    const id = window.setInterval(() => { if (document.visibilityState === 'visible') void load(); }, ms);
    const on = () => void load();
    window.addEventListener('kerp-sync', on);
    document.addEventListener('visibilitychange', on);
    return () => { window.clearInterval(id); window.removeEventListener('kerp-sync', on); document.removeEventListener('visibilitychange', on); };
  }, [path, ms, load]);
  return { data, error, status, reload: load };
}

/** A short two-note chime for a new ticket or a call from a table (the browser allows it after the first tap). */
export function chime() {
  try {
    const Ctx = window.AudioContext ?? (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
    const ctx = new Ctx();
    [880, 1320].forEach((f, i) => {
      const o = ctx.createOscillator();
      const g = ctx.createGain();
      o.frequency.value = f;
      o.type = 'sine';
      g.gain.setValueAtTime(0.0001, ctx.currentTime + i * 0.18);
      g.gain.exponentialRampToValueAtTime(0.25, ctx.currentTime + i * 0.18 + 0.02);
      g.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + i * 0.18 + 0.3);
      o.connect(g).connect(ctx.destination);
      o.start(ctx.currentTime + i * 0.18);
      o.stop(ctx.currentTime + i * 0.18 + 0.32);
    });
    setTimeout(() => void ctx.close(), 1200);
  } catch { /* no sound on this device */ }
}

/** Minutes as a waiter reads them: "4 min", "1 h 05". */
export const mins = (m: number) => (m < 60 ? `${m} min` : `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')}`);

/** The guest's link for a table's QR. */
export const tableUrl = (token: string) => `${window.location.origin}/m/${token}`;
