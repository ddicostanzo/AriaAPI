// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
﻿using AriaAPI.Core;
using AriaAPI.Networking.Core;
using Hl7.Fhir.Model;
using Hl7.Fhir.Support;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static AriaAPI.API.SearchHelpers.SearchTypes;
using static AriaAPI.API.SearchHelpers.SearchHelpers;

namespace AriaAPI.API.MultiResourceSearch
{
    public static partial class MultiResourceSearch
    {
        // You can centralize this if you want to switch keys (e.g., "appointment-type")
        private const string CategorySearchParam = "service-category";

        /// <summary>
        /// Resolves a patient by identifier. Returns null if no patient is found.
        /// Throws <see cref="InvalidOperationException"/> if the resolved patient has a null Id.
        /// </summary>
        private static async Task<Patient?> ResolvePatientAsync(
            ClientConfigurator configurator,
            string patientIdentifier,
            CancellationToken ct = default)
        {
            var patientClient = configurator.ForResource<Patient>(ct);
            var patientParams = new Builder<Patient>().ByIdentifier(patientIdentifier).Build();
            var results = await patientClient.SearchFirstPageAsync(patientParams, pageSize: 1).ConfigureAwait(false);
            var patient = results.FirstOrDefault();

            if (patient is not null && string.IsNullOrWhiteSpace(patient.Id))
                throw new InvalidOperationException(
                    $"Patient resolved for identifier '{patientIdentifier}' has a null or empty Id.");

            return patient;
        }

        /// <summary>
        /// Maps an <see cref="AppointmentCategory"/> enum value to its FHIR display string.
        /// </summary>
        private static string ResolveCategoryDisplay(AppointmentCategory category)
        {
            return AppointmentCategoryMap.TryGetValue(category, out var d) ? d : category.ToString();
        }

        /// <summary>
        /// Retrieves a patient by identifier and returns the patient resource along with
        /// all associated appointments falling within the specified inclusive date window.
        /// </summary>
        public static async Task<(Patient? patient, List<Appointment> appointments)>
            PatientAndAppointmentsByDateAsync(
                ClientConfigurator configurator,
                string patientIdentifier,
                DateTimeOffset start,
                DateTimeOffset end,
                int listReturnLimit = -1,
                CancellationToken ct = default)
        {
            if (end < start) throw new ArgumentException("end must be >= start");
            if (listReturnLimit <= 0) listReturnLimit = int.MaxValue;

            var patient = await ResolvePatientAsync(configurator, patientIdentifier, ct);
            if (patient == null)
                return (null, new List<Appointment>());

            var apptClient = configurator.ForResource<Appointment>(ct);
            var apptBuilder = new Builder<Appointment>()
                                .ForPatient(patient.Id ?? throw new InvalidOperationException("Patient has null Id"))
                                .With("date", $"ge{start:O}")
                                .With("date", $"le{end:O}");
            if (listReturnLimit != int.MaxValue) apptBuilder.WithCount(listReturnLimit);
            var appointments = await apptClient.AggregateResourcesAsync(apptBuilder.Build()).ConfigureAwait(false);
            return (patient, appointments);
        }

        /// <summary>
        /// Retrieves a patient and returns appointments within a date window AND matching a single category.
        /// </summary>
        public static async Task<(Patient? patient, List<Appointment> appointments)>
            PatientAndAppointmentsByDateAndCategoryAsync(
                ClientConfigurator configurator,
                string patientIdentifier,
                DateTimeOffset start,
                DateTimeOffset end,
                AppointmentCategory category,
                int listReturnLimit = -1,
                CancellationToken ct = default)
        {
            if (end < start) throw new ArgumentException("end must be >= start");
            if (listReturnLimit <= 0) listReturnLimit = int.MaxValue;

            var patient = await ResolvePatientAsync(configurator, patientIdentifier, ct);
            if (patient == null)
                return (null, new List<Appointment>());

            var categoryDisplay = ResolveCategoryDisplay(category);

            var apptClient = configurator.ForResource<Appointment>(ct);
            var apptBuilder = new Builder<Appointment>()
                                .ForPatient(patient.Id ?? throw new InvalidOperationException("Patient has null Id"))
                                .With("date", $"ge{start:O}")
                                .With("date", $"le{end:O}")
                                .With(CategorySearchParam, categoryDisplay);
            if (listReturnLimit != int.MaxValue) apptBuilder.WithCount(listReturnLimit);
            var appointments = await apptClient.AggregateResourcesAsync(apptBuilder.Build()).ConfigureAwait(false);
            return (patient, appointments);
        }

