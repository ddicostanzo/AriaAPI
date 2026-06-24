// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AriaAPI.API.IdentityResolvers;
using AriaAPI.Core;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging;
using static AriaAPI.API.Create.CreateHelpers;
using static AriaAPI.API.SearchHelpers.SearchTypes;
using CodeableConcept = Hl7.Fhir.Model.CodeableConcept;

namespace AriaAPI.API.DocumentReferenceCreate
{
    /// <summary>
    /// Entry point to create DocumentReference resources with an embedded Attachment.
    /// </summary>
    public static class DocumentReferenceCreate
    {
        /// <summary>
        /// Default maximum file size in bytes (10 MB).
        /// </summary>
        public const long DefaultMaxFileSizeBytes = 10L * 1024 * 1024;

        /// <summary>
        /// Parameters required to create a DocumentReference with an embedded Attachment.
        /// </summary>
        public sealed class DocumentReferenceCreateParams
        {
            /// <summary>FHIR reference to the patient, e.g., "Patient/123". If not set, the create will proceed without subject.</summary>
            public string? PatientReference { get; init; }

            /// <summary>FHIR reference to the author, e.g., "Practitioner/456". Optional.</summary>
            public string? AuthorReference { get; init; }

            /// <summary>FHIR reference to the authenticator, e.g., "Organization/RadOnc-1". Optional.</summary>
            public string? AuthenticatorReference { get; init; }

            /// <summary>DocumentReference.status: "current" | "entered-in-error" | "superseded". Defaults to "current".</summary>
            public string Status { get; init; } = "current";

            /// <summary>docStatus: "preliminary" | "final" | "entered-in-error" | "amended".</summary>
            public string? DocStatus { get; init; } = "final";

            /// <summary>Document type (domain enum). Will be mapped to a CodeableConcept.</summary>
            public DocumentType? Type { get; init; }

            /// <summary>Date/time the document was created (DocumentReference.date).</summary>
            public DateTime? Date { get; init; }

            /// <summary>Optional identifiers attached to DocumentReference.identifier.</summary>
            public List<string>? Identifiers { get; init; }

            /// <summary>Attachment title (usually file name).</summary>
            public string? Title { get; init; }

            /// <summary>Absolute file path to the artifact to embed as Attachment.data.</summary>
            public string SourceFilePath { get; init; } = default!;

            /// <summary>Creation timestamp for the attachment.</summary>
            public DateTime? Creation { get; init; }

            /// <summary>Category classification for the document reference. Defaults to "Patient Document".</summary>
            public List<CodeableConcept> Category { get; init; } = new List<CodeableConcept>()
                                                                        { new CodeableConcept()
                                                                            { Coding = {
                                                                              new Coding("http://varian.com/fhir/CodeSystem/DocumentReference/documentreference-class",
                                                                              "Patient Document",
                                                                              "Patient Document") } } };

            // ── Varian extension values (opt-in via the Include*Extension flags below) ──

            /// <summary>Supervisor reference (Varian extension). Used when <see cref="IncludeSupervisorExtension"/> is set.</summary>
            public string? SupervisorReference { get; init; }

            /// <summary>Optional display for the supervisor reference.</summary>
            public string? SupervisorDisplay { get; init; }

            /// <summary>Authenticated timestamp (Varian extension). Used when <see cref="IncludeAuthenticatedDateExtension"/> is set.</summary>
            public DateTime? AuthenticatedDate { get; init; }

            /// <summary>Template name (Varian extension). Used when <see cref="IncludeTemplateNameExtension"/> is set.</summary>
            public string? TemplateName { get; init; }

            /// <summary>Login-institution reference (Varian extension). Used when <see cref="IncludeLoginInstitutionExtension"/> is set.</summary>
            public string? InstitutionReference { get; init; }

            /// <summary>Optional display for the login-institution reference.</summary>
            public string? InstitutionDisplay { get; init; }

            /// <summary>Document storage location (Varian extension). Used when <see cref="IncludeDocumentLocationExtension"/> is set.</summary>
            public string? DocumentLocation { get; init; }

