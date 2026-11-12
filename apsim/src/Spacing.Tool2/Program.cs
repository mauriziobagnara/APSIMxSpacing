using Spacing.Core.Adapters;
using Spacing.Core.Api;
using Spacing.Core.Calibration;
using Spacing.Core.Config;
using Spacing.Core.Domain;
using Spacing.Core.Simulation;
using System.Text.Json;
using Newtonsoft.Json.Linq;

// =============================================================================
// SPACING TOOL 2 — Calibrazione e simulazione on-demand
//
// Modalita' di utilizzo:
//   spacing-tool2 --lat 45.4 --lon 9.2 --rotation "Maize" [--mock] [--calibrate]
//   spacing-tool2 --request request.json [--mock] [--calibrate]
//   spacing-tool2 --met-file path.met --rotation Maize [--mock] [--calibrate]
//
// Opzioni calibrazione:
//   --calibrate             : attiva Multi-Start Simplex prima della simulazione
//   --observations file.csv : osservazioni da file CSV (default: mock)
//                             Formato: Province,Crop,Year,DOY,LAI,Yield
//                             Yield in t/ha (<100) o kg/ha (>=100)
//   --yield-weight 0.5      : peso componente resa nella loss (default 0.5)
//   --lai-weight   0.5      : peso componente LAI nella loss  (default 0.5)
//   --simplexes    30       : numero di simplessi multi-start (default 30)
//
// Output (JSON su stdout):
// {
//   "success": true,
//   "calibrated": true,
//   "bestParams": { "Maize": { "Phenology.EndJuvenile.Target.FixedValue": 250.0 } },
//   "yields": { "Maize": 9800 },
//   "tradeoffs": { ... }
// }
// =============================================================================

var useMock      = args.Contains("--mock");
var useCalib     = args.Contains("--calibrate");
var requestPath  = GetArg(args, "--request",       null);
var configPath   = GetArg(args, "--config",        "spacing.config.json");
var extMetFile   = GetArg(args, "--met-file",      null);
var obsFilePath  = GetArg(args, "--observations",  null);  // CSV con dati calibrazione
var calibParams  = GetArg(args, "--calib-params",  null);  // JSON parametri (default: calibration_params.json)
var resultPath   = GetArg(args, "--result",        null);  // optional JSON result file for rSpacing
var manifestPath = GetArg(args, "--manifest",      null);  // optional provenance manifest
var requestDoc   = requestPath != null ? LoadRequestDocument(requestPath) : null;
var requestBaseDirectory = requestPath == null
    ? Directory.GetCurrentDirectory()
    : Path.GetDirectoryName(Path.GetFullPath(requestPath))
      ?? Directory.GetCurrentDirectory();

// Values in the explicit CLI take precedence over request JSON.
useMock  = useMock  || (requestDoc?["mock"]?.Value<bool?>() ?? false);
useCalib = useCalib || (requestDoc?["calibrate"]?.Value<bool?>() ?? false);
extMetFile  ??= requestDoc?["weather_file"]?.Value<string>() ?? requestDoc?["met_file"]?.Value<string>();
obsFilePath ??= requestDoc?["observations_file"]?.Value<string>();
calibParams ??= requestDoc?["calibration_parameters_file"]?.Value<string>();
resultPath  ??= requestDoc?["result_file"]?.Value<string>();
manifestPath ??= requestDoc?["manifest_file"]?.Value<string>();

// Resolve portable input paths. Relative paths are anchored to the request
// directory. If an absolute path came from another workstation, retry by file
// name in the request/config/current directories and their parents.
extMetFile = ResolveExistingInputFile(extMetFile, requestBaseDirectory, configPath);
obsFilePath = ResolveExistingInputFile(obsFilePath, requestBaseDirectory, configPath);
calibParams = ResolveExistingInputFile(calibParams, requestBaseDirectory, configPath);

Console.Error.WriteLine("=== SPACING Tool 2 — Calibrazione e simulazione ===");

var config = SpacingConfig.Load(configPath);

if (GetArg(args, "--start-year", null) is string sy) config.StartYear = int.Parse(sy);
if (GetArg(args, "--end-year",   null) is string ey) config.EndYear   = int.Parse(ey);

