// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.

using AriaAPI.API.SearchHelpers;
using AriaAPI.Core;
using AriaAPI.Networking.Core;
using AriaAPI.Resources.Includes;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static AriaAPI.API.SearchHelpers.SearchTypes;

namespace AriaAPI.API.SingleResourceSearch;

/// <summary>
/// Provides search operations for FHIR <see cref="DocumentReference"/> resources using
/// <c>ClientConfigurator</c> and <c>Builder&lt;T&gt;</c>.
/// Supports strongly-typed and raw <c>_include</c>/<c>_revinclude</c> parameters,
/// type OR semantics, date range filters, and server-side sorting/count hints.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="DocumentsAsync(ClientConfigurator, DocumentReferenceSearchParams, int, CancellationToken)"/>
/// to perform server-side filtered queries with optional paging aggregation.
/// </para>
/// <para>
/// For inclusive date ranges, prefer providing <see cref="DocumentReferenceSearchParams.Dates"/>
/// with FHIR comparators (e.g., <c>geYYYY-MM-DD</c> and <c>leYYYY-MM-DD</c>).
/// </para>
/// </remarks>
public static class DocumentReferenceSearch
{
    /// <summary>
    /// Reverse include paths from <see cref="Provenance"/> to <see cref="DocumentReference"/>.
    /// Example wire form: <c>Provenance:target</c>.
    /// </summary>
    public enum RevFromProvenance
    {
        /// <summary>
        /// Reverse include for <c>Provenance:target</c>, returning associated <see cref="DocumentReference"/> items.
        /// </summary>
        Target
    }

    /// <summary>
    /// Encapsulates search parameters for <see cref="DocumentReference"/> queries,
    /// including filters, include options, sorting, and result limits.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <see cref="Dates"/> is preferred over <see cref="Date"/>; supply multiple <c>date</c> parameters
    ///       with FHIR comparators for inclusive ranges (e.g., <c>ge2024-01-01</c>, <c>le2024-12-31</c>).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <see cref="Types"/> supports OR semantics via a single, comma-joined token list (emitted as
    ///       <c>type=tok1,tok2,...</c>). If both <see cref="Types"/> and <see cref="Type"/> are provided,
    ///       <see cref="Types"/> takes precedence.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       When <see cref="IncludeContent"/> is true, adds <c>_include=DocumentReference:content</c>
    ///       to bring related attachments alongside the references (server permitting).
    ///     </description>
    ///   </item>
    /// </list>
    /// </remarks>
    public sealed class DocumentReferenceSearchParams
    {
        /// <summary>
        /// Logical resource id filter (<c>_id</c>).
        /// </summary>
        public string? Id { get; init; }

        /// <summary>
        /// Authenticator filter (<c>authenticator</c>).
        /// </summary>
        public string? Authenticator { get; init; }

        /// <summary>
        /// Author filter (<c>author</c>).
        /// </summary>
        public string? Author { get; init; }

        /// <summary>
        /// Multiple date parameters using FHIR comparators (repeatable).
        /// Example values: <c>ge2024-01-01</c>, <c>le2024-12-31</c>.
        /// Preferred for inclusive ranges.
        /// </summary>
        public IEnumerable<string>? Dates { get; init; }

        /// <summary>
        /// Single raw <c>date</c> parameter. <b>Deprecated</b> in favor of <see cref="Dates"/>.
        /// </summary>
        public string? Date { get; init; }

        /// <summary>
        /// Document status detail filter (<c>doc-status</c>).
        /// </summary>
        public string? DocStatus { get; init; }

        /// <summary>
        /// Repeatable identifier filters (<c>identifier</c>).
        /// </summary>
        public List<string>? Identifiers { get; init; }

        /// <summary>
        /// Subject (patient) reference filter (<c>patient</c>).
        /// </summary>
        public string? Patient { get; init; }

        /// <summary>
        /// Related filter (<c>related</c>), repeatable.
        /// </summary>
        public List<string>? Related { get; init; }

