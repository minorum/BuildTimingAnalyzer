# Changelog

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
