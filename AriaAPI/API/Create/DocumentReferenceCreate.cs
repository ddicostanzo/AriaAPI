// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AriaAPI.API.Create;
using AriaAPI.API.IdentityResolvers;
using AriaAPI.API.SearchHelpers;
using AriaAPI.Core;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;
using CodeableConcept = Hl7.Fhir.Model.CodeableConcept;

namespace AriaAPI.API.DocumentReferenceCreate
{
    /// <summary>
    /// Entry point to create DocumentReference resources with an embedded Attachment.
    /// </summary>
    public static class DocumentReferenceCreate
    {
        /// <summary>
        /// Parameters required to create a DocumentReference with an embedded Attachment.
        /// </summary>
        public sealed class DocumentReferenceCreateParams
        {
            /// <summary>FHIR reference to patient (e.g., "Patient/123").</summary>
            public string? PatientReference { get; init; }

            /// <summary>Optional display for patient.</summary>
            public string? PatientDisplay { get; init; }

            /// <summary>FHIR reference to author.</summary>
            public string? AuthorReference { get; init; }

            /// <summary>Optional display for author.</summary>
            public string? AuthorDisplay { get; init; }

            /// <summary>FHIR reference to authenticator (preferred Practitioner).</summary>
            public string? AuthenticatorReference { get; init; }

            /// <summary>Optional display for authenticator.</summary>
            public string? AuthenticatorDisplay { get; init; }

            /// <summary>FHIR reference to custodian organization.</summary>
            public string? CustodianReference { get; init; }

            /// <summary>Optional display for custodian.</summary>
            public string? CustodianDisplay { get; init; }

            /// <summary>Organization used for document type resolution.</summary>
            public string? DocumentTypeResolverOrganizationReference { get; init; }

            /// <summary>DocumentReference.status.</summary>
            public string Status { get; init; } = "current";

            /// <summary>DocumentReference.docStatus.</summary>
            public string? DocStatus { get; init; } = "final";

            /// <summary>Document type enum.</summary>
            public SearchTypes.DocumentType? Type { get; init; }

            /// <summary>Document creation date.</summary>
            public DateTime? Date { get; init; }

            /// <summary>Document description.</summary>
            public string? Description { get; init; }

            /// <summary>Optional identifiers.</summary>
            public List<string>? Identifiers { get; init; }

            /// <summary>Attachment title.</summary>
            public string? Title { get; init; }

            /// <summary>File path to embed.</summary>
            public string SourceFilePath { get; init; } = string.Empty;

            /// <summary>Attachment creation timestamp.</summary>
            public DateTime? Creation { get; init; }

            /// <summary>Document categories.</summary>
            public List<CodeableConcept> Category { get; init; } =
                new()
                {
                    new CodeableConcept
                    {
                        Coding =
                        {
                            new Coding(
                                "http://varian.com/fhir/CodeSystem/DocumentReference/documentreference-class",
                                "Patient Document",
                                "Patient Document")
                        }
                    }
                };

            /// <summary>Supervisor reference (Varian extension).</summary>
            public string? SupervisorReference { get; init; }

            /// <summary>Supervisor display.</summary>
            public string? SupervisorDisplay { get; init; }

            /// <summary>Authenticated timestamp (Varian extension).</summary>
            public DateTime? AuthenticatedDate { get; init; }

            /// <summary>Template name (Varian extension).</summary>
            public string? TemplateName { get; init; }

            /// <summary>Institution reference (Varian extension).</summary>
            public string? InstitutionReference { get; init; }

            /// <summary>Institution display.</summary>
            public string? InstitutionDisplay { get; init; }

            /// <summary>Document storage location (Varian extension).</summary>
            public string? DocumentLocation { get; init; }


            /// <summary>
            /// Enables Varian supervisor extension.
            /// Default false so package consumers can opt in after validation.
            /// </summary>
            public bool IncludeSupervisorExtension { get; init; } = false;

            /// <summary>
            /// Enables Varian authenticated timestamp extension.
            /// Default false so package consumers can opt in after validation.
            /// </summary>
            public bool IncludeAuthenticatedDateExtension { get; init; } = false;

            /// <summary>
            /// Enables Varian template name extension.
            /// Default false so package consumers can opt in after validation.
            /// </summary>
            public bool IncludeTemplateNameExtension { get; init; } = false;

            /// <summary>
            /// Enables Varian login institution extension.
            /// Default false so package consumers can opt in after validation.
            /// </summary>
            public bool IncludeLoginInstitutionExtension { get; init; } = false;

            /// <summary>
            /// Enables Varian document location extension.
            /// Default false so package consumers can opt in after validation.
            /// </summary>
            public bool IncludeDocumentLocationExtension { get; init; } = false;

            /// <summary>
            /// Convenience flag to enable all currently supported Varian extensions.
            /// Individual flags are still checked with their corresponding values.
            /// </summary>
            public bool IncludeAllVarianExtensions { get; init; } = false;

