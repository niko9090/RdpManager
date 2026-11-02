# k-5: prova della suite con la VPN WireGuard

Ramo: worktree-agent-af917d15f499793c3, dopo `git merge main` (dentro ci sono VPN WireGuard e gruppi/cartelle).
Comando: `dotnet test RdpManager.sln -c Release`

| Esecuzione | Superati | Non superati | Ignorati | Totale |
|---|---|---|---|---|
| 1 (con ricompilazione, 2 m 7 s) | 621 | 0 | 5 | 626 |
| 2 (`--no-build`) | 621 | 0 | 5 | 626 |

Nessun test rosso in nessuna delle due esecuzioni: l'errore visto prima non si è ripresentato
(probabile corsa con l'altra unione o test instabile; non riprodotto, quindi nessuna modifica al codice).
Ignorati: LiveRdpServerTests.FakeUser_IsRefusedWithoutAnyPromptAndReasonShownInTab e 4 ScreenshotTests (ignorati per progetto).

Test WireGuard eseguiti (filtro WireGuard|VpnProfile): 28 superati, 0 non superati.
Classi: WireGuardKeysTests (vettori RFC 7748, Diffie-Hellman, Generate, IsValidKey), WireGuardConfigTests,
VpnProfileTests, WireGuardTunnelTests.
