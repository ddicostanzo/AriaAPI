// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
using AriaAPI.Core;
using RichardSzalay.MockHttp;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace AriaAPI.Tests.Core
{
    /// <summary>
    /// Tests for <see cref="RawCaptureHandler"/>. Relies on the
    /// <c>InternalsVisibleTo("AriaAPI.Tests")</c> declaration in the library csproj.
    /// </summary>
    /// <remarks>
    /// Assertions use the synchronous capture events rather than the AsyncLocal-backed static accessors,
    /// because an AsyncLocal value set inside the handler's <c>SendAsync</c> does not reliably propagate
    /// back to the caller after an awaited suspension.
    /// </remarks>
    public sealed class RawCaptureHandlerTests
    {
        private const string TestUrl = "http://test.local/resource";

        private static HttpClient BuildClient(
            RawCaptureHandler handler,
            string responseBody = "{\"resourceType\":\"Bundle\"}",
            string mediaType = "application/fhir+json",
            HttpStatusCode status = HttpStatusCode.OK)
        {
            var mock = new MockHttpMessageHandler();
            mock.When(TestUrl).Respond(status, mediaType, responseBody);
            handler.InnerHandler = mock;
            return new HttpClient(handler);
        }

        [Fact]
        public async Task SendAsync_RaisesResponseCapturedEvent_WithBody()
        {
            var handler = new RawCaptureHandler();
            string? captured = null;
            handler.OnResponseCaptured += (_, e) => captured = e.ResponseBody;
            using var client = BuildClient(handler, responseBody: "{\"resourceType\":\"Bundle\",\"id\":\"abc\"}");

            await client.GetAsync(TestUrl);

            Assert.Equal("{\"resourceType\":\"Bundle\",\"id\":\"abc\"}", captured);
        }

        [Fact]
        public async Task SendAsync_RaisesRequestCapturedEvent_WithBody()
        {
            var handler = new RawCaptureHandler();
            string? captured = null;
            handler.OnRequestCaptured += (_, e) => captured = e.RequestBody;
            using var client = BuildClient(handler);

            await client.PostAsync(TestUrl, new StringContent("{\"resourceType\":\"Patient\"}"));

            Assert.Equal("{\"resourceType\":\"Patient\"}", captured);
        }

        [Fact]
        public async Task SendAsync_ResponseBodyReturnedUnmodified()
        {
            var handler = new RawCaptureHandler();
            using var client = BuildClient(handler, responseBody: "{\"id\":\"1\"}");

            var response = await client.GetAsync(TestUrl);
            var content = await response.Content.ReadAsStringAsync();

            Assert.Equal("{\"id\":\"1\"}", content);
        }

        [Fact]
        public async Task SendAsync_RequestBodyForwardedUnmodifiedToInner()
        {
            var handler = new RawCaptureHandler();
            var mock = new MockHttpMessageHandler();
            mock.Expect(HttpMethod.Post, TestUrl)
                .WithContent("{\"resourceType\":\"Task\"}")
                .Respond("application/fhir+json", "{}");
            handler.InnerHandler = mock;
            using var client = new HttpClient(handler);

            await client.PostAsync(TestUrl, new StringContent("{\"resourceType\":\"Task\"}"));

            mock.VerifyNoOutstandingExpectation();
        }

        [Fact]
        public async Task SendAsync_PreservesResponseContentType()
        {
            // Capture must not rewrite the response Content-Type (regression: rebuilding as
            // StringContent reset it to text/plain, breaking the FHIR deserializer).
            var handler = new RawCaptureHandler();
            using var client = BuildClient(handler, mediaType: "application/fhir+json");

            var response = await client.GetAsync(TestUrl);

            Assert.Equal("application/fhir+json", response.Content.Headers.ContentType?.MediaType);
        }

        [Fact]
        public async Task SendAsync_DoesNotCorruptBinaryResponseBody()
        {
            // Bytes that are not valid UTF-8 must survive the handler unchanged (regression:
            // round-tripping through a UTF-8 string replaced invalid sequences with U+FFFD).
            var payload = new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x80, 0x7F };
            var mock = new MockHttpMessageHandler();
            mock.When(TestUrl).Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });

            var handler = new RawCaptureHandler { InnerHandler = mock };
            using var client = new HttpClient(handler);

            var response = await client.GetAsync(TestUrl);
            var roundTripped = await response.Content.ReadAsByteArrayAsync();

            Assert.Equal(payload, roundTripped);
        }
    }
}
