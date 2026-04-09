# Changelog

## 1.0.0

Initial release.

- MSBuild binary log analysis with streaming parser
- Exclusive duration calculation (subtracts MSBuild/CallTarget orchestration time)
- Project and target timing breakdown with percentage of total build time
- Automated heuristic analysis: bottlenecks, disproportion, clustering, dominant targets, outliers, costly package resolution, warning concentration
- Console output with rich ANSI-colored tables via Spectre.Console
- HTML report export (dark theme, self-contained)
- JSON report export (AOT-compatible source-generated serialization)
- Native AOT binaries for Windows, Linux, and macOS