config.EnsureDirectories();

ISpacingDbClient dbClient = useMock
    ? new MockSpacingDbClient()
    : BuildHttpClient(config);

// ---- Rotazione ----
var rotation = requestPath != null
    ? LoadRotationFromJson(requestDoc)
    : BuildRotationFromArgs(args);

if (rotation == null)
{
    Console.Error.WriteLine("Errore: specificare --request <file.json> o --lat --lon --rotation <colture>");
    Environment.Exit(1);
}

// ---- Coordinate ----
var latText = GetArg(args, "--lat", null);
var lonText = GetArg(args, "--lon", null);
var lat = latText != null
    ? double.Parse(latText, System.Globalization.CultureInfo.InvariantCulture)
    : requestDoc?["lat"]?.Value<double?>() ?? requestDoc?["latitude"]?.Value<double?>() ?? 45.4;
var lon = lonText != null
    ? double.Parse(lonText, System.Globalization.CultureInfo.InvariantCulture)
    : requestDoc?["lon"]?.Value<double?>() ?? requestDoc?["longitude"]?.Value<double?>() ?? 9.2;
var cellId = requestDoc?["cell_id"]?.Value<string>()
             ?? $"CELL_{(int)(Math.Abs(lat) * 10):D3}_{(int)(Math.Abs(lon) * 10):D3}";

Console.Error.WriteLine($"Cella: {cellId} | Rotazione: {rotation}");

// ---- Suolo ----
SoilProfile soil;
if (requestDoc?["soil_profile"] is JObject soilJson)
{
    soil = soilJson.ToObject<SoilProfile>()
           ?? throw new InvalidDataException("soil_profile non valido nel request JSON.");
    soil.CellId ??= cellId;
    soil.ProfileId ??= $"SOIL_{cellId}";
    if (soil.Layers == null || soil.Layers.Count == 0)
        throw new InvalidDataException("soil_profile.layers deve contenere almeno uno strato.");
}
else
{
    var soilProfiles = (await dbClient.GetSoilProfilesAsync(cellId)).ToList();
    soil = soilProfiles.OrderByDescending(p => p.AreaFraction).First();
}

var unit = new SimulationUnit
{
    Cell        = new() { Id = cellId, Lat = lat, Lon = lon },
    SoilProfile = soil,
    Rotation    = rotation,
    StartYear   = requestDoc?["start_year"]?.Value<int?>() ?? config.StartYear,
    EndYear     = requestDoc?["end_year"]?.Value<int?>() ?? config.EndYear,
    RunId       = requestDoc?["run_id"]?.Value<string>() ?? Guid.NewGuid().ToString("N")
};
if (requestDoc?["metadata"] is JObject metadata)
    foreach (var item in metadata.Properties())
        unit.Metadata[item.Name] = item.Value.Type == JTokenType.String
            ? item.Value.Value<string>()
            : item.Value.ToString(Newtonsoft.Json.Formatting.None);

// ---- Pipeline APSIM ----
var weatherAdapter = new WeatherAdapter(config.TempWeatherDir);
var soilAdapter    = new SoilAdapter();
var rotBuilder     = new RotationBuilder(soilAdapter, config.OutputDir);
var runner         = new SimulationRunner(config.OutputDir);

// ---- File meteo ----
string metPath;
if (!string.IsNullOrWhiteSpace(extMetFile))
{
    if (!File.Exists(extMetFile))
        throw new FileNotFoundException(
            $"File .met non trovato dopo la risoluzione portabile: {extMetFile}",
            extMetFile);

    metPath = Path.GetFullPath(extMetFile);
    Console.Error.WriteLine($"File meteo esterno: {metPath}");
}
else
{
    var weather = await dbClient.GetWeatherAsync(
        cellId,
        new DateOnly(unit.StartYear, 1, 1),
        new DateOnly(unit.EndYear, 12, 31));
    metPath = weatherAdapter.WriteMetFile(weather, unit.StartYear, unit.EndYear);
    Console.Error.WriteLine($"File meteo generato: {metPath}");
}

// ---- Calibrazione (opzionale) ----
Dictionary<string, Dictionary<string, double>> calibratedParams = null;
string calibratedJsonPath = null;
string timeSeriesCsvPath  = null;
string harvestCsvPath     = null;
SimulationResult result;

