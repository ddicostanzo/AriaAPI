// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AriaAPI.Core
{
    /// <summary>
    /// Captures the raw request and response bodies for the current async context.
    /// <para>
    /// <b>Memory warning:</b> Captured bodies are stored in <see cref="AsyncLocal{T}"/>
    /// storage and persist for the lifetime of the async flow unless explicitly cleared
    /// via <see cref="Clear"/>. In long-running processes or tight loops, call
    /// <see cref="Clear"/> after consuming the captured data to avoid memory pressure.
    /// </para>
    /// </summary>
    internal sealed class RawCaptureHandler : DelegatingHandler
    {

        private static readonly AsyncLocal<Capture> _current = new();

        private static Capture CurrentCapture => _current.Value ??= new Capture();

        public static string? LastRequestBody => _current.Value?.LastRequestBody;
        public static string? LastResponseBody => _current.Value?.LastResponseBody;

        /// <summary>
        /// Clears captured request and response bodies for the current async context,
        /// releasing the associated memory.
        /// </summary>
        public static void Clear()
        {
            if (_current.Value is { } capture)
            {
                capture.LastRequestBody = null;
                capture.LastResponseBody = null;
            }
        }

        public event EventHandler<(HttpRequestMessage Request, string? RequestBody)>? OnRequestCaptured;
        public event EventHandler<(HttpResponseMessage Response, string? ResponseBody)>? OnResponseCaptured;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Capture request body (non-destructively). Buffer the content so it can be read here
            // AND re-read by downstream handlers without mutating it — this preserves the original
            // Content-Type/charset headers and exact bytes (binary payloads are not corrupted).
            string? reqBody = null;
            if (request.Content is not null)
            {
                await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                reqBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            CurrentCapture.LastRequestBody = reqBody;
            OnRequestCaptured?.Invoke(this, (request, reqBody));

            // Send
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Capture response body (non-destructively) via the same buffer-then-read approach,
            // leaving the response Content (and its headers) intact for the FHIR deserializer.
            string? respBody = null;
            if (response.Content is not null)
            {
                await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                respBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            CurrentCapture.LastResponseBody = respBody;
            OnResponseCaptured?.Invoke(this, (response, respBody));

            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Clear();
            }
            base.Dispose(disposing);
        }

        private sealed class Capture
        {
            public string? LastRequestBody { get; set; }
            public string? LastResponseBody { get; set; }
        }

    }
}
