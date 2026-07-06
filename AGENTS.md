# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

BuildTimeAnalyzer (`btanalyzer`) is a .NET 10 CLI tool that analyzes MSBuild binary logs to identify build performance bottlenecks. It runs `dotnet build` with binary logging, parses the `.binlog`, renders results to the console, and optionally exports HTML/JSON reports.

## Build & Run

```bash
# Build
dotnet build

# Run (analyzes the build of a target solution/project)
dotnet run --project src/BuildTimeAnalyzer.TUI -- build <path-to-sln-or-csproj> [options]

# Run tests
dotnet test --solution BuildTimeAnalyzer.slnx

# Publish self-contained
dotnet publish src/BuildTimeAnalyzer.TUI -c Release
```

## Commands

- `build [project]` — build with binary logging, then analyze.
- `analyze <file.binlog>` — analyze an existing binary log without building (never deletes the input).

## CLI Options

`--configuration` (`-c`): Build configuration (default: Debug; `build` only)
`--top` (`-n`): Number of top results to display (default: 20)
`--output` (`-o`): Export report to file (.html, .json, or .md)
`--config`: Path to a `btanalyzer.json` (default: discovered near the project)
`--compare`: Compare against a previously exported JSON report
`--fail-on`: Exit non-zero for CI gating (`critical|warning|errors|wallclock:<s>|regression:<pct>`)
`--history`: Append a one-line run summary (JSONL) for trend tracking
`--no-open`: Do not launch the browser after generating an HTML report
`--keep-log`: Keep the .binlog file after analysis (`build` only)
`--args`: Additional arguments passed to `dotnet build` (`build` only)

## Architecture

CLI pipeline with manual argument parsing (no framework dependencies beyond MSBuild.StructuredLogger):

1. **Program.cs** — Entry point; routes top-level commands (`build`, `analyze`, `--help`, `--version`) inside a top-level error boundary
2. **Commands/** — `BuildCommand` runs the build (Ctrl-C aware); `AnalyzeCommand` handles an existing binlog; both delegate the post-binlog stages to `ReportPipeline` (parse → analyze → compare → export → gate → cleanup)
3. **Services/** — `BuildRunner` executes `dotnet build -bl` (via `ArgumentList`); `LogAnalyzer` stream-parses the `.binlog` using `BinLogReader.ReadRecords()`; `BuildAnalyzer` produces findings (thresholds from `BtaConfig`/`AnalyzerThresholds`); `BuildComparison`, `FailOnPolicy`, `BuildHistory` support the CI workflow
4. **Models/** — Immutable record types (`BuildReport`, `ProjectTiming`, `TargetTiming`) with required/init properties
5. **Rendering/** — `ConsoleReportRenderer` holds shared formatting helpers; `Throbber`/`BuildOutputController` drive progress output
6. **Export/** — `HtmlReportExporter`, `JsonReportExporter`, and `MarkdownReportExporter` generate output files

## Key Conventions

- Classes are **sealed** unless inheritance is needed
- Data models use **records** with `required` and `init` properties
- Exporters and renderers are **static classes** with static methods
- Binary log parsing uses **streaming** (`BinLogReader.ReadRecords()`) — avoid loading the full structured build tree for performance
- Deduplication: projects tracked by full path, targets by (name, project) pair
- No reflection at runtime — AOT-safe throughout

## Dependencies

- **MSBuild.StructuredLogger**: Binary log parsing
- **TUnit**: Unit testing framework (tests only)