        /// <summary>
        /// Resource status filter (<c>status</c>).
        /// Defaults to <c>current</c>.
        /// </summary>
        public string? Status { get; init; } = "current";

        /// <summary>
        /// Document types (OR semantics via comma-joined token list).
        /// Preferred over <see cref="Type"/>.
        /// </summary>
        public IEnumerable<DocumentType>? Types { get; init; }

        /// <summary>
        /// Single document type (used when <see cref="Types"/> is not supplied).
        /// </summary>
        public DocumentType? Type { get; init; }

        /// <summary>
        /// Strongly-typed includes (e.g., <see cref="DocumentReferenceInclude.Content"/>, Author).
        /// </summary>
        public IEnumerable<DocumentReferenceInclude>? Includes { get; init; }

        /// <summary>
        /// Strongly-typed reverse includes (e.g., <see cref="RevFromProvenance.Target"/>).
        /// </summary>
        public IEnumerable<RevFromProvenance>? RevIncludes { get; init; }

        /// <summary>
        /// When true, applies the <c>:iterate</c> modifier to enum-based includes/revincludes (server permitting).
        /// </summary>
        public bool UseIterateModifier { get; init; } = false;

        /// <summary>
        /// Raw include tokens for advanced include paths not covered by the enum
        /// (value is a tuple of include path and modifier).
        /// </summary>
        public IEnumerable<(string include, IncludeModifier modifier)>? RawIncludes { get; init; }

        /// <summary>
        /// Raw reverse include tokens for advanced reverse include paths
        /// (value is a tuple of reverse include path and modifier).
        /// </summary>
        public IEnumerable<(string revInclude, IncludeModifier modifier)>? RawRevIncludes { get; init; }

        /// <summary>
        /// Optional target type for enum-based includes (e.g., <c>:Practitioner</c>).
        /// </summary>
        public string? IncludeTargetType { get; init; }

        /// <summary>
        /// Optional target type for enum-based reverse includes.
        /// </summary>
        public string? RevIncludeTargetType { get; init; }

        /// <summary>
        /// When true, emits a server-side sort hint descending by date (<c>_sort=-date</c>) when supported.
        /// </summary>
        public bool SortByDateDescending { get; init; } = true;

        /// <summary>
        /// Preferred server page size hint (<c>_count</c>). When null, falls back to the
        /// <c>listReturnLimit</c> specified at call site.
        /// </summary>
        public int? Count { get; init; } = null;

        /// <summary>
        /// When true, adds <c>_include=DocumentReference:content</c> to bring related attachments.
        /// </summary>
        public bool IncludeContent { get; init; } = true;
    }

    /// <summary>
    /// Executes a <see cref="DocumentReference"/> search using the provided parameters and returns
    /// a fully aggregated list (paging via <c>Bundle.link[next]</c> handled by the underlying client).
    /// </summary>
    /// <param name="configurator">Client configurator providing a FHIR resource client and auth.</param>
    /// <param name="p">Search parameters; see remarks on <see cref="DocumentReferenceSearchParams.Dates"/> and <see cref="DocumentReferenceSearchParams.Types"/>.</param>
    /// <param name="listReturnLimit">
    /// Final defensive cap on the number of returned results (post-aggregation). Values ≤ 0 are treated as unbounded
    /// (<see cref="int.MaxValue"/>). The server may still enforce its own paging and caps.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Aggregated <see cref="List{T}"/> of <see cref="DocumentReference"/> resources matching the criteria (possibly empty).
    /// </returns>
    /// <remarks>
    /// <para>
    /// List parameters (<see cref="DocumentReferenceSearchParams.Identifiers"/> and
    /// <see cref="DocumentReferenceSearchParams.Related"/>) are fanned out into individual FHIR queries
    /// via <see cref="FanOutSearchHelper"/> to avoid repeated search-parameter keys that the Aria FHIR
    /// server rejects. Results are unioned within each parameter and intersected across parameters,
    /// then deduplicated by <c>Resource.Id</c>.
    /// </para>
    /// <para>
    /// Scalar parameters (<c>patient</c>, <c>type</c> (comma-joined for OR), repeated <c>date</c>
    /// with comparators, <c>_include</c>/<c>_revinclude</c>, <c>_sort</c>, and <c>_count</c>) are
    /// folded into the base builder shared by every fan-out query.
    /// </para>
    /// <para>
    /// The return list is defensively trimmed to <paramref name="listReturnLimit"/> if the server returns more.
    /// </para>
    /// </remarks>
    public static async Task<List<DocumentReference>> DocumentsAsync(
        ClientConfigurator configurator,
        DocumentReferenceSearchParams p,
        int listReturnLimit = SearchExecutor.DefaultServerMaxResults,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configurator);
        p ??= new DocumentReferenceSearchParams();

