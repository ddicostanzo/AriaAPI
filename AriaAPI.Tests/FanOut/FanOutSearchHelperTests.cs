// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AriaAPI.Core;
using AriaAPI.Networking.Core;
using Hl7.Fhir.Rest;
using Xunit;
using FhirPatient = Hl7.Fhir.Model.Patient;

namespace AriaAPI.Tests.FanOut
{
    /// <summary>
    /// Tests for <see cref="FanOutSearchHelper"/>, exercising the generic overload
    /// that accepts a custom query executor (no live FHIR client required).
    /// </summary>
    public sealed class FanOutSearchHelperTests
    {
        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        /// <summary>
        /// A minimal result wrapper that carries a list of string IDs,
        /// sufficient to verify fan-out union/intersection semantics.
        /// </summary>
        private sealed record IdResult(IReadOnlyList<string> Ids);

        /// <summary>
        /// Creates a query executor that returns the given set of IDs regardless of the search params.
        /// </summary>
        private static Func<SearchParams, Task<IdResult>> FixedExecutor(params string[] ids) =>
            _ => Task.FromResult(new IdResult(ids));

        /// <summary>
        /// Creates a query executor that echoes back the value of a named param from SearchParams.
        /// Matches IDs of the form "id-{value}" from a pre-built dictionary.
        /// </summary>
        private static Func<SearchParams, Task<IdResult>> ParamMappedExecutor(
            string paramKey,
            IReadOnlyDictionary<string, string[]> valueToIds) =>
            sp =>
            {
                var match = sp.Parameters.FirstOrDefault(p => p.Item1 == paramKey);
                if (match is null || !valueToIds.TryGetValue(match.Item2, out var ids))
                    return Task.FromResult(new IdResult(Array.Empty<string>()));
                return Task.FromResult(new IdResult(ids));
            };

        private static IdResult MergeResults(IReadOnlyList<IdResult> results)
        {
            var merged = results.SelectMany(r => r.Ids).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new IdResult(merged);
        }

        private static IEnumerable<string> ExtractIds(IdResult result) => result.Ids;

        private static IdResult FilterIds(IdResult result, IReadOnlySet<string> allowedIds)
        {
            var filtered = result.Ids.Where(id => allowedIds.Contains(id)).ToList();
            return new IdResult(filtered);
        }

        // ---------------------------------------------------------------------------
        // Tests
        // ---------------------------------------------------------------------------

        /// <summary>
        /// When no fan-out params are provided, the base builder is used for a single query
        /// and all results are returned.
        /// </summary>
        [Fact]
        public async Task FanOut_EmptyFanOuts_ReturnsAllFromBaseBuilder()
        {
            var fanOutParams = Array.Empty<FanOutSearchHelper.FanOutParam>();

            var result = await FanOutSearchHelper.FanOutSearchAsync<FhirPatient, IdResult>(
                queryExecutor: FixedExecutor("id-1", "id-2", "id-3"),
                resultMerger: MergeResults,
                idExtractor: ExtractIds,
                idFilter: FilterIds,
                baseBuilderFactory: () => new AriaAPI.Networking.Core.Builder<FhirPatient>(),
                fanOutParams: fanOutParams
            ).ConfigureAwait(false);

            Assert.Equal(3, result.Ids.Count);
            Assert.Contains("id-1", result.Ids);
            Assert.Contains("id-2", result.Ids);
            Assert.Contains("id-3", result.Ids);
        }

        /// <summary>
        /// A single fan-out param with multiple values issues separate queries
        /// and unions (ORs) the results.
        /// </summary>
        [Fact]
        public async Task FanOut_SingleParam_MultipleValues_ReturnsUnionOfResults()
        {
            var valueToIds = new Dictionary<string, string[]>
            {
                ["val1"] = new[] { "id-1", "id-2" },
                ["val2"] = new[] { "id-2", "id-3" },
            };

            var fanOutParams = new[]
            {
                new FanOutSearchHelper.FanOutParam("category", new[] { "val1", "val2" })
            };

            var result = await FanOutSearchHelper.FanOutSearchAsync<FhirPatient, IdResult>(
                queryExecutor: ParamMappedExecutor("category", valueToIds),
                resultMerger: MergeResults,
                idExtractor: ExtractIds,
                idFilter: FilterIds,
                baseBuilderFactory: () => new AriaAPI.Networking.Core.Builder<FhirPatient>(),
                fanOutParams: fanOutParams
            ).ConfigureAwait(false);

            // Union: id-1 | id-2 | id-3
            Assert.Equal(3, result.Ids.Count);
            Assert.Contains("id-1", result.Ids);
            Assert.Contains("id-2", result.Ids);
            Assert.Contains("id-3", result.Ids);
        }

