# CreatorPantry Terms of Service

> **MOCK-UP FOR OWNER AND LEGAL REVIEW. NOT PUBLISHED. NOT LEGAL ADVICE.**
> Drafted 2026-10-24 at the owner's request so the landing page, FAQ and pricing work have something concrete
> to point at. A qualified attorney must review it before it is published or relied on. Text in
> `[BRACKETS]` is a placeholder or an open decision. Clauses marked *(product fact)* describe what the
> software does today and must be re-checked whenever that changes.

> **Revision for counsel, 2026-10-28.** Updated for the owner's billing decisions in
> `docs/architecture-decisions/ai-pricing.md`: the trial and its picture cap (B-48, B-52), credits and packs
> (B-50, B-56), when plan changes take effect (B-54, B-56), Stripe and Link as merchant of record (B-57, B-58), and
> a low-balance notice in place of automatic top-up (B-59). Changed: section 5 ("Limits"), section 7 (payments),
> and section 8, which is rewritten. Points still open or only proposed stay in `[BRACKETS]`. The merchant-of-record
> wording in section 8 is the part most in need of review: under Stripe Managed Payments, purchases are sold
> through Link, not by us.

> **Revision for counsel, 2026-10-28 (second).** The low-balance notice of B-59 is now built (AP.6.4), so the
> bracketed proposal in section 7 is replaced by what the software does: on by default, at most one notice per
> allowance period, and a setting in the account that turns it off. Nothing is ever charged or bought for a
> creator by it. No other clause changed.

**Last updated:** [DATE OF PUBLICATION]

## 1. Who we are and what these terms cover

CreatorPantry ("CreatorPantry", "we", "us") is operated by [LEGAL ENTITY NAME], [ENTITY TYPE AND STATE OF
FORMATION], [REGISTERED ADDRESS]. You can reach us at robert@creatorpantry.com.

These Terms of Service ("Terms") govern your use of the CreatorPantry website, web application and related
services (the "Service"). Our [Acceptable Use Policy](acceptable-use-policy.md) and [Privacy Policy](privacy-policy.md) are part of these Terms. By creating an account or using the Service, you agree to these Terms. If
you use the Service for a business, you agree on its behalf and confirm you have authority to do so.

## 2. Accounts and workspaces

- You must be at least [18] years old, or the age of majority where you live, to use the Service.
- Give us accurate sign-up information and keep it current. You are responsible for your password and for
  everything that happens under your account. Tell us promptly at robert@creatorpantry.com if you think your
  account has been used without your permission.
- An account belongs to one person. Your work lives in **workspaces**. The person who creates a workspace is
  its **Owner**, and each person in a workspace has a role (Viewer, Contributor, Editor or Owner) that decides
  what they can do there *(product fact)*.
- A workspace's Owner is responsible for who has access to it and for the AI features switched on in it.

## 3. The Service is early and will change

CreatorPantry is in early development. We add, change and sometimes remove features. Anything described as
"coming", "in the works" or similar is a plan, not a promise, and is not part of what you are buying until it
is available to your account. We will tell you in advance, by email or in the Service, before we remove a
paid feature you rely on [NOTICE PERIOD, e.g. 30 days].

## 4. Your content

**You own your content.** "Your content" means everything you or your workspace members put into the Service
— recipes, notes, brand documents, pictures, prompts — and the output the Service generates for you (see
section 5).

**What you let us do with it.** You give us a worldwide, non-exclusive, royalty-free licence to host, store,
copy, process, display and transmit your content **only as needed to run the Service for you**: for example, to
show it to the people in your workspace, to make picture renditions, to send a prompt to an AI provider you
have switched on, or to deliver a post you have scheduled. This licence ends when your content is deleted from
the Service, except for copies in routine backups that age out [BACKUP RETENTION PERIOD] and anything we must
keep by law.

**What we don't do with it.**
- We do not use your content to train AI models.
- We do not use one workspace's content to ground or inform AI work in another workspace *(product fact)*.
- We do not write the contents of your prompts or drafts to our application logs *(product fact)*.
- We do not sell your content.

[CONFIRM WITH COUNSEL AND EACH PROVIDER'S CURRENT TERMS: whether each AI provider in section 7 may retain or
train on submitted content, and state the result here.]

**Your responsibility.** You confirm you have the rights to the content you upload, including photographs and
recipes from other people, and that using it in the Service does not break the law or anyone else's rights.

## 5. AI features and what they generate

