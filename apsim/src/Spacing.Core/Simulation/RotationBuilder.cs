using Newtonsoft.Json;
using Spacing.Core.Adapters;
using Spacing.Core.Domain;

namespace Spacing.Core.Simulation;

/// <summary>
/// Costruisce il file .apsimx JSON per una SimulationUnit.
///
/// I Manager scripts usano la stessa struttura dei file APSIM originali:
///   - $type: "Models.Manager, Models"  (NON Models.Management.Manager)
///   - class Script : Model  dentro namespace Models {}
///   - Parameters array per i valori configurabili
///   - [Link(ByName=true)] per linkare la coltura giusta in rotazioni multi-crop
/// </summary>
public class RotationBuilder
{
    private readonly SoilAdapter _soilAdapter;
    private readonly string _outputDir;

    public RotationBuilder(SoilAdapter soilAdapter, string outputDir)
    {
        _soilAdapter = soilAdapter;
        _outputDir = outputDir;
        Directory.CreateDirectory(_outputDir);
    }

    /// <summary>
    /// Genera il file .apsimx per una SimulationUnit e restituisce il percorso assoluto.
    /// cultivarOverrides: dizionario "CropName" -> (parametro APSIM -> valore)
    ///   es. { "Sorghum": { "Phenology.TTEndJuvToFI.Target.FixedValue", 400.0 } }
    /// </summary>
    public string BuildApsimxFile(SimulationUnit unit, string weatherMetPath,
        Dictionary<string, Dictionary<string, double>> cultivarOverrides = null)
    {
        var startDate = new DateTime(unit.StartYear, 1, 1);
        var endDate   = new DateTime(unit.EndYear,   12, 31);

        var cropNames    = unit.Rotation.Crops.Select(c => c.CropName);
        var soilJson     = _soilAdapter.ToApsimJson(unit.SoilProfile, cropNames);
        var zoneChildren = BuildZoneChildren(unit.Rotation, soilJson, cultivarOverrides);

        // JsonConvert serializza il path con escaping corretto per backslash Windows
        var metPathJson = JsonConvert.ToString(weatherMetPath);

        var json =
            "{\n" +
            "  \"$type\": \"Models.Core.Simulations, Models\",\n" +
            "  \"Version\": 168,\n" +
            $"  \"Name\": \"Spacing_{EscapeId(unit.Id)}\",\n" +
            "  \"Children\": [\n" +
            "    {\n" +
            "      \"$type\": \"Models.Storage.DataStore, Models\",\n" +
            "      \"Name\": \"DataStore\",\n" +
            "      \"useFirebird\": false,\n" +
            "      \"CustomFileName\": null,\n" +
            "      \"Children\": [],\n" +
            "      \"Enabled\": true,\n" +
            "      \"ReadOnly\": false\n" +
            "    },\n" +
            "    {\n" +
            "      \"$type\": \"Models.Core.Simulation, Models\",\n" +
            $"      \"Name\": \"{EscapeId(unit.Id)}\",\n" +
            "      \"Children\": [\n" +
            "        {\n" +
            "          \"$type\": \"Models.Clock, Models\",\n" +
            "          \"Name\": \"Clock\",\n" +
            $"          \"Start\": \"{startDate:yyyy-MM-dd}T00:00:00\",\n" +
            $"          \"End\": \"{endDate:yyyy-MM-dd}T00:00:00\"\n" +
            "        },\n" +
            "        {\n" +
            "          \"$type\": \"Models.Summary, Models\",\n" +
            "          \"Name\": \"Summary\",\n" +
            "          \"Verbosity\": 100\n" +
            "        },\n" +
            "        {\n" +
            "          \"$type\": \"Models.Climate.Weather, Models\",\n" +
            "          \"Name\": \"Weather\",\n" +
            $"          \"FileName\": {metPathJson}\n" +
            "        },\n" +
            // SoilArbitrator: richiesto da APSIM NG per la gestione water/N uptake delle piante.
            // Senza di esso il plant node non può interagire con il suolo → crescita bloccata.
            "        {\n" +
            "          \"$type\": \"Models.Soils.Arbitrator.SoilArbitrator, Models\",\n" +
            "          \"Name\": \"SoilArbitrator\",\n" +
            "          \"Children\": [],\n" +
            "          \"Enabled\": true,\n" +
            "          \"ReadOnly\": false\n" +
            "        },\n" +
            "        {\n" +
            "          \"$type\": \"Models.MicroClimate, Models\",\n" +
            "          \"Name\": \"MicroClimate\",\n" +
            "          \"a_interception\": 0.0,\n" +
            "          \"b_interception\": 1.0,\n" +
            "          \"c_interception\": 0.0,\n" +
            "          \"d_interception\": 0.0,\n" +
            "          \"SoilHeatFluxFraction\": 0.4,\n" +
            "          \"NightInterceptionFraction\": 0.5,\n" +
            "          \"ReferenceHeight\": 2.0\n" +
            "        },\n" +
            "        {\n" +
            "          \"$type\": \"Models.Core.Zone, Models\",\n" +
            "          \"Name\": \"Field\",\n" +
            "          \"Area\": 1.0,\n" +
            "          \"Children\": [\n" +
            "            " + zoneChildren + "\n" +
            "          ]\n" +
            "        }\n" +
            "      ]\n" +
            "    }\n" +
            "  ]\n" +
            "}";

        var fileName = $"spacing_{EscapeId(unit.Id)}.apsimx";
        var filePath = Path.Combine(_outputDir, fileName);
        File.WriteAllText(filePath, json);
        return filePath;
    }

