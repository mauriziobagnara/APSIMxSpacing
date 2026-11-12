## Spacing — CarbonFarming Italia

Piattaforma di simulazione agronomica per la stima del sequestro di carbonio nei sistemi agricoli italiani. Usa **APSIM Next Generation** come motore di simulazione e include strumenti C# per simulazioni batch, run on-demand, gestione agronomica e calibrazione.

### Versione C# stabile di riferimento

La baseline stabile corrente è la **soluzione completa v0.09.05**.

Principali caratteristiche consolidate:

- `Spacing.Tool1` per simulazioni batch su griglia;
- `Spacing.Tool2` per simulazioni on-demand tramite coordinate o request JSON;
- propagazione di `run_id`, metadata, coordinate, periodo, suolo, meteo, rotazione e management;
- profili di suolo stratificati forniti dal client;
- cultivar e override dei parametri APSIM;
- fertilizzazione frazionata con tipi supportati dal modello, incluso `UreaN`;
- irrigazione automatica con soglia, target, dose massima e finestra temporale;
- output JSON, manifest, APSIM DataStore SQLite e CSV;
- correzione runtime del Manager irriguo tramite `ISoilWater`.

La soluzione v0.09.05 resta il riferimento finché una successiva modifica C# non viene verificata e versionata. Input, cultivar, scenari e configurazioni devono essere gestiti prioritariamente dal client R/rSpacing quando non richiedono una modifica del sorgente C#.

### Struttura del repository

```text
spacing/
└── apsim/
    ├── apsim source/                 # Sorgente APSIM Next Generation
    │   ├── APSIM.Core/
    │   ├── APSIM.Shared/
    │   ├── DeepCloner.Core/
    │   └── Models/
    ├── src/
    │   ├── Spacing.Core/             # Dominio, adattatori, simulazione e calibrazione
    │   ├── Spacing.Tool1/            # Simulazioni batch
    │   ├── Spacing.Tool2/            # Simulazioni on-demand e interfaccia machine-to-machine
    │   ├── runner/                   # Runner APSIM legacy
    │   ├── optimizer/                # Multi-Start Simplex
    │   └── source/                   # Utility
    ├── examples/                     # Request JSON ed esempi rSpacing
    ├── spacing.config.example.json   # Template di configurazione
    └── spacing.sln                   # Solution Visual Studio
```

### Prerequisiti

- .NET 8 SDK;
- **Visual Studio 2026** per la baseline di sviluppo corrente;
- runtime APSIM Next Generation incluso nella soluzione;
- backend Spacing disponibile, oppure modalità `--mock` per sviluppo e test locali.

### Compilazione

```powershell
cd apsim
dotnet build spacing.sln
```

Configurazione Visual Studio raccomandata:

```text
Debug | Any CPU
```

DLL Tool2 attesa dopo la compilazione:

```text
src/Spacing.Tool2/bin/Debug/net8.0/spacing-tool2.dll
```

### Configurazione

Copiare il template e personalizzare i percorsi locali:

```powershell
Copy-Item spacing.config.example.json spacing.config.json
```

Chiavi principali:

| Chiave | Descrizione |
|---|---|
| `DbApiBaseUrl` | URL base del backend Spacing |
| `DbApiKey` | Chiave API; non deve essere committata |
| `TempWeatherDir` | Directory dei file meteorologici temporanei |
| `OutputDir` | Directory degli output |
| `StartYear`, `EndYear` | Periodo predefinito |
| `MaxParallelism` | Parallelismo, soprattutto per Tool1 |

`spacing.config.json` deve restare escluso da Git. Usare `spacing.config.example.json` come template.

### Tool 1 — Simulazioni batch

```powershell
dotnet run --project src/Spacing.Tool1 -- `
  --config spacing.config.json `
  --nuts2 ITC4 `
  --mock
```

Tool1 esegue scenari multipli e restituisce un risultato batch JSON/CSV con identificativi di run e metadata.

### Tool 2 — Simulazione on-demand

#### Coordinate e rotazione semplice

```powershell
dotnet run --project src/Spacing.Tool2 -- `
  --config spacing.config.json `
  --lat 45.4 `
  --lon 9.2 `
  --rotation Maize `
  --mock
```

#### Request JSON completa

```powershell
dotnet run --project src/Spacing.Tool2 -- `
  --config spacing.config.json `
  --request examples/request.json `
  --result output/result.json `
  --manifest output/manifest.json `
  --mock
```

Il separatore `--` dopo il progetto è necessario quando si usa `dotnet run`.

La request può includere:

- `run_id` e metadata;
- latitudine, longitudine e `cell_id`;
- anni iniziale e finale;
- profilo di suolo completo;
- file meteorologico oppure meteo mock/API;
- colture e cultivar;
- fertilizzazione e irrigazione;
- override dei parametri APSIM;
- percorsi di risultato e manifest.

### Management agronomico

Per ogni coltura sono supportati, secondo il contratto corrente:

- cultivar;
- lavorazione;
- finestra, densità, profondità e interfila di semina;
- azoto minerale e organico;
- fertilizzazione frazionata;
- irrigazione automatica;
- soglia e target irrigui;
- dose massima per evento;
- finestra temporale irrigua.

Le cultivar verificate nei workflow di riferimento sono:

| Coltura | Cultivar |
|---|---|
| Wheat | `Hartog` |
| Maize | `B_110` |
| Soybean | `Generic_MG1` |

### Output

`Spacing.Tool2` può produrre:

- JSON macchina con esito, identificativi, metadata e risultati;
- manifest della run;
- file `.apsimx` generato;
- APSIM DataStore SQLite;
- CSV giornalieri, Report e riepiloghi colturali;
- resa e biomassa per coltura;
- output stratificati del suolo, fra cui acqua, azoto, carbonio, CO2 e N2O quando configurati nel Report APSIM.

I messaggi diagnostici sono inviati su `stderr`; il risultato macchina resta separato e leggibile dal client R.

### Calibrazione e parametri

I parametri e i limiti di calibrazione sono definiti in:

```text
src/Spacing.Tool2/calibration_params.json
```

`Spacing.Tool2` supporta anche `parameter_overrides` forniti dalla request. Gli override devono fare riferimento a percorsi APSIM validi; la loro validazione preventiva è gestita preferibilmente nel client rSpacing.

### Integrazione rSpacing

Il client R di riferimento è **rSpacing 0.10.04**. L’integrazione comprende configurazione, request, run-spec, model run reale in modalità mock, importazione JSON/CSV/SQLite, tabelle e grafici.

Il manuale R Markdown di riferimento è:

```text
rSpacing_v0.10.04_workflow_completo_tutte_funzioni_rev18_rotazione_management_resa_carbonio.Rmd
```

La modifica del C# non è necessaria per variazioni di input, cultivar, rotazione, fertilizzazione o irrigazione già rappresentabili dal contratto corrente.

### Verifica minima

Prima di considerare stabile una modifica C#:

1. ricompilare `spacing.sln`;
2. eseguire Tool2 con `--mock`;
3. verificare `result.json` e `manifest.json`;
4. verificare la creazione del DataStore e dei CSV;
5. eseguire il workflow rSpacing end-to-end;
6. controllare che gli errori tecnici restino distinti da casi agronomici validi, come una resa assente.

### Dipendenze APSIM e licenza

Il sorgente APSIM Next Generation è incluso sotto `apsim source/` ed è soggetto ai termini e alle condizioni APSIM. Il codice Spacing/CarbonFarming Italia resta proprietario, salvo diversa indicazione nei file di licenza del repository.
