// Kabuor ERP's privacy notice and terms of service, written for Kenyan law (Data Protection Act 2019 and its 2021
// regulations, Tax Procedures Act, Consumer Protection Act). Plain language first: a shop owner should understand
// what happens to their data and their customers' and employees' data. Each document opens with the short version,
// then numbered sections with a contents list. The contact email, the host and the database region come from the
// server's settings (Legal:Email, Legal:Hosting, Legal:HostingRegion), so they're never guessed here. How long drivers'
// locations are kept is enforced by Dispatch/DriverTrailRetention.cs. Have a Kenyan advocate review both.
import { Link } from 'react-router';
import { ArrowLeft } from 'lucide-react';
import { useApi } from '../lib/api';

type Info = { email: string; hosting: string; hostingRegion: string; updated: string };
type Section = { id: string; title: string; body: React.ReactNode };

const COMPANY = 'https://www.kabuor-technologies.company';

function useInfo(): Info {
  const { data } = useApi<Info>('/legal');
  return data ?? { email: '', hosting: '', hostingRegion: '', updated: '' };
}

const Contact = ({ email }: { email: string }) =>
  email ? (
    <a className="font-semibold text-brand underline" href={`mailto:${email}`}>
      {email}
    </a>
  ) : (
    <>
      the contact details on{' '}
      <a className="font-semibold text-brand underline" href={`${COMPANY}/#contact`}>
        kabuor-technologies.company
      </a>
    </>
  );

function Doc({ title, updated, summary, sections, other }: { title: string; updated: string; summary: string[]; sections: Section[]; other: React.ReactNode }) {
  return (
    <main className="mx-auto max-w-5xl px-4 py-10 sm:px-6">
      <Link to="/" className="mb-6 inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink">
        <ArrowLeft className="h-4 w-4" /> Kabuor ERP
      </Link>
      <h1 className="font-display text-3xl font-semibold tracking-tight sm:text-4xl">{title}</h1>
      {updated && <p className="mt-2 text-sm text-muted">Last updated {updated}</p>}

      <div className="mt-8 grid gap-10 lg:grid-cols-[14rem_minmax(0,1fr)]">
        <nav aria-label="Contents" className="hidden lg:sticky lg:top-6 lg:block lg:self-start">
          <p className="text-sm font-semibold">Contents</p>
          <ol className="mt-3 space-y-1.5 text-sm">
            {sections.map((s, i) => (
              <li key={s.id}>
                <a href={`#${s.id}`} className="text-muted hover:text-ink hover:underline">
                  {i + 1}. {s.title}
                </a>
              </li>
            ))}
          </ol>
        </nav>

        <article className="max-w-2xl">
          <section aria-label="The short version" className="rounded-md bg-paper p-5 ring-1 ring-line">
            <h2 className="font-semibold">The short version</h2>
            <ul className="mt-2 list-disc space-y-1 pl-5 text-sm leading-relaxed text-muted">
              {summary.map((s) => (
                <li key={s}>{s}</li>
              ))}
            </ul>
          </section>
          <details className="mt-4 rounded-md ring-1 ring-line lg:hidden">
            <summary className="cursor-pointer px-4 py-3 text-sm font-semibold">Contents</summary>
            <ol className="space-y-1 border-t border-line px-4 py-3 text-sm">
              {sections.map((s, i) => (
                <li key={s.id}>
                  <a href={`#${s.id}`} className="block py-0.5 text-muted hover:underline">
                    {i + 1}. {s.title}
                  </a>
                </li>
              ))}
            </ol>
          </details>
          <div className="mt-8 space-y-8 text-sm leading-relaxed sm:text-[15px]">
            {sections.map((s, i) => (
              <section key={s.id} id={s.id} className="scroll-mt-6">
                <h2 className="text-lg font-bold">
                  {i + 1}. {s.title}
                </h2>
                <div className="mt-2 space-y-3 [&_li]:ml-5 [&_li]:list-disc [&_ul]:space-y-1.5">{s.body}</div>
              </section>
            ))}
          </div>
          <p className="mt-10 border-t border-line pt-5 text-sm text-muted">{other}</p>
        </article>
      </div>
    </main>
  );
}

