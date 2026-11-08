using System.Runtime.CompilerServices;
using RdpManager.Core;

namespace RdpManager.Tests;

/// <summary>I test usano il nucleo anche senza toccare tipi di RdpManager: si collega a Windows (DPAPI, tasti) prima di tutto.</summary>
internal static class TestModuleInit
{
    [ModuleInitializer]
    internal static void Init() => PlatformInit.Register();
}