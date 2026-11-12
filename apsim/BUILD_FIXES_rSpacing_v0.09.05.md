# rSpacing v0.09.05

## Irrigation manager compilation fix

The generated APSIM Manager script `Irrigate_<Crop>` used `ISoilWater`, whose
namespace is `Models.Interfaces`, but did not import that namespace. APSIM's
runtime Manager compiler therefore raised CS0246 while loading the generated
`.apsimx` file.

`RotationBuilder.BuildIrrigationManager()` now includes:

```csharp
using Models.Interfaces;
```

This is a source-level correction. Irrigation remains enabled and no error or
warning is suppressed.

## RStudio example

The complete RStudio example is stored in:

`examples/rSpacing/rSpacing_APSIMx_5year_three_crops.R`

It now returns structured summaries to `rspacing_demo$summaries` and writes PNG
plots and CSV summaries into each run's isolated output directory.