    // -------------------------------------------------------------------------
    // Children della Zone
    // -------------------------------------------------------------------------

    private string BuildZoneChildren(CropRotation rotation, string soilJson,
        Dictionary<string, Dictionary<string, double>> cultivarOverrides = null)
    {
        var parts = new List<string>();

        parts.Add(soilJson);
        parts.Add(BuildSurfaceOrganicMatterNode());
        parts.Add(BuildIrrigationNode());
        parts.Add(BuildFertiliserNode());

        foreach (var crop in rotation.Crops)
        {
            Dictionary<string, double> overrides = null;
            cultivarOverrides?.TryGetValue(crop.CropName, out overrides);
            var effectiveOverrides = new Dictionary<string, double>(crop.ParameterOverrides, StringComparer.OrdinalIgnoreCase);
            if (overrides != null)
                foreach (var item in overrides)
                    effectiveOverrides[item.Key] = item.Value;
            var appliedOverrides = effectiveOverrides.Count == 0 ? null : effectiveOverrides;
            parts.Add(BuildPlantNode(crop.CropName, appliedOverrides));
            parts.Add(BuildSowingManager(crop, appliedOverrides));
            parts.Add(BuildHarvestManager(crop));

            // ---- Fertilizzazione ----
            // Costruisce tutti i Manager di fertilizzazione necessari:
            //   • dose alla semina (NitrogenMineralKgHa > 0, Trigger=Sowing)
            //   • applicazioni frazionate (SplitFertiliser, Trigger=DaysAfterSowing)
            foreach (var fertManager in BuildFertilisationManagers(crop))
                parts.Add(fertManager);

            // ---- Irrigazione regolata ----
            if (crop.Management.AutoIrrigate)
                parts.Add(BuildIrrigationManager(crop));
        }

        parts.Add(BuildReport(rotation));

        return string.Join(",\n            ", parts);
    }

    // -------------------------------------------------------------------------
    // Nodi infrastruttura
    // -------------------------------------------------------------------------

    private static string BuildPlantNode(string cropName,
        Dictionary<string, double> cultivarOverrides = null)
    {
        string childrenJson = "[]";

        if (cultivarOverrides != null && cultivarOverrides.Count > 0)
        {
            // Aggiunge un nodo Cultivar "override" come figlio del Plant.
            // Il nome della cultivar sown deve corrispondere a questo Name.
            //
            // Formato corretto APSIM Cultivar Command (verificato su Maize.json / Sorghum.json):
            //   "[SubModello].percorso.al.parametro = valore"
            //   es. "[Phenology].Juvenile.Target.FixedValue = 250"
            //       "[Leaf].Photosynthesis.RUE.FixedValue = 1.8"
            //
            // CalibrationParameter.Name usa "SubModello.percorso" (es. "Phenology.Juvenile.Target.FixedValue").
            // Spezziamo al primo punto per ottenere il formato con [].
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var commands = cultivarOverrides
                .Select(kv =>
                {
                    var dot = kv.Key.IndexOf('.');
                    var apsimPath = dot > 0
                        ? $"[{kv.Key[..dot]}].{kv.Key[(dot + 1)..]}"
                        : $"[{kv.Key}]";
                    return JsonConvert.ToString(
                        $"{apsimPath} = {kv.Value.ToString("G6", inv)}");
                })
                .ToList();

            var cmdArray = "[" + string.Join(", ", commands) + "]";

            childrenJson =
                "[\n" +
                "  {\n" +
                "    \"$type\": \"Models.PMF.Cultivar, Models\",\n" +
                $"    \"Name\": \"{cropName}_cal\",\n" +
                "    \"ResourceName\": null,\n" +
                $"    \"Command\": {cmdArray},\n" +
                "    \"Children\": [],\n" +
                "    \"Enabled\": true,\n" +
                "    \"ReadOnly\": false\n" +
                "  }\n" +
                "]";
        }

        return
            "{\n" +
            "  \"$type\": \"Models.PMF.Plant, Models\",\n" +
            $"  \"Name\": \"{cropName}\",\n" +
            $"  \"ResourceName\": \"{cropName}\",\n" +
            $"  \"Children\": {childrenJson}\n" +
            "}";
    }

    private static string BuildIrrigationNode() =>
        "{\n" +
        "  \"$type\": \"Models.Irrigation, Models\",\n" +
        "  \"Name\": \"Irrigation\",\n" +
        "  \"Children\": []\n" +
        "}";

    private static string BuildFertiliserNode() =>
        "{\n" +
        "  \"$type\": \"Models.Fertiliser, Models\",\n" +
        "  \"Name\": \"Fertiliser\",\n" +
        "  \"ResourceName\": \"Fertiliser\",\n" +
        "  \"Children\": []\n" +
        "}";

    private static string BuildSurfaceOrganicMatterNode() =>
        "{\n" +
        "  \"$type\": \"Models.Surface.SurfaceOrganicMatter, Models\",\n" +
        "  \"Name\": \"SurfaceOrganicMatter\",\n" +
        "  \"ResourceName\": \"SurfaceOrganicMatter\",\n" +
        "  \"InitialResidueName\": \"wheat_stubble\",\n" +
        "  \"InitialResidueType\": \"wheat\",\n" +
        "  \"InitialResidueMass\": 500.0,\n" +
        "  \"InitialStandingFraction\": 0.0,\n" +
        "  \"InitialCPR\": 0.0,\n" +
        "  \"InitialCNR\": 80.0,\n" +
        "  \"Children\": []\n" +
        "}";

