# Changelog

All notable changes to AriaAPI will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

### Changed
- **Upgraded from .NET 8 to .NET 10** — `TargetFramework` updated to `net10.0`; all `Microsoft.Extensions.*` packages upgraded to v10.0.1
- **License changed from GPL v3 to AGPL v3** — `PackageLicenseExpression` updated to `AGPL-3.0-only`; `LICENSE.txt` replaced with AGPL v3 text

### Added
- **XML documentation on all public API surface** — every public type and member now has `/// <summary>` XML doc comments; build produces zero missing-doc warnings
- **Security hardening** — PHI-safe logging via `PhiMask.Mask()` for all identifiers; token error messages scrubbed of secrets; path traversal guard on SQL file reads; recursion depth limit on fan-out search; HTTP connection pool limits on `SocketsHttpHandler`

---

## [2026-04-06] — FanOutSearchHelper comma normalization (PR #3, Issue #2)

### Added
- **`FanOutParam.PreserveCommas` flag** — optional `bool` (default `false`) on `FanOutSearchHelper.FanOutParam`; when `false`, comma-separated values are split, trimmed, and issued as separate fan-out queries; when `true`, raw comma-joined values pass through unchanged
- **`NormalizeCommaValues` helper** — splits values on commas, trims whitespace, drops empty segments; applied automatically in `SeparateParams` unless `PreserveCommas` is set
- **5 new unit tests** in `FanOutSearchHelperTests` — comma splitting, mixed values, whitespace trimming, empty segment handling, and `PreserveCommas` opt-out

### Fixed
- **HAPI-1953 for non-repeatable parameters** — `FanOutSearchHelper.SeparateParams` now normalizes comma-separated values into individual entries before classifying them, preventing the FHIR server from receiving `service-type=typeA,typeB` which triggers `HAPI-1953: Multiple Values detected for non-repeatable parameter`; all 13+ search classes benefit automatically

### Changed
- **`PatientSearch` identifier fan-out** — explicitly opts in to `PreserveCommas: true` to preserve intentional comma-joined OR groups for AND-of-OR identifier semantics

---

## [1.0.1] — DiagnosisAggregator enhancements (PR #1)

### Added
- **`DiagnosisAggregator.AggregateIcd10CodeList`** — returns structured `List<(string code, string description)>` tuples for ICD-10-CM diagnoses; supports patient filtering and `DistinctBy` on code

### Changed
- **Refined exception handling in `TryGetOnset`** — replaced bare `catch { }` blocks with specific `catch (FormatException)` / `catch (ArgumentException)` handlers; outer catch uses exception filter (`when (ex is not OutOfMemoryException and not StackOverflowException)`) with `Debug.WriteLine` for diagnostics
- **`GetClinicalStatusLabel` optimization** — extracted `statusCoding` variable to avoid redundant `FirstOrDefault()` calls
- **Removed unused `using System.Text;`** directive
- **Version bumped from `1.0.0-beta.1` to `1.0.1`**

---

## [2026-03-15] — NuGet GitHub Packages publishing infrastructure (PR #11)

### Added
- **NuGet package metadata** — `PackageId`, `Authors`, `Company`, `Description`, `PackageTags`, `PackageProjectUrl`, `RepositoryUrl`, `PackageLicenseFile`, `PackageReadmeFile` added to `AriaAPI.csproj`; `dotnet pack` now produces a fully described package
- **`README.NuGet.md`** — consumer-facing package documentation embedded in the `.nupkg`; covers GitHub Packages feed setup, PAT authentication, DI quickstart (`AddAriaFhirClient`), `FhirOptions` configuration, and HIPAA/PHI guidance
- **`nuget.config`** — registers `https://nuget.pkg.github.com/ddicostanzo/index.json` alongside `nuget.org` as a named package source
- **`.github/workflows/publish.yml`** — tag-triggered CI pipeline (`v*`) that builds, runs all 277 tests, packs with the tag-derived version (`-p:Version`), and pushes to GitHub Packages using `GITHUB_TOKEN`

### Changed
- **Removed `win-x64` `RuntimeIdentifier`** from `AriaAPI.csproj` — library is pure managed code; consumers declare their own target platform; removes restore failures on Linux/macOS CI runners
- **`Microsoft.Extensions.Configuration.UserSecrets` marked `PrivateAssets="all"`** — prevents the dev-only secrets provider from appearing in consumer dependency graphs
- **`README.md`** updated — corrected test count (96 → 277), resource count (20+ → 37), added Operations/Write feature bullets, synced project structure tree

---

## [2026-03-15] — Fill coverage gaps: 13 FHIR search classes, HTTP resilience, expanded test suite (PR #9)

