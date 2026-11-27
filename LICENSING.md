# Licensing

Farzan Arena (with Farzan Graph, Code Arena and everything else in this repository,
unless a file says otherwise) is **dual-licensed**:

1. under the **GNU Affero General Public License, version 3 only**
   (AGPL-3.0-only, the full and unchanged text in [LICENSE.md](LICENSE.md)), with
   the additional terms below that section 7 of the AGPL permits; or
2. under a **commercial license** from the copyright holder, Binchitects, on the
   terms in [COMMERCIAL-LICENSE.md](COMMERCIAL-LICENSE.md), which include
   royalties on sales.

Copyright (C) 2026 Binchitects and contributors. All rights reserved, except as
these licenses grant.

Every source file says so in its first lines:

```
SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
Copyright (C) 2026 Binchitects
```

`LicenseRef-Farzan-Commercial` is the commercial license in COMMERCIAL-LICENSE.md.
`tools/licence-check.py` keeps those lines on every file, and the release does
not go out without them.

## Using it under the AGPL

Anyone may use, study, change and share Farzan Arena under the AGPL, at no cost
and with no royalty. The AGPL is a strong copyleft license, and its conditions
apply to every use, commercial or not:

- **Running a modified version for others over a network** (inside a company
  included) obliges you to offer every user of it the complete source code of
  your version, under the AGPL (section 13). The "Source" link the app shows
  beside its version is how this version offers it; a modified version must
  point it at its own source.
- **Distributing it** (as source, binaries, images, packages or in a product)
  is allowed only under the AGPL, with the complete corresponding source, and
  only as a whole under the AGPL: it cannot be combined into software you
  distribute under other terms.
- **Developing and publishing an engine from it.** An engine, gateway, runtime,
  server, library, SDK, plugin host or other component built from any part of
  Farzan Arena (a "derived engine") is a work based on it. Whoever publishes,
  distributes or offers a derived engine over a network must license the whole
  of it, and every work based on it that they convey or offer with it, under
  the AGPL, and give its complete source to everyone who receives it or uses it. A derived engine that is
  closed, proprietary, under another license, or sold without its source, is
  allowed only under the commercial license, and its sales pay the royalties
  below. This is what the AGPL itself requires; it adds no new restriction.
- There is no warranty and no liability (sections 15 and 16).

## When a commercial license is required

Commercial use that does not fully meet the AGPL's conditions needs a commercial
license. In particular, a commercial license is required to:

- offer Farzan Arena, Code Arena or a modified version of either as a hosted
  service, a managed offering or part of a paid product **without** releasing
  your complete source code under the AGPL to its users;
- develop and publish, sell or offer a **derived engine** (above) under any terms
  other than the AGPL;
- embed or bundle any part of it in proprietary or closed-source software,
  appliances or images, or distribute it under any license other than the AGPL;
- keep your modifications private while others use them over a network;
- use the names or logos below in a product, service or offering
  (see the trademark terms).

### Royalties under the commercial license

The commercial license charges a royalty on every sale of a product or service
that includes, is derived from or is provided with Farzan Arena (a derived engine
included), in tiers of the licensee's cumulative gross sales. Each rate applies
only to the part of the sales within its tier:

| Cumulative gross sales (USD) | Royalty |
|---|---|
| up to $500,000 | 15% |
| from $500,000 up to $2,000,000 | 10% |
| above $2,000,000 | 5% |

For example, cumulative sales of $3,000,000 owe $75,000 + $150,000 + $50,000 =
$275,000. [COMMERCIAL-LICENSE.md](COMMERCIAL-LICENSE.md) defines the terms
(sales, derived engines, affiliates), and sets the reports, payment and audit.

A commercial license replaces the AGPL's obligations with the terms agreed in it
(for example: private modifications, closed distribution, support), and is
granted only in a written agreement signed by Binchitects. Using the software in
any of the ways above without one, and without meeting the AGPL, infringes the
copyright. To ask for a commercial license, open an issue titled "Commercial
license" at <https://github.com/Binchitects/farzan/issues> or contact Binchitects
through <https://github.com/Binchitects>.

