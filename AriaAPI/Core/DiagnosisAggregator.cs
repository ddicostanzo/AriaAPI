// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
﻿
using Hl7.Fhir.Model;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AriaAPI.Core
{
    /// <summary>
    /// Helper methods to aggregate diagnoses from FHIR Condition resources.
    /// </summary>
    public static class DiagnosisAggregator
    {
        private const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";

        /// <summary>
        /// Aggregates ICD-10-CM diagnoses for a patient from a set of Condition resources.
        /// Output format: "CODE - DESCRIPTION" per line.
        /// </summary>
        public static string AggregateIcd10CmDiagnoses(
            IEnumerable<Condition> conditions,
            string? patientReference = null,
            bool distinct = true)
        {
            if (conditions == null) return string.Empty;

            // Filter to a patient if a reference was provided (e.g., "Patient/123" or just "123")
            string? normalizedPatientRef = NormalizePatientRef(patientReference);

            IEnumerable<Condition> scoped = conditions;
            if (!string.IsNullOrWhiteSpace(normalizedPatientRef))
            {
                scoped = scoped.Where(c =>
                    string.Equals(NormalizePatientRef(c.Subject?.Reference), normalizedPatientRef,
                        StringComparison.OrdinalIgnoreCase));
            }

            // Pull ICD-10-CM codings and format "code - description"
            var lines = scoped
                .SelectMany(c => c.Code?.Coding ?? Enumerable.Empty<Coding>())
                .Where(cd => string.Equals(cd.System, Icd10CmSystem, StringComparison.OrdinalIgnoreCase))
                .Select(cd =>
                {
                    var code = cd.Code?.Trim();
                    // Prefer Coding.display; if missing, use code text fallback
                    var desc = (cd.Display ?? string.Empty).Trim();

                    // If display missing, we still output the code (description blank)
                    return string.IsNullOrWhiteSpace(desc)
                        ? code
                        : $"{code} - {desc}";
                })
                .Where(x => !string.IsNullOrWhiteSpace(x));

            if (distinct)
                lines = lines.Distinct(StringComparer.OrdinalIgnoreCase);

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Returns a structured list of (code, description) tuples for all ICD-10-CM diagnoses.
        /// Useful when callers need semicolon-delimited codes and descriptions separately.
        /// </summary>
        public static List<(string code, string description)> AggregateIcd10CodeList(
            IEnumerable<Condition> conditions,
            string? patientReference = null,
            bool distinct = true)
        {
            if (conditions == null) return [];

            string? normalizedPatientRef = NormalizePatientRef(patientReference);

            IEnumerable<Condition> scoped = conditions;
            if (!string.IsNullOrWhiteSpace(normalizedPatientRef))
            {
                scoped = scoped.Where(c =>
                    string.Equals(NormalizePatientRef(c.Subject?.Reference), normalizedPatientRef,
                        StringComparison.OrdinalIgnoreCase));
            }

            var items = scoped
                .SelectMany(c => c.Code?.Coding ?? Enumerable.Empty<Coding>())
                .Where(cd => string.Equals(cd.System, Icd10CmSystem, StringComparison.OrdinalIgnoreCase))
                .Select(cd =>
                {
                    var code = (cd.Code ?? string.Empty).Trim();
                    var desc = (cd.Display ?? string.Empty).Trim();
                    return (code, desc);
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.code));

            if (distinct)
                items = items.DistinctBy(x => x.code, StringComparer.OrdinalIgnoreCase);

            return items.ToList();
        }

        private static string? NormalizePatientRef(string? patientRef)
        {
            if (string.IsNullOrWhiteSpace(patientRef)) return null;

            var s = patientRef.Trim();

            // Allow passing "123" instead of "Patient/123"
            if (!s.Contains('/', StringComparison.Ordinal) && !s.StartsWith("Patient", StringComparison.OrdinalIgnoreCase))
                return $"Patient/{s}";

            return s;
        }


        /// <summary>
        /// Renders ICD-10-CM diagnoses as:
        /// "CODE - DESCRIPTION, Diagnosed M/d/yyyy (Status)"
        /// Each diagnosis appears on its own line. Optionally filters to primary malignancies and appends stage summary.
        /// </summary>
        public static string AggregateIcd10CmDiagnosesWithDateStatus(
            IEnumerable<Condition> conditions,
            bool onlyPrimaryMalignancies = true,
            bool includeStageSummary = false)
        {
            if (conditions == null) return string.Empty;

            var items = conditions
                .Where(c => c?.Code != null)
                .Where(c => HasIcd10CmCoding(c))                                  // keep ICD-10-CM focus
                .Where(c => !onlyPrimaryMalignancies || IsLikelyPrimaryMalignancy(c))
                .OrderBy(c => TryGetOnset(c) ?? DateTimeOffset.MaxValue)
                .Select(c =>
                {
                    var (code, desc) = GetPreferredIcd10CodeAndDisplay(c.Code!);
                    var head = string.IsNullOrWhiteSpace(code) ? desc : $"{code} - {desc}";

                    var onset = TryGetOnset(c);
                    var diagnosed = onset.HasValue
                        ? $"Diagnosed {FormatUsShortDate(onset.Value)}"
                        : "Diagnosed (date not documented)";

                    var status = GetClinicalStatusLabel(c);
                    var statusText = string.IsNullOrWhiteSpace(status) ? "Unknown" : status;

                    var line = $"{head}, {diagnosed} ({statusText})";

                    if (includeStageSummary)
                    {
                        var stage = GetStageSummary(c);
                        if (!string.IsNullOrWhiteSpace(stage))
                            line = $"{head}, Stage: {stage}, {diagnosed} ({statusText})";
                    }

                    return line;
                });

            // IMPORTANT: do NOT Distinct() here, or you may collapse diagnoses that differ by onset/status.
            return string.Join(Environment.NewLine, items);
        }

        private static bool HasIcd10CmCoding(Condition c) =>
            c.Code?.Coding?.Any(cd =>
                string.Equals(cd.System, Icd10CmSystem, StringComparison.OrdinalIgnoreCase) &&
                (!string.IsNullOrWhiteSpace(cd.Code) || !string.IsNullOrWhiteSpace(cd.Display))) == true;

        private static (string code, string display) GetPreferredIcd10CodeAndDisplay(Hl7.Fhir.Model.CodeableConcept cc)
        {
            var icd = cc.Coding?.FirstOrDefault(cd =>
                string.Equals(cd.System, Icd10CmSystem, StringComparison.OrdinalIgnoreCase));

            if (icd != null)
            {
                var code = icd.Code?.Trim() ?? string.Empty;
                var disp = !string.IsNullOrWhiteSpace(icd.Display) ? icd.Display.Trim()
                         : !string.IsNullOrWhiteSpace(cc.Text) ? cc.Text.Trim()
                         : "Diagnosis";
                return (code, disp);
            }

            // Fallback: any coding or cc.Text
            var any = cc.Coding?.FirstOrDefault(cd => !string.IsNullOrWhiteSpace(cd.Display) || !string.IsNullOrWhiteSpace(cd.Code));
            if (any != null)
            {
                return (any.Code?.Trim() ?? string.Empty,
                        !string.IsNullOrWhiteSpace(any.Display) ? any.Display.Trim()
                        : (!string.IsNullOrWhiteSpace(cc.Text) ? cc.Text.Trim() : "Diagnosis"));
            }

            return (string.Empty, string.IsNullOrWhiteSpace(cc.Text) ? "Diagnosis" : cc.Text.Trim());
        }

        /// <summary>
        /// Heuristic for primary malignancy: ICD-10-CM codes that start with 'C' (malignant neoplasms).
        /// Adjust if your feed uses a dedicated primary flag/extension.
        /// </summary>
        private static bool IsLikelyPrimaryMalignancy(Condition c)
        {
            var icd = c.Code?.Coding?.FirstOrDefault(cd =>
                string.Equals(cd.System, Icd10CmSystem, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(cd.Code));

            // Most primary malignant neoplasms are in C00–C97 (ICD-10-CM)
            return icd?.Code?.StartsWith("C", StringComparison.OrdinalIgnoreCase) == true;
        }

        private static DateTimeOffset? TryGetOnset(Condition c)
        {
            try
            {
                if (c.Onset is FhirDateTime fdt)
                {
                    // Try native parse first (may throw on partial), then value fallback
                    try
                    {
                        var dto = fdt.ToDateTimeOffset(TimeSpan.Zero);
                        if (dto is DateTimeOffset d) return d;
                    }
                    catch (FormatException) { }
                    catch (ArgumentException) { }

                    if (!string.IsNullOrWhiteSpace(fdt.Value) &&
                        DateTimeOffset.TryParse(fdt.Value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeLocal, out var parsed))
                        return parsed;
                }
                else if (c.Onset is Period p)
                {
                    if (p.StartElement != null)
                    {
                        try
                        {
                            var dto = p.StartElement.ToDateTimeOffset(TimeSpan.Zero);
                            if (dto is DateTimeOffset d) return d;
                        }
                        catch (FormatException) { }
                        catch (ArgumentException) { }
                    }
                    if (p.EndElement != null)
                    {
                        try
                        {
                            var dto = p.EndElement.ToDateTimeOffset(TimeSpan.Zero);
                            if (dto is DateTimeOffset d) return d;
                        }
                        catch (FormatException) { }
                        catch (ArgumentException) { }
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine($"DiagnosisAggregator: ignoring {ex.GetType().Name} in TryGetOnset: {ex.Message}");
            }

            return null;
        }

        private static string FormatUsShortDate(DateTimeOffset dto) =>
            dto.ToLocalTime().ToString("M/d/yyyy", CultureInfo.GetCultureInfo("en-US"));

        private static string GetClinicalStatusLabel(Condition c)
        {
            var statusCoding = c.ClinicalStatus?.Coding?.FirstOrDefault();
            string? raw = statusCoding?.Display ?? statusCoding?.Code ?? c.ClinicalStatus?.Text;

            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            raw = raw.Trim();
            return raw.Equals("active", StringComparison.OrdinalIgnoreCase) ? "Active" :
                   raw.Equals("inactive", StringComparison.OrdinalIgnoreCase) ? "Inactive" :
                   raw.Equals("resolved", StringComparison.OrdinalIgnoreCase) ? "Resolved" :
                   CultureInfo.CurrentCulture.TextInfo.ToTitleCase(raw.ToLowerInvariant());
        }

        private static string? GetStageSummary(Condition c)
        {
            var s = c.Stage?.FirstOrDefault();
            var txt = s?.Summary?.Text;
            if (!string.IsNullOrWhiteSpace(txt)) return txt;

            var disp = s?.Summary?.Coding?.FirstOrDefault(cd => !string.IsNullOrWhiteSpace(cd.Display))?.Display;
            return string.IsNullOrWhiteSpace(disp) ? null : disp;
        }

    }
}