    // -------------------------------------------------------------------------
    // Manager di semina
    // Struttura identica al SowingRule di Sorghum.apsimx originale APSIM.
    // I parametri (finestra, cultivar, densita') passano via "Parameters" array.
    // -------------------------------------------------------------------------

    private static string BuildSowingManager(RotationCrop crop,
        Dictionary<string, double> cultivarOverrides = null)
    {
        var m        = crop.Management;
        // Se ci sono override, usa il cultivar "_cal" iniettato nel Plant
        var cultivar = cultivarOverrides != null
            ? $"{crop.CropName}_cal"
            : (m.Cultivar ?? DefaultCultivar(crop.CropName));

        // Finestra di semina: da ManagementPractice se configurata, altrimenti default per coltura.
        // Per impostare una finestra specifica: management.SowingWindowStart = "15-apr"
        var (startDate, endDate) = (m.SowingWindowStart, m.SowingWindowEnd) switch
        {
            ({ } s, { } e) => (s, e),
            _              => SowingWindow(crop.CropName)
        };

        // Codice del Manager: derivato dal SowingRule di Sorghum.apsimx.
        // Inclusi i parametri rowConfig/tillering/ftn richiesti dal modello Sorghum.
        // Per colture non-Sorghum (Wheat, Maize) la firma base e' sufficiente.
        bool isSorghum = crop.CropName == "Sorghum";

        // Manager minimalista: usa [Link(ByName=true)] per trovare la coltura per nome.
        // Nessuna dipendenza ISoilWater/Accumulator — semina nella finestra se non è viva.
        var cropField = crop.CropName; // es. "Sorghum"

        var code =
            "using APSIM.Shared.Utilities;\n" +
            "using Models.PMF;\n" +
            "using Models.Core;\n" +
            "using System;\n" +
            "\n" +
            "namespace Models\n" +
            "{\n" +
            "    [Serializable]\n" +
            "    public class Script : Model\n" +
            "    {\n" +
            "        [Link] private Clock Clock;\n" +
            "        [Link] private Summary Summary;\n" +
            $"        [Link(ByName = true)] private Plant {cropField};\n" +
            "\n" +
            "        [Description(\"Start of sowing window (d-mmm)\")]\n" +
            "        public string StartDate { get; set; }\n" +
            "\n" +
            "        [Description(\"End of sowing window (d-mmm)\")]\n" +
            "        public string EndDate { get; set; }\n" +
            "\n" +
            "        [Description(\"Cultivar name\")]\n" +
            "        public string CultivarName { get; set; }\n" +
            "\n" +
            "        [Description(\"Sowing depth (mm)\")]\n" +
            "        public double SowingDepth { get; set; }\n" +
            "\n" +
            "        [Description(\"Row spacing (mm)\")]\n" +
            "        public double RowSpacing { get; set; }\n" +
            "\n" +
            "        [Description(\"Sowing population (/m2)\")]\n" +
            "        public double Population { get; set; }\n" +
            "\n" +
            (isSorghum ?
            "        [Description(\"Row config (0=Solid)\")]\n" +
            "        public double RowConfig { get; set; }\n" +
            "        [Description(\"Tillering method (0=Fixed)\")]\n" +
            "        public int TilleringMethod { get; set; }\n" +
            "        [Description(\"Fertile tiller number\")]\n" +
            "        public double Ftn { get; set; }\n\n"
            : "") +
            "        [EventSubscribe(\"DoManagement\")]\n" +
            "        private void OnDoManagement(object sender, EventArgs e)\n" +
            "        {\n" +
            $"            if (DateUtilities.WithinDates(StartDate, Clock.Today, EndDate) && !{cropField}.IsAlive)\n" +
            "            {\n" +
            $"                Summary.WriteMessage(this, $\"Sowing {cropField} on {{Clock.Today:dd-MMM-yyyy}} cultivar={{CultivarName}} pop={{Population}}\", MessageType.Diagnostic);\n" +
            (isSorghum ?
            $"                {cropField}.Sow(cultivar: CultivarName, population: Population, depth: SowingDepth,\n" +
            "                         rowSpacing: RowSpacing, rowConfig: RowConfig,\n" +
            "                         tillering: TilleringMethod, ftn: Ftn);\n"
            :
            $"                {cropField}.Sow(cultivar: CultivarName, population: Population,\n" +
            "                         depth: SowingDepth, rowSpacing: RowSpacing);\n") +
            "            }\n" +
            "        }\n" +
            "    }\n" +
            "}";

        var population = m.SowingDensity.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        var depth      = m.SowingDepth.ToString("F0");
        var spacing    = m.RowSpacing.ToString("F0");

        var baseParams = new List<object>
        {
            new { Key = "StartDate",   Value = startDate },
            new { Key = "EndDate",     Value = endDate },
            new { Key = "CultivarName",Value = cultivar },
            new { Key = "SowingDepth", Value = depth },
            new { Key = "RowSpacing",  Value = spacing },
            new { Key = "Population",  Value = population }
        };

        if (isSorghum)
        {
            baseParams.Add(new { Key = "RowConfig",      Value = "0" });   // 0 = Solid
            baseParams.Add(new { Key = "TilleringMethod",Value = "0" });   // 0 = FixedTillering
            baseParams.Add(new { Key = "Ftn",            Value = "3" });
        }

        var parameters  = JsonConvert.SerializeObject(baseParams, Formatting.Indented)
                                     .Replace("\n", "\n  ");
        var codeArrayJson = CodeToArrayJson(code);

        return
            "{\n" +
            "  \"$type\": \"Models.Manager, Models\",\n" +
            $"  \"Name\": \"Sow_{crop.CropName}\",\n" +
            $"  \"CodeArray\": {codeArrayJson},\n" +
            $"  \"Parameters\": {parameters},\n" +
            "  \"Children\": []\n" +
            "}";
    }

