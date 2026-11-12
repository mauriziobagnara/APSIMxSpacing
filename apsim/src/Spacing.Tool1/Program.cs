using System.Net.Http.Headers;
using Spacing.Core.Adapters;
using Spacing.Core.Api;
using Spacing.Core.Config;
using Spacing.Core.Domain;
using Spacing.Core.Simulation;

// =============================================================================
// SPACING TOOL 1 — Simulazioni batch su griglia italiana
//
// Uso:
//   spacing-tool1 [--config spacing.config.json] [--nuts2 ITC4] [--mock]
//
// Il tool:
//   1. Legge la configurazione
//   2. Recupera celle + suoli + meteo dal DB Spacing (o dal mock)
//   3. Per ogni SimulationUnit (cella × suolo × scenario) esegue APSIM
//   4. Scrive i risultati aggregati in CSV per la webapp
// =============================================================================

var configPath = GetArg(args, "--config", "spacing.config.json");
var nuts2Filter = GetArg(args, "--nuts2", null);
var useMock     = args.Contains("--mock");
var scenariosPath = GetArg(args, "--scenarios", null);
var resultJsonPath = GetArg(args, "--result", null);

Console.WriteLine("=== SPACING Tool 1 — Batch scientifico ===");
Console.WriteLine($"Config: {configPath} | NUTS2: {nuts2Filter ?? "tutta Italia"} | Mock: {useMock}");

var config = SpacingConfig.Load(configPath);
config.EnsureDirectories();

// Client DB (mock in sviluppo, HTTP in produzione)
ISpacingDbClient dbClient = useMock
    ? new MockSpacingDbClient()
    : BuildHttpClient(config);

var weatherAdapter = new WeatherAdapter(config.TempWeatherDir);
var soilAdapter    = new SoilAdapter();
var rotationBuilder = new RotationBuilder(soilAdapter, config.OutputDir);
var runner         = new SimulationRunner(config.OutputDir);

// ---- Definizione scenari ----
// TODO: in futuro questi scenari verranno letti da file di configurazione regionale.
// Per ora: esempio con sorgum in monosuccessione e rotazione frumento-sorgum.
var scenarios = scenariosPath == null ? DefineScenarios() : LoadScenarios(scenariosPath);
Console.WriteLine($"\nScenari definiti: {scenarios.Count}");
foreach (var s in scenarios) Console.WriteLine($"  • {s}");

// ---- Loop principale ----
var cells = (await dbClient.GetCellsAsync(nuts2Filter)).ToList();
Console.WriteLine($"\nCelle da processare: {cells.Count}");

var results = new List<(string unitId, bool ok, string summary)>();
var sem = new SemaphoreSlim(config.MaxParallelism);

var tasks = new List<Task>();
foreach (var cell in cells)
{
    var soilProfiles = (await dbClient.GetSoilProfilesAsync(cell.Id)).ToList();
    var weather      = await dbClient.GetWeatherAsync(
                           cell.Id,
                           new DateOnly(config.StartYear, 1, 1),
                           new DateOnly(config.EndYear,  12, 31));

    var metPath = weatherAdapter.WriteMetFile(weather, config.StartYear, config.EndYear);

    foreach (var soil in soilProfiles)
    {
        foreach (var scenario in scenarios)
        {
            var unit = new SimulationUnit
            {
                Cell        = cell,
                SoilProfile = soil,
                Rotation    = scenario,
                StartYear   = config.StartYear,
                EndYear     = config.EndYear,
                RunId       = $"{cell.Id}__{soil.ProfileId}__{scenario.Id}"
            };

            await sem.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    Console.Write($"  ▶ {unit.Id} ... ");
                    var apsimxPath = rotationBuilder.BuildApsimxFile(unit, metPath);
                    var result     = await runner.RunAsync(unit, apsimxPath);

                    if (result.Success)
                    {
                        var summary = string.Join(", ", result.MeanYieldKgHa
                            .Select(kv => $"{kv.Key}={kv.Value:F0} kg/ha"));
                        Console.WriteLine($"OK | {summary}");
                        lock (results) results.Add((unit.Id, true, summary));
                    }
                    else
                    {
                        Console.WriteLine($"ERRORE: {result.ErrorMessage}");
                        lock (results) results.Add((unit.Id, false, result.ErrorMessage));
                    }
                }
                finally { sem.Release(); }
            }));
        }
    }
}

