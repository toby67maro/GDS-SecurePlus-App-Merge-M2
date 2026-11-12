# Evaluation of the CIS Benchmark CSV Files (Copilot, 2026-11)

`Documents/Frameworks/CIS Benchmarks CSV` holds one CSV per CIS Benchmark PDF (99 columns, 9,694 recommendations from 57 files),
plus `CIS_Benchmarks_Column_Dictionary.csv`, `CIS_Benchmarks_Index.csv` and `CIS_Benchmarks_Staging_Load.sql`. They were
checked against the PDFs (`pdftotext -layout`) before they were used for knowledge base phase 2.

## Verdict

**Used, with corrections.** The structure, recommendation numbers, titles, profiles, sections, CIS Controls mappings and the verbatim
text are reliable and saved most of the PDF parsing. The registry paths are not reliable and are rebuilt from the verbatim audit text.

| Check | Result |
|---|---|
| Recommendations per benchmark vs. PDF headings | 9,694 = 9,694. 5 rows had a number taken from the title (`90`, `180`, `900`, `300`, `0`); corrected from the PDF, with their titles. |
| Titles | All 9,649 titles (after removing the duplicate SQL Server 2016 file) appear in the PDF. |
| CIS Controls v8 IDs | Every ID appears in the recommendation's CIS Controls table; rows without one are marked *Explicitly Not Mapped* in the PDF. 7,966 links loaded. |
| Group Policy paths, Settings Catalog paths | 99.9% found in the PDF (the rest differ only in quote characters). |
| Registry paths | **1,468 of about 5,300 wrong (28%)**: words lost where the PDF wraps a line (`WindowsServices` for `Windows NT\Terminal Services`, `WindowsGuard` for `Windows Defender\Windows Defender Exploit Guard`, `SessionManager`), keys cut at a space with an empty value name (`...\Microsoft\Windows:`), and `[USERSID]` for `[USER SID]`. Rebuilt from the audit text; where a wrap is ambiguous the spelling Microsoft publishes in its security baselines wins. 4,896 keys then match the PDF exactly; 142 differ only by a space where the PDF breaks a long name mid-word. |
| MITRE ATT&CK | Only 2 benchmarks print ATT&CK mappings (34 rows); 37 links loaded. |
| Duplicates | `CIS_Microsoft_SQL_Server_2016_Benchmark_v1.4.0_Archive (1).pdf` is byte-identical to the archive copy; skipped. |

## Licence

The CIS text (description, rationale, impact, audit, remediation, references) is loaded only into `cfg.RecommendationSourceText`
with `IsInternalOnly = 1`. `cfg.Recommendation` keeps the first sentence of the description, rationale and impact as summaries.

## Re-running

`build/scripts/knowledge/generate_baselines.py --documents <Documents> --pdf-text <folder of pdftotext output>` re-applies every
check and correction; each correction is listed in the output's `issues` array.