### Added
- **13 new `SingleResourceSearch` classes** — `CoverageSearch`, `DiagnosticReportSearch`, `EncounterSearch`, `ImagingStudySearch`, `ImmunizationSearch`, `MedicationAdministrationSearch`, `MedicationRequestSearch`, `NutritionOrderSearch`, `PractitionerRoleSearch`, `RelatedPersonSearch`, `RiskAssessmentSearch`, `ScheduleSearch`, `SlotSearch`; each follows the standard params-bag / convenience-method / `SearchExecutor` pattern
- **`TransientFaultRetryHandler`** — configurable retry delegating handler with exponential back-off (default 3 attempts, 500 ms base delay, doubles per attempt, jitter ±10%); registered in the HTTP pipeline between logging and bearer-token layers
- **6 new `SearchTypes` enums** — `CoverageType`, `DiagnosticReportStatus`, `ImagingStudyStatus`, `MedicationAdministrationStatus`, `MedicationRequestStatus`, `NutritionOrderStatus`; each with `ToToken()` mapping for FHIR query construction
- **13 new FHIR include enums** in `FhirIncludeEnums.cs` — one per new search class; wired into the include registry
- **96 new unit tests** across 15 new test files covering all new search classes, `TransientFaultRetryHandler`, `CurrencySanitizerHandler`, `LoggingTimingHandler`, `DocumentReferenceCreate`, `TaskCreate`, `PatientResolver`, and `PractitionerResolver`

### Fixed
- **HIPAA — PHI in logs**: `TransientFaultRetryHandler` now logs `request.RequestUri?.AbsolutePath` instead of the full URI, preventing patient IDs embedded as query parameters from appearing in plain-text log output
- **Double-dispose bug**: `ClientConfigurator.Dispose()` no longer explicitly disposes `_authHandler`; `DelegatingHandler.Dispose()` already cascades disposal to its `InnerHandler`, making the explicit call a double-dispose
- **Invalid FHIR R4 include**: Removed `EncounterInclude.Practitioner` (`Encounter:practitioner` is not a valid FHIR R4 search parameter); `EncounterInclude.Participant` (`Encounter:participant`) is the correct include path and was already present
- **`UseIterateModifier` silently ignored**: All 12 new search classes (excluding `EncounterSearch`) now wire `UseIterateModifier` through the full call chain — `SearchParams` property → `modifier` local → `builder.Include(…, modifier: modifier)` → all convenience method signatures (`ByPatientAsync`, `ByIdAsync`, `ByActorAsync`, `ByPractitionerAsync`, `ByOrganizationAsync`, `ByScheduleAsync`)

---

## [2026-03-14] — Cap unbounded result sets, patient input validation, and date range guard (PR #8)

### Added
- **`PatientSearch.ValidateIdentifierInput`** (`internal`) — rejects identifiers exceeding 200 characters or containing characters outside the allowed set (`A-Za-z0-9 - _ . | : / # space`); called before query construction in `PatientsAsync`, `PatientAsync(string _id)`, and `BuildSearchAsync`
- **`PatientDocuments.ValidateDateRange`** (`internal`) — guards document searches against future end dates, inverted date ranges, and spans exceeding 730 days (2 years); bypassed when `AllDates = true`
- **20 unit tests** in `AriaAPI.Tests/Validation/InputValidationTests.cs` covering both validators: boundary cases (200-char limit, 730-day span), rejection cases (201 chars, semicolon injection, future end date, inverted range), and FHIR system-qualified identifier format

### Changed
- **Default `ListReturnLimit` capped at 500** across all 25 search parameter classes and their convenience method signatures (`ActivityDefinitionSearch`, `AppointmentSearch`, `BodyStructureSearch`, `CarePlanSearch`, `CareTeamSearch`, `ChargeItemSearch`, `ConditionSearch`, `DeviceSearch`, `DocumentReferenceSearch`, `GroupSearch`, `HealthcareServiceSearch`, `LocationSearch`, `ObservationSearch`, `OrganizationSearch`, `PatientSearch`, `PractitionerSearch`, `ProcedureSearch`, `ServiceRequestSearch`, `TaskSearch`, `ValueSetSearch`, `PatientDocuments`); previously defaulted to `int.MaxValue`, risking resource exhaustion on open queries
- `ByIdAsync` methods preserved with unbounded defaults (single-ID lookups must always return the requested resource)
- `CareTeamSearch.ByParticipantAsync` / `ByPatientAsync` and all `ValueSetExpand` overloads changed from `-1` sentinel (which mapped to `int.MaxValue`) to `SearchExecutor.DefaultServerMaxResults`

---

## [2026-03-14] — Test suite expansion, XML docs, and version bump to 1.0.0-beta.1 (PR #7)