            /// <summary>
            /// When <see langword="true"/>, the created DocumentReference is serialized to FHIR
            /// JSON and written to the logger at <see cref="LogLevel.Debug"/> so the resource can
            /// be verified against the vendor's Aria tooling.
            /// <para>
            /// <b>PHI warning:</b> the serialized resource contains Protected Health Information
            /// (patient/practitioner references, identifiers, and — when
            /// <see cref="IncludeAttachmentDataInJsonLog"/> is set — the full document bytes).
            /// Enable only in controlled environments and route Debug logs to a secure sink.
            /// Defaults to <see langword="false"/>.
            /// </para>
            /// </summary>
            public bool LogResourceJson { get; init; } = false;

            /// <summary>
            /// Controls whether the base64 <c>Attachment.data</c> payload is included when
            /// <see cref="LogResourceJson"/> serializes the resource. When <see langword="false"/>
            /// (the default) the data is redacted with a placeholder noting its size, keeping the
            /// log small while preserving the resource structure (type, category, identifiers, and
            /// Varian extensions) for verification.
            /// <para><b>PHI warning:</b> setting this to <see langword="true"/> writes the full
            /// document contents to the log in plain text.</para>
            /// </summary>
            public bool IncludeAttachmentDataInJsonLog { get; init; } = false;

        }

        /// <summary>Default max file size = 10 MB.</summary>
        public const long DefaultMaxFileSizeBytes = 10485760L;
        

        /// <summary>
        /// Creates a DocumentReference from a file.
        /// </summary>
        public static async Task<DocumentReference> CreateFromFileAsync(
            ClientConfigurator configurator,
            DocumentReferenceCreateParams p,
            ILogger logger,
            long maxFileSizeBytes = DefaultMaxFileSizeBytes,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(configurator);
            ArgumentNullException.ThrowIfNull(p);
            ArgumentNullException.ThrowIfNull(logger);

            if (string.IsNullOrWhiteSpace(p.SourceFilePath))
                throw new FileNotFoundException("SourceFilePath must be specified.", p.SourceFilePath);

            if (!p.Type.HasValue)
                throw new ArgumentException("Document Type is required.", nameof(p));

            string path = Path.GetFullPath(p.SourceFilePath);

            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}", path);

            FileInfo fi = new(path);

            if (fi.Length > maxFileSizeBytes)
                throw new InvalidOperationException($"File too large: {fi.Length}");

            ct.ThrowIfCancellationRequested();

            string resolverRef = GetResolverOrganizationReference(p);
            string resolverId = ExtractId(resolverRef);


            var service = await DocumentTypeConceptService.CreateAsync(
                    configurator,
                    publisher: resolverId,
                    listReturnLimit: 250
                ).ConfigureAwait(false);

            var ccType = service.Resolve(p.Type.Value);

            byte[] bytes = await File.ReadAllBytesAsync(path, ct);
            string contentType = CreateHelpers.ContentTypeHelper.MapFromExtension(Path.GetExtension(path));

            var attachment = new Attachment
            {
                ContentType = contentType,
                Title = p.Title ?? Path.GetFileName(path),
                Data = bytes,
                Size = bytes.Length,
                CreationElement = p.Creation.HasValue ? new FhirDateTime(p.Creation.Value) : null
            };

            var doc = new DocumentReference
            {
                Status = ParseStatus(p.Status),
                DocStatus = ParseDocStatus(p.DocStatus),
                DateElement = p.Date.HasValue ? new Instant(p.Date.Value) : null,
                Type = ccType.ToFhirCodeableConcept(),
                Description = p.Description,
                Content = new List<DocumentReference.ContentComponent>
                {
                    new() { Attachment = attachment }
                },
                Category = p.Category
            };

            if (!string.IsNullOrWhiteSpace(p.PatientReference))
                doc.Subject = CreateRef(p.PatientReference, p.PatientDisplay);

            AddAuthor(doc, p);

            if (!string.IsNullOrWhiteSpace(p.AuthenticatorReference))
                doc.Authenticator = CreateRef(p.AuthenticatorReference, p.AuthenticatorDisplay);

            if (!string.IsNullOrWhiteSpace(p.CustodianReference))
                doc.Custodian = CreateRef(p.CustodianReference, p.CustodianDisplay);

            AddIdentifiers(doc, p, bytes);

            AddExtensions(doc, p);

            var created = await configurator
                .ForResource<DocumentReference>(ct)
                .CreateAsync(doc);

            if (p.LogResourceJson && logger.IsEnabled(LogLevel.Debug) && created != null)
            {
                logger.LogDebug(
                    "DocumentReference JSON for verification: {Json}",
                    SerializeForVerification(created, p.IncludeAttachmentDataInJsonLog));
            }

            logger.LogInformation("Created DocumentReference {Id}", PhiMask.Mask(created?.Id ?? ""));

