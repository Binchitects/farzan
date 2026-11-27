# Common Criteria sources: a few records

`files/portal/ccfiles/cc_2022.xml` and `cc3R5.xml` are excerpts of the Common Criteria's own XML (classes FIA and ALC,
families FIA_UAU and ALC_FLR, and CC 3.1's EAL4), as the CC portal publishes them (https://www.commoncriteriaportal.org/cc/):
copyright the governmental organizations of the CCRA, together with ISO/IEC for CC:2022, whose Legal Notice grants every
CC user the re-use and publication of the text. Everything else here (the listings, the example PPs, the Supporting Document,
the Technical Decision) is written for these tests and names no real PP. The PDFs are placeholders: their text, as
pdftotext would give it, is in `text/<sha256>.txt`, which `tools/cc-texts.py` reads first. `tests/fixtures/code-intel/packs/commoncriteria`
is written from them by `tools/cc-texts.py --from`.