if (useCalib)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("=== CALIBRAZIONE Multi-Start Simplex ===");

    // Parametri da calibrare: carica da JSON (calibration_params.json) se esiste,
    // altrimenti usa i default hardcoded come fallback.
    // L'utente modifica calibration_params.json per scegliere cosa calibrare.
    var paramsFile = calibParams
                     ?? Path.Combine(AppContext.BaseDirectory, "calibration_params.json")
                     ?? "calibration_params.json";

    var cropParams = File.Exists(paramsFile)
        ? LoadCalibrationParamsFromJson(paramsFile, rotation)
        : BuildCalibrationParameters(rotation);

    if (File.Exists(paramsFile))
        Console.Error.WriteLine($"[Calib] Parametri caricati da: {paramsFile}");
    else
        Console.Error.WriteLine($"[Calib] AVVISO: '{paramsFile}' non trovato, uso parametri default.");

    if (cropParams.Count == 0)
    {
        Console.Error.WriteLine("AVVISO: nessun parametro di calibrazione definito per questa rotazione.");
        Console.Error.WriteLine($"Colture nella rotazione: {string.Join(", ", rotation.Crops.Select(c => c.CropName))}");
        Console.Error.WriteLine($"Colture nel file params: aggiungere una sezione corrispondente in {paramsFile}");
        Console.Error.WriteLine("Eseguo simulazione senza calibrazione.");
        var apsimxPath0 = rotBuilder.BuildApsimxFile(unit, metPath);
        result = runner.Run(unit, apsimxPath0);
        goto output;
    }

    // Osservazioni di calibrazione: da file CSV o mock
    List<ReferenceObservation> yieldObs;
    List<LaiObservation>       laiObs;

    if (obsFilePath != null)
    {
        Console.Error.WriteLine($"Caricamento osservazioni da: {obsFilePath}");
        // Il loader accetta CSV con header Province,Crop,Year,DOY,LAI,Yield
        // Per usare il file Excel: salvarlo prima come CSV (UTF-8)
        (yieldObs, laiObs) = CalibrationDataLoader.LoadFromCsv(obsFilePath);
    }
    else
    {
        Console.Error.WriteLine("Nessun file osservazioni: uso dati mock Pianura Padana.");
        yieldObs = BuildMockYieldObservations(rotation, config.StartYear, config.EndYear);
        laiObs   = new List<LaiObservation>();
    }

    // Pesi e parametri ottimizzatore da CLI
    double yieldW   = double.TryParse(GetArg(args, "--yield-weight", "0.5"),
                          System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out var yw) ? yw : 0.5;
    double laiW     = double.TryParse(GetArg(args, "--lai-weight", "0.5"),
                          System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out var lw) ? lw : 0.5;
    int    nSimplex = int.TryParse(GetArg(args, "--simplexes", "0"), out var ns) && ns > 0
                      ? ns
                      : (useMock ? 10 : 30);

    var calibrator = new SpacingCalibrator(soilAdapter, rotBuilder, runner)
    {
        NofSimplexes  = nSimplex,
        MaxIterations = 150,
        Ftol          = 1e-4,
        YieldWeight   = yieldW,
        LaiWeight     = laiW,
        OutputDir     = config.OutputDir   // dove salvare cultivar_*.json e comparison_*.csv
    };

    (calibratedParams, result, calibratedJsonPath, timeSeriesCsvPath, harvestCsvPath) =
        calibrator.Calibrate(unit, metPath, cropParams, yieldObs, laiObs);
}
else
{
    // Simulazione diretta senza calibrazione
    var apsimxPath = rotBuilder.BuildApsimxFile(unit, metPath);
    result = runner.Run(unit, apsimxPath);
}