    // -------------------------------------------------------------------------
    // Manager di raccolta
    // Identico al "Harvesting rule" di Sorghum.apsimx
    // -------------------------------------------------------------------------

    private static string BuildHarvestManager(RotationCrop crop)
    {
        // Usa esattamente il pattern del "Harvesting rule" di Sorghum.apsimx:
        //   [Link] private IPlant crop;   ← type-based (non ByName), risolve il primo IPlant nello scope
        // IPlant è in Models.Core; Plant implementa IPlant ed ha IsReadyForHarvesting → EndPhase.
        // NON usare [Link(ByName=true)] private Plant X: se la risoluzione per nome fallisce
        // silenziosamente il campo rimane null e OnEndOfDay lancia NullReferenceException silenziosa.
        var cropName = crop.CropName;
        var code =
            "using Models.Soils;\n" +
            "using System;\n" +
            "using System.Linq;\n" +
            "using Models.Core;\n" +
            "using Models.PMF;\n" +
            "\n" +
            "namespace Models\n" +
            "{\n" +
            "    [Serializable]\n" +
            "    public class Script : Model\n" +
            "    {\n" +
            "        [Link]\n" +
            "        private Clock clock;\n" +
            "\n" +
            "        [Link]\n" +
            "        private IPlant crop;\n" +
            "\n" +
            "        [EventSubscribe(\"EndOfDay\")]\n" +
            "        private void OnDoCalculations(object sender, EventArgs e)\n" +
            "        {\n" +
            "            if (crop.IsReadyForHarvesting)\n" +
            "            {\n" +
            "                crop.Harvest();\n" +
            "                crop.EndCrop();\n" +
            "            }\n" +
            "        }\n" +
            "    }\n" +
            "}";

        return
            "{\n" +
            "  \"$type\": \"Models.Manager, Models\",\n" +
            $"  \"Name\": \"Harvest_{cropName}\",\n" +
            $"  \"CodeArray\": {CodeToArrayJson(code)},\n" +
            "  \"Parameters\": [],\n" +
            "  \"Children\": []\n" +
            "}";
    }

    // -------------------------------------------------------------------------
    // Manager di fertilizzazione (frazionata)
    //
    // Costruisce uno o più Manager APSIM a seconda delle applicazioni configurate:
    //
    //   Trigger "Sowing"          → applica alla semina (evento Sowing del crop)
    //   Trigger "DaysAfterSowing" → applica N giorni dopo la semina, usando una
    //                               flag reset dal Sowing e controllata in DoManagement
    //
    // Ogni applicazione diventa un Manager separato per semplicità e tracciabilità.
    // I nomi dei Manager sono: Fertilise_{Crop}_S (sowing), Fertilise_{Crop}_D{DAS} (DAS).
    //
    // Se SplitFertiliser è null usa la dose singola NitrogenMineralKgHa alla semina.
    // Se SplitFertiliser è valorizzata ignora NitrogenMineralKgHa (la lista è completa).
    // -------------------------------------------------------------------------

    private static IEnumerable<string> BuildFertilisationManagers(RotationCrop crop)
    {
        var m   = crop.Management;
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        // Costruisce la lista di applicazioni
        List<FertiliserApplication> apps;
        if (m.SplitFertiliser != null && m.SplitFertiliser.Count > 0)
        {
            apps = m.SplitFertiliser;
        }
        else if (m.NitrogenMineralKgHa > 0)
        {
            apps = new List<FertiliserApplication>
            {
                new() { Trigger = "Sowing", AmountKgNHa = m.NitrogenMineralKgHa, FertType = "UreaN" }
            };
        }
        else
        {
            yield break;   // nessuna fertilizzazione
        }

        int dasCount = 0; // indice per evitare duplicati di nome
        foreach (var app in apps)
        {
            if (app.AmountKgNHa <= 0) continue;

            string amount = app.AmountKgNHa.ToString("F1", inv);
            string ftype  = app.FertType ?? "UreaN";

            if (app.Trigger == "Sowing")
            {
                yield return BuildSowingFertilisationManager(crop, amount, ftype);
            }
            else if (app.Trigger == "DaysAfterSowing")
            {
                dasCount++;
                yield return BuildDasNitrogenManager(crop, app.DaysAfterSowing, amount, ftype, dasCount);
            }
        }
    }

