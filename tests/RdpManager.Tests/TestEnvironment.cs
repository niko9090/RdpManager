using System.Runtime.CompilerServices;
using RdpManager.UI;

namespace RdpManager.Tests;

/// <summary>Impostazioni valide per tutta la suite: i test non dipendono dallo schermo touch della macchina.</summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Init() => TouchUi.TouchScreenForTests = () => false;
}