await Task.WhenAll(tasks);

// ---- Output CSV ----
var csvPath = Path.Combine(config.OutputDir, "tool1_results.csv");
WriteResultsCsv(csvPath, results);
var batchOutput = new
{
    success = results.All(r => r.ok),
    engine = "Spacing.Tool1",
    scenarios_file = scenariosPath == null ? null : Path.GetFullPath(scenariosPath),
    result_count = results.Count,
    success_count = results.Count(r => r.ok),
    failure_count = results.Count(r => !r.ok),
    summary_csv = Path.GetFullPath(csvPath),
    runs = results.Select(r => new { simulation_unit_id = r.unitId, success = r.ok, summary = r.summary })
};
var batchJson = Newtonsoft.Json.JsonConvert.SerializeObject(batchOutput, Newtonsoft.Json.Formatting.Indented);
if (!string.IsNullOrWhiteSpace(resultJsonPath))
{
    var full = Path.GetFullPath(resultJsonPath);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, batchJson);
}
Console.WriteLine($"\n✓ Completato. Risultati: {csvPath}");
Console.WriteLine($"  Successi: {results.Count(r => r.ok)} / {results.Count}");

// ---- Helpers ----

static List<CropRotation> LoadScenarios(string path)
{
    if (!File.Exists(path))
        throw new FileNotFoundException($"File scenari non trovato: {path}");
    var token = Newtonsoft.Json.Linq.JToken.Parse(File.ReadAllText(path));
    var array = token.Type == Newtonsoft.Json.Linq.JTokenType.Array
        ? token
        : token["scenarios"] ?? throw new InvalidDataException("Il JSON deve essere un array o contenere la proprietà scenarios.");
    var scenarios = array.ToObject<List<CropRotation>>() ?? new List<CropRotation>();
    if (scenarios.Count == 0)
        throw new InvalidDataException("Nessuno scenario definito.");
    foreach (var scenario in scenarios)
        if (string.IsNullOrWhiteSpace(scenario.Id) || scenario.Crops == null || scenario.Crops.Count == 0)
            throw new InvalidDataException("Ogni scenario richiede Id e almeno una coltura.");
    return scenarios;
}

static List<CropRotation> DefineScenarios()
{
    return new List<CropRotation>
    {
        // Scenario 1: Sorgum in monosuccessione, gestione convenzionale
        new CropRotation
        {
            Id = "S1_sorghum_conv",
            Name = "Sorgum monosuccessione convenzionale",
            Crops = new()
            {
                new RotationCrop
                {
                    CropName = "Sorghum",
                    Management = new() { TillageType = "conventional", NitrogenMineralKgHa = 120 }
                }
            },
            Cycles = 5
        },

        // Scenario 2: Rotazione frumento-sorgum, no-till + N ridotto
        new CropRotation
        {
            Id = "S2_wheat_sorghum_notill",
            Name = "Rotazione Frumento-Sorgum no-till",
            Crops = new()
            {
                new RotationCrop
                {
                    CropName = "Wheat",
                    Management = new() { TillageType = "no-till", NitrogenMineralKgHa = 80 }
                },
                new RotationCrop
                {
                    CropName = "Sorghum",
                    Management = new() { TillageType = "no-till", NitrogenMineralKgHa = 90 }
                }
            },
            Cycles = 3
        }
    };
}

static ISpacingDbClient BuildHttpClient(SpacingConfig config)
{
    var http = new HttpClient { BaseAddress = new Uri(config.DbApiBaseUrl) };
    http.DefaultRequestHeaders.Add("X-Api-Key", config.DbApiKey);
    return new HttpSpacingDbClient(http);
}

static void WriteResultsCsv(string path, List<(string unitId, bool ok, string summary)> results)
{
    using var w = new StreamWriter(path);
    w.WriteLine("unit_id,success,summary");
    foreach (var (id, ok, summary) in results)
        w.WriteLine($"{id},{ok},\"{summary}\"");
}

static string GetArg(string[] args, string key, string defaultVal)
{
    var idx = Array.IndexOf(args, key);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : defaultVal;
}