output:
// ---- Output JSON su stdout ----
var output = new
{
    success    = result.Success,
    error      = result.ErrorMessage,
    calibrated = useCalib,
    bestParams = calibratedParams,
    yields     = result.MeanYieldKgHa,
    biomass    = result.MeanAbovegroundBiomassKgHa,
    run_id     = unit.RunId,
    simulation_unit_id = unit.Id,
    engine      = "Spacing.Tool2",
    metadata    = unit.Metadata,
    inputs      = new
    {
        cell_id = cellId,
        latitude = lat,
        longitude = lon,
        soil_profile_id = soil.ProfileId,
        soil_source = soil.DataSource,
        weather_file = Path.GetFullPath(metPath),
        start_year = unit.StartYear,
        end_year = unit.EndYear,
        rotation = rotation,
        fixed_parameter_overrides = rotation.Crops.ToDictionary(c => c.CropName, c => c.ParameterOverrides)
    },
    tradeoffs  = new
    {
        mean_yield_kgha    = result.MeanYieldKgHa.Values.DefaultIfEmpty(0).Average(),
        soil_n_final_kgha  = result.FinalSoilCarbonKgHa,
        datastore_path     = result.DataStorePath,
        control_csv_path   = result.ControlCsvPath,
        daily_csv_path     = result.DailyCsvPath,
        crop_summary_path  = result.CropSummaryCsvPath,
        calibrated_json    = calibratedJsonPath,   // output/calibrated/{Crop}_{Cell}.json
        timeseries_csv     = timeSeriesCsvPath,   // LAI sim+obs giornaliero
        harvest_csv        = harvestCsvPath        // resa sim vs obs per anno
    }
};

var outputJson = Newtonsoft.Json.JsonConvert.SerializeObject(
    output, Newtonsoft.Json.Formatting.Indented);
Console.WriteLine(outputJson);

if (!string.IsNullOrWhiteSpace(resultPath))
{
    var fullResultPath = Path.GetFullPath(resultPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullResultPath)!);
    File.WriteAllText(fullResultPath, outputJson);
}

var resolvedManifestPath = manifestPath;
if (string.IsNullOrWhiteSpace(resolvedManifestPath))
    resolvedManifestPath = Path.Combine(config.OutputDir, $"run_manifest_{SanitizeFileName(unit.RunId)}.json");
var fullManifestPath = Path.GetFullPath(resolvedManifestPath);
Directory.CreateDirectory(Path.GetDirectoryName(fullManifestPath)!);
File.WriteAllText(fullManifestPath, outputJson);

// =============================================================================
// Caricamento parametri di calibrazione da JSON esterno
// =============================================================================

/// <summary>
/// Carica i parametri di calibrazione da calibration_params.json.
/// Solo le colture presenti nella rotazione corrente vengono caricate.
///
/// Il file ha struttura:
/// {
///   "Maize": { "Parameters": [ { "Name": "...", "LowerBound": x, "UpperBound": y, "InitialValue": z } ] },
///   "Sorghum": { ... }
/// }
///
/// Per aggiungere una coltura o modificare i range: editare calibration_params.json.
/// </summary>
static Dictionary<string, List<CalibrationParameter>> LoadCalibrationParamsFromJson(
    string jsonPath, CropRotation rotation)
{
    try
    {
        var json = File.ReadAllText(jsonPath);
        // Usa System.Text.Json perché gestisce init-only properties nativamente
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling         = JsonCommentHandling.Skip,
            AllowTrailingCommas         = true
        };

        // Struttura del JSON: dizionario CropName → oggetto con campo "Parameters"
        var rawMap = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, opts)
                     ?? new Dictionary<string, JsonElement>();

        var result = new Dictionary<string, List<CalibrationParameter>>();
        var cropNamesInRotation = rotation.Crops.Select(c => c.CropName).ToHashSet();

        foreach (var (cropName, cropEl) in rawMap)
        {
            // Ignora chiavi speciali (commenti, metadati)
            if (cropName.StartsWith("_")) continue;
            if (!cropNamesInRotation.Contains(cropName))   continue; // non in questa rotazione

            if (!cropEl.TryGetProperty("Parameters", out var paramsEl)) continue;
            var paramList = new List<CalibrationParameter>();

            foreach (var p in paramsEl.EnumerateArray())
            {
                string name = p.TryGetProperty("Name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;

                double lower = p.TryGetProperty("LowerBound",   out var lo) ? lo.GetDouble() : 0;
                double upper = p.TryGetProperty("UpperBound",   out var up) ? up.GetDouble() : 1;
                double init  = p.TryGetProperty("InitialValue", out var iv) ? iv.GetDouble() : (lower + upper) / 2.0;

                paramList.Add(new CalibrationParameter
                {
                    Name         = name,
                    LowerBound   = lower,
                    UpperBound   = upper,
                    InitialValue = init
                });
            }

            if (paramList.Count > 0)
            {
                result[cropName] = paramList;
                Console.Error.WriteLine(
                    $"[Calib] {cropName}: {paramList.Count} parametri caricati da JSON");
            }
        }

        return result;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Calib] ERRORE lettura {jsonPath}: {ex.Message}. Uso parametri default.");
        return BuildCalibrationParameters(rotation);
    }
}

