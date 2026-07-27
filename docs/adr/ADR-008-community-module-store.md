# ADR-008: Community Module Store Architecture

**Status:** Proposed
**Date:** 2026-07-11
**Related:** module registry (`IVoipClientModule`, `ModuleRegistry`), licensing (`CalloraVoipSdk.Licensing.*`), store backend (`store-backend/`)

---

## Context

CalloraVoipSdk ships a first-party module store: a static storefront (`website/store/`)
plus an ASP.NET Core backend (`store-backend/`) selling five first-party modules with
Stripe checkout and signed, offline-verifiable license tokens.

The strategic goal is to open this into a **community store** — a curated marketplace where
third-party vendors publish and sell their own modules, comparable to the Shopware Store for
plugins. This ADR records the target architecture, the conditions a module must satisfy, and
the two decisions that shape the model, so implementation can proceed incrementally without
re-deciding the foundation.

### What already supports this (implemented, Accepted)

The licensing foundation was deliberately built marketplace-ready:

- **Open extension point** — any developer implements `IVoipClientModule`. The registry
  (`ModuleRegistry.Register`) is neutral: it never checks a license. External modules that
  implement `IVoipClientModule` directly require **no license** and always load. Only
  commercial modules opt into gating by deriving `LicensedVoipClientModule`, which validates
  in `OnAttached`.
- **Namespaced module IDs** — license tokens carry `mods: ["publisher.module"]`
  (e.g. `callora.realtime`). Third-party publishers get their own namespace (`acme.dialer`)
  with no format change. `SignedLicenseTokenService.Namespaced` keeps already-namespaced IDs
  intact.
- **Central store signing** (Apple / Shopware model) — the store signs *all* license tokens
  with one ECDSA-P256 private key; every module validates against the one public key embedded
  in the SDK (`LicenseValidator.EmbeddedPublicKeyBase64`). The store can therefore issue
  licenses for third-party modules with no per-publisher key distribution.
- **Fulfillment + delivery** — order, signed download tokens, artifact streaming.

---

## Decision

Extend the store into a **curated, centrally-signed marketplace**. Concretely:

### Backend extensions

| Area | Today | Community store |
|------|-------|-----------------|
| Publisher domain | — | Vendor accounts, **namespace reservation** (`acme.*`), identity / VAT / payout verification |
| Catalog | 5 hardcoded modules (`InMemoryCatalogProvider`) | Persistent DB catalog, submission workflow, versioning, status (draft → in-review → published → deprecated) |
| Review pipeline | — | Malware scan, manifest validation, **SDK-API / TFM compatibility check**, forbidden-API scan, optional manual review |
| Artifact signing | placeholder `.nupkg` | Publisher uploads → store **counter-signs** (NuGet signature / Authenticode) → integrity-checked delivery |
| Payments | simple Stripe checkout | **Stripe Connect** marketplace: buyer → platform, platform retains commission, remainder to publisher connected account |
| License | one Callora keypair | Token carries `publisher` + `version` constraints; **revocation** for refunds / subscription end (short expiries + renewal, offline-friendly) |
| SDK loading | manual `Modules.Register` | Standardized discovery/loading (load modules from a directory, discover `IVoipClientModule` via DI/reflection) — "install" like a Shopware plugin |

### Conditions for a module

**Technical (mandatory):**

1. Implements `IVoipClientModule` with a unique, **namespaced `ModuleId`** (`publisher.module`)
   under a reserved publisher namespace.
2. Paid → derives `LicensedVoipClientModule` (self-gates against the store public key).
   Free / OSS → implements `IVoipClientModule` directly, no license.
3. Targets compatible TFMs (net8/9/10) and **declares min/max SDK version** in its manifest.
4. Uses **only public SDK contracts** (module registry, media-tap
   `CreateReceiver`/`CreateSender`) — never `Infrastructure/*` internals.
5. Ships a **manifest**: id, publisher, version, SDK compatibility, dependencies,
   description, price/license type, requested permissions.
6. Strong-named + store-counter-signed artifact; thread-safe, clean dispose/lifecycle
   (runs inside a consumer's process — the project engineering rules are the minimum bar).

**Policy (store):** verified publisher, accepted revenue-share terms, passes review, no misuse
of the media tap (privacy — aligns with the "your data stays yours" positioning), and
compatibility maintenance across SDK releases.

### Two shaping decisions

1. **Curated vs. open** — Shopware is curated (review before publish). Start **curated**
   (trust/security fit a commercial SDK); a more open tier can follow.
2. **Trust boundary** — .NET modules run **in-process with full access**; there is no real
   in-process sandbox. Realistic mitigation is strict review + signing; `AssemblyLoadContext`
   isolates only partially; true isolation means out-of-process modules (expensive). This must
   be decided explicitly before admitting third-party code.

---

## Consequences

- The license format and module contract need **no breaking change** to admit third parties —
  namespacing and central signing already carry it.
- The backend must move from in-memory/hardcoded to a persistent, multi-tenant service
  (publishers, catalog, submissions, payouts) — the largest build item.
- Curation adds an operational burden (review pipeline + staff) but is the defensible choice
  for security and brand.
- The trust-boundary decision is a genuine risk gate: admitting unreviewed third-party
  in-process code without a mitigation strategy would undermine the security posture the SDK
  markets.

### Suggested build order

1. **Persistent catalog + publisher/manifest model** (foundation the rest builds on).
2. Submission + review pipeline (compatibility + security gates).
3. Stripe Connect payouts + revenue share.
4. Artifact counter-signing + SDK-side discovery/loading.
5. License revocation / renewal for the subscription lifecycle.

Nothing here is built yet. This ADR is the agreed concept; each build item is a separate,
scoped piece of work.