### Added
- **Version `1.0.0-beta.1`** — `<Version>` element added to `AriaAPI.csproj`; `dotnet pack` now produces `AriaAPI.1.0.0-beta.1.nupkg`
- **38 new unit tests** (96 total) across 5 new test files, all targeting pure/infrastructure-free components:
  - `NameFormattingTests` — 15 tests for `ToTitleCaseFirstLastWithSuffixes` (null/whitespace, hyphenated names, apostrophes, roman numerals, credentials, deduplication)
  - `FhirClientFactoryTests` — 10 tests for `FhirClientFactory.GetActiveSystem` and `FhirService.GetActive` using an inline `IOptionsMonitor<T>` stub
  - `CodeableConceptTests` — 6 tests for `CodeableConcept` constructor defaults, `ToString` format, and `ToFhirCodeableConcept` mapping
  - `HelpersTests` — 5 tests for `AriaFhirClientHelpers.EscapeCsv` edge cases (null, empty, comma, embedded quote, clean value)
  - `PhiMaskTests` — 4 tests for `PhiMask.Mask` (8-char hex format, null safety, determinism, collision avoidance)
- **XML documentation** (`<summary>`, `<param>`, `<returns>`, `<exception>`) added to all previously undocumented public members in `FhirService`, `CodeableConcept`, `Helpers`, `IPractitionerResolver`, `IFhirFactory`, and `AriaAPIClient`

### Fixed
- **`FhirService.GetActive()` empty-scope guard** — replaced unreachable `?? throw` (dead code; `AuthOptions.Scope` is a non-nullable `string`) with `string.IsNullOrEmpty` to correctly enforce the documented contract that an empty scope throws `InvalidOperationException`

---

## [2026-03-14] — Backlog cleanup: PhiMask, FanOut CancellationToken, null guards (PR #6)

### Added
- **`PhiMask`** — shared `internal static class` in `AriaAPI.API.IdentityResolvers` centralising SHA-256 PHI log-masking; removes the duplicated `MaskPhi` method that existed independently in `PatientResolver` and `PractitionerResolver`

### Changed
- **`FanOutSearchHelper.FanOutSearchAsync`** — both overloads now accept `CancellationToken ct = default` and forward it to `SemaphoreSlim.WaitAsync(ct)`, so cancelled requests no longer hold concurrency slots open
- All four `FanOutSearchAsync` call sites (`SearchExecutor`, `AppointmentSearch`, `AppointmentSearch.Internal`, `PatientAppointments`) updated to forward `ct`

### Fixed
- **`serviceCategories` null guard** — three `AppointmentSearch.Convenience` methods now use `serviceCategories?.ToList() ?? []` instead of calling `.ToList()` directly, preventing `NullReferenceException` when caller passes `null`

---

## [2026-03-14] — AriaAPI Improvements (PR #5)

### Added
- **`AriaAPI.Tests` project** — xUnit test suite covering `Builder<T>`, `FanOutSearchHelper`, `BuilderExtensions`, `FhirActionExtensions`, and `SearchTypes` (100+ test cases)
- **`SearchExecutor`** — internal static helper centralising the null-guard, `ForResource<T>(ct)` creation, and defensive result-trim so individual search classes stay thin
- **`ServiceCollectionExtensions`** — `IServiceCollection.AddAriaFhirClient(...)` DI extension method; callers can now register `ClientConfigurator` as a singleton via the standard DI container
- **`AppointmentSearch` partial-class decomposition** — split the monolithic 700-line file into four focused partials: `AppointmentSearch.cs` (core), `AppointmentSearch.Internal.cs`, `AppointmentSearch.Sliced.cs`, `AppointmentSearch.Convenience.cs`
- **End-to-end `CancellationToken` propagation** — `ClientConfiguratorExtensions.ForResource<T>(ct)` now accepts and forwards a `CancellationToken`; all public search methods and multi-resource helpers expose `ct = default` as a final parameter and propagate it through to every HTTP call

### Changed
- All single- and multi-resource search classes refactored to use `SearchExecutor` (eliminates repeated guard/trim boilerplate)
- `ConfigureAwait(false)` applied consistently across all async paths in the library
- `WithCount` guarded against `int.MaxValue` in all builders to avoid sending `_count=2147483647` to FHIR servers

### Fixed
- **PHI redacted from logs** — `PatientResolver` and `PractitionerResolver` now log SHA-256–masked identifiers instead of raw MRN / name values (HIPAA)
- **Patient reference removed from `TaskCreate` log** — `"Task created with id: {Id}."` no longer includes the patient reference string
- **`PatientTasks` Builder type bug** — `else` branch was constructing `Builder<DocumentReference>` instead of `Builder<Hl7.Fhir.Model.Task>`
- **Path traversal in `ReadSQLQuery.ReturnQuery`** — caller-supplied filename is now sanitised with `Path.GetFileName` before `Path.Combine`

---