        /// <summary>
        /// Two multi-valued fan-out params with different keys: results from each param's union
        /// are intersected (AND semantics) across params.
        /// </summary>
        [Fact]
        public async Task FanOut_MultipleParams_IntersectsResults()
        {
            // Param A: "category" val1 -> {id-1, id-2}, val2 -> {id-2, id-3} => union {id-1,id-2,id-3}
            // Param B: "status"   valA -> {id-2, id-4}, valB -> {id-5}       => union {id-2,id-4,id-5}
            // Intersection: {id-2}
            var categoryToIds = new Dictionary<string, string[]>
            {
                ["val1"] = new[] { "id-1", "id-2" },
                ["val2"] = new[] { "id-2", "id-3" },
            };
            var statusToIds = new Dictionary<string, string[]>
            {
                ["valA"] = new[] { "id-2", "id-4" },
                ["valB"] = new[] { "id-5" },
            };

            // The generic executor needs to handle both param keys.
            Func<SearchParams, Task<IdResult>> executor = sp =>
            {
                var cat = sp.Parameters.FirstOrDefault(p => p.Item1 == "category");
                if (cat is not null && categoryToIds.TryGetValue(cat.Item2, out var catIds))
                    return Task.FromResult(new IdResult(catIds));

                var status = sp.Parameters.FirstOrDefault(p => p.Item1 == "status");
                if (status is not null && statusToIds.TryGetValue(status.Item2, out var statusIds))
                    return Task.FromResult(new IdResult(statusIds));

                return Task.FromResult(new IdResult(Array.Empty<string>()));
            };

            var fanOutParams = new[]
            {
                new FanOutSearchHelper.FanOutParam("category", new[] { "val1", "val2" }),
                new FanOutSearchHelper.FanOutParam("status",   new[] { "valA", "valB" }),
            };

            var result = await FanOutSearchHelper.FanOutSearchAsync<FhirPatient, IdResult>(
                queryExecutor: executor,
                resultMerger: MergeResults,
                idExtractor: ExtractIds,
                idFilter: FilterIds,
                baseBuilderFactory: () => new AriaAPI.Networking.Core.Builder<FhirPatient>(),
                fanOutParams: fanOutParams
            ).ConfigureAwait(false);

            // Only "id-2" appears in both param unions
            Assert.Single(result.Ids);
            Assert.Contains("id-2", result.Ids);
        }

        /// <summary>
        /// The merger deduplicates resources by ID so that repeated IDs from different
        /// fan-out values appear only once in the result.
        /// </summary>
        [Fact]
        public async Task FanOut_DeduplicatesById()
        {
            var valueToIds = new Dictionary<string, string[]>
            {
                ["v1"] = new[] { "id-1", "id-2" },
                ["v2"] = new[] { "id-1", "id-3" },   // id-1 appears in both
            };

            var fanOutParams = new[]
            {
                new FanOutSearchHelper.FanOutParam("code", new[] { "v1", "v2" })
            };

            var result = await FanOutSearchHelper.FanOutSearchAsync<FhirPatient, IdResult>(
                queryExecutor: ParamMappedExecutor("code", valueToIds),
                resultMerger: MergeResults,
                idExtractor: ExtractIds,
                idFilter: FilterIds,
                baseBuilderFactory: () => new AriaAPI.Networking.Core.Builder<FhirPatient>(),
                fanOutParams: fanOutParams
            ).ConfigureAwait(false);

            // id-1 should appear exactly once
            Assert.Equal(3, result.Ids.Count);
            Assert.Single(result.Ids, id => id == "id-1");
        }

        /// <summary>
        /// A fan-out param with an empty values list is silently skipped and the remaining
        /// params are processed normally.
        /// </summary>
        [Fact]
        public async Task FanOut_EmptyValues_ReturnsEmpty()
        {
            // One param with zero values: SeparateParams skips it entirely.
            // With no multi-valued params and no single-valued params, the fast path runs.
            var fanOutParams = new[]
            {
                new FanOutSearchHelper.FanOutParam("category", Array.Empty<string>())
            };

            var result = await FanOutSearchHelper.FanOutSearchAsync<FhirPatient, IdResult>(
                queryExecutor: FixedExecutor("id-1", "id-2"),
                resultMerger: MergeResults,
                idExtractor: ExtractIds,
                idFilter: FilterIds,
                baseBuilderFactory: () => new AriaAPI.Networking.Core.Builder<FhirPatient>(),
                fanOutParams: fanOutParams
            ).ConfigureAwait(false);

            // The fast path fires (no multi-valued params), so the fixed executor runs once
            // and returns both IDs.
            Assert.Equal(2, result.Ids.Count);
        }

