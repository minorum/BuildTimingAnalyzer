# Changelog

## 0.0.12

Fixes a regression introduced in v0.0.11: corporate antivirus engines started
flagging the release zip because the in-process pager introduced Win32 console input
API calls and terminal control sequence strings into the binary. Together those
looked like a classic keylogger signature to heuristic scanners.

### Pager reimplemented as an external subprocess
- The pager is now spawned as a separate process (like git, man, psql, kubectl do)
- The subprocess reads content from our stdin pipe and handles keyboard input itself
- Our binary no longer contains any keyboard-reading APIs or terminal control strings
- Same pager UX, better features (you get full `less` with search, regex, etc.)

### Pager selection
- `BTANALYZER_PAGER` environment variable (tool-specific override)
- `PAGER` environment variable (standard Unix convention)
- Platform default: `more` on Windows, `less -R -F` on Unix
- `-F` makes `less` auto-exit when the content fits in one screen, so short reports
  don't show a pager prompt
- `--no-pager` flag still works the same way

### Behavior
- If the selected pager is not found, falls back to direct output
- If stdout is redirected (piped to a file or grep), the pager is skipped automatically
- If writing to the pager fails mid-stream (user quit with `q`), we handle the broken
  pipe cleanly and exit normally

### Binary profile
- No `Console.ReadKey` anywhere
- No ANSI escape sequences in string literals
- Import table matches v0.0.10 (no new console input APIs)
- Should no longer trip corporate AV heuristics that flagged v0.0.11

## 0.0.11

Report information architecture overhaul. The default report is now answer-first, not
dashboard-first: you see the summary, the headline, and the analysis before anything else.
Supporting data follows in descending order of usefulness, and sections must earn their
place by actually having something to say.

### New default section order
1. Summary
2. **Headline** — 2–4 line factual synthesis with a pointer to the top finding
3. **Analysis** — findings moved to the top, directly after the headline
4. Build Context (demoted, more compact)
5. Top Projects with inline composition + target drill-down
6. Top Targets
7. Self Time by Category
8. Reference Overhead
9. **Dependency Graph** — consolidated parent section containing Graph Health, Cycle
   Status, Critical Path Validation, Dependency Hubs, and the Critical Path Estimate
10. Build Timeline
11. Span Outliers
12. Project Count Tax
13. Potentially Custom Targets

### Severity-ordered findings
- Findings are now sorted Critical → Warning → Info, then by detection order within a
  severity. Numbering reflects the new order. The top finding is always the most
  actionable one.

### Signal-based section gating
- Sections must earn their place, not just meet a project-count threshold:
  - Dependency Graph: hidden unless graph has ≥ 2 projects AND ≥ 1 edge
  - Timeline: hidden for 1–2 project solutions, or when all projects span the full wall clock
  - Category Totals: hidden unless ≥ 3 categories are non-trivial
  - Project Count Tax: shown either when the solution has ≥ 10 projects OR when any indicator is non-zero
  - Potentially Custom Targets: hidden unless at least one item has self time ≥ 1s (unchanged)

### Collapsed graph sections
- Previous separate sections (Dependency Graph Health, Dependency Hubs, Cycle Check,
  Critical Path Validation, Critical Path Estimate) were competing as top-level peers.
  They are now subsections of a single **Dependency Graph** section with compact one-line
  headers for health/cycles/validation and full subsections for hubs and the critical path.
- Cycle status is a one-liner ("none detected" or "N detected, first: A → B → C → A"),
  never a full section anymore.

### Interactive console pager
- New built-in pager modeled on `less`, with zero third-party dependencies
- Uses ANSI alternate screen buffer so your scrollback stays intact on exit
- Keys: Space / PgDn / f (down), b / PgUp (up), j/k or arrows (line), g/Home (top),
  G/End (bottom), q / Esc (quit)
- Status bar at the bottom shows position and key hints
- Auto-disabled when stdout is redirected (piped to a file, CI, grep, etc.)
- Auto-disabled when the report fits on screen without scrolling
- New `--no-pager` flag to opt out explicitly

## 0.0.10

Build reproducibility.

- Default to `--no-incremental` when invoking `dotnet build`, so running btanalyzer twice
  on the same workspace produces comparable numbers instead of mixing "expensive" with
  "happened to be cached"
- New `--incremental` flag opts back into the old behavior for measuring the dev inner loop
- Build mode surfaced in the Build Context section (`full (--no-incremental)` or `incremental`)
- Add `-nologo` to the dotnet build invocation to cut noise
- Build Context fields are also exported in JSON (new `buildMode` field) and HTML report

Note: even with `--no-incremental`, MSBuild still skips some internal targets whose inputs
haven't changed (restore graph resolution, etc.). The `Incremental` line in Build Context
still reports the actual executed/skipped ratio so the behavior is visible.

## 0.0.9

Evidence/interpretation separation release. The report now keeps measured facts,
heuristic explanations, and investigation suggestions in strictly separate layers, adds
several new solution-shape insight sections, and tones down every finding that previously
snuck interpretation into the measured bucket.

### Finding structure refactor
- `AnalysisFinding` split into `Measured`, `LikelyExplanation` (optional), and `InvestigationSuggestion`
- Console and HTML renderers show the three layers distinctly
- Titles made factual: "holds the largest share" → "Largest self-time share: X"
- "self time is Xx the next project" → "Largest self-time gap to next project: X"
- "is an outlier" → "Target outlier: X in Y"
- "broadly distributed" (was "systemic") — the hypothesis moved to `LikelyExplanation`
- Every existing finding audited to prevent interpretation leaking into `Measured`

