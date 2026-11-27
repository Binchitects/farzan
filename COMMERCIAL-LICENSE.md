# Farzan Arena Commercial License

Copyright (C) 2026 Binchitects. All rights reserved.

These are the standard terms on which Binchitects ("Licensor") licenses Farzan
Arena for uses the GNU Affero General Public License, version 3 (the "AGPL")
does not allow. They take effect only in a written agreement signed by the
Licensor and the licensee ("Licensee") that names this document and its version
(the "Agreement"). Without a signed Agreement nobody holds a commercial license,
and every use of the software is under the AGPL alone.

Version 1.0, 2026-11-27.

## 1. Definitions

- **"Licensed Software"** means Farzan Arena, Farzan Graph, Code Arena and every
  other work in the Licensor's repository <https://github.com/Binchitects/farzan>
  that the Licensor owns: its source code, object code, container images,
  installers, documentation, user interfaces and data, in every version the
  Agreement covers, and every update the Licensor delivers under it.
- **"Derivative"** means any work based on the Licensed Software within the
  meaning of copyright law. This includes a modified version, a translation into
  another programming language, a port, and any work that contains, links to,
  embeds or is compiled from any part of it.
- **"Derived Engine"** means a Derivative that is an engine, gateway, runtime,
  server, library, SDK, plugin host, agent framework or other component that
  others can build on or run. A Derived Engine is a Derivative, whether it is
  sold, given away, published as source, or offered over a network.
- **"Commercial Product"** means any product, service, appliance, hosted or
  managed offering, subscription, or bundle that the Licensee or its Affiliates
  sell, license, rent or provide for a fee or other consideration, and that
  includes, is a Derivative of, is distributed with, or is provided with the
  help of the Licensed Software or a Derived Engine.
- **"Sale"** means each transaction in which the Licensee, its Affiliates, or
  their distributors or resellers receive consideration for a Commercial Product.
- **"Gross Sales"** means the total amount invoiced or received for every Sale,
  before any deduction, in United States dollars. It excludes only (a) sales,
  use and value-added taxes collected and paid to a tax authority, and
  (b) amounts actually refunded to customers. Consideration that is not money is
  counted at its fair market value. When a Commercial Product is sold in a
  bundle, Gross Sales is the price of the whole bundle unless the Licensor
  agrees in writing to a different allocation.
- **"Cumulative Gross Sales"** means the Gross Sales of the Licensee and all of
  its Affiliates, added together from the first Sale under the Agreement to the
  Sale in question.
- **"Affiliate"** means any entity that controls, is controlled by, or is under
  common control with the Licensee.

## 2. License grant

Subject to the Agreement and to timely payment of the royalties, the Licensor
grants the Licensee a non-exclusive, non-transferable, worldwide license, for
the term of the Agreement, to use, modify and create Derivatives (including
Derived Engines) of the Licensed Software, and to distribute and provide them as
part of Commercial Products, under terms the Licensee chooses, without the
AGPL's obligation to release source code.

The license does not allow the Licensee to:

1. sublicense the Licensed Software or a Derived Engine as a stand-alone
   development platform, engine or SDK to third parties who would then build
   their own products on it, unless the Agreement says so expressly; each such
   third party needs its own commercial license;
2. remove or alter the Licensor's copyright notices, the SPDX license
   identifiers, or the license and source notices in the software;
3. use the names "Farzan Arena", "Farzan Graph", "Code Arena" or their logos,
   except as the Agreement or section 8 allows;
4. assign or transfer the Agreement, by change of control or otherwise, without
   the Licensor's prior written consent.

All rights not expressly granted are reserved by the Licensor.

## 3. Royalties

The Licensee pays the Licensor a royalty on the Gross Sales of every Sale. The
rate depends on the Cumulative Gross Sales, in tiers. Each rate applies only to
the part of the Gross Sales that falls within its tier, like tax brackets:

| Cumulative Gross Sales (USD) | Royalty on the part within this tier |
|---|---|
| from $0 up to $500,000 | 15% |
| above $500,000 up to $2,000,000 | 10% |
| above $2,000,000 | 5% |

When one Sale crosses a tier boundary, the part of it below the boundary is
charged at the lower tier's rate, and the part above at the next tier's rate.

**Worked example.** A Licensee whose Cumulative Gross Sales reach $3,000,000 owes:

- 15% of the first $500,000 = $75,000
- 10% of the next $1,500,000 = $150,000
- 5% of the remaining $1,000,000 = $50,000
- in all, $275,000 (an effective rate of about 9.2%).

The tiers never reset: they count from the first Sale under the Agreement, across
all Commercial Products and all Affiliates. Splitting Sales among Affiliates,
distributors or products does not restart them.

A minimum annual royalty, an up-front fee, or a fixed fee instead of royalties
may be agreed in the Agreement. Without one, the royalties above apply.

## 4. Reports, payment and audit

Unless the Agreement sets other figures:

1. **Reports.** Within 30 days after the end of each calendar quarter, the
   Licensee sends the Licensor a written report of that quarter's Sales: the
   Commercial Products, the number of units or subscriptions, the Gross Sales,
   the Cumulative Gross Sales, and the royalty owed in each tier. A quarter with
   no Sales is reported as such.