If you are unsure whether your use needs a commercial license, ask before you use
it. Nothing in this file reduces the rights the AGPL grants; it says what the
AGPL does not grant.

### Versions already released

Each version is licensed on the terms in its own copy of this file. A version
already released under the AGPL stays available under the AGPL to those who
received it. The royalty schedule applies to every commercial license signed
from 2026-11-27, for every version.

## Additional terms under section 7 of the AGPL

These terms are added to the AGPL as section 7 of the AGPL permits, and apply to
every copy and every modified version:

1. **Attribution and source notice (section 7(b)).** The interactive user
   interfaces of the software show the product's license and a "Source" link (in
   the web app and in Farzan Graph's own web app, beside the version at the foot
   of the navigation; in Code Arena, in its about box and `--version` output).
   Those Appropriate Legal Notices, the copyright and license notices in the
   source files (their SPDX lines included), and this file must be preserved in
   every copy and modified version, and the "Source" link must lead to the
   complete source of the version being run.
2. **Origin (section 7(c)).** A modified version must be marked as changed, and
   must not be presented as the original or as made or endorsed by Binchitects.
3. **No endorsement (section 7(d)).** The names of Binchitects and of the
   contributors may not be used to promote a modified version without their
   written permission.
4. **Trademarks (section 7(e)).** The AGPL grants no rights to the names
   "Farzan Arena", "Farzan Graph" (as a product name), "Code Arena", "Binchitects"
   or their logos. You may say truthfully that your work is based on Farzan
   Arena, but a product or service offered to others under one of those names,
   or a confusingly similar one, needs written permission from Binchitects.
5. **Indemnity for those who add warranties (section 7(f)).** Anyone who conveys
   the software or a modified version with contractual assumptions of liability
   to the recipient indemnifies Binchitects and the contributors for any
   liability those assumptions impose on them.

## Third-party components

Components and data from others keep their own licenses, and those licenses,
not this one, govern them. [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) lists
each package the release ships, at its version, with its license: the npm and
NuGet packages, the Python packages and programs the images install, and the
base images. `tools/licence-check.py` writes it, and fails when a package's
license is not one [docs/compliance/licence-policy.yaml](docs/compliance/licence-policy.yaml)
allows or accepts.

Also under their own licenses: the language, speech, picture and video models
(each under its own license, which its model card on Hugging Face gives;
Admin → Models → **Find on Hugging Face** shows it before a download), Laya
(Apache-2.0), sqlite-vec's Linux build in `native/` (MIT or Apache-2.0, taken from
its published wheel and checked against its SHA-256), the Lucide icons (ISC), and
the knowledge packs (CC-BY-4.0, CC-BY-SA-3.0, PSF-2.0, GFDL-1.3 or public domain,
per pack; `code-intel pack info <name>` prints each).

### Source of the container images

The container images Binchitects publishes (`ghcr.io/binchitects/farzan/*`)
contain programs under the GPL, the LGPL and other licenses that require their
source to be available: the packages of each image's base distribution (Debian
or Ubuntu), and programs an image installs, such as Blender (GPL-2.0-or-later) in
the blender image, Universal Ctags (GPL-2.0-or-later) and opengrep (LGPL-2.1). Each
runs as a separate program; none is combined with Farzan Arena's own code.

- The Dockerfiles in this repository say exactly what each image contains, at
  which version.
- The source of a distribution package is in that distribution's archive at the
  same version (`apt-get source <package>`, or <https://snapshot.debian.org>).
- **Written offer.** For three years after Binchitects last publishes an image,
  Binchitects will give anyone who asks a copy of the complete corresponding
  source of every component in it that is under the GPL or the LGPL, for no
  more than the cost of providing it. Ask by an issue titled "Source request" at
  <https://github.com/Binchitects/farzan/issues>, naming the image and its version.

## Contributions

Contributions are accepted under the terms in
[CONTRIBUTING.md](CONTRIBUTING.md#licensing-of-contributions), which let
Binchitects offer them under both licenses.

*This file explains the licensing; it is not legal advice. No license text can
stop every lawsuit, but clear terms that match what the code and images contain
make one far harder to bring and far easier to answer. Have a lawyer review this
file and COMMERCIAL-LICENSE.md before relying on them.*