    /// <summary>
    /// Fertilizza alla semina (evento Sowing del crop specifico).
    /// Identico al vecchio BuildFertilisationManager.
    /// </summary>
    private static string BuildSowingFertilisationManager(
        RotationCrop crop, string amount, string fertType)
    {
        var code =
            "using Models.Core;\n" +
            "using System;\n" +
            "\n" +
            "namespace Models\n" +
            "{\n" +
            "    [Serializable]\n" +
            "    public class Script : Model\n" +
            "    {\n" +
            "        [Link] private Fertiliser Fertiliser;\n" +
            "        [Link] private Summary Summary;\n" +
            $"        [Link(ByName = true)] private Models.PMF.Plant {crop.CropName};\n" +
            "\n" +
            "        [Description(\"Amount of N (kg/ha)\")]\n" +
            "        public double Amount { get; set; }\n" +
            "\n" +
            "        [Description(\"Fertiliser type\")]\n" +
            "        public string FertType { get; set; }\n" +
            "\n" +
            "        [EventSubscribe(\"Sowing\")]\n" +
            "        private void OnSowing(object sender, EventArgs e)\n" +
            "        {\n" +
            $"            if (sender == {crop.CropName} && Amount > 0)\n" +
            "            {\n" +
            "                Fertiliser.Apply(amount: Amount, type: FertType);\n" +
            $"                Summary.WriteMessage(this, $\"N alla semina {crop.CropName}: {{Amount:F1}} kg N/ha\", MessageType.Diagnostic);\n" +
            "            }\n" +
            "        }\n" +
            "    }\n" +
            "}";

        var parameters = JsonConvert.SerializeObject(new[]
        {
            new { Key = "Amount",   Value = amount },
            new { Key = "FertType", Value = fertType }
        }, Formatting.Indented).Replace("\n", "\n  ");

        return
            "{\n" +
            "  \"$type\": \"Models.Manager, Models\",\n" +
            $"  \"Name\": \"Fertilise_{crop.CropName}_S\",\n" +
            $"  \"CodeArray\": {CodeToArrayJson(code)},\n" +
            $"  \"Parameters\": {parameters},\n" +
            "  \"Children\": []\n" +
            "}";
    }

    /// <summary>
    /// Fertilizza N giorni dopo la semina (copertura azotata frazionata).
    ///
    /// Strategia:
    ///   1. OnSowing: memorizza la data di semina, resetta il flag "applicato".
    ///   2. OnDoManagement: se il crop è vivo e (oggi - data semina) >= DAS → applica e imposta flag.
    ///   3. Se il crop termina (EndCrop) prima del DAS: resetta la data (stagione persa).
    ///
    /// Non usa Phenology.Stage perché IPlant non espone lo stage direttamente
    /// (richiederebbe un [Link] al tipo concreto Plant che varia per coltura).
    /// DaysAfterSowing è più robusto e interpretabile agronomicamente.
    /// </summary>
    private static string BuildDasNitrogenManager(
        RotationCrop crop, int daysAfterSowing, string amount, string fertType, int idx)
    {
        var cropName = crop.CropName;
        var dasStr   = daysAfterSowing.ToString();

        var code =
            "using Models.Core;\n" +
            "using System;\n" +
            "\n" +
            "namespace Models\n" +
            "{\n" +
            "    [Serializable]\n" +
            "    public class Script : Model\n" +
            "    {\n" +
            "        [Link] private Clock Clock;\n" +
            "        [Link] private Fertiliser Fertiliser;\n" +
            "        [Link] private Summary Summary;\n" +
            $"        [Link(ByName = true)] private Models.PMF.Plant {cropName};\n" +
            "\n" +
            "        [Description(\"Days after sowing\")]\n" +
            "        public int DAS { get; set; }\n" +
            "\n" +
            "        [Description(\"Amount of N (kg/ha)\")]\n" +
            "        public double Amount { get; set; }\n" +
            "\n" +
            "        [Description(\"Fertiliser type\")]\n" +
            "        public string FertType { get; set; }\n" +
            "\n" +
            "        private DateTime _sowDate = DateTime.MinValue;\n" +
            "        private bool _applied = false;\n" +
            "\n" +
            "        [EventSubscribe(\"Sowing\")]\n" +
            "        private void OnSowing(object sender, EventArgs e)\n" +
            "        {\n" +
            $"            if (sender == {cropName})\n" +
            "            {\n" +
            "                _sowDate = Clock.Today;\n" +
            "                _applied = false;\n" +
            "            }\n" +
            "        }\n" +
            "\n" +
            "        [EventSubscribe(\"DoManagement\")]\n" +
            "        private void OnDoManagement(object sender, EventArgs e)\n" +
            "        {\n" +
            $"            if (!_applied && _sowDate != DateTime.MinValue && {cropName}.IsAlive)\n" +
            "            {\n" +
            "                int das = (Clock.Today - _sowDate).Days;\n" +
            "                if (das >= DAS && Amount > 0)\n" +
            "                {\n" +
            "                    Fertiliser.Apply(amount: Amount, type: FertType);\n" +
            $"                    Summary.WriteMessage(this, $\"N copertura {cropName}: {{Amount:F1}} kg N/ha a {{das}} DAS\", MessageType.Diagnostic);\n" +
            "                    _applied = true;\n" +
            "                }\n" +
            "            }\n" +
            "            // Reset quando la coltura è morta/raccolta\n" +
            $"            if (!{cropName}.IsAlive) {{ _sowDate = DateTime.MinValue; _applied = false; }}\n" +
            "        }\n" +
            "    }\n" +
            "}";

        var parameters = JsonConvert.SerializeObject(new[]
        {
            new { Key = "DAS",      Value = dasStr },
            new { Key = "Amount",   Value = amount },
            new { Key = "FertType", Value = fertType }
        }, Formatting.Indented).Replace("\n", "\n  ");

        return
            "{\n" +
            "  \"$type\": \"Models.Manager, Models\",\n" +
            $"  \"Name\": \"Fertilise_{cropName}_D{daysAfterSowing}_{idx}\",\n" +
            $"  \"CodeArray\": {CodeToArrayJson(code)},\n" +
            $"  \"Parameters\": {parameters},\n" +
            "  \"Children\": []\n" +
            "}";
    }

