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

Command-based pipeline using **Spectre.Console.Cli**:

1. **Commands/** — `BuildCommand` orchestrates the full pipeline; `BuildCommandSettings` defines CLI args with Spectre attributes
2. **Services/** — `BuildRunner` executes `dotnet build -bl`; `LogAnalyzer` stream-parses the `.binlog` using `BinLogReader.ReadRecords()` (memory-efficient, does not load the full build tree)
3. **Models/** — Immutable record types (`BuildReport`, `ProjectTiming`, `TargetTiming`) with required/init properties
4. **Rendering/** — `ConsoleReportRenderer` displays results via Spectre.Console tables/panels
5. **Export/** — `HtmlReportExporter` and `JsonReportExporter` generate output files

## Key Conventions

- Classes are **sealed** unless inheritance is needed
- Data models use **records** with `required` and `init` properties
- Exporters and renderers are **static classes** with static methods
- User-provided strings rendered via Spectre must use `Markup.Escape()` to prevent markup injection
- Binary log parsing uses **streaming** (`BinLogReader.ReadRecords()`) — avoid loading the full structured build tree for performance
- Deduplication: projects tracked by full path, targets by (name, project) pair

## Dependencies

- **MSBuild.StructuredLogger**: Binary log parsing
- **Spectre.Console / Spectre.Console.Cli**: CLI framework and rich console output
- **TUnit**: Unit testing framework (tests only)