// =============================================================================
// Parametri di calibrazione per coltura (fallback hardcoded)
//
// NOTA: usati solo se calibration_params.json non esiste.
// Preferire la modifica del file JSON per cambiare range/parametri.
// =============================================================================

static Dictionary<string, List<CalibrationParameter>> BuildCalibrationParameters(
    CropRotation rotation)
{
    var dict = new Dictionary<string, List<CalibrationParameter>>();

    foreach (var crop in rotation.Crops)
    {
        var parms = crop.CropName switch
        {
            // ---- MAIS ----
            // Path verificati da Models/Resources/Maize.json e cultivar B_110.
            // Formato: "SubModello.percorso" → BuildPlantNode genera "[SubModello].percorso"
            "Maize" => new List<CalibrationParameter>
            {
                // Durata fase giovanile (°Cd): controlla il momento in cui inizia
                // l'espansione fogliare → principale driver del LAI precoce.
                // B_110 default = 210. Range plausibile mais italiano: 150–320.
                new()
                {
                    Name         = "Phenology.Juvenile.Target.FixedValue",
                    LowerBound   = 100.0,
                    UpperBound   = 350.0,
                    InitialValue = 210.0
                },
                // Durata grain filling (°Cd): principale driver della resa granellare.
                // B_110 default = 730. Range: 500–900.
                new()
                {
                    Name         = "Phenology.GrainFilling.Target.FixedValue",
                    LowerBound   = 400.0,
                    UpperBound   = 950.0,
                    InitialValue = 730.0
                },
                // RUE (g DM / MJ PAR): controlla biomassa totale → resa.
                // Default Maize = 2.0. Range tipico C4: 1.5–3.0.
                new()
                {
                    Name         = "Leaf.Photosynthesis.RUE.FixedValue",
                    LowerBound   = 1.2,
                    UpperBound   = 3.2,
                    InitialValue = 2.0
                }
            },

            // ---- SORGO ----
            // Path verificati da Models/Resources/Sorghum.json e cultivar Buster.
            // Sorghum usa un modello fogliare diverso (C4LeafArea con parametro aX0).
            "Sorghum" => new List<CalibrationParameter>
            {
                // aX0: intercetta della curva area fogliare vs numero foglia.
                // Buster default = 0.687. Controlla il picco di LAI.
                new()
                {
                    Name         = "Leaf.Parameters.aX0.FixedValue",
                    LowerBound   = 0.3,
                    UpperBound   = 1.5,
                    InitialValue = 0.687
                },
                // Durata fase giovanile (°Cd). Default = 100.
                new()
                {
                    Name         = "Phenology.Juvenile.Target.FixedValue",
                    LowerBound   = 60.0,
                    UpperBound   = 250.0,
                    InitialValue = 100.0
                },
                // RUE. Default Sorghum = 1.25.
                new()
                {
                    Name         = "Leaf.Photosynthesis.RUE.FixedValue",
                    LowerBound   = 0.8,
                    UpperBound   = 2.0,
                    InitialValue = 1.25
                }
            },

            // ---- FRUMENTO ----
            // TODO: verificare path da Models/Resources/Wheat.json
            "Wheat" => new List<CalibrationParameter>
            {
                new()
                {
                    Name         = "Leaf.Photosynthesis.RUE.FixedValue",
                    LowerBound   = 0.8,
                    UpperBound   = 2.5,
                    InitialValue = 1.4
                }
            },

            // ---- ORZO ----
            // TODO: verificare path da Models/Resources/Barley.json
            "Barley" => new List<CalibrationParameter>
            {
                new()
                {
                    Name         = "Leaf.Photosynthesis.RUE.FixedValue",
                    LowerBound   = 0.8,
                    UpperBound   = 2.0,
                    InitialValue = 1.3
                }
            },

            // ---- SOIA ----
            // TODO: verificare path da Models/Resources/Soybean.json
            "Soybean" => new List<CalibrationParameter>
            {
                new()
                {
                    Name         = "Leaf.Photosynthesis.RUE.FixedValue",
                    LowerBound   = 0.5,
                    UpperBound   = 1.8,
                    InitialValue = 1.0
                }
            },

            _ => new List<CalibrationParameter>()
        };

        if (parms.Count > 0)
            dict[crop.CropName] = parms;
    }

    return dict;
}

