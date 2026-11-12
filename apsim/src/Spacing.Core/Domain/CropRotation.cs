namespace Spacing.Core.Domain;

/// <summary>
/// Una singola applicazione di fertilizzante nel calendario colturale.
/// Consente la fertilizzazione frazionata con trigger flessibili.
///
/// Trigger disponibili:
///   "Sowing"          → alla semina (stesso giorno, evento Sowing del crop)
///   "DaysAfterSowing" → N giorni dopo la semina (es. DAS=35 = copertura mais a V6)
///
/// Esempi comuni Pianura Padana:
///   Mais:      80 kg N alla semina + 80 kg N a 35 DAS (V6)
///   Frumento:  60 kg N alla semina + 60 kg N alla levata (~DAS=100) + 40 kg N spigatura
///   Sorgo:     60 kg N alla semina + 60 kg N a 40 DAS
/// </summary>
public class FertiliserApplication
{
    /// <summary>
    /// Tipo di trigger:
    ///   "Sowing"          : applica alla semina
    ///   "DaysAfterSowing" : applica N giorni dopo la semina (usa DaysAfterSowing)
    /// </summary>
    public string Trigger { get; set; } = "Sowing";

    /// <summary>
    /// Giorni dopo la semina per l'applicazione (usato solo con Trigger="DaysAfterSowing").
    /// Es. 35 = mais a V6, 100 = frumento alla levata.
    /// </summary>
    public int DaysAfterSowing { get; set; } = 0;

    /// <summary>Dose di azoto (kg N/ha).</summary>
    public double AmountKgNHa { get; set; }

    /// <summary>Tipo fertilizzante APSIM (es. "UreaN", "AmmoniumNitrate", "Urea").</summary>
    public string FertType { get; set; } = "UreaN";
}

/// <summary>
/// Pratica gestionale applicata a una coltura in rotazione.
/// </summary>
public class ManagementPractice
{
    /// <summary>Tipo di lavorazione: "conventional", "minimum", "no-till"</summary>
    public string TillageType { get; set; } = "conventional";

    /// <summary>Dose azoto minerale, kg N/ha. 0 = nessuna fertilizzazione.</summary>
    public double NitrogenMineralKgHa { get; set; } = 0.0;

    /// <summary>Dose azoto organico (letame/compost), kg N/ha.</summary>
    public double NitrogenOrganicKgHa { get; set; } = 0.0;

    /// <summary>Data di semina fissa (null = trigger meteo/suolo come in Sorghum.apsimx).</summary>
    public DateOnly? SowingDate { get; set; } = null;

    /// <summary>Cultivar APSIM da usare per questa coltura.</summary>
    public string Cultivar { get; set; } = null;

    /// <summary>Densità di semina, piante/m².</summary>
    public double SowingDensity { get; set; } = 10.0;

    /// <summary>Profondità di semina, mm.</summary>
    public double SowingDepth { get; set; } = 30.0;

    /// <summary>Sesto di impianto, mm.</summary>
    public double RowSpacing { get; set; } = 750.0;

    // ---- Irrigazione regolata ----
    // Quando AutoIrrigate = true, RotationBuilder aggiunge un Manager che monitora
    // il contenuto idrico del suolo e applica irrigazione al bisogno.

    /// <summary>Irrigazione regolata abilitata.</summary>
    public bool AutoIrrigate { get; set; } = false;

    /// <summary>
    /// Soglia di intervento: applica irrigazione quando PAW/PAWC scende sotto questa frazione.
    /// Tipici valori italiani: mais 0.45 (stressante), 0.55 (moderata), 0.65 (piena).
    /// </summary>
    public double IrrigationThreshold { get; set; } = 0.50;

    /// <summary>
    /// Obiettivo di ricarica: riempi il suolo fino a questa frazione di PAWC.
    /// Di solito = 0.90 (ricarica quasi completa) o 0.75 (risparmio idrico).
    /// </summary>
    public double IrrigationTarget { get; set; } = 0.85;

    /// <summary>
    /// Dose massima per intervento (mm). Es. 40mm per aspersione, 25mm per goccia.
    /// Se il deficit è maggiore, vengono programmati più interventi nei giorni successivi.
    /// </summary>
    public double MaxIrrigationMm { get; set; } = 40.0;

    /// <summary>
    /// Inizio finestra stagionale di irrigazione (formato APSIM "d-mmm").
    /// Null = usa gli stessi estremi della finestra di semina.
    /// Es. mais: "1-may", frumento: non irrigato.
    /// </summary>
    public string IrrigationWindowStart { get; set; } = null;

    /// <summary>Fine finestra stagionale di irrigazione (formato APSIM "d-mmm").</summary>
    public string IrrigationWindowEnd { get; set; } = null;

    // ---- Finestra di semina (opzionale) ----
    // Se null, RotationBuilder usa i default per coltura (es. Maize → "1-apr" / "1-jun").
    // Formato APSIM: "d-mmm" — es. "15-apr", "30-may".

    /// <summary>Inizio finestra di semina (formato APSIM "d-mmm"). Null = default coltura.</summary>
    public string SowingWindowStart { get; set; } = null;

    /// <summary>Fine finestra di semina (formato APSIM "d-mmm"). Null = default coltura.</summary>
    public string SowingWindowEnd { get; set; } = null;

    // ---- Fertilizzazione frazionata ----
    // Lista di applicazioni separate nel tempo (null = singola dose alla semina).
    // Ogni applicazione specifica evento APSIM, dose e tipo.
    // Es.: { Event="Sowing", Amount=80, Type="UreaN" }
    //       { Event="Tillering", Amount=40, Type="UreaN" }

    /// <summary>
    /// Applicazioni di fertilizzante aggiuntive rispetto alla dose alla semina.
    /// Null = solo applicazione alla semina con NitrogenMineralKgHa.
    /// </summary>
    public List<FertiliserApplication> SplitFertiliser { get; set; } = null;

    public override string ToString() =>
        $"{TillageType} | N={NitrogenMineralKgHa + NitrogenOrganicKgHa}kgN/ha | cult={Cultivar ?? "default"}";
}

/// <summary>
/// Singola coltura nella rotazione, con le sue pratiche gestionali.
/// </summary>
public class RotationCrop
{
    /// <summary>Fixed APSIM cultivar/model overrides supplied by rSpacing. Keys omit the leading brackets, e.g. Phenology.Juvenile.Target.FixedValue.</summary>
    public Dictionary<string, double> ParameterOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nome del modello APSIM (es. "Sorghum", "Wheat", "Maize", "Grapevine").</summary>
    public string CropName { get; set; }

    /// <summary>Durata in anni nella rotazione (di solito 1, per le arboree può essere >1).</summary>
    public int Years { get; set; } = 1;

    public ManagementPractice Management { get; set; } = new();
}

/// <summary>
/// Sequenza di colture in rotazione con le pratiche associate.
/// Usata sia dal Tool1 (rotazioni predefinite) sia dal Tool2 (composte dall'agricoltore).
/// </summary>
public class CropRotation
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }

    /// <summary>Sequenza delle colture nell'ordine di rotazione.</summary>
    public List<RotationCrop> Crops { get; set; } = new();

    /// <summary>Numero di cicli di rotazione da simulare.</summary>
    public int Cycles { get; set; } = 5;

    public override string ToString() =>
        $"{Name}: {string.Join(" → ", Crops.Select(c => c.CropName))} × {Cycles} cicli";
}