        /// <summary>
        /// Retrieves a patient and returns appointments matching a single category (no date window).
        /// </summary>
        public static async Task<(Patient? patient, List<Appointment> appointments)>
            PatientAndAppointmentsByCategoryAsync(
                ClientConfigurator configurator,
                string patientIdentifier,
                AppointmentCategory category,
                int listReturnLimit = -1,
                CancellationToken ct = default)
        {
            if (listReturnLimit <= 0) listReturnLimit = int.MaxValue;

            var patient = await ResolvePatientAsync(configurator, patientIdentifier, ct);
            if (patient == null)
                return (null, new List<Appointment>());

            var categoryDisplay = ResolveCategoryDisplay(category);

            var apptClient = configurator.ForResource<Appointment>(ct);
            var apptBuilder = new Builder<Appointment>()
                                .ForPatient(patient.Id ?? throw new InvalidOperationException("Patient has null Id"))
                                .With(CategorySearchParam, categoryDisplay);
            if (listReturnLimit != int.MaxValue) apptBuilder.WithCount(listReturnLimit);
            var appointments = await apptClient.AggregateResourcesAsync(apptBuilder.Build()).ConfigureAwait(false);
            return (patient, appointments);
        }

        /// <summary>
        /// Retrieves a patient by identifier and returns appointments within the specified inclusive date
        /// window that match any of the supplied categories (OR semantics).
        /// </summary>
        /// <param name="configurator">Client configurator providing a FHIR resource client and auth.</param>
        /// <param name="patientIdentifier">Patient identifier used to resolve the patient resource.</param>
        /// <param name="start">Inclusive start of the date window.</param>
        /// <param name="end">Inclusive end of the date window.</param>
        /// <param name="categories">
        /// One or more <see cref="AppointmentCategory"/> values. Each category is fanned out into a
        /// separate FHIR query via <see cref="FanOutSearchHelper"/> to avoid repeated
        /// <c>service-category</c> keys that the Aria FHIR server rejects. Results are unioned and
        /// deduplicated by <c>Resource.Id</c>.
        /// </param>
        /// <param name="listReturnLimit">
        /// Defensive cap on the number of returned appointments. Values &lt;= 0 are treated as unbounded.
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// A tuple of the resolved <see cref="Patient"/> (or <c>null</c> if not found) and the list of
        /// matching <see cref="Appointment"/> resources.
        /// </returns>
        public static async Task<(Patient? patient, List<Appointment> appointments)>
            PatientAndAppointmentsByDateAndCategoriesAsync(
                ClientConfigurator configurator,
                string patientIdentifier,
                DateTimeOffset start,
                DateTimeOffset end,
                IEnumerable<AppointmentCategory> categories,
                int listReturnLimit = -1,
                CancellationToken ct = default)
        {
            if (end < start) throw new ArgumentException("end must be >= start");
            if (listReturnLimit <= 0) listReturnLimit = int.MaxValue;
            categories ??= [];

            var patient = await ResolvePatientAsync(configurator, patientIdentifier, ct);
            if (patient == null)
                return (null, new List<Appointment>());

            var apptClient = configurator.ForResource<Appointment>(ct);

            Builder<Appointment> MakeBaseBuilder()
            {
                var b = new Builder<Appointment>()
                    .ForPatient(patient.Id ?? throw new InvalidOperationException("Patient has null Id"))
                    .With("date", $"ge{start:O}")
                    .With("date", $"le{end:O}");
                if (listReturnLimit != int.MaxValue) b.WithCount(listReturnLimit);
                return b;
            }

            var catValues = categories.Select(c => ResolveCategoryDisplay(c)).ToList();
            var fanOuts = new List<FanOutSearchHelper.FanOutParam>();
            if (catValues.Count > 0)
                fanOuts.Add(new FanOutSearchHelper.FanOutParam(CategorySearchParam, catValues));

            var appts = await FanOutSearchHelper.FanOutSearchAsync(apptClient, MakeBaseBuilder, fanOuts, ct: ct)
                .ConfigureAwait(false);
            return (patient, appts);
        }
    }
}