### Transitive graph metrics
- `ProjectGraphNode` now includes `TransitiveDependentsCount` and `TransitiveDependenciesCount`
- Hubs table sorted by transitive dependents (downstream subtree size), not immediate fan-in
- Softened description: high fan-in is a structural signal, not automatic bottleneck status

### Cycle status always shown
- "No project-reference cycles detected" when empty — never silent
- Dedicated section with explicit detection status

### Critical path validation status
- New **Critical Path Validation** section: shows CPM total, wall clock, accept/reject, and reason
- Rendered whenever the graph has content, independent of whether the path itself is rendered
- Section title softened: "Critical Path Estimate (model-based, not a scheduler trace)"
- Findings describe it as "an estimate" with explicit caveats about DAG accuracy

### Per-project category composition
- Top projects and critical-path projects get an inline composition row in the projects table:
  `composition: compile 42%, references 28%, restore 18%, copy 12%`
- Normalised so percentages always sum to 100%

### Project Count Tax section
- New section with concrete indicators:
  - Projects where references &gt; compile
  - Projects where references are the **majority** of self time (&gt; 50%)
  - Projects matching the span-outlier rule
- Per-kind median self time, median span, and median span/self ratio
- Kinds come from name-based heuristic detection (explicitly labelled as heuristic)

### Project kind heuristic
- New `ProjectKind` enum: `Test`, `Benchmark`, `Other`
- Used in span outliers, critical path, and project-count-tax sections
- **Always labelled "heuristic"** in the UI — never presented as authoritative
- Critical path finding notes when the path runs through test/benchmark projects so the reader can weight them

### Language softening across sections
- Hubs description: "high fan-in is a structural signal, not automatic proof of bottleneck status"
- Span-outlier note lists possible causes explicitly (dependency waiting, SDK orchestration, reference work, static-web-assets, test/benchmark shape, incremental effects) instead of asserting one
- Reference overhead finding framed as "broadly distributed", with the project-count hypothesis explicitly tagged as `LikelyExplanation`
- Critical path recommendations: "candidate list for sequential-ordering investigation" instead of "they determine the lower bound on build time"

### Warning reconciliation strengthened
- Warning finding explicitly distinguishes "top attributed sources" from total attributed and unattributed

## 0.0.8

Graph-level insights release. The report now surfaces solution-shape cost, not just local
hotspots. Fixes a broken critical path and a warning accounting bug.

### Trust fixes
- **Warning reconciliation**: the report now tracks attributed vs unattributed warnings
  separately. Summary shows `X total (Y attributed, Z unattributed)`. Findings explicitly
  say "attributed warnings" when discussing source breakdowns. Totals always reconcile.
- **Fixed dependency DAG extraction**: v0.0.7 used `ProjectStartedEventArgs.ParentProjectBuildEventContext`
  which in solution builds points to the solution metaproject, not project references. This made
  the DAG shallow and the critical path almost always a single node. v0.0.8 extracts real project
  references from `ProjectEvaluationFinishedEventArgs.Items` (`ProjectReference` item type), with
  `ProjectStartedEventArgs.Items` as a fallback.
- **Hard rule: invalid critical path never leaks into presentation**. If the graph has too few
  edges or CPM validation fails, the critical path section, timeline highlighting, projects-table
  markers, and all critical-path findings are suppressed together.

### New: Dependency Graph Health
- Total projects / edges / isolated nodes / nodes with outgoing / incoming
- Longest chain by project count
- Explicit warning when extraction produced too few edges (helps catch broken extraction early)

### New: Dependency Hubs
- Top projects by fan-in (referenced by the most projects) + fan-out
- Helps identify structural bottlenecks that block downstream scheduling

### New: Dependency Cycle Detection
- DFS-based cycle detection with explicit reporting
- Silence is no longer acceptable — if no cycles exist, nothing is shown (the finding is only rendered when cycles are found)

### New: Self Time by Category
- Aggregate self time per category across all projects
- The data was already computed in v0.0.7 but not rendered; now it is
- Shown as a dedicated table with bars

### New: Reference Overhead
- Aggregated reference-related self time across the solution
- Total, % of self, paying-projects count + pct, median per paying project, top 10
- Answers "how much is my solution paying for repeated reference resolution?"

### New: Span vs Self Outliers
- Projects where wall-clock span is much longer than local work
- Rule: Span ≥ 5s, SelfTime > 0, Span/SelfTime ≥ 5x, Span − SelfTime ≥ 3s
- Highlights projects that are waiting on the graph rather than doing heavy local work

### New: Evidence-driven findings
- **"Reference-related build work appears to be systemic, not isolated"**: fires when reference
  overhead is ≥ 10% of self time, paid by ≥ 50% of projects, with median ≥ 250ms per paying project
- **"N project(s) have long span relative to low self time"**: fires when ≥ 3 span outliers match the rule above
- All existing findings continue to cite concrete metrics and named threshold constants

### Demoted
- **Potentially Custom Targets** now only shows entries with self time ≥ 1s, capped at 5 rows.
  No more noisy lists of tiny SDK plumbing targets.
- Expanded the `TargetCategorizer` SDK pattern list to correctly bin more targets
  (`GenerateGlobalUsings`, `PrepareForBuild`, `ResolveFrameworkReferences`, etc.)

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