// ---- Osservazioni mock (fallback senza --observations) ----

static List<ReferenceObservation> BuildMockYieldObservations(
    CropRotation rotation, int startYear, int endYear)
{
    var obs = new List<ReferenceObservation>();
    var rng = new Random(42);

    // Rese medie Pianura Padana (kg/ha): fonte ISTAT media 2015-2023
    var expectedYields = new Dictionary<string, (double mean, double std)>
    {
        ["Maize"]   = (9500, 1000),
        ["Sorghum"] = (7200,  800),
        ["Wheat"]   = (5800,  600),
        ["Barley"]  = (5000,  500),
        ["Soybean"] = (3200,  400)
    };

    var years = Enumerable.Range(startYear + 1, Math.Max(1, endYear - startYear - 2))
                          .OrderBy(_ => rng.Next()).Take(5).OrderBy(y => y).ToList();

    foreach (var crop in rotation.Crops)
    {
        if (!expectedYields.TryGetValue(crop.CropName, out var stats)) continue;
        foreach (var year in years)
        {
            obs.Add(new ReferenceObservation
            {
                Year           = year,
                CropName       = crop.CropName,
                GrainYieldKgHa = Math.Max(0, stats.mean + (rng.NextDouble() * 2 - 1) * stats.std)
            });
        }
    }

    return obs;
}

// ---- Helpers ----

static JObject LoadRequestDocument(string path)
{
    if (!File.Exists(path))
        throw new FileNotFoundException($"Request JSON non trovato: {path}");
    return JObject.Parse(File.ReadAllText(path));
}