            return created ?? throw new InvalidOperationException("Failed to create DocumentReference");
        }

        /// <summary>Adds author or fallback.</summary>
        private static void AddAuthor(DocumentReference doc, DocumentReferenceCreateParams p)
        {
            if (!string.IsNullOrWhiteSpace(p.AuthorReference))
            {
                doc.Author = new List<ResourceReference>
                {
                    CreateRef(p.AuthorReference, p.AuthorDisplay)
                };
            }
            else if (!string.IsNullOrWhiteSpace(p.AuthenticatorReference))
            {
                doc.Author = new List<ResourceReference>
                {
                    CreateRef(p.AuthenticatorReference, p.AuthenticatorDisplay)
                };
            }
        }

        /// <summary>Add identifiers + SHA256.</summary>
        private static void AddIdentifiers(DocumentReference doc, DocumentReferenceCreateParams p, byte[] bytes)
        {
            doc.Identifier ??= new List<Identifier>();

            if (p.Identifiers != null)
            {
                foreach (var id in p.Identifiers)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                        doc.Identifier.Add(new Identifier("urn:aria:doc", id));
                }
            }

            doc.Identifier.Add(new Identifier("urn:hash:sha256", CreateHelpers.HashHelper.Sha256Hex(bytes)));
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

        /// <summary>
        /// Serializes a DocumentReference to FHIR JSON for out-of-band verification.
        /// </summary>
        /// <remarks>
        /// When <paramref name="includeAttachmentData"/> is <see langword="false"/> the base64
        /// <c>Attachment.data</c> payload is redacted on a deep copy (the original is never
        /// mutated) so the structure — including Varian extensions — can be inspected without
        /// dumping document bytes.
        /// </remarks>
        /// <param name="doc">The resource to serialize.</param>
        /// <param name="includeAttachmentData">Whether to retain the base64 attachment data.</param>
        /// <returns>Pretty-printed FHIR JSON.</returns>
        internal static string SerializeForVerification(DocumentReference doc, bool includeAttachmentData)
        {
            ArgumentNullException.ThrowIfNull(doc);

            var target = doc;

            if (!includeAttachmentData)
            {
                target = (DocumentReference)doc.DeepCopy();

                if (target.Content != null)
                {
                    foreach (var content in target.Content)
                    {
                        if (content.Attachment?.Data is { } data)
                        {
                            content.Attachment.Data = null;
                            content.Attachment.AddExtension(
                                "urn:aria:redacted-attachment-data",
                                new FhirString($"<redacted {data.Length} bytes>"));
                        }
                    }
                }
            }

            var options = new JsonSerializerOptions().ForFhir(typeof(DocumentReference).Assembly);
            options.WriteIndented = true;
            return JsonSerializer.Serialize(target, options);
        }

        private static ResourceReference CreateRef(string reference, string? display = null)
        {
            var r = new ResourceReference(reference);
            if (!string.IsNullOrWhiteSpace(display))
                r.Display = display;
            return r;
        }

        private static string ExtractId(string reference)
        {
            var parts = reference.Split('/');
            if (parts.Length < 2)
                throw new ArgumentException("Invalid FHIR reference");
            return parts[1];
        }

        internal static DocumentReferenceStatus ParseStatus(string? s) =>
            (s ?? "current").Trim().ToLowerInvariant() switch
            {
                "current" => DocumentReferenceStatus.Current,
                "entered-in-error" => DocumentReferenceStatus.EnteredInError,
                "superseded" => DocumentReferenceStatus.Superseded,
                _ => throw new ArgumentException(
                    $"Invalid DocumentReference status '{s}'. Valid values: current, entered-in-error, superseded.",
                    nameof(s))
            };

        internal static CompositionStatus? ParseDocStatus(string? s) =>
            (s ?? "final").Trim().ToLowerInvariant() switch
            {
                "preliminary" => CompositionStatus.Preliminary,
                "final" => CompositionStatus.Final,
                "entered-in-error" => CompositionStatus.EnteredInError,
                "amended" => CompositionStatus.Amended,
                _ => throw new ArgumentException(
                    $"Invalid DocumentReference docStatus '{s}'. Valid values: preliminary, final, entered-in-error, amended.",
                    nameof(s))
            };


        private static string GetResolverOrganizationReference(DocumentReferenceCreateParams p)
        {
            var resolverRef =
                p.DocumentTypeResolverOrganizationReference ??
                p.CustodianReference ??
                p.InstitutionReference;

            if (string.IsNullOrWhiteSpace(resolverRef))
                throw new ArgumentException(
                    "DocumentTypeResolverOrganizationReference, CustodianReference, or InstitutionReference is required for document type resolution.",
                    nameof(p));

            if (!resolverRef.StartsWith("Organization/", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Document type resolver must be an Organization reference, got: {resolverRef}",
                    nameof(p));

            return resolverRef;
        }
    }
}
