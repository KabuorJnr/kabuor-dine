// The five core modules of the business OS, plus Intelligence, the layer that reads across them. Each has one colour,
// one icon and one name, used everywhere it appears (sidebar, tiles, the chain), so a module is recognisable at a
// glance. Colours are a validated categorical set (checked for colour-blind separation in this display order); they
// always come with the icon and the name, never alone.
import { Banknote, Brain, Cog, Megaphone, PackageSearch, UsersRound } from 'lucide-react';

export type ModuleKey = 'operations' | 'procurement' | 'sales' | 'people' | 'finance' | 'intelligence';

export const MODULES: Record<ModuleKey, { name: string; short: string; color: string; icon: typeof Cog; promise: string; home: string }> = {
  operations: { name: 'Operations', short: 'Ops', color: '#eb6834', icon: Cog, promise: 'Keep the assets working: faults, work orders, maintenance plans, tools, fleet and production.', home: '/maintenance' },
  procurement: { name: 'Procurement & supply', short: 'Supply', color: '#1baf7a', icon: PackageSearch, promise: 'Get what the business needs: requests, tenders, orders, stock for sale and stores for own use.', home: '/procurement' },
  sales: { name: 'Sales & marketing', short: 'Sales', color: '#2a78d6', icon: Megaphone, promise: 'Bring customers in and serve them: campaigns, leads, quotes, invoices and the till.', home: '/invoices' },
  people: { name: 'Human resources', short: 'People', color: '#e87ba4', icon: UsersRound, promise: 'The workforce: departments, employees, hiring, leave and payroll.', home: '/people' },
  finance: { name: 'Finance', short: 'Finance', color: '#008300', icon: Banknote, promise: 'Control the money: budgets, sign-offs, the books, cash, tax and the approvals register.', home: '/finance' },
  intelligence: { name: 'Intelligence', short: 'Insight', color: '#6250d6', icon: Brain, promise: 'Decide on what the numbers say: dashboards, the business chain and forecasts that learn.', home: '/os?tab=forecasts' },
};

export const MODULE_ORDER: ModuleKey[] = ['operations', 'procurement', 'sales', 'people', 'finance', 'intelligence'];

/** The chain's links in the words of each industry. The data behind them is the same: only the names change. */
export type ChainKey = 'assets' | 'maintenance' | 'supply' | 'people' | 'sales' | 'marketing' | 'finance' | 'decide';
type Lens = { label: string; story: string; words: Record<ChainKey, string> };

export const LENSES: Record<string, Lens> = {
  transportation: {
    label: 'Airline & transport',
    story: 'An airline only earns while its planes fly. Planes fly only when they work, they work only when they’re maintained, maintenance needs engineers, parts and tools, passengers have to book, marketing brings them, the money has to be there, and every decision should rest on what the numbers say.',
    words: { assets: 'Aircraft & vehicles flying', maintenance: 'Maintained on time', supply: 'Spares & tools in hand', people: 'Engineers, crew & staff', sales: 'Bookings & tickets', marketing: 'Marketing fills seats', finance: 'Money to keep flying', decide: 'Decisions & forecasts' },
  },
  manufacturing: {
    label: 'Manufacturing',
    story: 'A factory earns while its lines run. Lines run when the machines work, machines work when they’re maintained, maintenance needs technicians, spares and tools, orders have to come in, marketing brings them, cash has to cover materials and pay, and planning should follow the numbers.',
    words: { assets: 'Machines & lines running', maintenance: 'Maintained on time', supply: 'Materials, spares & tools', people: 'Operators & technicians', sales: 'Orders shipped', marketing: 'Demand generated', finance: 'Cash for materials & pay', decide: 'Planning & forecasts' },
  },
  healthcare: {
    label: 'Hospital & clinic',
    story: 'A hospital serves patients only when its theatres, scanners and wards are ready. Equipment works when it’s maintained, maintenance needs biomedical engineers, parts and tools, patients have to come and be billed, outreach brings them, the money has to be there, and care decisions should rest on the data.',
    words: { assets: 'Equipment & theatres ready', maintenance: 'Biomedical maintenance', supply: 'Medical supplies & spares', people: 'Clinicians & staff', sales: 'Patients served & billed', marketing: 'Outreach & referrals', finance: 'Funds & insurers paid', decide: 'Decisions & forecasts' },
  },
  distribution: {
    label: 'Distribution & logistics',
    story: 'A distributor earns when trucks deliver. Trucks deliver when they work, they work when they’re serviced, servicing needs mechanics, spares and tools, retailers have to order, sales reps win them, cash has to turn over, and routes and stock should follow the numbers.',
    words: { assets: 'Trucks & warehouse working', maintenance: 'Serviced on time', supply: 'Stock, spares & tools', people: 'Drivers, reps & staff', sales: 'Orders delivered', marketing: 'Retailers won', finance: 'Cash turning over', decide: 'Decisions & forecasts' },
  },
  retail: {
    label: 'Retail',
    story: 'A shop earns when the doors are open and the shelves are full. The till, fridges and building have to work, the stock has to be bought in time, staff have to be on shift, customers have to come, promotions bring them, cash has to cover suppliers, and buying should follow what sells.',
    words: { assets: 'Shop, tills & fridges working', maintenance: 'Kept in repair', supply: 'Shelves stocked', people: 'Staff on shift', sales: 'Customers buying', marketing: 'Promotions bring them', finance: 'Cash to pay suppliers', decide: 'What to buy next' },
  },
  general: {
    label: 'Any business',
    story: 'Every business is a chain: the assets have to work, they have to be maintained, that takes people, parts and tools, customers have to buy, marketing brings them, the money has to be there, and decisions should rest on what the numbers say. Break one link and the next one feels it.',
    words: { assets: 'Assets working', maintenance: 'Maintained', supply: 'Parts & tools in hand', people: 'The workforce', sales: 'Customers buying', marketing: 'Marketing brings them', finance: 'The money', decide: 'Decisions & forecasts' },
  },
};

export function lensFor(industry: string): Lens {
  const i = (industry || '').toLowerCase();
  if (/(transport|airline|aviation|logistic)/.test(i)) return LENSES.transportation;
  if (/(manufactur|factory|production)/.test(i)) return LENSES.manufacturing;
  if (/(health|hospital|clinic|medical|pharma)/.test(i)) return LENSES.healthcare;
  if (/(distribut|wholesale)/.test(i)) return LENSES.distribution;
  if (/(retail|shop|supermarket)/.test(i)) return LENSES.retail;
  return LENSES.general;
}

/** Which module owns each link of the chain. */
export const CHAIN_MODULE: Record<ChainKey, ModuleKey> = {
  assets: 'operations', maintenance: 'operations', supply: 'procurement', people: 'people', sales: 'sales', marketing: 'sales', finance: 'finance', decide: 'intelligence',
};