const WHO = 'Kabuor Technologies, Ngong Road, Nairobi, Kenya (Kabuor Technologies Ltd is being incorporated in Kenya; until that is complete, its founder, Rooney Odhiambo Kabuor, is responsible for the service)';

export function Privacy() {
  const info = useInfo();
  const sections: Section[] = [
    {
      id: 'roles',
      title: 'Who we are, and our two roles',
      body: (
        <>
          <p>Kabuor ERP is run by {WHO}. This notice explains what personal data the service holds, why, who sees it, and your rights under the Data Protection Act, 2019.</p>
          <ul>
            <li><b>Your account</b> (the business and the people who sign in): Kabuor Technologies decides how this is used, so we are the <b>data controller</b>.</li>
            <li><b>The records you keep</b> (your customers, suppliers, employees, drivers, payslips, invoices): your business decides what to record and why, so your business is the <b>controller</b> and Kabuor Technologies is your <b>data processor</b>. We use it only to run the service for you, under the processing terms in our <Link className="font-semibold text-brand underline" to="/terms">terms of service</Link>.</li>
          </ul>
        </>
      ),
    },
    {
      id: 'collect',
      title: 'What we collect',
      body: (
        <ul>
          <li>Business details: name, KRA PIN, phone, email, address, county, VAT status, logo, M-Pesa shortcode.</li>
          <li>People who sign in: name, email, phone, role, a scrambled (hashed) password, and two-step sign-in settings.</li>
          <li>Records you enter: customers, invoices, payments, stock, purchases, and, if you use payroll, employee details such as ID, KRA PIN, NSSF, SHA and bank or M-Pesa details.</li>
          <li>Payment details Safaricom sends us when someone pays your Paybill or Till: the amount, M-Pesa receipt, phone number and name.</li>
          <li>Drivers’ locations, if you use fleet tracking: while a trip is on the road and the driver has its link open, the phone shares where it is (position, speed, direction) every few seconds, and where each delivery was confirmed. Nothing is shared before the trip leaves or after it ends.</li>
          <li>Basic technical logs (time, address of the request, errors) to keep the service secure and working.</li>
          <li>Verified report shares and view logs, if you share figures with lenders: the lender's name, when and who created or revoked the share, and each view's time, a salted hash of the visitor's IP address (never the raw IP), and a summary of their device or browser.</li>
          <li>If you use tracking links and the online shop: which link someone clicked, a random browser id (cookie <code>kv</code>), the link they came through (cookie <code>kref</code>), whether it was a phone, and what they asked. No advertising cookies are used. Your sign-in is kept in your browser’s storage, not a cookie.</li>
        </ul>
      ),
    },
    {
      id: 'why',
      title: 'Why we use it',
      body: (
        <>
          <ul>
            <li>To provide the service you signed up for (contract).</li>
            <li>To meet legal duties, such as sending invoices to KRA eTIMS and keeping tax records (legal obligation).</li>
            <li>To keep accounts secure and prevent fraud (legitimate interest).</li>
            <li>Marketing messages only go to customers you have not marked as having opted out, and every campaign respects that choice.</li>
          </ul>
          <p>We do not sell personal data, and we do not use your records to advertise to anyone.</p>
          <p>
            <b>Profiling and automated suggestions.</b> Kabuor Intelligence groups a business’s customers (for example “best customers” or “at risk”) and its agents suggest actions such as reminders or purchase orders. These help the business decide; no decision with a legal or similarly significant effect on a person is made by the system alone. A credit limit that holds an invoice can always be overridden by a person, and you can ask the business for a human review of any decision about you (section 35 of the Act).
          </p>
          <p>
            <b>Drivers.</b> A business that tracks its drivers must tell them, and use the locations only to run deliveries. The link shares only while the driver has it open on a trip that is on the road.
          </p>
        </>
      ),
    },
    {
      id: 'share',
      title: 'Who else sees it',
      body: (
        <>
          <ul>
            <li><b>Hosting</b>: the application runs on {info.hosting || 'a cloud host'} and the database on Neon{info.hostingRegion ? ` (${info.hostingRegion})` : ''}. Data is therefore stored outside Kenya.</li>
            <li><b>KRA</b> (Kenya): invoices are sent to eTIMS when your business is VAT-registered.</li>
            <li><b>Safaricom</b> (Kenya): when you use M-Pesa features (payment notices, prompts, subscriptions).</li>
            <li><b>Africa’s Talking</b> (Kenya): to send sign-in codes and messages by SMS, when SMS is switched on.</li>
            <li><b>Anthropic</b> (United States), for Msaidizi and the agents: the question and the records needed to answer it are sent to produce the answer. It suggests; a person approves anything that changes your books.</li>
            <li><b>OpenStreetMap</b> (United Kingdom), for the live map: the browser showing the map fetches its map pictures from OpenStreetMap, which sees that browser’s address. Drivers’ locations are not sent to it.</li>
            <li><b>GitHub</b> (United States): the nightly database backup, encrypted before it leaves our servers and kept 30 days.</li>
            <li><b>Sentry</b>: error reports, set up not to include personal data such as names, emails or request addresses.</li>
            <li><b>Lenders you choose</b>: when the business owner creates a verified report link, the named lender sees aggregated totals and counts only (confirmed M-Pesa revenue, payer counts, and KRA eTIMS signing rates), never customer names, phone numbers or invoice lines, until the share ends or is revoked.</li>
            <li>Anyone the law requires us to share with, such as a court order.</li>
          </ul>
          <p>We give account owners 30 days’ notice in the app before adding or replacing one of these providers.</p>
        </>
      ),
    },
    {
      id: 'abroad',
      title: 'Data kept outside Kenya',
      body: (
        <>
          <p>We only send personal data abroad on the grounds in sections 48 and 49 of the Act and regulations 40 and 41: under written terms with each provider that bind them to protect it, to providers whose security we have assessed, and only what the service needs. We keep a record of each kind of transfer and give it to the Data Commissioner on request.</p>
          <p><b>Schools and health providers</b>: personal data from basic education and from primary or secondary health care must be processed in Kenya or have a copy kept there (regulation 26). Kabuor ERP is hosted outside Kenya, so don’t use it for learners’ or patients’ records until Kenya hosting is offered.</p>
        </>
      ),
    },
    {
      id: 'keep',
      title: 'How long we keep it',
      body: (
        <ul>
          <li><b>Your records</b>: while your account is open, reviewed at least once a year (regulation 19). Tax law (Tax Procedures Act, section 23) requires business records to be kept for at least five years, so export or keep yours for that long.</li>
          <li><b>When you close your account</b>: you have 30 days to export your records; we then delete or anonymise them, at the latest 90 days after closing, unless you ask us to keep them longer or the law requires it. Backups roll off 30 days after that.</li>
          <li><b>Drivers’ locations</b>: a trip’s trail is deleted 90 days after it was recorded, and a finished trip’s last position 90 days after its last update. Where a delivery was confirmed stays with that delivery, as part of your records.</li>
          <li><b>Verified shares and view records</b>: kept while your account is open, because they record explicit consent under sections 30 and 32 of the Data Protection Act, 2019.</li>
          <li><b>Technical logs</b>: kept by our hosting provider for as long as it needs to run and secure the service.</li>
        </ul>
      ),
    },
    {
      id: 'security',
      title: 'How we protect it',
      body: (
        <ul>
          <li>Encrypted connections (HTTPS) and hashed passwords; secrets such as M-Pesa and KRA keys are encrypted at rest.</li>
          <li>Each business only ever sees its own records; inside a business, each role only sees what its job needs.</li>
          <li>Optional two-step sign-in, and “sign out everywhere”.</li>
          <li>Nightly encrypted backups, kept separately.</li>
          <li>If a breach puts personal data at real risk, we tell the Data Commissioner within 72 hours and the people affected without delay (section 43). For records we process for a business, we tell that business within 48 hours so it can do the same.</li>
        </ul>
      ),
    },
    {
      id: 'rights',
      title: 'Your rights',
      body: (
        <>
          <p>
            You can ask to be told what we hold about you, get a copy, have it corrected, move it to another provider, object to its use, stop direct marketing, or have it deleted where the law allows. For records a business keeps about you (for example as its customer, employee or driver), ask that business first; the app lets it send you your data and erase it, and we help it answer. Write to <Contact email={info.email} />.
          </p>
          <p>The law sets how fast we answer (Data Protection (General) Regulations, 2021): a copy of your data within <b>7 days</b>; correction within <b>14 days</b>; erasure within <b>14 days</b>; stopping direct marketing within <b>7 days</b>; moving your data to another provider within <b>30 days</b>. Records that tax or employment law requires a business to keep (such as issued invoices) can’t be erased until that period ends; we tell you what was kept and why.</p>
          <p>
            You can also complain to the Office of the Data Protection Commissioner at{' '}
            <a className="font-semibold text-brand underline" href="https://www.odpc.go.ke" target="_blank" rel="noopener noreferrer">
              odpc.go.ke
            </a>
            .
          </p>
        </>
      ),
    },
    {
      id: 'children',
      title: 'Children',
      body: <p>The service is for businesses and is not meant for children under 18.</p>,
    },
    {
      id: 'changes',
      title: 'Changes to this notice',
      body: <p>If we change this notice in a way that matters, we will tell account owners in the app before it takes effect.</p>,
    },
  ];
  return (
    <Doc
      title="Privacy notice"
      updated={info.updated}
      summary={[
        'Your business owns its records. For your customers’, employees’ and drivers’ data we are your processor and act only on your instructions.',
        'We use data only to run Kabuor ERP. We never sell it or use it to advertise.',
        'Data is kept outside Kenya with providers bound to protect it; the list is below.',
        'Drivers share their location only while their trip is on the road; trails are deleted after 90 days.',
        'You can see, correct, move or delete your data, and we answer within the times the law sets.',
      ]}
      sections={sections}
      other={
        <>
          See also the <Link className="font-semibold text-brand underline" to="/terms">terms of service</Link> and the{' '}
          <a className="font-semibold text-brand underline" href={`${COMPANY}/privacy`}>
            Kabuor Technologies website privacy notice
          </a>
          .
        </>
      }
    />
  );
}

