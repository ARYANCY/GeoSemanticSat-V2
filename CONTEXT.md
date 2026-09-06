# CONTEXT

Shared vocabulary for UpaGraha / GeoSemanticSat. This file is a glossary only — no
implementation detail, no plans. If a term here conflicts with a label in the UI or a
name in the code, this file wins and the code should be corrected.

## Analysis subjects

**Observation**
A single satellite image of a place at a single moment. What gets ingested and indexed.

**Patch**
A small square cut out of an Observation. The unit the system actually searches over,
not a unit an analyst thinks in. Avoid showing this word to analysts.

**Result**
A Patch returned by a search, ranked by how well it matches what the analyst asked for.
A Result says *"this looks like what you described"*. It makes no claim that anything
changed. Ranked by similarity, never shown when similarity is at or below zero — a
negative score means the opposite of the query and is not a result.

**Candidate**
A place the system believes physically changed between two dates, which **no human has
checked yet**. A Candidate always carries a change type (Construction, Clearance, Water
Extent Variation, Road Development, Activity Concentration).

**Verified**
A Candidate a human analyst has examined and confirmed. The only state that may be
presented as fact.

**Rejected**
A Candidate a human analyst examined and dismissed. Rejected is a real outcome and is
recorded — it is not the same as "not yet reviewed".

### The distinction that matters

Result, Candidate and Verified are three different things and the word *candidate* must
mean only the middle one. Previously the UI printed `Match #1 … [CANDIDATE]` (a search
Result) and `Candidate Patch S2_20240` (also a search Result) on the same card, while the
header counted change Candidates. Three meanings, one word.

- A search **Result** answers "does this look like what I asked for?"
- A **Candidate** answers "did this change, and how?"
- **Verified** answers "did a person agree?"

A Result is not evidence of change. A Candidate is not evidence of anything until it is
Verified.

## Scoring

**Similarity**
How closely a Patch matches a text query, from -1 to 1. A property of a Result.
Not a probability and not a confidence.

**Evidence score**
How strongly the measurements support a Candidate's change type, from 0 to 1.
**Explicitly not a calibrated probability.** It ranks Candidates against each other. It
must never be presented as "an N% chance this is real" without a labelled validation set.

**High-confidence**
A Candidate whose Evidence score is at or above 0.85. This threshold is a single agreed
number; any count of high-confidence Candidates must be derived from it, never asserted.

## Workflow

The analyst moves through five Stages. A Stage is a step in the review, not a screen.

1. **Discover** — describe what you are looking for; get Results.
2. **Target** — pick which Results are worth investigating.
3. **Verify** — compare dates on a target; Candidates become Verified or Rejected.
4. **Group** — cluster nearby Verified sites into one facility or area of interest.
5. **Sign off** — export the audit trail.

**Stage** is the canonical word. Not "step", not "tab", not "phase".

## Imagery

**Band**
One measured slice of the light spectrum (Red, Green, Blue, Near-Infrared, Shortwave
Infrared). Analysts see band *names*, never band indices.

**Basemap**
The background map behind the analysis. Purely context — it is never the data being
analysed, and must never be confused with an Observation.

**Air-gapped**
The system runs with no internet and no external service. A Basemap fetched from the
internet is therefore an online convenience, not part of the air-gapped guarantee. Claims
of air-gapped operation must not depend on any Basemap being reachable.
