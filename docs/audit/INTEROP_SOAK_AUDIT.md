# Interop- & Soak-Audit — Fehlerregister

> Lebendes Register. **Nur Dokumentation — kein autonomes Fixen.** Jeder Fix ist ein separates,
> eigens freigegebenes Paket. Design: `docs/audit/2026-07-21-interop-soak-audit-design.md`.

**Finding-Typen:** `Interop-Abweichung` · `Soak-Leak` · `Media-Defekt` · `Wire-Robustheit` · `Facade-Coupling-Gap`

| FID | Typ | Evidenz (Test/Peer) | Symptom | Fehlerquelle | Fundstelle (Datei:Zeile) | Fix-Vorschlag | Schweregrad | Status |
|-----|-----|---------------------|---------|--------------|--------------------------|---------------|-------------|--------|
| F001 | Facade-Coupling-Gap | Phase 0.1 (InteropHarness-Setup) | L0–L3-Komponenten (`RtpCallMediaSession` u. a.) sind `internal`; Test unter der Facade erfordert `InternalsVisibleTo`-Eintrag | Bewusste Kapselung: Sub-Facade-Typen nicht öffentlich | `src/Core/Infrastructure/Rtp/RtpCallMediaSession.cs:22`, `src/Core/Properties/AssemblyInfo.cs:3` | Kein Fix — dokumentiert. Bewertung, ob ein schmales öffentliches Test-/Diagnose-Seam sinnvoll ist, in späterem Paket | Info | dokumentiert |