            /// <summary>
            /// Enables the Varian supervisor extension.
            /// Default <see langword="false"/> so package consumers can opt in after validating that
            /// the extension does not interfere with Aria document ingestion.
            /// </summary>
            public bool IncludeSupervisorExtension { get; init; } = false;

            /// <summary>
            /// Enables the Varian authenticated-timestamp extension.
            /// Default <see langword="false"/> so package consumers can opt in after validation.
            /// </summary>
            public bool IncludeAuthenticatedDateExtension { get; init; } = false;

            /// <summary>
            /// Enables the Varian template-name extension.
            /// Default <see langword="false"/> so package consumers can opt in after validation.
            /// </summary>
            public bool IncludeTemplateNameExtension { get; init; } = false;

            /// <summary>
            /// Enables the Varian login-institution extension.
            /// Default <see langword="false"/> so package consumers can opt in after validation.
            /// </summary>
            public bool IncludeLoginInstitutionExtension { get; init; } = false;

            /// <summary>
            /// Enables the Varian document-location extension.
            /// Default <see langword="false"/> so package consumers can opt in after validation.
            /// </summary>
            public bool IncludeDocumentLocationExtension { get; init; } = false;

            /// <summary>
            /// Convenience flag to enable all currently supported Varian extensions whose values are
            /// present. Unlike the individual flags, this is best-effort: extensions whose values are
            /// missing are skipped rather than throwing.
            /// </summary>
            public bool IncludeAllVarianExtensions { get; init; } = false;
        }

        /// <summary>
        /// Reads the file at <see cref="DocumentReferenceCreateParams.SourceFilePath"/>,
        /// builds a DocumentReference, and creates it on the FHIR server.
        /// </summary>
        /// <param name="configurator">FHIR client configurator.</param>
        /// <param name="p">Parameters describing the document to create.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="maxFileSizeBytes">
        /// Maximum allowed file size in bytes. Files exceeding this limit cause an
        /// <see cref="InvalidOperationException"/>. Defaults to <see cref="DefaultMaxFileSizeBytes"/> (10 MB).
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The created <see cref="DocumentReference"/> resource.</returns>
        /// <exception cref="FileNotFoundException">Thrown when <paramref name="p"/>.SourceFilePath does not exist.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the file exceeds <paramref name="maxFileSizeBytes"/>.</exception>
        public static async Task<DocumentReference> CreateFromFileAsync(
            ClientConfigurator configurator,
            DocumentReferenceCreateParams p,
            ILogger logger,
            long maxFileSizeBytes = DefaultMaxFileSizeBytes,
            CancellationToken ct = default)
        {
            if (configurator is null)
                throw new ArgumentNullException(nameof(configurator));
            if (p is null)
                throw new ArgumentNullException(nameof(p));

            if (string.IsNullOrWhiteSpace(p.SourceFilePath))
                throw new FileNotFoundException("SourceFilePath must be specified.", p.SourceFilePath);

            var resolvedPath = Path.GetFullPath(p.SourceFilePath);

            if (!File.Exists(resolvedPath))
                throw new FileNotFoundException($"Source file not found at path: {resolvedPath}", resolvedPath);

            var fileInfo = new FileInfo(resolvedPath);
            if (fileInfo.Length > maxFileSizeBytes)
                throw new InvalidOperationException(
                    $"File size ({fileInfo.Length:N0} bytes) exceeds the maximum allowed size ({maxFileSizeBytes:N0} bytes): {resolvedPath}");

            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(p.AuthenticatorReference))
                throw new ArgumentException(
                    "AuthenticatorReference is required to resolve document types (e.g., \"Organization/JamesRO\").",
                    nameof(p));

            var refParts = p.AuthenticatorReference.Split('/');
            if (refParts.Length < 2 || string.IsNullOrWhiteSpace(refParts[1]))
                throw new ArgumentException(
                    $"AuthenticatorReference must be in 'ResourceType/Id' format (e.g., \"Organization/JamesRO\"), got: \"{p.AuthenticatorReference}\".",
                    nameof(p));

            var service = await DocumentTypeConceptService.CreateAsync(
                    configurator,
                    publisher: refParts[1],
                    listReturnLimit: 250
                ).ConfigureAwait(false);

