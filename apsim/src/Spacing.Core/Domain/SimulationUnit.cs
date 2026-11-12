namespace Spacing.Core.Domain;

/// <summary>
/// Unità atomica di simulazione: una combinazione di cella × profilo suolo × rotazione.
/// È il "job" che il SimulationRunner esegue.
/// </summary>
public class SimulationUnit
{
    /// <summary>Stable identifier supplied by the caller, normally one MCMC iteration or agronomic scenario.</summary>
    public string RunId { get; set; }

    /// <summary>Opaque provenance fields propagated to the machine-readable result.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Id => $"{Cell.Id}__{SoilProfile.ProfileId}__{Rotation.Id}";

    public GridCell Cell { get; set; }
    public SoilProfile SoilProfile { get; set; }
    public CropRotation Rotation { get; set; }

    /// <summary>Anno di inizio simulazione.</summary>
    public int StartYear { get; set; }

    /// <summary>Anno di fine simulazione.</summary>
    public int EndYear { get; set; }
}

/// <summary>
/// Risultato aggregato di una SimulationUnit dopo l'esecuzione.
/// Contiene i KPI principali letti dal DataStore APSIM.
/// </summary>
public class SimulationResult
{
    public string SimulationUnitId { get; set; }
    public bool Success { get; set; }
    public string ErrorMessage { get; set; }

    /// <summary>Resa media annua per coltura, kg/ha.</summary>
    public Dictionary<string, double> MeanYieldKgHa { get; set; } = new();

    /// <summary>Biomassa aerea media, kg/ha.</summary>
    public Dictionary<string, double> MeanAbovegroundBiomassKgHa { get; set; } = new();

    /// <summary>Emissioni N2O cumulate, kg N2O-N/ha/anno (da integrare).</summary>
    public double MeanN2OEmissions { get; set; }

    /// <summary>Stock di carbonio nel suolo a fine simulazione, kg C/ha.</summary>
    public double FinalSoilCarbonKgHa { get; set; }

    /// <summary>Percorso del DataStore SQLite per analisi dettagliate.</summary>
    public string DataStorePath { get; set; }

    /// <summary>Percorso del CSV di controllo (righe harvest) scritto dal SimulationRunner.</summary>
    public string ControlCsvPath { get; set; }

    /// <summary>Percorso del CSV giornaliero (fenologia, LAI, biomassa) scritto dal SimulationRunner.</summary>
    public string DailyCsvPath { get; set; }

    /// <summary>
    /// Percorso del CSV riassuntivo per coltura (una riga per raccolto):
    /// resa, ΔC_suolo, N2O stagionale, CO2 stagionale, lisciviazione NO3.
    /// Calcolato in post-processing dal DailyReport nel SimulationRunner.
    /// </summary>
    public string CropSummaryCsvPath { get; set; }

    /// <summary>Tabella completa Report APSIM harvest (raw, per uso avanzato).</summary>
    public System.Data.DataTable RawReport { get; set; }

    /// <summary>Tabella DailyReport APSIM (raw, una riga per giorno).</summary>
    public System.Data.DataTable RawDailyReport { get; set; }
}
