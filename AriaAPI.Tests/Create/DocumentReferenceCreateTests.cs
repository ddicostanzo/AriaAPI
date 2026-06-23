// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
using AriaAPI.API.DocumentReferenceCreate;
using AriaAPI.Core;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace AriaAPI.Tests.Create
{
    /// <summary>
    /// Tests for validation guards in <see cref="DocumentReferenceCreate.CreateFromFileAsync"/>.
    /// All cases throw before any FHIR call is made.
    /// </summary>
    public sealed class DocumentReferenceCreateTests
    {
        private static readonly NullLogger<DocumentReferenceCreateTests> Logger = NullLogger<DocumentReferenceCreateTests>.Instance;

        /// <summary>
        /// Returns a non-null <see cref="ClientConfigurator"/> whose fields are uninitialized.
        /// Safe to use only in tests where the guard fires before the configurator is dereferenced.
        /// </summary>
        private static ClientConfigurator UninitializedConfigurator() =>
            (ClientConfigurator)RuntimeHelpers.GetUninitializedObject(typeof(ClientConfigurator));

        [Fact]
        public async Task CreateFromFileAsync_NullConfigurator_ThrowsArgumentNullException()
        {
            var p = new DocumentReferenceCreate.DocumentReferenceCreateParams
            {
                SourceFilePath = "some/path.pdf"
            };

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                DocumentReferenceCreate.CreateFromFileAsync(null!, p, Logger));

            Assert.Equal("configurator", ex.ParamName);
        }

        [Fact]
        public async Task CreateFromFileAsync_NullParams_ThrowsArgumentNullException()
        {
            var configurator = UninitializedConfigurator();

            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                DocumentReferenceCreate.CreateFromFileAsync(configurator, null!, Logger));

            Assert.Equal("p", ex.ParamName);
        }

        [Fact]
        public async Task CreateFromFileAsync_EmptySourceFilePath_ThrowsFileNotFoundException()
        {
            var configurator = UninitializedConfigurator();
            var p = new DocumentReferenceCreate.DocumentReferenceCreateParams
            {
                SourceFilePath = string.Empty
            };

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                DocumentReferenceCreate.CreateFromFileAsync(configurator, p, Logger));
        }

        [Fact]
        public async Task CreateFromFileAsync_NonExistentFilePath_ThrowsFileNotFoundException()
        {
            var configurator = UninitializedConfigurator();
            var p = new DocumentReferenceCreate.DocumentReferenceCreateParams
            {
                SourceFilePath = "/this/path/does/not/exist/file.pdf"
            };

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                DocumentReferenceCreate.CreateFromFileAsync(configurator, p, Logger));
        }

        [Fact]
        public async Task CreateFromFileAsync_FileTooLarge_ThrowsInvalidOperationException()
        {
            var configurator = UninitializedConfigurator();
            var tmpFile = Path.GetTempFileName();
            try
            {
                // Write 100 bytes, set limit to 10 bytes
                await File.WriteAllBytesAsync(tmpFile, new byte[100]);

                var p = new DocumentReferenceCreate.DocumentReferenceCreateParams
                {
                    SourceFilePath = tmpFile
                };

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    DocumentReferenceCreate.CreateFromFileAsync(configurator, p, Logger, maxFileSizeBytes: 10));
            }
            finally
            {
                File.Delete(tmpFile);
            }
        }

        [Fact]
        public async Task CreateFromFileAsync_MissingAuthenticatorReference_ThrowsArgumentException()
        {
            var configurator = UninitializedConfigurator();
            var tmpFile = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(tmpFile, new byte[10]);

                var p = new DocumentReferenceCreate.DocumentReferenceCreateParams
                {
                    SourceFilePath = tmpFile,
                    AuthenticatorReference = string.Empty
                };

                await Assert.ThrowsAsync<ArgumentException>(() =>
                    DocumentReferenceCreate.CreateFromFileAsync(configurator, p, Logger));
            }
            finally
            {
                File.Delete(tmpFile);
            }
        }

        [Fact]
        public async Task CreateFromFileAsync_MalformedAuthenticatorReference_ThrowsArgumentException()
        {
            var configurator = UninitializedConfigurator();
            var tmpFile = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(tmpFile, new byte[10]);

                var p = new DocumentReferenceCreate.DocumentReferenceCreateParams
                {
                    SourceFilePath = tmpFile,
                    AuthenticatorReference = "NoSlashHere"
                };

                await Assert.ThrowsAsync<ArgumentException>(() =>
                    DocumentReferenceCreate.CreateFromFileAsync(configurator, p, Logger));
            }
            finally
            {
                File.Delete(tmpFile);
            }
        }

        [Fact]
        public async Task CreateFromFileAsync_PathWithTraversal_ResolvesToFullPath()
        {
            // Arrange - a path with traversal sequences that won't exist
            var traversalPath = "/tmp/../tmp/../nonexistent/file.pdf";
            var expectedResolvedPath = Path.GetFullPath(traversalPath);

            var configurator = UninitializedConfigurator();
            var p = new DocumentReferenceCreate.DocumentReferenceCreateParams
            {
                SourceFilePath = traversalPath,
                AuthenticatorReference = "Organization/Test",
                Type = AriaAPI.API.SearchHelpers.SearchTypes.DocumentType.AdvanceDirective
            };

            // Act & Assert - should throw FileNotFoundException with the resolved path
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
                DocumentReferenceCreate.CreateFromFileAsync(configurator, p, Logger));

            // The exception should reference the resolved path, not the traversal path
            Assert.Equal(expectedResolvedPath, ex.FileName);
        }

        private const string SupervisorExtensionUrl =
            "http://varian.com/fhir/v1/StructureDefinition/documentreference-supervisor";

        [Fact]
        public void BuildVarianExtensions_SupervisorFlagWithReference_AddsSupervisorExtension()
        {
            var p = new DocumentReferenceCreate.DocumentReferenceCreateParams
            {
                IncludeSupervisorExtension = true,
                SupervisorReference = "Practitioner/123",
                SupervisorDisplay = "Dr. Smith"
            };

            var ext = DocumentReferenceCreate.BuildVarianExtensions(p);

            var supervisor = Assert.Single(ext);
            Assert.Equal(SupervisorExtensionUrl, supervisor.Url);
            var reference = Assert.IsType<ResourceReference>(supervisor.Value);
            Assert.Equal("Practitioner/123", reference.Reference);
            Assert.Equal("Dr. Smith", reference.Display);
        }

        [Fact]
        public void BuildVarianExtensions_SupervisorFlagWithoutReference_ThrowsArgumentException()
        {
            var p = new DocumentReferenceCreate.DocumentReferenceCreateParams
            {
                IncludeSupervisorExtension = true
            };

            Assert.Throws<ArgumentException>(() => DocumentReferenceCreate.BuildVarianExtensions(p));
        }

        [Fact]
        public void BuildVarianExtensions_IncludeAllWithOnlySupervisor_ReturnsOnlySupervisor()
        {
            // IncludeAllVarianExtensions must be best-effort: it should not throw just because
            // the other four extension values are absent.
            var p = new DocumentReferenceCreate.DocumentReferenceCreateParams
            {
                IncludeAllVarianExtensions = true,
                SupervisorReference = "Practitioner/123"
            };

            var ext = DocumentReferenceCreate.BuildVarianExtensions(p);

            var supervisor = Assert.Single(ext);
            Assert.Equal(SupervisorExtensionUrl, supervisor.Url);
        }

        [Fact]
        public void BuildVarianExtensions_NoFlags_ReturnsEmpty()
        {
            // A reference present without any opt-in flag must not emit an extension.
            var p = new DocumentReferenceCreate.DocumentReferenceCreateParams
            {
                SupervisorReference = "Practitioner/123"
            };

            Assert.Empty(DocumentReferenceCreate.BuildVarianExtensions(p));
        }

        [Fact]
        public void SerializeForVerification_RedactsAttachmentDataByDefault()
        {
            var doc = new DocumentReference
            {
                Content = new List<DocumentReference.ContentComponent>
                {
                    new DocumentReference.ContentComponent
                    {
                        Attachment = new Attachment
                        {
                            Data = new byte[] { 1, 2, 3, 4 },
                            ContentType = "application/pdf"
                        }
                    }
                },
                Extension = new List<Extension>
                {
                    new Extension(SupervisorExtensionUrl, new ResourceReference("Practitioner/123"))
                }
            };

            var json = DocumentReferenceCreate.SerializeForVerification(doc, includeAttachmentData: false);

            Assert.Contains("urn:aria:redacted-attachment-data", json);
            Assert.DoesNotContain("\"data\"", json);
            // The structure under verification (supervisor extension) is preserved.
            Assert.Contains("documentreference-supervisor", json);
            // The original resource is never mutated.
            Assert.NotNull(doc.Content[0].Attachment.Data);
        }

        [Fact]
        public void SerializeForVerification_IncludesAttachmentDataWhenRequested()
        {
            var doc = new DocumentReference
            {
                Content = new List<DocumentReference.ContentComponent>
                {
                    new DocumentReference.ContentComponent
                    {
                        Attachment = new Attachment
                        {
                            Data = new byte[] { 1, 2, 3, 4 },
                            ContentType = "application/pdf"
                        }
                    }
                }
            };

            var json = DocumentReferenceCreate.SerializeForVerification(doc, includeAttachmentData: true);

            Assert.Contains("\"data\"", json);
            Assert.DoesNotContain("urn:aria:redacted-attachment-data", json);
        }

        [Fact]
        public void ParseStatus_InvalidValue_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => DocumentReferenceCreate.ParseStatus("bogus"));
        }

        [Fact]
        public void ParseStatus_NullValue_DefaultsToCurrent()
        {
            Assert.Equal(DocumentReferenceStatus.Current, DocumentReferenceCreate.ParseStatus(null));
        }

        [Fact]
        public void ParseDocStatus_InvalidValue_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => DocumentReferenceCreate.ParseDocStatus("bogus"));
        }

        [Fact]
        public void ParseDocStatus_NullValue_DefaultsToFinal()
        {
            Assert.Equal(CompositionStatus.Final, DocumentReferenceCreate.ParseDocStatus(null));
        }
    }
}