static CropRotation LoadRotationFromJson(JObject req)
{
    if (req == null) return null;
    var crops = new List<RotationCrop>();
    dynamic dynamicReq = req;
    foreach (var c in dynamicReq.crops)
    {
        var m = new ManagementPractice
        {
            TillageType         = (string)(c.management?.tillage          ?? "conventional"),
            NitrogenMineralKgHa = (double)(c.management?.nitrogen_mineral ?? 0.0),
            SowingDensity       = (double)(c.management?.sowing_density   ?? 10.0),
            RowSpacing          = (double)(c.management?.row_spacing       ?? 750.0),
            SowingDepth         = (double)(c.management?.sowing_depth      ?? 30.0),
            Cultivar            = (string)(c.management?.cultivar          ?? null),
            SowingWindowStart   = (string)(c.management?.sowing_window_start ?? null),
            SowingWindowEnd     = (string)(c.management?.sowing_window_end   ?? null),
            AutoIrrigate          = (bool)(c.management?.auto_irrigate        ?? false),
            IrrigationThreshold   = (double)(c.management?.irrigation_threshold ?? 0.50),
            IrrigationTarget      = (double)(c.management?.irrigation_target    ?? 0.85),
            MaxIrrigationMm       = (double)(c.management?.max_irrigation_mm    ?? 40.0),
            IrrigationWindowStart = (string)(c.management?.irrigation_window_start ?? null),
            IrrigationWindowEnd   = (string)(c.management?.irrigation_window_end   ?? null)
        };

        // Fertilizzazione frazionata (lista opzionale)
        if (c.management?.split_fertiliser != null)
        {
            m.SplitFertiliser = new List<FertiliserApplication>();
            foreach (var sf in c.management.split_fertiliser)
            {
                m.SplitFertiliser.Add(new FertiliserApplication
                {
                    Trigger         = (string)(sf.trigger          ?? "Sowing"),
                    DaysAfterSowing = (int)   (sf.days_after_sowing ?? 0),
                    AmountKgNHa     = (double)(sf.amount_kg_n_ha    ?? 0),
                    FertType        = (string)(sf.fert_type         ?? "UreaN")
                });
            }
        }

        var rotationCrop = new RotationCrop
        {
            CropName   = (string)c.name,
            Management = m
        };
        JToken cropToken = c;
        var overrideToken = cropToken["parameter_overrides"] ?? cropToken["parameters"];
        if (overrideToken is JObject overrides)
        {
            foreach (var property in overrides.Properties())
            {
                if (!double.TryParse(property.Value.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                    throw new InvalidDataException($"Override non numerico: {rotationCrop.CropName}.{property.Name}");
                rotationCrop.ParameterOverrides[property.Name] = value;
            }
        }
        crops.Add(rotationCrop);
    }

    return new CropRotation
    {
        Id     = (string)(dynamicReq.id     ?? "tool2_custom"),
        Name   = (string)(dynamicReq.name   ?? "Rotazione personalizzata"),
        Crops  = crops,
        Cycles = (int)(dynamicReq.cycles ?? 3)
    };
}

static CropRotation BuildRotationFromArgs(string[] args)
{
    var rotArg = GetArg(args, "--rotation", null);
    if (rotArg == null) return null;

    var cropNames = rotArg.Split(',', StringSplitOptions.RemoveEmptyEntries);
    return new CropRotation
    {
        Id    = "tool2_cli",
        Name  = $"Rotazione CLI: {rotArg}",
        Crops = cropNames.Select(n => new RotationCrop
        {
            CropName   = n.Trim(),
            Management = new ManagementPractice { NitrogenMineralKgHa = 80.0 }
        }).ToList(),
        Cycles = 3
    };
}

static ISpacingDbClient BuildHttpClient(SpacingConfig config)
{
    var http = new HttpClient { BaseAddress = new Uri(config.DbApiBaseUrl) };
    http.DefaultRequestHeaders.Add("X-Api-Key", config.DbApiKey);
    return new HttpSpacingDbClient(http);
}

static string ResolveExistingInputFile(string path, string requestBaseDirectory, string configPath)
{
    if (string.IsNullOrWhiteSpace(path))
        return path;

    var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
    var candidates = new List<string>();

    void AddCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;
        try
        {
            var full = Path.GetFullPath(candidate);
            if (!candidates.Contains(full, StringComparer.OrdinalIgnoreCase))
                candidates.Add(full);
        }
        catch (Exception) when (candidate != expanded)
        {
            // Ignore only fallback candidates which cannot be normalised.
        }
    }

    if (Path.IsPathRooted(expanded))
        AddCandidate(expanded);
    else
    {
        AddCandidate(Path.Combine(requestBaseDirectory, expanded));
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
        if (!string.IsNullOrWhiteSpace(configDirectory))
            AddCandidate(Path.Combine(configDirectory, expanded));
        AddCandidate(Path.Combine(Directory.GetCurrentDirectory(), expanded));
    }

    var fileName = Path.GetFileName(expanded);
    if (!string.IsNullOrWhiteSpace(fileName))
    {
        foreach (var start in new[]
        {
            requestBaseDirectory,
            Path.GetDirectoryName(Path.GetFullPath(configPath)),
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        }.Where(directory => !string.IsNullOrWhiteSpace(directory)))
        {
            var directory = new DirectoryInfo(start);
            for (var level = 0; directory != null && level < 8; level++, directory = directory.Parent)
                AddCandidate(Path.Combine(directory.FullName, fileName));
        }
    }

    return candidates.FirstOrDefault(File.Exists)
           ?? candidates.FirstOrDefault()
           ?? expanded;
}

static string SanitizeFileName(string value)
{
    var invalid = Path.GetInvalidFileNameChars();
    return new string((value ?? "run").Select(c => invalid.Contains(c) ? '_' : c).ToArray());
}

static string GetArg(string[] args, string key, string defaultVal)
{
    var idx = Array.IndexOf(args, key);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : defaultVal;
}
