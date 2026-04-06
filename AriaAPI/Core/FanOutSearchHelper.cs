// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
using AriaAPI.Networking.Core;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AriaAPI.Core
{
    /// <summary>
    /// Fans out multi-valued FHIR search parameters into individual queries, then
    /// unions results within each parameter (OR) and intersects across parameters (AND).
    /// Deduplicates by <see cref="Resource.Id"/> (case-insensitive).
    /// </summary>
    public static class FanOutSearchHelper
    {
        /// <summary>
        /// Describes a single search parameter key and its possible values.
        /// When Values contains more than one entry, each value is issued as a separate query.
        /// By default, values containing commas are split into individual entries before
        /// classification. Set <see cref="PreserveCommas"/> to <see langword="true"/> to
        /// pass comma-separated values through unchanged (e.g., for FHIR OR-group semantics).
        /// </summary>
        /// <param name="Key">The FHIR search parameter key.</param>
        /// <param name="Values">One or more values for the parameter.</param>
        /// <param name="PreserveCommas">
        /// When <see langword="false"/> (default), values containing commas are split into
        /// individual entries and each is issued as a separate query. When <see langword="true"/>,
        /// comma-separated values are passed through unchanged — use this when the FHIR server
        /// supports OR-group semantics and the caller intentionally comma-joins values.
        /// </param>
        public readonly record struct FanOutParam(string Key, IReadOnlyList<string> Values, bool PreserveCommas = false);

        /// <summary>
        /// Fans out multi-valued parameters into individual FHIR queries using
        /// <see cref="AriaFhirClientHelpers.AggregateResourcesAsync{T}"/>, then unions results
        /// within each parameter (OR semantics) and intersects across parameters (AND semantics).
        /// Single-valued fan-out params are folded into the base builder for a single query (fast path).
        /// </summary>
        /// <typeparam name="T">The FHIR resource type being searched.</typeparam>
        /// <param name="client">The <see cref="AriaFhirClient{TResource}"/> used to execute search queries.</param>
        /// <param name="baseBuilderFactory">
        /// Factory that returns a fresh <see cref="Builder{TResource}"/> pre-populated with scalar (non-fan-out)
        /// parameters. Called once per individual query.
        /// </param>
        /// <param name="fanOutParams">
        /// Descriptors for parameters whose values should be fanned out. Each descriptor specifies
        /// a FHIR search key and one or more values. Multi-valued entries trigger separate queries.
        /// </param>
        /// <param name="maxConcurrency">Maximum number of concurrent FHIR queries per fan-out parameter group (default 4). Each multi-valued parameter is processed sequentially; concurrency applies within a single parameter's fan-out queries.</param>
        /// <param name="ct">Cancellation token propagated to concurrent FHIR queries.</param>
        /// <returns>A deduplicated list of resources, unioned within each parameter and intersected across parameters.</returns>
        public static async Task<List<T>> FanOutSearchAsync<T>(
            AriaFhirClient<T> client,
            Func<Builder<T>> baseBuilderFactory,
            IReadOnlyList<FanOutParam> fanOutParams,
            int maxConcurrency = 4,
            CancellationToken ct = default) where T : Resource
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(baseBuilderFactory);

            SeparateParams(fanOutParams, out var singleValued, out var multiValued);

            Builder<T> MakeBuilder()
            {
                var b = baseBuilderFactory();
                foreach (var sv in singleValued)
                    if (sv.Values.Count == 1)
                        b.With(sv.Key, sv.Values[0]);
                return b;
            }

            // Fast path: no multi-valued params → single query
            if (multiValued.Count == 0)
            {
                var sp = MakeBuilder().Build();
                return await client.AggregateResourcesAsync(sp).ConfigureAwait(false);
            }

            using var sem = new SemaphoreSlim(maxConcurrency);
            var perParamIdSets = new List<HashSet<string>>();
            var allResources = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

            foreach (var mvParam in multiValued)
            {
                var tasks = mvParam.Values.Select(async value =>
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var b = MakeBuilder();
                        b.With(mvParam.Key, value);
                        return await client.AggregateResourcesAsync(b.Build()).ConfigureAwait(false);
                    }
                    finally { sem.Release(); }
                }).ToList();

                var whenAll = System.Threading.Tasks.Task.WhenAll(tasks);
                try
                {
                    await whenAll.ConfigureAwait(false);
                }
                catch
                {
                    // Surface all exceptions from concurrent queries, not just the first.
                    if (whenAll.Exception is not null)
                        throw whenAll.Exception;
                    throw;
                }

                var paramIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var list in whenAll.Result)
                    foreach (var r in list)
                        if (!string.IsNullOrWhiteSpace(r.Id))
                        {
                            paramIds.Add(r.Id);
                            allResources[r.Id] = r;
                        }

                perParamIdSets.Add(paramIds);
            }

            return IntersectAndCollect(perParamIdSets, allResources);
        }

        /// <summary>
        /// Generic fan-out for custom result types (e.g., bundle-paged searches with includes).
        /// The caller provides an executor to run each query, a merger to combine partial results,
        /// an ID extractor for intersection, and an ID filter to narrow the merged result.
        /// </summary>
        /// <typeparam name="TResource">The FHIR resource type used to build search parameters.</typeparam>
        /// <typeparam name="TResult">The custom result type returned by each query (e.g., a result with included resources or stats).</typeparam>
        /// <param name="queryExecutor">Executes a single FHIR query given <see cref="SearchParams"/> and returns the custom result.</param>
        /// <param name="resultMerger">Combines multiple partial results into a single merged result (union/dedup).</param>
        /// <param name="idExtractor">Extracts resource IDs from a result for use in cross-parameter intersection.</param>
        /// <param name="idFilter">Filters a merged result to only include resources whose IDs are in the allowed set.</param>
        /// <param name="baseBuilderFactory">
        /// Factory that returns a fresh <see cref="Builder{TResource}"/> pre-populated with scalar (non-fan-out)
        /// parameters. Called once per individual query.
        /// </param>
        /// <param name="fanOutParams">
        /// Descriptors for parameters whose values should be fanned out. Each descriptor specifies
        /// a FHIR search key and one or more values.
        /// </param>
        /// <param name="maxConcurrency">Maximum number of concurrent FHIR queries per fan-out parameter group (default 4). Each multi-valued parameter is processed sequentially; concurrency applies within a single parameter's fan-out queries.</param>
        /// <param name="ct">Cancellation token propagated to concurrent FHIR queries.</param>
        /// <returns>A merged and deduplicated result combining all fan-out queries.</returns>
        public static async Task<TResult> FanOutSearchAsync<TResource, TResult>(
            Func<SearchParams, Task<TResult>> queryExecutor,
            Func<IReadOnlyList<TResult>, TResult> resultMerger,
            Func<TResult, IEnumerable<string>> idExtractor,
            Func<TResult, IReadOnlySet<string>, TResult> idFilter,
            Func<Builder<TResource>> baseBuilderFactory,
            IReadOnlyList<FanOutParam> fanOutParams,
            int maxConcurrency = 4,
            CancellationToken ct = default) where TResource : Resource
        {
            ArgumentNullException.ThrowIfNull(queryExecutor);
            ArgumentNullException.ThrowIfNull(resultMerger);
            ArgumentNullException.ThrowIfNull(idExtractor);
            ArgumentNullException.ThrowIfNull(idFilter);
            ArgumentNullException.ThrowIfNull(baseBuilderFactory);

            SeparateParams(fanOutParams, out var singleValued, out var multiValued);

            Builder<TResource> MakeBuilder()
            {
                var b = baseBuilderFactory();
                foreach (var sv in singleValued)
                    if (sv.Values.Count == 1)
                        b.With(sv.Key, sv.Values[0]);
                return b;
            }

            // Fast path
            if (multiValued.Count == 0)
            {
                var sp = MakeBuilder().Build();
                return await queryExecutor(sp).ConfigureAwait(false);
            }

            using var sem = new SemaphoreSlim(maxConcurrency);
            var perParamIdSets = new List<HashSet<string>>();
            var allPartialResults = new List<TResult>();

            foreach (var mvParam in multiValued)
            {
                var tasks = mvParam.Values.Select(async value =>
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var b = MakeBuilder();
                        b.With(mvParam.Key, value);
                        return await queryExecutor(b.Build()).ConfigureAwait(false);
                    }
                    finally { sem.Release(); }
                }).ToList();

                var whenAll = System.Threading.Tasks.Task.WhenAll(tasks);
                try
                {
                    await whenAll.ConfigureAwait(false);
                }
                catch
                {
                    if (whenAll.Exception is not null)
                        throw whenAll.Exception;
                    throw;
                }

                var paramIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var result in whenAll.Result)
                {
                    foreach (var id in idExtractor(result))
                        if (!string.IsNullOrWhiteSpace(id))
                            paramIds.Add(id);
                    allPartialResults.Add(result);
                }

                perParamIdSets.Add(paramIds);
            }

            var merged = resultMerger(allPartialResults);

            // If multiple multi-valued params, intersect IDs to enforce AND semantics
            if (perParamIdSets.Count > 1)
            {
                var finalIds = new HashSet<string>(perParamIdSets[0], StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < perParamIdSets.Count; i++)
                    finalIds.IntersectWith(perParamIdSets[i]);
                merged = idFilter(merged, finalIds);
            }

            return merged;
        }

        /// <summary>
        /// Classifies fan-out parameters into single-valued (folded into the base builder)
        /// and multi-valued (requiring separate queries).
        /// </summary>
        private static void SeparateParams(
            IReadOnlyList<FanOutParam> fanOutParams,
            out List<FanOutParam> singleValued,
            out List<FanOutParam> multiValued)
        {
            singleValued = new List<FanOutParam>();
            multiValued = new List<FanOutParam>();

            if (fanOutParams is null) return;

            foreach (var fp in fanOutParams)
            {
                if (fp.Values is null || fp.Values.Count == 0)
                    continue;

                var values = fp.PreserveCommas
                    ? fp.Values
                    : NormalizeCommaValues(fp.Values);

                if (values.Count == 0)
                    continue;
                if (values.Count == 1)
                    singleValued.Add(new FanOutParam(fp.Key, values, fp.PreserveCommas));
                else
                    multiValued.Add(new FanOutParam(fp.Key, values, fp.PreserveCommas));
            }
        }

        /// <summary>
        /// Splits comma-separated values into individual entries, trims whitespace,
        /// and drops empty segments. Returns the original list unchanged when no
        /// commas are present.
        /// </summary>
        private static IReadOnlyList<string> NormalizeCommaValues(IReadOnlyList<string> values)
        {
            // Fast path: check if any value contains a comma before allocating
            bool hasComma = false;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is not null && values[i].Contains(','))
                {
                    hasComma = true;
                    break;
                }
            }

            if (!hasComma) return values;

            var normalized = new List<string>();
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is null) continue;

                if (values[i].Contains(','))
                {
                    var segments = values[i].Split(',');
                    foreach (var seg in segments)
                    {
                        var trimmed = seg.Trim();
                        if (trimmed.Length > 0)
                            normalized.Add(trimmed);
                    }
                }
                else
                {
                    var trimmed = values[i].Trim();
                    if (trimmed.Length > 0)
                        normalized.Add(trimmed);
                }
            }

            return normalized;
        }

        /// <summary>
        /// Intersects per-parameter ID sets and returns the corresponding resources from the lookup dictionary.
        /// </summary>
        private static List<T> IntersectAndCollect<T>(
            List<HashSet<string>> perParamIdSets,
            Dictionary<string, T> allResources) where T : Resource
        {
            if (perParamIdSets.Count == 0)
                return new List<T>();

            var finalIds = new HashSet<string>(perParamIdSets[0], StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < perParamIdSets.Count; i++)
                finalIds.IntersectWith(perParamIdSets[i]);

            return finalIds
                .Where(allResources.ContainsKey)
                .Select(id => allResources[id])
                .ToList();
        }
    }
}