    // -------------------------------------------------------------------------
    // Manager di irrigazione regolata (threshold-based)
    //
    // Logica:
    //   1. Solo nella finestra stagionale configurata
    //   2. Solo quando il crop è in campo (IsAlive = true)
    //   3. Calcola PAW corrente e PAWC con SW, LL15, DUL e Thickness
    //      → ottiene i dati via Zone.FindDescendant (pattern APSIM standard)
    //   4. Se PAW/PAWC < Threshold → applica min(deficit_to_Target, MaxMm)
    //
    // Zone.FindDescendant è il modo corretto per accedere ai modelli suolo
    // da un Manager script (evita [Link] diretti che dipendono dai nomi dei nodi).
    // -------------------------------------------------------------------------

    private static string BuildIrrigationManager(RotationCrop crop)
    {
        var m    = crop.Management;
        var inv  = System.Globalization.CultureInfo.InvariantCulture;

        // Finestra: usa quella di irrigazione se impostata, altrimenti usa semina
        var (winStart, winEnd) = (m.IrrigationWindowStart, m.IrrigationWindowEnd) switch
        {
            ({ } s, { } e) => (s, e),
            _              => SowingWindow(crop.CropName)
        };

        var code =
            "using Models.Core;\n" +
            "using Models.Soils;\n" +
            "using Models.Interfaces;\n" +
            "using System;\n" +
            "using System.Linq;\n" +
            "using APSIM.Shared.Utilities;\n" +
            "\n" +
            "namespace Models\n" +
            "{\n" +
            "    [Serializable]\n" +
            "    public class Script : Model\n" +
            "    {\n" +
            "        [Link] private Clock Clock;\n" +
            "        [Link] private Irrigation Irrigation;\n" +
            "        [Link] private Summary Summary;\n" +
            "        [Link] private Zone Zone;\n" +
            $"        [Link(ByName = true)] private Models.PMF.Plant {crop.CropName};\n" +
            "\n" +
            "        [Description(\"Soglia PAW/PAWC sotto cui irrigare (0-1)\")]\n" +
            "        public double Threshold { get; set; }\n" +
            "\n" +
            "        [Description(\"Obiettivo di ricarica PAW/PAWC (0-1)\")]\n" +
            "        public double Target { get; set; }\n" +
            "\n" +
            "        [Description(\"Dose massima per turno (mm)\")]\n" +
            "        public double MaxMm { get; set; }\n" +
            "\n" +
            "        [Description(\"Inizio finestra irrigua (d-mmm)\")]\n" +
            "        public string WindowStart { get; set; }\n" +
            "\n" +
            "        [Description(\"Fine finestra irrigua (d-mmm)\")]\n" +
            "        public string WindowEnd { get; set; }\n" +
            "\n" +
            "        [EventSubscribe(\"DoManagement\")]\n" +
            "        private void OnDoManagement(object sender, EventArgs e)\n" +
            "        {\n" +
            $"            if (!{crop.CropName}.IsAlive) return;\n" +
            "            if (!DateUtilities.WithinDates(WindowStart, Clock.Today, WindowEnd)) return;\n" +
            "\n" +
            "            // Calcola PAW e PAWC dal profilo suolo corrente\n" +
            "            var physical = Zone.FindDescendant<IPhysical>();\n" +
            "            var soilWater = Zone.FindDescendant<ISoilWater>();\n" +
            "            if (physical == null || soilWater == null) return;\n" +
            "\n" +
            "            double[] sw        = soilWater.SW;         // vol. water content\n" +
            "            double[] thickness = soilWater.Thickness;   // ISoilWater espone Thickness\n" +
            "            double[] dul       = physical.DUL;          // IPhysical espone DUL e LL15\n" +
            "            double[] ll15      = physical.LL15;\n" +
            "\n" +
            "            double pawc = 0, paw = 0;\n" +
            "            int nLayers = Math.Min(sw.Length, Math.Min(dul.Length, Math.Min(ll15.Length, thickness.Length)));\n" +
            "            for (int i = 0; i < nLayers; i++)\n" +
            "            {\n" +
            "                double lPawc = thickness[i] * Math.Max(0, dul[i] - ll15[i]);\n" +
            "                double lPaw  = thickness[i] * Math.Max(0, Math.Min(sw[i], dul[i]) - ll15[i]);\n" +
            "                pawc += lPawc;\n" +
            "                paw  += lPaw;\n" +
            "            }\n" +
            "\n" +
            "            if (pawc <= 0) return;\n" +
            "            double fraction = paw / pawc;\n" +
            "\n" +
            "            if (fraction < Threshold)\n" +
            "            {\n" +
            "                double deficit = (Target - fraction) * pawc;\n" +
            "                double amount  = Math.Min(Math.Max(0, deficit), MaxMm);\n" +
            "                if (amount >= 1.0)\n" +
            "                {\n" +
            "                    Irrigation.Apply(amount);\n" +
            $"                    Summary.WriteMessage(this, $\"Irrigazione {crop.CropName}: {{amount:F1}} mm (PAW {{fraction:P0}} → target {{Target:P0}})\", MessageType.Diagnostic);\n" +
            "                }\n" +
            "            }\n" +
            "        }\n" +
            "    }\n" +
            "}";

        var parameters = JsonConvert.SerializeObject(new[]
        {
            new { Key = "Threshold",   Value = m.IrrigationThreshold.ToString("F2", inv) },
            new { Key = "Target",      Value = m.IrrigationTarget.ToString("F2", inv) },
            new { Key = "MaxMm",       Value = m.MaxIrrigationMm.ToString("F1", inv) },
            new { Key = "WindowStart", Value = winStart },
            new { Key = "WindowEnd",   Value = winEnd }
        }, Formatting.Indented).Replace("\n", "\n  ");

        return
            "{\n" +
            "  \"$type\": \"Models.Manager, Models\",\n" +
            $"  \"Name\": \"Irrigate_{crop.CropName}\",\n" +
            $"  \"CodeArray\": {CodeToArrayJson(code)},\n" +
            $"  \"Parameters\": {parameters},\n" +
            "  \"Children\": []\n" +
            "}";
    }

