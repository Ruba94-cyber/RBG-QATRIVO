using System.Runtime.CompilerServices;

// Consente alla suite TestLoom.App.Tests di verificare direttamente le routine di housekeeping/recovery
// volutamente internal senza renderle API pubbliche dell'applicazione. Non modifica il comportamento runtime.
[assembly: InternalsVisibleTo("TestLoom.App.Tests")]

// FASE 4 (multi-PC 6.4.2): l'host di test TestLoom.App.MultiPcHost deve poter impostare
// WorkspaceLocalPaths.LocalApplicationDataOverrideForTests (internal) per simulare un %LOCALAPPDATA% proprio
// in un processo OS separato — nessun'altra via esiste per isolare DeviceIdentityService/indici locali fra
// due "PC" senza usare processi realmente distinti. Non modifica il comportamento di produzione.
[assembly: InternalsVisibleTo("TestLoom.App.MultiPcHost")]

// I test headless di layout (Category=LayoutHeadless) stanno in un assembly di test separato: creano un'Application
// Avalonia che resta impostata per tutto il processo, quindi non possono condividere il processo con la suite principale.
[assembly: InternalsVisibleTo("TestLoom.App.Tests.Headless")]
