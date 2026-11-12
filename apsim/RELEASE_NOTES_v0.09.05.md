# rSpacing / APSIM integration v0.09.05

Base: v0.09.04, preserving the Visual Studio 2026 package graph.

Changes:

- fixed APSIM runtime compilation of threshold irrigation Manager scripts by
  importing `Models.Interfaces` for `ISoilWater`;
- added the complete RStudio end-to-end example to the solution;
- added CSV summaries and PNG plots to the RStudio workflow;
- no package version was changed;
- no compiler diagnostic was suppressed.