    // -------------------------------------------------------------------------
    // Report
    // -------------------------------------------------------------------------

    private static string BuildReport(CropRotation rotation)
    {
        var vars = new List<string> { "[Clock].Today", "[Clock].Today.Year as Year" };

        foreach (var crop in rotation.Crops)
        {
            vars.Add($"[{crop.CropName}].Grain.Wt * 10 as {crop.CropName}_GrainYield_kgha");
            vars.Add($"[{crop.CropName}].AboveGround.Wt * 10 as {crop.CropName}_Biomass_kgha");
            vars.Add($"[{crop.CropName}].Leaf.LAI as {crop.CropName}_LAI");
        }

        // [NO3] cerca il Solute per nome nell'intera scope — più robusto di [Soil].NO3.kgha
        vars.Add("sum([NO3].kgha) as SoilNO3_kgha");

        var varArrayJson = JsonConvert.SerializeObject(vars, Formatting.Indented)
            .Replace("\n", "\n        ");
        // Triggera su Harvesting di ogni coltura in rotazione
        var harvestEvents = rotation.Crops.Select(c => $"[{c.CropName}].Harvesting").ToArray();
        var eventsJson  = JsonConvert.SerializeObject(harvestEvents, Formatting.Indented)
            .Replace("\n", "\n        ");

        return
            "{\n" +
            "  \"$type\": \"Models.Report, Models\",\n" +
            "  \"Name\": \"Report\",\n" +
            $"  \"VariableNames\": {varArrayJson},\n" +
            $"  \"EventNames\": {eventsJson},\n" +
            "  \"Children\": []\n" +
            "},\n" +
            BuildDailyReport(rotation);
    }