- **Off until you switch them on.** AI features are off in a new workspace. Its Owner chooses which to switch
  on *(product fact)*.
- **Proposals, not changes.** AI output is a draft or a proposal. It does not change your recipes or other
  content until you accept it *(product fact)*.
- **Ownership of output.** As between you and us, you own the output the Service generates for you. To the
  extent we have any rights in that output, we assign them to you. Some places do not grant copyright in
  material generated by AI, so we cannot promise that output is protectable, and similar output may be
  generated for other people.
- **Accuracy.** AI can be wrong, incomplete or out of date. Review everything before you publish it or rely on
  it, especially quantities, times, temperatures and anything about health or safety.
- **Picture safety.** Picture generation runs with the provider's safety filtering on, and pictures it flags
  are not shown to you *(product fact)*. Filtering is not perfect, and you remain responsible for what you
  publish.
- **Limits.** AI use is measured in credits and limited by your plan — including a daily picture limit that
  counts your pictures across every workspace you work in — and by fair-use limits per workspace that protect the
  Service *(product fact)* (section 8).

## 6. Food, health and safety

The Service helps you write and present recipes. It does not give medical, dietary, nutritional or food-safety
advice, and it never certifies a dish.

- Dietary and allergen information describes what a recipe says. It is not a guarantee that a dish is free of
  any allergen or suitable for any person. Always check every label.
- Nutrition figures are estimates, labelled with their method and source.
- Warnings about preservation, canning, fermentation, cooking temperatures and similar methods are prompts to
  check a trusted source. Finding no warning does not mean a recipe is safe.

You are responsible for the recipes and claims you publish to your audience.

## 7. Third-party services

The Service relies on other companies, including [CONFIRM THE LIST AT PUBLICATION]: Microsoft (Azure AI
Foundry) for text AI, Venice.ai for pictures, Buffer for scheduling social posts, the USDA FoodData Central
service for nutrition evidence, and Stripe — through Link, its merchant-of-record service — for payments
(section 8). When you use a feature that depends on one of them, their own terms may also apply.

- **Publishing.** Nothing is posted to an external service without your explicit confirmation *(product
  fact)*. Scheduling through Buffer is not yet available to every account.
- We are not responsible for a third party's outages, changes, or decisions about your account with them. A
  third-party outage never stops you reading the content you already have in CreatorPantry *(product fact)*.

## 8. Trials, plans, credits and payment

> Billing is not live yet *(product fact)*. This section describes the decided model and must be checked
> against the shipped billing work (AP.6 and AP.7) before publication.

**Free trial**

- New accounts get a free trial of 14 days. No card is needed to start it.
- During the trial you can generate at most [10] pictures in all, beside the trial's daily picture limit. The
  trial's credits are given once, not every month.
- Running out of trial credits or pictures stops AI work but does not end the trial; the trial ends after its 14
  days.
- Because we never take a card for the trial, it never turns into a paid plan by itself. [CONFIRM (AP.7.1): what
  an account can still do once its trial has ended and no plan has been chosen.]

**Plans**

- Each account has one plan at a time, shared across every workspace it works in *(product fact)*. Plan
  features, prices, included credits and picture limits are shown on our pricing page at the time you buy.
- You pay for a plan monthly or yearly, in advance.
- **Upgrading** takes effect straight away. You are charged the difference for the rest of the current period,
  and you get the difference in credits as soon as that payment goes through.
- **Downgrading** takes effect at the end of the current period. You keep your current plan until then.

**Credits**

- Plans include a monthly allowance of AI credits, and you can buy extra credit packs. You pay before you use.
  **We never bill you afterwards for AI use**: when your credits run out, new AI requests are refused until your
  allowance renews or you add credits *(product fact)*. Your existing content stays available.
- **Plan credits** do not carry over to the next period [PROPOSED, NOT YET DECIDED].
- **Pack credits do not expire.** They keep until you spend them, and are used after your plan's credits for the
  period, oldest pack first *(product fact)*.
- Credits have no cash value, cannot be transferred, and are not refundable except as this section or the law
  provides.
- **Running low.** We never buy credits for you. When your credits run low, we tell you and offer a pack, and you
  decide whether to buy one. The notice is on by default, you are told at most once in each allowance period, and
  you can turn it off in your account at any time *(product fact)*. Turning it off changes nothing else: when your
  credits run out, new AI requests are still refused and you are still told why.

**Who you pay, and tax**

