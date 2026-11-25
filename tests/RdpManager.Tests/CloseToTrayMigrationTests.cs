using RdpManager.Core;
using Xunit;

namespace RdpManager.Tests;

/// <summary>«Chiudo e non resta nell'area di notifica» (1.19.1): un file salvato prima con CloseToTray=false vinceva sul valore di serie.</summary>
public class CloseToTrayMigrationTests
{
    private static string TempFile(int version, bool closeToTray)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rdpm-ctt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "connections.json");
        File.WriteAllText(path,
            $$"""{"version":{{version}},"settings":{"showTrayIcon":true,"closeToTray":{{(closeToTray ? "true" : "false")}}},"groups":[],"connections":[]}""");
        return path;
    }

    [Fact]
    public void OldFile_WithCloseToTrayOff_IsTurnedOnOnce_ThenUserChoiceSticks()
    {
        var path = TempFile(version: 7, closeToTray: false);
        try
        {
            var store = new ConnectionStore(path);
            var doc = store.Load();
            Assert.True(doc.Settings.CloseToTray);

            store.Save(doc);
            Assert.True(store.Load().Settings.CloseToTray);

            doc.Settings.CloseToTray = false; // scelta dell'utente dalle Impostazioni
            store.Save(doc);
            Assert.False(store.Load().Settings.CloseToTray);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void CurrentFile_WithCloseToTrayOff_IsLeftAlone()
    {
        var path = TempFile(ConnectionsDocument.CurrentVersion, closeToTray: false);
        try
        {
            Assert.False(new ConnectionStore(path).Load().Settings.CloseToTray);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
