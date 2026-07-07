# btanalyzer

A .NET CLI tool that analyzes MSBuild binary logs to identify build performance bottlenecks.

It runs `dotnet build` with binary logging, stream-parses the `.binlog`, and surfaces the slowest projects and targets with automated diagnostics.

## Installation

Download the latest binary for your platform from [GitHub Releases](../../releases) and place it on your `PATH`.

Available binaries: `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64`.

## Usage

```bash
# Analyze the solution in the current directory
btanalyzer build

# Analyze a specific project
btanalyzer build path/to/MyApp.csproj

# Release configuration, show top 10 results
btanalyzer build -c Release -n 10

# Export HTML, JSON, or Markdown report (format inferred from extension)
btanalyzer build -o report.html
btanalyzer build -o report.json
btanalyzer build -o report.md

# Analyze an existing binary log without building
btanalyzer analyze build.binlog -o report.html

# Pass extra arguments to dotnet build
btanalyzer build --args "--no-restore"

# Keep the binary log for further inspection
btanalyzer build --keep-log
```

## Commands

- `btanalyzer build [project]` — build a project/solution with binary logging, then analyze.
- `btanalyzer analyze <file.binlog>` — analyze a pre-existing binary log (e.g. a CI artifact) without building.
- `btanalyzer init [path]` — write a default `btanalyzer.json`.
- `btanalyzer config` — print the effective configuration.

## Options

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--configuration` | `-c` | `Debug` | Build configuration (`build` only) |
| `--top` | `-n` | `20` | Number of top results to display |
| `--output` | `-o` | | Export report to file (`.html`, `.json`, `.md`, or `.sarif`) |
| `--config` | | | Path to a `btanalyzer.json` (default: discovered near the project) |
| `--compare` | | | Compare against a JSON report; a file path or `git:<revspec>` (e.g. `git:origin/main:baseline.json`) |
| `--fail-on` | | | Exit non-zero for CI gating (see below) |
| `--history` | | | Append a one-line run summary (JSONL) for trend tracking |
| `--no-open` | | | Do not launch the browser after generating an HTML report |
| `--incremental` | | | Allow incremental build (default: `--no-incremental`; `build` only) |
| `--keep-log` | | | Keep the `.binlog` file after analysis (`build` only) |
| `--args` | | | Additional arguments passed to `dotnet build` (`build` only) |

## CI usage

`--fail-on` gates a pipeline; the tool exits non-zero when any comma-separated rule trips:

- `critical` — any Critical finding
- `warning` — any Warning or Critical finding
- `errors` — the build did not succeed cleanly
- `wallclock:<seconds>` — wall-clock exceeds N seconds
- `regression:<percent>` — worst of wall-clock/self-time regressed more than N% vs the `--compare` baseline

```bash
# Fail the build if any critical finding appears or self-time regresses > 10% vs a baseline
btanalyzer build --compare baseline.json --fail-on critical,regression:10 -o report.md
```

### GitHub Action

A composite action installs btanalyzer and runs it. Emit SARIF and upload it so findings show up as
inline PR annotations:

```yaml
- uses: minorum/BuildTimingAnalyzer@v0.0.16
  with:
    project: MyApp.sln
    output: btanalyzer.sarif
    args: '--compare git:origin/main:baseline.json --fail-on regression:10'
- uses: github/codeql-action/upload-sarif@v3
  with:
    sarif_file: btanalyzer.sarif
```

Or write a Markdown report straight into the job summary:

```yaml
- uses: minorum/BuildTimingAnalyzer@v0.0.16
  with:
    output: summary.md
- run: cat summary.md >> "$GITHUB_STEP_SUMMARY"
```

## Configuration (`btanalyzer.json`)

Optional. Discovered by walking up from the project directory, or set explicitly with `--config`.

```json
{
  "heavyPackages": ["My.Company.HugeSdk"],
  "thresholds": {
    "largestShareCriticalPercent": 30,
    "largestShareWarningPercent": 18,
    "costlyResolvePackageAssetsSeconds": 4,
    "tfmNegotiationAggregateSeconds": 120,
    "warningsOnCriticalPathPerProject": 50,
    "serializedBuildParallelismRatio": 0.5,
    "serializedBuildMinProjects": 5,
    "projectCountTaxMinProjects": 10,
    "projectCountTaxProjectSharePercent": 40
  }
}
```

Any field may be omitted; omitted fields keep their defaults. `heavyPackages` extends the built-in set.

## What it does

1. Runs `dotnet build -bl` on your project/solution
2. Stream-parses the binary log (memory-efficient, no full tree loading)
3. Computes **exclusive** build times by subtracting orchestration task durations (MSBuild/CallTarget)
4. Deduplicates projects by full path and targets by (name, project) pair
5. Runs automated analysis with heuristic-based diagnostics:
   - **Bottleneck projects** taking a disproportionate share of build time, with how far ahead of the next-slowest they are
   - **Under-parallelised builds** — achieved parallelism vs available build nodes, correlated with the critical path to bound recoverable wall-clock
   - **Dependency cycles** in the ProjectReference graph
   - **Project-count tax** — projects spending more time on references than compiling code
   - **Costly package resolution** (ResolvePackageAssets > 3s)
   - **Reference-TFM-negotiation overhead** across project edges
   - **Warning concentration** on the blocking chain
   - **Source-generator/analyzer outliers** (Gen.Logging, ComInterfaceGenerator, Roslyn analyzers in application projects)

   Findings are ranked by severity, then by the share of build time they cover, so the report leads with what costs the most.

## Output formats

- **Console** -- Formatted tables with colored build output
- **HTML** -- Self-contained dark-themed report with severity coloring
- **JSON** -- Machine-readable output (AOT-compatible source-generated serialization)

## Requirements

Pre-built binaries from GitHub Releases are self-contained and need no runtime.

To build from source: .NET 10 SDK or later.

## License

[MIT](LICENSE)
