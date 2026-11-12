# Package security baseline v0.09.04

## Status

This document reflects the package versions actually saved by Visual Studio 2026
in the project files after the successful compilation of Spacing.Tool2.
It supersedes `PACKAGE_SECURITY_v0.09.03.md`.

- Projects inspected: 12
- Direct PackageReference entries: 38
- Unique direct package names: 28
- Central package management: not enabled (`Directory.Packages.props` is absent)
- Lock files: not enabled (`packages.lock.json` is absent)
- NuGet audit: enabled for direct and transitive dependencies
- Audit threshold: low, moderate, high and critical vulnerabilities
- Build-blocking diagnostics: NU1901, NU1902, NU1903 and NU1904

## Security controls

The solution-level `Directory.Build.props` contains:

```xml
<NuGetAudit>true</NuGetAudit>
<NuGetAuditMode>all</NuGetAuditMode>
<NuGetAuditLevel>low</NuGetAuditLevel>
<WarningsAsErrors>$(WarningsAsErrors);NU1901;NU1902;NU1903;NU1904</WarningsAsErrors>
```

A NuGet restore or build therefore fails when a known vulnerability is reported
for a direct or transitive dependency. No NU1901-NU1904 diagnostic is suppressed
or downgraded.

## Current graphics stack

- Mapsui: 5.1.0
- Mapsui.Nts: 5.1.0
- Mapsui.Rendering.Skia: 5.1.0
- Mapsui.Tiling: 5.1.0
- SkiaSharp: 4.155.0
- SkiaSharp.NativeAssets.Linux: 4.155.0
- System.Drawing.Common: 10.0.14

`SixLabors.ImageSharp`, `PdfSharpCore` and `MigraDocCore` are not directly
referenced by any project and are not referenced by the source tree. The unused
PDF dependency chain which introduced ImageSharp was removed in v0.09.03.

## Current data, serialization and compiler stack

- Microsoft.Data.SqlClient: 7.1.1
- Microsoft.Data.Sqlite: 10.0.14
- Newtonsoft.Json: 13.0.4
- MessagePack: 3.1.12
- Microsoft.CodeAnalysis.CSharp: 5.9.0
- Microsoft.CodeAnalysis.VisualBasic: 5.9.0
- DocumentFormat.OpenXml: 3.5.1
- ExcelDataReader.DataSet: 3.9.0
- NetMQ: 4.0.4.5

## Version consistency

All packages shared by multiple projects use the same direct version except:

- MathNet.Numerics 5.0.0 is used by APSIM.Core and Models.
- MathNet.Numerics 6.0.0-beta1 is used only by the legacy `src/runner` project.

This difference is intentionally retained because replacing the stable 5.0.0
references with a beta package would increase risk, while Spacing.Tool2 already
compiles successfully with the current graph.

## Visual Studio 2026 verification procedure

1. Open `apsim/spacing.sln` in Visual Studio 2026.
2. Select `Debug` and `Any CPU`.
3. Use **Build > Rebuild Solution**.
4. Confirm that the build finishes with zero failed projects.
5. Open **Manage NuGet Packages for Solution** and inspect **Installed**,
   **Updates**, and **Consolidate**.
6. Do not suppress NU1901, NU1902, NU1903 or NU1904. If any appears, record the
   package, resolved version and dependency path shown by Visual Studio.

## Source of truth

`PACKAGE_INVENTORY_v0.09.04.tsv` is generated directly from the current
`PackageReference` elements in all `.csproj` files. The project files remain the
normative source; this document is a synchronized human-readable summary.