            // Resolve from your enum
            var ccType = service.Resolve(p.Type!.Value);

            // 1) Package file as Attachment (base64)
            var bytes = await File.ReadAllBytesAsync(resolvedPath, ct);
            var contentType = ContentTypeHelper.MapFromExtension(Path.GetExtension(resolvedPath));
            var title = string.IsNullOrWhiteSpace(p.Title) ? Path.GetFileName(resolvedPath) : p.Title;

            var attachment = new Attachment
            {
                ContentType = contentType,
                Title = title,
                Data = bytes,                          // The SDK will base64 encode for wire format
                Size = bytes.Length,
                CreationElement = p.Creation.HasValue ? new FhirDateTime(p.Creation.Value) : null
            };

            // 2) Build DocumentReference skeleton
            var docRef = new DocumentReference
            {
                Status = ParseDocRefStatus(p.Status),
                DocStatus = ParseDocStatus(p.DocStatus),
                DateElement = p.Date.HasValue ? new Instant(p.Date.Value) : null,
                Type = ccType.ToFhirCodeableConcept(),
                Content = new List<DocumentReference.ContentComponent>
                {
                    new DocumentReference.ContentComponent { Attachment = attachment }
                },
                Category = p.Category,
            };

            // 3) Subject (Patient), Author, Authenticator
            if (!string.IsNullOrWhiteSpace(p.PatientReference))
                docRef.Subject = new ResourceReference(p.PatientReference);
            if (!string.IsNullOrWhiteSpace(p.AuthorReference))
                docRef.Author = new List<ResourceReference> { new ResourceReference(p.AuthorReference) };
            if (!string.IsNullOrWhiteSpace(p.AuthenticatorReference))
                docRef.Authenticator = new ResourceReference(p.AuthenticatorReference);

            // 4) Identifiers (optional)
            if (p.Identifiers is { Count: > 0 })
            {
                docRef.Identifier = new List<Identifier>();
                foreach (var id in p.Identifiers!)
                {
                    docRef.Identifier.Add(new Identifier(system: "urn:aria:doc", value: id));
                }
            }

            // 5) Add a SHA256 hash of the content as an identifier for traceability (optional but handy)
            docRef.Identifier ??= new List<Identifier>();
            docRef.Identifier.Add(new Identifier(system: "urn:hash:sha256", value: HashHelper.Sha256Hex(bytes)));

            // 6) Varian extensions (all opt-in; nothing is emitted unless a flag is set)
            AddExtensions(docRef, p);

            // 7) Create via resource client
            var docClient = configurator.ForResource<DocumentReference>(ct);
            var created = await docClient.CreateAsync(docRef).ConfigureAwait(false);

            logger.LogInformation("DocumentReference created with id: {Id}", PhiMask.Mask(created?.Id ?? ""));
            return created!;
        }

        /// <summary>Builds Varian extensions, then assigns them to the resource when any apply.</summary>
        private static void AddExtensions(DocumentReference doc, DocumentReferenceCreateParams p)
        {
            var ext = BuildVarianExtensions(p);
            if (ext.Count > 0)
                doc.Extension = ext;
        }