2. **Payment.** The royalty for each quarter is due with its report, in United
   States dollars, by bank transfer to the account the Licensor names in
   writing, without set-off or deduction. If a tax authority requires a
   withholding, the Licensee pays enough more that the Licensor receives the
   full amount, unless the Agreement says otherwise.
3. **Late payment.** Amounts not paid when due carry interest at 1.5% per month
   (or the highest rate the law allows, if lower) from the due date until paid.
4. **Records and audit.** The Licensee keeps complete and accurate records of
   all Sales for at least five years after each Sale. Once a year, on 15 days'
   notice, the Licensor or an independent auditor it appoints may examine those
   records. If an audit finds an underpayment, the Licensee pays it with
   interest at once; if the underpayment exceeds 5% of the amount due for the
   audited period, the Licensee also pays the cost of the audit.

## 5. Ownership and contributions

The Licensed Software, and all copyrights and other intellectual property rights
in it, are and remain the property of the Licensor and its licensors. The
Licensee owns the code it writes itself, but grants the Licensor a perpetual,
worldwide, royalty-free license to any fix or improvement to the Licensed
Software that the Licensee sends to the Licensor. Nothing in the Agreement
transfers ownership of the Licensed Software.

## 6. Third-party components

The Licensed Software uses components owned by others (libraries, container base
images, programs, models and data), listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Each remains under its own
license, and this license does not and cannot grant rights to them. The
Licensee is responsible for complying with those licenses in its Commercial
Products, including any obligation to offer source code (for example for
components under the GPL or LGPL in the container images).

## 7. No warranty and limits of liability

1. **No warranty.** The Licensed Software is provided "as is" and "as
   available", with all faults. To the fullest extent the law allows, the
   Licensor disclaims every warranty and condition, express, implied or
   statutory, including warranties of merchantability, fitness for a particular
   purpose, title, non-infringement, accuracy, and that it will run without
   interruption or error. The output of any language model or other model the
   software runs may be wrong, and the Licensee is responsible for checking it
   before relying on it.
2. **Limit of liability.** To the fullest extent the law allows, the Licensor is
   not liable for any indirect, incidental, special, consequential, exemplary or
   punitive damages, or for loss of profits, revenue, data, goodwill or business,
   however caused and on any theory of liability, even if told they were
   possible. The Licensor's total liability under or in connection with the
   Agreement is limited to the royalties the Licensee paid in the twelve months
   before the claim arose.
3. **Indemnity.** The Licensee defends, indemnifies and holds harmless the
   Licensor, its owners, employees and contributors against every claim, loss,
   damage, cost and expense (lawyers' fees included) arising from the
   Licensee's Commercial Products, its modifications, its use of the Licensed
   Software, its breach of the Agreement, or its breach of any law or any
   third-party license.
4. **Compliance.** The Licensee is responsible for complying with all laws that
   apply to its Commercial Products and its use of the Licensed Software,
   including export control, sanctions, data protection and AI regulations.

## 8. Trademarks

The Agreement grants no right to the names "Farzan Arena", "Farzan Graph",
"Code Arena", "Binchitects" or their logos, except to state truthfully that a
Commercial Product is "based on Farzan Arena" or "powered by Farzan Arena". Any
other use needs the Licensor's written permission.

## 9. Term and termination

1. The Agreement runs for the term it states.
2. The Licensor may terminate the Agreement by written notice if the Licensee
   (a) fails to pay any amount within 30 days after notice that it is overdue,
   (b) fails to send a report, or under-reports Gross Sales, (c) breaches
   section 2, 5 or 8, or (d) challenges the Licensor's copyright in the
   Licensed Software.
3. On termination the commercial license ends. The Licensee must stop
   distributing and providing Commercial Products and Derived Engines, unless it
   complies fully with the AGPL for them. Royalties for Sales made before
   termination remain due. Sections 3 to 7, 9.3 and 10 survive termination.
4. Use of the Licensed Software outside the commercial license, or after it
   ends, and without complying with the AGPL, infringes the Licensor's
   copyright. The Licensor may seek every remedy the law provides, including
   injunctions, damages and the Licensee's profits.

## 10. General

1. **Governing law and courts.** The Agreement is governed by the laws of
   [JURISDICTION, to be stated in the Agreement], without regard to its rules on
   conflict of laws. Disputes go to the courts of [VENUE, to be stated in the
   Agreement], unless the Agreement provides for arbitration.
2. **Entire agreement.** The Agreement, with this document, is the whole
   agreement between the parties on its subject. Changes need a written document
   signed by both parties.
3. **Severability.** If any provision is held unenforceable, it is enforced to
   the greatest extent possible and the rest of the Agreement stays in effect.
4. **No waiver.** Not enforcing a provision is not a waiver of it.
5. **Order of precedence.** If the signed Agreement and this document conflict,
   the signed Agreement prevails.

## How to obtain a commercial license

Open an issue titled "Commercial license" at
<https://github.com/Binchitects/farzan/issues>, or contact Binchitects through
<https://github.com/Binchitects>. A commercial license exists only once an
Agreement is signed by both parties.

---

*This is a template of the standard commercial terms, not legal advice. A lawyer
admitted in the chosen jurisdiction should review it, and fill in the
bracketed items, before it is signed or relied on.*
