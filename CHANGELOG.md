# Changelog

## 0.0.7

Trust and interpretability overhaul — the report now tells you exactly what every
number means, where it came from, and what the analysis is actually based on.

### Terminology
- `Duration` renamed to `Self Time` (genuinely exclusive work for each project/target)
- New `Span` column (wall-clock first-to-last activity — display metric only, never reused as a cost input)
- `% of Build` renamed to `% Self` — share of total self time
- Consistent labels across console, HTML, JSON exports

### Build Context
- New **Build Context** block at the top of the report captures `Configuration`,
  SDK version, MSBuild version, OS, parallelism, and whether restore was observed
- Fields are only shown when reliably captured from the binlog — missing fields are omitted, never guessed
- Observed incremental behavior line: `X of Y targets skipped as up-to-date` (aggregate counts only,
  no inference of clean vs incremental build mode)

### Target Categorization
- New `TargetCategory` enum: `Compile, SourceGen, StaticWebAssets, Copy, Restore, References, Uncategorized, Other`
- Category column added to the targets table
- Categorization is deterministic pattern-matching against a fixed SDK target list — a grouping hint, not authoritative
- Dedicated **Potentially Custom Targets** section lists targets that didn't match any known SDK pattern
  (first-class hotspot view for optimization investigation)

### Critical Path (CPM)
- Project-level critical path computed using classic CPM:
  node cost = Self Time, edges = project dependency DAG from `ParentProjectBuildEventContext`,
  chain derived from earliest/latest finish with zero slack
- Validation gate: computed total must be ≤ wall-clock build time, otherwise the result is rejected and omitted
- Critical path gets a dedicated section and highlights the corresponding projects in the timeline and projects table
- New finding: "Critical path concentrates X% of total self time" when the path covers ≥30% of work
- Span is still displayed in the timeline and projects table, but is never used as a weighting input for the critical path

### Evidence-Driven Findings
- Every finding now cites the concrete metric that triggered it and the named threshold constant it crossed
- Thresholds are static named constants in `BuildAnalyzer` — no config, no percentiles, no runtime tuning
- Recommendations are phrased as **investigations to run**, never as structural conclusions ("investigate" instead of "split")
- Existing findings rewritten to match this rule — no more "consider splitting the project" from timing data alone
- Removed the dominant-target-type finding (called `CopyFilesToOutputDirectory` "compiler work" — incorrect)
- Removed the project-cluster finding (low signal, high noise)

### Drill-Down
- Top 3 projects by self time (plus anything on the critical path) now get inline target breakdowns
  in the projects table — no more mental stitching between sections
- Each drilled-down target shows its category badge

### Warning Reconciliation
- Warning findings now show top-N sources, explicit "remaining" count, and total attributed — they always reconcile exactly

## 0.0.6

- Fix timeline to show actual target execution spans instead of project instance lifetimes

## 0.0.5

- Add build timeline showing wall-clock project spans in console and HTML reports
- Visualize parallelism and sequencing at a glance

## 0.0.4

- Fix AOT crash when analyzing binlogs — preserve MSBuild.Framework types from trimming
- Include version in release archive filenames

## 0.0.3

- Remove Spectre.Console dependency entirely for full AOT compatibility
- Replace CLI framework with manual arg parsing (zero reflection)
- Replace rich console tables with plain-text formatted output

## 0.0.2

- Rename executable from `BuildTimeAnalyzer.TUI` to `btanalyzer`
- Show help/usage when run without arguments instead of throwing
- Read version from build-time constant instead of hardcoded string
- Strip PDB files from release builds

## 0.0.1

Initial release.

- MSBuild binary log analysis with streaming parser
- Exclusive duration calculation (subtracts MSBuild/CallTarget orchestration time)
- Project and target timing breakdown with percentage of total build time
- Automated heuristic analysis: bottlenecks, disproportion, clustering, dominant targets, outliers, costly package resolution, warning concentration
- Console output with rich ANSI-colored tables via Spectre.Console
- HTML report export (dark theme, self-contained)
- JSON report export (AOT-compatible source-generated serialization)
- Native AOT binaries for Windows, Linux, and macOS
