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

## CLI Options

`--configuration` (`-c`): Build configuration (default: Debug)
`--top` (`-n`): Number of top results to display (default: 20)
`--keep-log`: Keep the .binlog file after analysis
`--output` (`-o`): Export report to file (.html or .json)
`--args`: Additional arguments passed to `dotnet build`

## Architecture

CLI pipeline with manual argument parsing (no framework dependencies beyond MSBuild.StructuredLogger):

1. **Program.cs** — Entry point with arg parsing for top-level commands (`build`, `--help`, `--version`)
2. **Commands/** — `BuildCommand` orchestrates the full pipeline; `BuildCommandSettings` holds parsed CLI args
3. **Services/** — `BuildRunner` executes `dotnet build -bl`; `LogAnalyzer` stream-parses the `.binlog` using `BinLogReader.ReadRecords()` (memory-efficient, does not load the full build tree)
4. **Models/** — Immutable record types (`BuildReport`, `ProjectTiming`, `TargetTiming`) with required/init properties
5. **Rendering/** — `ConsoleReportRenderer` displays results via plain `Console.WriteLine` with formatted tables
6. **Export/** — `HtmlReportExporter` and `JsonReportExporter` generate output files

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