    /// <summary>
    /// Report giornaliero completo per analisi carbon farming e calcolo trade-off.
    /// Trigger: EndOfDay (ogni giorno della simulazione).
    ///
    /// Gruppi di variabili:
    ///   Meteo      : MaxT, MinT, Rain, Radn, Wind, VP, ET0 (PetTotal MicroClimate)
    ///   Coltura    : IsAlive, Stage, LAI, Biomassa, Radici, Granella, N_Biomassa
    ///   Acqua suolo: SW per strato, ESW, percolazione fondo, runoff, Es, Eo, LeachNO3
    ///   N suolo    : NO3 e NH4 per strato, N mineralizzato, N nitrificato
    ///   C suolo    : TotalC per strato (C organico dinamico)
    ///   GHG        : CO2-C da decomposizione (Catm), N2O-N (N2Oatm) per strato
    ///
    /// Il nome del modello WaterBalance è "SoilWater" (come in SoilAdapter).
    /// Il nome del modello Nutrient   è "Nutrient"  (come in SoilAdapter).
    /// Arrays per strato: APSIM crea automaticamente colonne (1),(2)...(N).
    /// </summary>
    private static string BuildDailyReport(CropRotation rotation)
    {
        var vars = new List<string>
        {
            // --- Identificatori temporali ---
            "[Clock].Today",
            "[Clock].Today.Year as Year",
            "[Clock].Today.DayOfYear as DOY",

            // --- Meteo ---
            "[Weather].MaxT as Tx",
            "[Weather].MinT as Tn",
            "[Weather].Rain as Rain_mm",
            "[Weather].Radn as Radn_MJm2",
            "[Weather].Wind as Wind_ms",
            "[Weather].VP as VP_hPa",
            // ET0: PetTotal di MicroClimate (Penman-Monteith sulla canopy + suolo)
            "[MicroClimate].PetTotal as ET0_mm",
            // Componenti evapotraspirazione reale dal WaterBalance
            "[SoilWater].Eo as PotEvapSoil_mm",
            "[SoilWater].Es as ActEvapSoil_mm",

            // --- Bilancio idrico ---
            "[SoilWater].Runoff as Runoff_mm",
            // Percolazione al fondo del profilo = Flux[ultimo strato]
            "[SoilWater].Drainage as Perc_mm",
            // Lisciviazione NO3 al fondo (kg N/ha/giorno)
            "[SoilWater].LeachNO3 as LeachNO3_kgNha",
            // Irrigazione applicata (mm/giorno). 0 se non irrigato.
            "[Irrigation].IrrigationApplied as Irrig_mm",

            // --- Acqua nel suolo ---
            // Per strato (mm): APSIM scrive colonne SWmm(1)..SWmm(N)
            "[SoilWater].SWmm as SWmm",
            // Totale profilo (mm): colonna scalare comoda per analisi
            "sum([SoilWater].SWmm) as SoilSWtot_mm",

            // --- N minerale nel suolo (per strato, kg N/ha) ---
            // [NO3] e [NH4] cercano i Solute per nome nell'intera scope
            "[NO3].kgha as NO3_kgha",
            "[NH4].kgha as NH4_kgha",

            // --- Ciclo N (somme giornaliere, kg N/ha) ---
            "sum([Nutrient].MineralisedN) as MinerN_kgNha",
            "sum([Nutrient].NitrifiedN)   as NitrN_kgNha",

            // --- Carbonio organico nel suolo (per strato, kg C/ha) ---
            "[Nutrient].TotalC as SoilC_kgCha",  // C organico totale per layer

            // --- GHG (giornalieri, per strato, kg/ha) ---
            // CO2-C da decomposizione (respirazione eterotrofa)
            "[Nutrient].Catm as CO2C_kgCha",
            // N2O-N da denitrificazione/nitrificazione
            "[Nutrient].N2Oatm as N2O_kgNha",

            // --- Somme GHG (profilo intero) ---
            "sum([Nutrient].Catm)   as CO2C_total_kgCha",
            "sum([Nutrient].N2Oatm) as N2O_total_kgNha",
            "sum([Nutrient].TotalC) as SoilCtot_kgCha"
        };

        // --- Coltura (per ogni crop nella rotazione) ---
        foreach (var crop in rotation.Crops)
        {
            vars.Add($"[{crop.CropName}].IsAlive as {crop.CropName}_IsAlive");
            vars.Add($"[{crop.CropName}].Phenology.Stage as {crop.CropName}_Stage");
            vars.Add($"[{crop.CropName}].Phenology.CurrentStageName as {crop.CropName}_StageName");
            vars.Add($"[{crop.CropName}].Leaf.LAI as {crop.CropName}_LAI");
            // Wt in g/m² → *10 = kg/ha
            vars.Add($"[{crop.CropName}].AboveGround.Wt * 10 as {crop.CropName}_Biomass_kgha");
            vars.Add($"[{crop.CropName}].Root.Wt * 10 as {crop.CropName}_RootWt_kgha");
            vars.Add($"[{crop.CropName}].Grain.Wt * 10 as {crop.CropName}_GrainWt_kgha");
            // N nella biomassa aerea (g N/m² → *10 = kg N/ha)
            vars.Add($"[{crop.CropName}].AboveGround.N * 10 as {crop.CropName}_BiomassN_kgNha");
        }

        var varArrayJson = JsonConvert.SerializeObject(vars, Formatting.Indented)
            .Replace("\n", "\n        ");

        var eventsJson = JsonConvert.SerializeObject(
            new[] { "[Clock].EndOfDay" }, Formatting.Indented)
            .Replace("\n", "\n        ");

        return
            "{\n" +
            "  \"$type\": \"Models.Report, Models\",\n" +
            "  \"Name\": \"DailyReport\",\n" +
            $"  \"VariableNames\": {varArrayJson},\n" +
            $"  \"EventNames\": {eventsJson},\n" +
            "  \"Children\": []\n" +
            "}";
    }

    // -------------------------------------------------------------------------
    // Dati agronomici di default
    // -------------------------------------------------------------------------

    private static string DefaultCultivar(string cropName) => cropName switch
    {
        "Sorghum"   => "Buster",
        "Wheat"     => "Hartog",
        "Maize"     => "B_110",
        "Barley"    => "Grimmett",
        "Soybean"   => "Baxters_Black",
        "Grapevine" => "Cabernet_Sauvignon",
        _           => cropName
    };

    // Finestre di semina nel formato d-mmm come usa APSIM (DateUtilities.WithinDates)
    private static (string start, string end) SowingWindow(string cropName) =>
        cropName switch
        {
            "Sorghum" => ("15-apr", "15-jun"),
            "Wheat"   => ("1-oct",  "30-nov"),
            "Maize"   => ("1-apr",  "1-jun"),
            "Barley"  => ("1-oct",  "15-nov"),
            "Soybean" => ("1-may",  "15-jun"),
            _         => ("1-apr",  "1-jun")
        };

    // Rende l'ID usabile come nome JSON (rimuove caratteri problematici)
    private static string EscapeId(string id) =>
        id.Replace(" ", "_").Replace(".", "_");

    /// <summary>
    /// Converte una stringa di codice C# in un JSON array di righe
    /// nel formato atteso da Manager.CodeArray.
    /// (Manager.Code è [JsonIgnore] — bisogna usare CodeArray.)
    /// </summary>
    private static string CodeToArrayJson(string code)
    {
        var lines = code.Split('\n');
        return JsonConvert.SerializeObject(lines, Formatting.Indented)
                          .Replace("\n", "\n  ");
    }
}
