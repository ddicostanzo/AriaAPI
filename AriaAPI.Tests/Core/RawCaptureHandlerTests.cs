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
    }
}