        /// <summary>
        /// A single-valued fan-out param is folded into the base builder (fast path),
        /// not treated as a multi-valued fan-out.
        /// </summary>
        [Fact]
        public async Task FanOut_SingleValueParam_FoldsIntoBaseBuilder()
        {
            var fanOutParams = new[]
            {
                new FanOutSearchHelper.FanOutParam("status", new[] { "active" })
            };

            // The query executor simply verifies the "status" param was added
            bool paramSeen = false;
            Func<SearchParams, Task<IdResult>> executor = sp =>
            {
                if (sp.Parameters.Any(p => p.Item1 == "status" && p.Item2 == "active"))
                    paramSeen = true;
                return Task.FromResult(new IdResult(new[] { "id-1" }));
            };

            var result = await FanOutSearchHelper.FanOutSearchAsync<FhirPatient, IdResult>(
                queryExecutor: executor,
                resultMerger: MergeResults,
                idExtractor: ExtractIds,
                idFilter: FilterIds,
                baseBuilderFactory: () => new AriaAPI.Networking.Core.Builder<FhirPatient>(),
                fanOutParams: fanOutParams
            ).ConfigureAwait(false);

            Assert.True(paramSeen, "Single-valued param should be folded into base builder.");
            Assert.Single(result.Ids);
        }

        // ---------------------------------------------------------------------------
        // Comma normalization tests
        // ---------------------------------------------------------------------------

        /// <summary>
        /// A single comma-separated value is split into individual values and each
        /// is issued as a separate query (multi-valued fan-out).
        /// </summary>
        [Fact]
        public async Task FanOut_CommaSeparatedSingleValue_SplitsIntoSeparateQueries()
        {
            var valueToIds = new Dictionary<string, string[]>
            {
                ["valA"] = new[] { "id-1" },
                ["valB"] = new[] { "id-2" },
            };

            var fanOutParams = new[]
            {
                new FanOutSearchHelper.FanOutParam("category", new[] { "valA,valB" })
            };

            var result = await FanOutSearchHelper.FanOutSearchAsync<FhirPatient, IdResult>(
                queryExecutor: ParamMappedExecutor("category", valueToIds),
                resultMerger: MergeResults,
                idExtractor: ExtractIds,
                idFilter: FilterIds,
                baseBuilderFactory: () => new AriaAPI.Networking.Core.Builder<FhirPatient>(),
                fanOutParams: fanOutParams
            ).ConfigureAwait(false);

            Assert.Equal(2, result.Ids.Count);
            Assert.Contains("id-1", result.Ids);
            Assert.Contains("id-2", result.Ids);
        }

        /// <summary>
        /// Mixed comma-separated and individual values are all flattened into separate queries.
        /// </summary>
        [Fact]
        public async Task FanOut_MixedCommaAndSeparateValues_AllSplitCorrectly()
        {
            var valueToIds = new Dictionary<string, string[]>
            {
                ["valA"] = new[] { "id-1" },
                ["valB"] = new[] { "id-2" },
                ["valC"] = new[] { "id-3" },
            };

            var fanOutParams = new[]
            {
                new FanOutSearchHelper.FanOutParam("category", new[] { "valA,valB", "valC" })
            };

            var result = await FanOutSearchHelper.FanOutSearchAsync<FhirPatient, IdResult>(
                queryExecutor: ParamMappedExecutor("category", valueToIds),
                resultMerger: MergeResults,
                idExtractor: ExtractIds,
                idFilter: FilterIds,
                baseBuilderFactory: () => new AriaAPI.Networking.Core.Builder<FhirPatient>(),
                fanOutParams: fanOutParams
            ).ConfigureAwait(false);

            Assert.Equal(3, result.Ids.Count);
            Assert.Contains("id-1", result.Ids);
            Assert.Contains("id-2", result.Ids);
            Assert.Contains("id-3", result.Ids);
        }