export function Terms() {
  const info = useInfo();
  const sections: Section[] = [
    {
      id: 'agreement',
      title: 'This agreement',
      body: <p>These terms are an agreement between your business and {WHO}, for the use of Kabuor ERP. By opening an account you agree to them on behalf of your business, and confirm you’re allowed to.</p>,
    },
    {
      id: 'service',
      title: 'The service',
      body: <p>Kabuor ERP helps you invoice, take payments, keep stock, run payroll and deliveries, and keep your books. It sends invoices to KRA eTIMS and reads M-Pesa payments when you connect them. We aim to keep it available at all times but can’t promise it will never be interrupted; we tell you in the app about planned maintenance.</p>,
    },
    {
      id: 'paying',
      title: 'Paying',
      body: (
        <ul>
          <li>A new business chooses a plan and pays its first month or year by M-Pesa before the account opens.</li>
          <li>When paid time runs out, and 7 more days’ grace, your records stay readable and downloadable, but adding and changing them waits until you renew.</li>
          <li>Plans are paid in Kenyan shillings, in advance, monthly or yearly (a year costs ten months). Prices are shown in Settings → Plan &amp; billing; we give 30 days’ notice of any price change.</li>
          <li>Paid time is not refunded if you stop early, except where the law requires.</li>
        </ul>
      ),
    },
    {
      id: 'responsibilities',
      title: 'Your responsibilities',
      body: (
        <ul>
          <li>Keep sign-in details private, and only give access to people who need it.</li>
          <li>The figures, tax returns and payroll you file are your responsibility. Kabuor ERP calculates PAYE, NSSF, SHA, the Housing Levy and VAT from the current rules, but check them and get professional advice where needed.</li>
          <li>You must have a lawful basis to record and message your customers and employees, and respect their opt-outs. If you track drivers, tell them, and use their locations only to run deliveries.</li>
          <li>Don’t use the service for anything illegal, or try to reach other businesses’ data.</li>
        </ul>
      ),
    },
    {
      id: 'data',
      title: 'Your data',
      body: (
        <p>
          Your records belong to your business. You can export them at any time. We handle personal data as described in the <Link className="font-semibold text-brand underline" to="/privacy">privacy notice</Link>, acting as your processor for the records you keep.
        </p>
      ),
    },
    {
      id: 'processing',
      title: 'Processing personal data for you',
      body: (
        <>
          <p>For the records you keep, your business is the data controller and Kabuor Technologies is your data processor. These terms are the written contract that section 42 of the Data Protection Act and regulation 24 require:</p>
          <ul>
            <li><b>What, how long and why</b>: we process your customers’, suppliers’, leads’, employees’ and drivers’ details (names, phone numbers, emails, KRA PINs, ID and NSSF/SHA numbers, pay and bank or M-Pesa details, delivery locations, invoices, payments and messages) for as long as your account is open, only to provide the service.</li>
            <li><b>Your instructions</b>: we act only on your instructions, given through the app and these terms. If we think an instruction breaks the law, we tell you.</li>
            <li><b>Confidentiality</b>: everyone who works on the service is bound to keep your data confidential.</li>
            <li><b>Security</b>: the technical and organisational measures in the privacy notice, kept up to date.</li>
            <li><b>Sub-processors</b>: you authorise the providers listed in the privacy notice. We give you 30 days’ notice in the app before adding or replacing one, so you can object; each is bound by written terms like these, and we stay responsible for them (regulation 25).</li>
            <li><b>Helping you</b>: we help you answer people’s requests, carry out impact assessments, and handle breaches, which we report to you within 48 hours of becoming aware.</li>
            <li><b>At the end</b>: when the agreement ends you choose to take your records or have them returned; we then delete them, at the latest 90 days after the account closes, except where the law requires us to keep them.</li>
            <li><b>Audit</b>: on reasonable notice, we give you the information you need to check we meet these terms, including the results of our security reviews.</li>
          </ul>
        </>
      ),
    },
    {
      id: 'liability',
      title: 'Liability',
      body: <p>We are responsible for running the service with reasonable care and skill. We are not liable for indirect losses (such as lost profit) or for losses caused by information you entered, by KRA, Safaricom or other services being unavailable, or by events outside our control. Our total liability in any year is limited to what you paid us in the 12 months before the claim. Nothing here limits rights you have under the Consumer Protection Act, 2012, or other law that can’t be excluded.</p>,
    },
    {
      id: 'verified',
      title: 'Verified reports',
      body: <p>The business owner alone chooses whether to share figures with a lender, which lender sees them, and for how long. Kabuor ERP provides verified facts (money confirmed by Safaricom and KRA-signed invoices), not credit scores, risk ratings, or lending decisions. Your business is responsible for telling a lender about anything the figures don't show, such as other liabilities, cash transactions, or upcoming costs.</p>,
    },
    {
      id: 'ending',
      title: 'Ending the agreement',
      body: <p>You can close your account at any time: ask us at <Contact email={info.email} /> and we’ll confirm. We may suspend an account that breaks these terms, or is unpaid after the grace period, after telling the owner. After closing, you have 30 days to export your records; they’re then deleted as the privacy notice describes.</p>,
    },
    {
      id: 'law',
      title: 'Law and disputes',
      body: <p>These terms are governed by the laws of Kenya. We will first try to settle any dispute by talking; if that fails, the courts of Kenya decide.</p>,
    },
    {
      id: 'changes',
      title: 'Changes to these terms',
      body: <p>We tell account owners in the app at least 30 days before a change that matters takes effect. Questions about these terms: <Contact email={info.email} />.</p>,
    },
  ];
  return (
    <Doc
      title="Terms of service"
      updated={info.updated}
      summary={[
        'You pay in shillings, monthly or yearly by M-Pesa, before the account opens. A year costs ten months.',
        'If paid time runs out, your records stay readable and downloadable; changes wait until you renew.',
        'Your records are yours and you can export them any time. We process personal data only on your instructions.',
        'The figures and returns you file are your responsibility; check them and get advice where needed.',
        'Close your account any time by asking us; you get 30 days to export. Kenyan law applies.',
      ]}
      sections={sections}
      other={
        <>
          See also the <Link className="font-semibold text-brand underline" to="/privacy">privacy notice</Link> and the{' '}
          <a className="font-semibold text-brand underline" href={`${COMPANY}/terms`}>
            Kabuor Technologies website terms of use
          </a>
          .
        </>
      }
    />
  );
}
