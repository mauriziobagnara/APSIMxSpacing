# rSpacing / APSIM integration v0.09.04

This release uses Maurizio Bagnara's Visual Studio 2026-updated v0.09.03 solution
as its source base. No executable source or package version was changed.

Changes:

- regenerated the package inventory from all current `.csproj` files;
- synchronized the package-security baseline with the versions actually saved by
  Visual Studio 2026;
- removed the two obsolete v0.09.03 inventory documents to prevent conflicts;
- retained the solution-wide NuGet audit and build-blocking NU1901-NU1904 policy.
