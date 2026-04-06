# Design: FanOutSearchHelper Comma-Separated Value Normalization

> GitHub Issue: #2
> Created: 2026-04-06
> Status: Approved

## Problem

The Aria FHIR endpoint (HAPI-based) treats certain search parameters like `service-type` as non-repeatable, rejecting both AND (repeated query params) and OR (comma-separated) syntax with error HAPI-1953.

`FanOutSearchHelper.SeparateParams` classifies fan-out parameters by `Values.Count`:
- Count == 1 -> single-valued -> folded into the base builder as-is
- Count >= 2 -> multi-valued -> separate query per value

When a caller passes a comma-separated string as a single list entry (e.g., `ServiceTypes = ["typeA,typeB"]`), the helper sees Count == 1, folds the raw value into the builder, and the server receives `service-type=typeA,typeB`. HAPI rejects this for non-repeatable parameters.

## Chosen Approach

**Approach A: Normalize in `SeparateParams` with opt-out flag.**

All fan-out values are split on commas by default. A `PreserveCommas` flag on `FanOutParam` allows callers to opt out when comma-separated OR syntax is intentional (e.g., `PatientSearch` identifier AND-of-OR groups).

### Why this approach

- Single code path to modify — all 13+ search classes benefit automatically
- Backward-compatible: existing two-arg `FanOutParam` constructors get `PreserveCommas = false`
- Only one known opt-out needed (`PatientSearch`)
- Rejected alternatives:
  - **Per-search-class normalization** (Approach B): scattered across 13+ files, easy to miss, no safety net for new search classes
  - **Builder-level splitting** (Approach C): wrong layer, breaks FHIR token values with commas in system URIs

## Design

### Data Model Change

Add `PreserveCommas` to `FanOutParam`:

```csharp
public readonly record struct FanOutParam(
    string Key,
    IReadOnlyList<string> Values,
    bool PreserveCommas = false);
```

Default `false` means commas are always split. The optional third parameter makes this backward-compatible with all existing call sites.

### Normalization Logic

Modify `FanOutSearchHelper.SeparateParams` to normalize values before classifying:

```
For each FanOutParam where PreserveCommas is false:
  1. Split each value string on ','
  2. Trim whitespace from each resulting segment
  3. Drop empty/whitespace-only segments
  4. Flatten into a single list of normalized values
Then classify by count (1 -> single-valued, 2+ -> multi-valued)
```

Examples:
- `FanOutParam("service-type", ["typeA,typeB"])` -> normalized to `["typeA", "typeB"]` -> multi-valued -> 2 separate queries
- `FanOutParam("service-type", ["typeA,typeB", "typeC"])` -> normalized to `["typeA", "typeB", "typeC"]` -> 3 separate queries
- `FanOutParam("service-type", ["typeA"])` -> normalized to `["typeA"]` -> single-valued -> folded into base builder
- `FanOutParam("identifier", ["sys|A,sys|B"], PreserveCommas: true)` -> raw `["sys|A,sys|B"]` preserved -> single-valued

### PatientSearch Opt-Out

`PatientSearch` intentionally comma-joins identifier OR groups for AND-of-OR semantics. Its existing call site:

```csharp
fanOuts.Add(new FanOutSearchHelper.FanOutParam("identifier", identifierValues));
```

Changes to:

```csharp
fanOuts.Add(new FanOutSearchHelper.FanOutParam("identifier", identifierValues, PreserveCommas: true));
```

No other search classes require changes.

### Files Modified

1. `AriaAPI/Core/FanOutSearchHelper.cs` — `FanOutParam` record + `SeparateParams` method
2. `AriaAPI/API/SingleResourceSearch/PatientSearch.cs` — add `PreserveCommas: true`
3. `AriaAPI.Tests/FanOut/FanOutSearchHelperTests.cs` — new test cases

### Test Plan

1. **Comma splitting**: A `FanOutParam` with `["valA,valB"]` issues two separate queries, each with one value. Verify via `ParamMappedExecutor` that each query's `SearchParams.Parameters` contains exactly one entry for the key.
2. **Mixed values**: `["valA,valB", "valC"]` produces three separate queries.
3. **PreserveCommas opt-out**: `FanOutParam("key", ["valA,valB"], PreserveCommas: true)` sends the raw comma-separated value as a single query parameter.
4. **Whitespace trimming**: `["valA , valB"]` normalizes to `["valA", "valB"]`.
5. **Empty segment handling**: `["valA,,valB"]` normalizes to `["valA", "valB"]` (empty segment dropped).
6. **Backward compatibility**: All existing `FanOutSearchHelperTests` pass unchanged.
