# rSpacing v0.09.02 - portable input paths

`Spacing.Tool2` now resolves relative input paths from the request JSON directory.
If a request or Visual Studio launch profile contains an absolute path copied from
another workstation, Tool2 retries by file name in the request directory, the
configuration directory, the current directory, the executable directory, and up
to eight parent directories.

The same resolver is used for the external `.met` file, observations CSV, and
calibration-parameter JSON. Weather generation now uses `SimulationUnit.StartYear`
and `SimulationUnit.EndYear`, including years supplied by rSpacing.