        var limit = SearchExecutor.NormalizeLimit(listReturnLimit);

        Builder<DocumentReference> MakeBaseBuilder()
        {
            var builder = new Builder<DocumentReference>();

            if (!string.IsNullOrWhiteSpace(p.Id))
                builder.ById(p.Id!);
            if (!string.IsNullOrWhiteSpace(p.Authenticator))
                builder.With("authenticator", p.Authenticator!);
            if (!string.IsNullOrWhiteSpace(p.Author))
                builder.With("author", p.Author!);

            if (p.Dates is not null)
            {
                foreach (var date in p.Dates.Where(s => !string.IsNullOrWhiteSpace(s)))
                    builder.With("date", date);
            }
            else if (!string.IsNullOrWhiteSpace(p.Date))
            {
                builder.With("date", p.Date!);
            }

            if (!string.IsNullOrWhiteSpace(p.DocStatus))
                builder.With("doc-status", p.DocStatus!);
            if (!string.IsNullOrWhiteSpace(p.Patient))
                builder.With("patient", p.Patient!);

            builder.With("status", string.IsNullOrWhiteSpace(p.Status) ? "current" : p.Status!);

            // Type OR semantics (comma-joined, stays as scalar)
            if (p.Types is not null && p.Types.Any())
            {
                var tokens = p.Types
                    .Select(t => t.ToSearchValue())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .ToArray();
                if (tokens.Length > 0)
                    builder.With("type", string.Join(",", tokens));
            }
            else if (p.Type.HasValue)
            {
                builder.With("type", p.Type.Value.ToSearchValue());
            }

            var modifier = p.UseIterateModifier ? IncludeModifier.Iterate : IncludeModifier.None;
            if (p.IncludeContent)
                builder.Include(DocumentReferenceInclude.Content, p.IncludeTargetType, modifier);
            if (p.Includes is not null && p.Includes.Any())
                builder.Include(p.Includes, p.IncludeTargetType, modifier);
            if (p.RevIncludes is not null && p.RevIncludes.Any())
                builder.RevInclude(p.RevIncludes, p.RevIncludeTargetType, modifier);
            if (p.RawIncludes is not null)
            {
                foreach (var (include, m) in p.RawIncludes)
                    builder.Include(include, modifier: m);
            }
            if (p.RawRevIncludes is not null)
            {
                foreach (var (revInclude, m) in p.RawRevIncludes)
                    builder.RevInclude(revInclude, modifier: m);
            }
            if (p.SortByDateDescending)
                builder.With("_sort", "-date");
            builder.WithCount(p.Count ?? limit);

            return builder;
        }

        var fanOuts = new List<FanOutSearchHelper.FanOutParam>();
        if (p.Identifiers is { Count: > 0 })
        {
            var ids = p.Identifiers.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (ids.Count > 0)
                fanOuts.Add(new FanOutSearchHelper.FanOutParam("identifier", ids));
        }
        if (p.Related is { Count: > 0 })
        {
            var rels = p.Related.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (rels.Count > 0)
                fanOuts.Add(new FanOutSearchHelper.FanOutParam("related", rels));
        }

        return await SearchExecutor.ExecuteAsync(
            configurator,
            MakeBaseBuilder,
            fanOuts,
            limit,
            ct).ConfigureAwait(false);
    }
}