- Purchases are made through Stripe's checkout and are sold through **Link**, operated by Stripe, which acts as
  the merchant of record. Link processes your payment, calculates and collects any sales tax, VAT or GST, and
  sends your receipts. Your card statement shows `LINK.COM*` followed by [STATEMENT DESCRIPTOR]. Link's own terms
  apply to the purchase. [COUNSEL: review the Stripe Managed Payments terms and how the relationship between the
  customer, Link and CreatorPantry should be described — including which party is the seller, who handles
  consumer-law obligations such as withdrawal rights, and how these Terms should refer to Link's.]
- Prices are set in US dollars. Checkout may show and charge the equivalent in your local currency. [OPEN:
  whether prices shown on our pricing page include tax, or tax is added at checkout.]

**Renewal, price changes and cancelling**

- Subscriptions renew automatically at the end of each monthly or yearly period until cancelled.
- We may change prices with at least [30] days' notice; a change takes effect at your next renewal.
- You can cancel at any time from the Billing page in your account settings, which opens Stripe's billing portal.
  Cancelling takes effect at the end of the current period, and you keep your plan until then. You can also
  manage the purchase through Link.
- [OPEN (AP.6.2): what happens when a renewal payment fails — how long we keep your plan while payment is retried,
  and what the account becomes if it never succeeds.]

**Refunds and disputes**

- If a pack is refunded or its payment is disputed, the credits from that pack that you have not yet spent are
  removed. Credits already spent are not taken back from your plan's allowance.
- Link may issue refunds directly, in some cases within 60 days of a purchase, as part of handling customer
  support for the payment.
- [OPEN: our own refund policy for subscriptions and packs (AP.6.3).]

## 9. Acceptable use

You agree to follow the [Acceptable Use Policy](acceptable-use-policy.md). We may remove content or limit
features that break it.

## 10. Suspension, termination and your data

- You can stop using the Service at any time. [OPEN: an account-deletion path does not exist in the product
  yet; describe how a person asks for deletion, e.g. by writing to robert@creatorpantry.com.]
- We may suspend or end your access if you seriously or repeatedly break these Terms, if you don't pay, or if
  we must by law. Where reasonable, we will warn you first and give you a chance to fix the problem.
- **Getting your content out.** You can download recipes (PDF, Markdown or JSON-LD), long-form pieces (Markdown
  or HTML) and pictures at any time while your account is active *(product fact)*. After your account ends, we
  keep your content for [RETENTION PERIOD, e.g. 30 days] so you can ask for it back, then delete it.
- If we discontinue the Service, we will give you at least [60] days' notice to download your content.

## 11. Our property

The Service, its software, design and the CreatorPantry name and logo belong to us or our licensors. These
Terms don't give you any rights in them except the right to use the Service as described here. If you send us
feedback, we may use it without owing you anything.

## 12. Disclaimers

The Service is provided "as is" and "as available". To the fullest extent the law allows, we disclaim all
warranties, express or implied, including merchantability, fitness for a particular purpose and
non-infringement. We don't promise the Service will be uninterrupted, error-free, or that AI output will be
accurate or suitable for any purpose.

## 13. Limitation of liability

To the fullest extent the law allows, neither we nor our suppliers are liable for indirect, incidental,
special, consequential or punitive damages, or for lost profits, revenue, data or goodwill. Our total liability
for any claim relating to the Service is limited to the greater of the amount you paid us in the [12] months
before the claim, or [US$100]. Some places don't allow these limits, so they may not apply to you.

## 14. Indemnity

If you use the Service for a business, you will defend and indemnify us against third-party claims arising from
your content, from what you publish, or from your breach of these Terms. [COUNSEL TO REVIEW SCOPE.]

## 15. Changes to these Terms

We may update these Terms. If a change is material, we will tell you by email or in the Service at least [30]
days before it takes effect. Continuing to use the Service after that means you accept the new Terms.

## 16. Governing law and disputes

These Terms are governed by the laws of [STATE / COUNTRY], without regard to conflict-of-law rules. [OPEN:
courts of [VENUE], or arbitration and a class-action waiver — counsel to advise.] Before filing a claim, please
write to robert@creatorpantry.com so we can try to resolve it informally.

## 17. General

These Terms are the whole agreement between you and us about the Service. If a part is unenforceable, the rest
still applies. If we don't enforce a part, we haven't waived it. You may not transfer these Terms without our
consent; we may transfer them in a merger, acquisition or sale of assets.

## 18. Contact

[LEGAL ENTITY NAME] · [ADDRESS] · robert@creatorpantry.com
