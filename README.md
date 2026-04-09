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

# Export HTML report
btanalyzer build -o report.html

# Export JSON report
btanalyzer build -o report.json

# Pass extra arguments to dotnet build
btanalyzer build --args "--no-restore"

# Keep the binary log for further inspection
btanalyzer build --keep-log
```

## Options

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--configuration` | `-c` | `Debug` | Build configuration |
| `--top` | `-n` | `20` | Number of top results to display |
| `--output` | `-o` | | Export report to file (`.html` or `.json`) |
| `--keep-log` | | | Keep the `.binlog` file after analysis |
| `--args` | | | Additional arguments passed to `dotnet build` |

## What it does

1. Runs `dotnet build -bl` on your project/solution
2. Stream-parses the binary log (memory-efficient, no full tree loading)
3. Computes **exclusive** build times by subtracting orchestration task durations (MSBuild/CallTarget)
4. Deduplicates projects by full path and targets by (name, project) pair
5. Runs automated analysis with heuristic-based diagnostics:
   - **Bottleneck projects** taking a disproportionate share of build time
   - **Disproportionately slow projects** compared to the next-slowest
   - **Project clusters** suggesting shared dependency chains
   - **Dominant target types** (e.g. CoreCompile dominating top targets)
   - **Unusually slow individual targets** (statistical outliers)
   - **Costly package resolution** (ResolvePackageAssets > 3s)
   - **Warning concentration** across projects

## Output formats

- **Console** -- Rich ANSI-colored tables and panels via Spectre.Console
- **HTML** -- Self-contained dark-themed report with severity coloring
- **JSON** -- Machine-readable output (AOT-compatible source-generated serialization)

## Requirements

Pre-built binaries from GitHub Releases are self-contained and need no runtime.

To build from source: .NET 10 SDK or later.

## License

[MIT](LICENSE)
