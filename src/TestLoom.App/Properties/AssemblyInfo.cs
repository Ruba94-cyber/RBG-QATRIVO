using System.Runtime.CompilerServices;

// Le API interne di persistence/maintenance restano fuori dalla superficie pubblica di TestLoom, ma le regressioni
// state-safety devono poter verificare direttamente i piccoli hook testabili (retention, identity repair, ecc.).
[assembly: InternalsVisibleTo("TestLoom.App.Tests")]

// tools/data-fix-6.4.2 (Hxfix642) apre più AppState nello stesso processo su cartelle dati diverse/riscritte:
// deve poter reindirizzare WorkspaceLocalPaths.LocalApplicationDataOverrideForTests verso una radice temporanea
// isolata per esecuzione (stesso meccanismo di TestLocalAppDataFixture), così esecuzioni ripetute dello
// strumento non leggono/scrivono indici locali stantii sotto il vero %LOCALAPPDATA% della macchina che lo
// esegue (gate 6.4.2-indice-stantio). Nessun impatto in produzione: il campo resta null a meno che questo
// harness (o la suite di test) lo imposti esplicitamente.
[assembly: InternalsVisibleTo("Hxfix642")]