        /// <summary>
        /// Builds the list of Varian-specific extensions requested by <paramref name="p"/>.
        /// </summary>
        /// <remarks>
        /// An extension is emitted when its individual flag is set <b>or</b> when
        /// <see cref="DocumentReferenceCreateParams.IncludeAllVarianExtensions"/> is set and the
        /// corresponding value is present. Setting an individual flag without its value throws
        /// <see cref="ArgumentException"/> (explicit, unfulfillable intent); the bulk flag skips
        /// extensions whose values are missing.
        /// </remarks>
        /// <param name="p">The create parameters describing which extensions to emit.</param>
        /// <returns>The list of extensions to attach (possibly empty).</returns>
        internal static List<Extension> BuildVarianExtensions(DocumentReferenceCreateParams p)
        {
            var ext = new List<Extension>();

            AddVarianExtension(
                ext,
                explicitFlag: p.IncludeSupervisorExtension,
                bulkFlag: p.IncludeAllVarianExtensions,
                hasValue: !string.IsNullOrWhiteSpace(p.SupervisorReference),
                missingMessage: "IncludeSupervisorExtension is true, but SupervisorReference was not provided.",
                build: () => new Extension(
                    "http://varian.com/fhir/v1/StructureDefinition/documentreference-supervisor",
                    CreateRef(p.SupervisorReference!, p.SupervisorDisplay)));

            AddVarianExtension(
                ext,
                explicitFlag: p.IncludeAuthenticatedDateExtension,
                bulkFlag: p.IncludeAllVarianExtensions,
                hasValue: p.AuthenticatedDate.HasValue,
                missingMessage: "IncludeAuthenticatedDateExtension is true, but AuthenticatedDate was not provided.",
                build: () => new Extension(
                    "http://varian.com/fhir/v1/StructureDefinition/documentreference-authenticated",
                    new FhirDateTime(p.AuthenticatedDate!.Value)));

            AddVarianExtension(
                ext,
                explicitFlag: p.IncludeTemplateNameExtension,
                bulkFlag: p.IncludeAllVarianExtensions,
                hasValue: !string.IsNullOrWhiteSpace(p.TemplateName),
                missingMessage: "IncludeTemplateNameExtension is true, but TemplateName was not provided.",
                build: () => new Extension(
                    "http://varian.com/fhir/v1/StructureDefinition/documentreference-templateName",
                    new FhirString(p.TemplateName)));

            AddVarianExtension(
                ext,
                explicitFlag: p.IncludeLoginInstitutionExtension,
                bulkFlag: p.IncludeAllVarianExtensions,
                hasValue: !string.IsNullOrWhiteSpace(p.InstitutionReference),
                missingMessage: "IncludeLoginInstitutionExtension is true, but InstitutionReference was not provided.",
                build: () => new Extension(
                    "http://varian.com/fhir/v1/StructureDefinition/login-institution",
                    CreateRef(p.InstitutionReference!, p.InstitutionDisplay)));

            AddVarianExtension(
                ext,
                explicitFlag: p.IncludeDocumentLocationExtension,
                bulkFlag: p.IncludeAllVarianExtensions,
                hasValue: !string.IsNullOrWhiteSpace(p.DocumentLocation),
                missingMessage: "IncludeDocumentLocationExtension is true, but DocumentLocation was not provided.",
                build: () => new Extension(
                    "http://varian.com/fhir/v1/StructureDefinition/documentreference-documentLocation",
                    new FhirString(p.DocumentLocation)));

            return ext;
        }

        /// <summary>
        /// Adds an extension when explicitly requested (throwing if its value is missing) or when
        /// bulk-enabled and the value is present.
        /// </summary>
        private static void AddVarianExtension(
            List<Extension> ext,
            bool explicitFlag,
            bool bulkFlag,
            bool hasValue,
            string missingMessage,
            Func<Extension> build)
        {
            if (explicitFlag)
            {
                if (!hasValue)
                    throw new ArgumentException(missingMessage, "p");

                ext.Add(build());
            }
            else if (bulkFlag && hasValue)
            {
                ext.Add(build());
            }
        }

        private static ResourceReference CreateRef(string reference, string? display = null)
        {
            var r = new ResourceReference(reference);
            if (!string.IsNullOrWhiteSpace(display))
                r.Display = display;
            return r;
        }

        private static DocumentReferenceStatus ParseDocRefStatus(string? s)
        {
            return (s ?? "current").ToLowerInvariant() switch
            {
                "current" => DocumentReferenceStatus.Current,
                "entered-in-error" => DocumentReferenceStatus.EnteredInError,
                "superseded" => DocumentReferenceStatus.Superseded,
                _ => DocumentReferenceStatus.Current
            };
        }

        private static CompositionStatus? ParseDocStatus(string? s)
        {
            return (s ?? "final").ToLowerInvariant() switch
            {
                "preliminary" => CompositionStatus.Preliminary,
                "final" => CompositionStatus.Final,
                "entered-in-error" => CompositionStatus.EnteredInError,
                "amended" => CompositionStatus.Amended,
                _ => CompositionStatus.Final
            };
        }
    }
}