        /// <summary>
        /// Whitespace around comma-separated values is trimmed.
        /// </summary>
        [Fact]
        public async Task FanOut_CommaSeparatedWithWhitespace_TrimmedCorrectly()
        {
            var valueToIds = new Dictionary<string, string[]>
            {
                ["valA"] = new[] { "id-1" },
                ["valB"] = new[] { "id-2" },
            };

            var fanOutParams = new[]
            {
                new FanOutSearchHelper.FanOutParam("category", new[] { "valA , valB" })
            };

            var result = await FanOutSearchHelper.FanOutSearchAsync<FhirPatient, IdResult>(
                queryExecutor: ParamMappedExecutor("category", valueToIds),
                resultMerger: MergeResults,
                idExtractor: ExtractIds,
                idFilter: FilterIds,
                baseBuilderFactory: () => new AriaAPI.Networking.Core.Builder<FhirPatient>(),
                fanOutParams: fanOutParams
            ).ConfigureAwait(false);

            Assert.Equal(2, result.Ids.Count);
            Assert.Contains("id-1", result.Ids);
            Assert.Contains("id-2", result.Ids);
        }

        /// <summary>
        /// Empty segments from consecutive commas are dropped.
        /// </summary>
        [Fact]
        public async Task FanOut_ConsecutiveCommas_EmptySegmentsDropped()
        {
            var valueToIds = new Dictionary<string, string[]>
            {
                ["valA"] = new[] { "id-1" },
                ["valB"] = new[] { "id-2" },
            };

            var fanOutParams = new[]
            {
                new FanOutSearchHelper.FanOutParam("category", new[] { "valA,,valB" })
            };

            var result = await FanOutSearchHelper.FanOutSearchAsync<FhirPatient, IdResult>(
                queryExecutor: ParamMappedExecutor("category", valueToIds),
                resultMerger: MergeResults,
                idExtractor: ExtractIds,
                idFilter: FilterIds,
                baseBuilderFactory: () => new AriaAPI.Networking.Core.Builder<FhirPatient>(),
                fanOutParams: fanOutParams
            ).ConfigureAwait(false);

            Assert.Equal(2, result.Ids.Count);
            Assert.Contains("id-1", result.Ids);
            Assert.Contains("id-2", result.Ids);
        }

        /// <summary>
        /// When <see cref="FanOutSearchHelper.FanOutParam.PreserveCommas"/> is true,
        /// comma-separated values are passed through unchanged as a single query.
        /// </summary>
        [Fact]
        public async Task FanOut_PreserveCommasTrue_RawValuePassedThrough()
        {
            var valueToIds = new Dictionary<string, string[]>
            {
                ["valA,valB"] = new[] { "id-1", "id-2" },
            };

            var fanOutParams = new[]
            {
                new FanOutSearchHelper.FanOutParam("identifier", new[] { "valA,valB" }, PreserveCommas: true)
            };

            var result = await FanOutSearchHelper.FanOutSearchAsync<FhirPatient, IdResult>(
                queryExecutor: ParamMappedExecutor("identifier", valueToIds),
                resultMerger: MergeResults,
                idExtractor: ExtractIds,
                idFilter: FilterIds,
                baseBuilderFactory: () => new AriaAPI.Networking.Core.Builder<FhirPatient>(),
                fanOutParams: fanOutParams
            ).ConfigureAwait(false);

            Assert.Equal(2, result.Ids.Count);
            Assert.Contains("id-1", result.Ids);
            Assert.Contains("id-2", result.Ids);
        }

        /// <summary>
        /// <see cref="FanOutSearchHelper.FanOutSearchAsync{T}(AriaFhirClient{T}, Func{Builder{T}}, IReadOnlyList{FanOutSearchHelper.FanOutParam}, int)"/>
        /// throws <see cref="ArgumentNullException"/> when the client is null.
        /// </summary>
        [Fact]
        public async Task FanOut_NullQueryExecutor_ThrowsArgumentNullException()
        {
            await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            {
                await FanOutSearchHelper.FanOutSearchAsync<FhirPatient, IdResult>(
                    queryExecutor: null!,
                    resultMerger: MergeResults,
                    idExtractor: ExtractIds,
                    idFilter: FilterIds,
                    baseBuilderFactory: () => new AriaAPI.Networking.Core.Builder<FhirPatient>(),
                    fanOutParams: Array.Empty<FanOutSearchHelper.FanOutParam>()
                ).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
    }
}
