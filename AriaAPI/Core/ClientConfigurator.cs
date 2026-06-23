// Copyright (c) 2025-2026 Dominic DiCostanzo. Licensed under AGPL-3.0.
﻿using AriaAPI.Security;
using Hl7.Fhir.Rest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


namespace AriaAPI.Core
{
    /// <summary>
    /// Configures and manages a single <see cref="FhirClient"/> instance over a long‑lived
    /// HTTP delegating handler chain that injects OAuth2 bearer tokens on outgoing requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ClientConfigurator"/> is intended to be registered with DI (typically as a singleton).
    /// It composes a <see cref="FhirClient"/> over:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><see cref="TransientFaultRetryHandler"/> (outermost) for exponential-backoff retries on 503, 429, and network errors.</description></item>
    ///   <item><description><see cref="BearerTokenHandler"/> for acquiring and caching OAuth2 tokens via <see cref="TokenProvider"/>.</description></item>
    ///   <item><description><see cref="CurrencySanitizerHandler"/> (custom) as an inner handler to normalize outbound requests.</description></item>
    /// </list>
    /// <para>
    /// The configurator constructs the underlying <see cref="FhirClient"/> upon creation. Call
    /// <see cref="CreateClient"/> to recreate the client (e.g., after settings changes) or
    /// <see cref="Reconfigure"/> to switch the active system (Test/Prod) without recreating the handlers.
    /// </para>
    /// </remarks>
    public sealed partial class ClientConfigurator : IDisposable
    {
        private readonly ILogger<ClientConfigurator> _logger;
        private readonly FhirClientSettings _settings;
        private readonly TokenProvider _tokenProvider;
        private readonly int _maxConnectionsPerServer;
        private readonly bool _captureRawBodies;

        private Uri _baseUrl;
        private string _scope;

        private readonly object _syncRoot = new();

        private BearerTokenHandler? _authHandler;
        private TransientFaultRetryHandler? _retryHandler;
        private CurrencySanitizerHandler? _currencyHandler;
        private RawCaptureHandler? _rawCaptureHandler;
        private FhirClient? _fhirClient;
        private bool _disposed;

        private readonly object _rawCaptureLock = new();
        private string? _lastRawRequestBody;
        private string? _lastRawResponseBody;

        /// <summary>
        /// Raised after the raw body of an outgoing HTTP request has been captured.
        /// Only fires when raw body capture is enabled (see <see cref="FhirOptions.CaptureRawBodies"/>).
        /// </summary>
        /// <remarks>
        /// <b>HIPAA warning:</b> the captured body may contain unmasked PHI (e.g., a FHIR bundle).
        /// Do not write it to plain-text logs or any non-PHI-safe sink.
        /// </remarks>
        public event EventHandler<(HttpRequestMessage Request, string? RequestBody)>? OnRawRequestCaptured;

        /// <summary>
        /// Raised after the raw body of an incoming HTTP response has been captured.
        /// Only fires when raw body capture is enabled (see <see cref="FhirOptions.CaptureRawBodies"/>).
        /// </summary>
        /// <remarks>
        /// <b>HIPAA warning:</b> the captured body may contain unmasked PHI (e.g., a FHIR bundle).
        /// Do not write it to plain-text logs or any non-PHI-safe sink.
        /// </remarks>
        public event EventHandler<(HttpResponseMessage Response, string? ResponseBody)>? OnRawResponseCaptured;

        /// <summary>
        /// Gets a value indicating whether raw HTTP body capture is enabled for this configurator.
        /// </summary>
        public bool RawBodyCaptureEnabled => _captureRawBodies;

        /// <summary>
        /// Gets the raw body of the most recently captured outgoing HTTP request,
        /// or <see langword="null"/> if capture is disabled or no request has been sent yet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reflects the last request observed across <i>all</i> concurrent async flows on this (typically
        /// singleton) configurator. For request-scoped correlation, subscribe to <see cref="OnRawRequestCaptured"/>
        /// instead, which fires synchronously at capture time.
        /// </para>
        /// <para><b>HIPAA warning:</b> the returned body may contain unmasked PHI. Treat it as sensitive.</para>
        /// </remarks>
        public string? LastRawRequestBody
        {
            get { lock (_rawCaptureLock) { return _lastRawRequestBody; } }
        }

        /// <summary>
        /// Gets the raw body of the most recently captured incoming HTTP response
        /// (e.g., the serialized FHIR bundle JSON), or <see langword="null"/> if capture is disabled
        /// or no response has been received yet.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reflects the last response observed across <i>all</i> concurrent async flows on this (typically
        /// singleton) configurator. For request-scoped correlation, subscribe to <see cref="OnRawResponseCaptured"/>
        /// instead, which fires synchronously at capture time.
        /// </para>
        /// <para><b>HIPAA warning:</b> the returned body may contain unmasked PHI. Treat it as sensitive.</para>
        /// </remarks>
        public string? LastRawResponseBody
        {
            get { lock (_rawCaptureLock) { return _lastRawResponseBody; } }
        }

        /// <summary>
        /// Clears the cached most-recent raw request and response bodies, releasing the associated memory.
        /// No-op when capture is disabled.
        /// </summary>
        public void ClearRawCapture()
        {
            lock (_rawCaptureLock)
            {
                _lastRawRequestBody = null;
                _lastRawResponseBody = null;
            }
            RawCaptureHandler.Clear();
        }

        /// <summary>
        /// Gets the configured <see cref="FhirClient"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the client has not been created yet. Call <see cref="CreateClient"/> first.
        /// </exception>
        /// <exception cref="ObjectDisposedException">Thrown when the configurator has been disposed.</exception>
        public FhirClient FhirClient
        {
            get
            {
                ThrowIfDisposed();
                if (_fhirClient is null)
                    throw new InvalidOperationException("Call CreateClient() before accessing FhirClient.");
                return _fhirClient;
            }
            private set => _fhirClient = value;
        }

        /// <summary>
        /// Initializes a new <see cref="ClientConfigurator"/> for a specific FHIR system (e.g., Test or Prod).
        /// </summary>
        /// <param name="system">
        /// The resolved <see cref="FhirSystemOptions"/> for the active system (contains BaseUrl and Auth.Scope).
        /// </param>
        /// <param name="settings">Firely <see cref="FhirClientSettings"/> applied to the constructed client.</param>
        /// <param name="tokenProvider">Token acquisition service used by the auth handler to get/refresh tokens.</param>
        /// <param name="logger">The logger instance for diagnostic and operational logging.</param>
        /// <param name="systemName">
        /// Optional system display name for logging (e.g., "Test" or "Prod"). If omitted, "(unknown)" is used.
        /// </param>
        /// <param name="maxConnectionsPerServer">
        /// Maximum number of concurrent HTTP connections per FHIR server. Defaults to 50.
        /// </param>
        /// <param name="captureRawBodies">
        /// When <see langword="true"/>, inserts a raw-body capture handler into the HTTP pipeline so the most
        /// recent request/response bodies (e.g., the FHIR bundle JSON) are exposed via
        /// <see cref="LastRawRequestBody"/>, <see cref="LastRawResponseBody"/>, and the raw-capture events.
        /// Defaults to <see langword="false"/>. <b>HIPAA warning:</b> captured bodies are not masked and may
        /// contain PHI.
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown if any required argument is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown if required system options are missing or invalid.</exception>
        public ClientConfigurator(
            FhirSystemOptions system,
            FhirClientSettings settings,
            TokenProvider tokenProvider,
            ILogger<ClientConfigurator> logger,
            string? systemName = null,
            int maxConnectionsPerServer = 50,
            bool captureRawBodies = false)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _maxConnectionsPerServer = maxConnectionsPerServer;
            _captureRawBodies = captureRawBodies;

            if (system is null) throw new ArgumentNullException(nameof(system));
            if (string.IsNullOrWhiteSpace(system.BaseUrl))
                throw new InvalidOperationException("FHIR BaseUrl is missing for the active system.");
            if (system.Auth is null)
                throw new InvalidOperationException("FHIR Auth configuration is missing for the active system.");
            if (system.Auth.Scope is null)
                throw new InvalidOperationException("FHIR Auth.Scope is missing for the active system.");

            _baseUrl = new Uri(system.BaseUrl);
            _scope = system.Auth.Scope;

            _logger.LogInformation(
                "Initializing ClientConfigurator for system {SystemName} with BaseUrl {BaseUrl}. " +
                "Settings: PreferredFormat={PreferredFormat}, UseAsync={UseAsync}, VerifyFhirVersion={VerifyFhirVersion}, TimeoutMs={Timeout}",
                systemName ?? "(unknown)",
                _baseUrl,
                _settings.PreferredFormat,
                _settings.UseAsync,
                _settings.VerifyFhirVersion,
                _settings.Timeout);

            CreateClient();
        }


        private static SocketsHttpHandler BuildSocketsHandler()
        {
            return new SocketsHttpHandler
            {
                // Transport perf
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = false,

                // Connection reuse
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),   // recycle before NATs expire
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2), // keep warm but not forever
                MaxConnectionsPerServer = 16,                          // allow parallelism
                EnableMultipleHttp2Connections = true,                 // fan-out on HTTP/2

                // TLS/HTTP/2
                // HTTP/2 is negotiated automatically via ALPN; nothing else to set here.

                // 100-continue – typically no large POSTs in searches, leave defaults
                // Expect100ContinueTimeout = TimeSpan.FromSeconds(0)
            };
        }


        /// <summary>
        /// (Re)creates the underlying <see cref="FhirClient"/> while preserving the long‑lived handlers.
        /// </summary>
        /// <remarks>
        /// Thread‑safe. Disposes any prior <see cref="FhirClient"/> instance, then creates a new one using the
        /// current <see cref="_baseUrl"/> and <see cref="_settings"/>. The token handler instance is reused.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">Thrown if the configurator has been disposed.</exception>
        public void CreateClient()
        {
            ThrowIfDisposed();

            lock (_syncRoot)
            {
                _logger.LogDebug("Creating FHIR client instance for {BaseUrl}.", _baseUrl);

                try
                {
                    // Build the long-lived delegating-handler chain exactly once. A DelegatingHandler
                    // rejects InnerHandler reassignment after it has sent its first request
                    // (InvalidOperationException), so the chain must not be re-linked on subsequent
                    // CreateClient() calls (e.g. via Reconfigure). The transport handlers are
                    // host-agnostic (the SocketsHttpHandler is host-independent and
                    // Http2NegotiationHandler only inspects the URI scheme), and the new base URL is
                    // applied through the recreated FhirClient below, so reusing the chain is safe.
                    if (_retryHandler is null)
                    {
                        BuildHandlerChain();
                    }

                    // Dispose any existing client
                    _fhirClient?.Dispose();

                    // Create Firely client
                    FhirClient = new FhirClient(_baseUrl, _settings, _retryHandler);

                    _logger.LogInformation(
                        "FHIR client created for {BaseUrl}. PreferredFormat={PreferredFormat}, TimeoutMs={Timeout}",
                        _baseUrl, _settings.PreferredFormat, _settings.Timeout);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create FHIR client for {BaseUrl}.", _baseUrl);
                    throw;
                }
            }
        }

        /// <summary>
        /// Assembles the long-lived delegating-handler chain (inner → outer) once and stores the
        /// outermost handler in <see cref="_retryHandler"/>. Must be called under <see cref="_syncRoot"/>
        /// and only when the chain has not yet been built.
        /// </summary>
        private void BuildHandlerChain()
        {
            var sockets = BuildSocketsHandler();
            var http2 = new Http2NegotiationHandler(sockets, _baseUrl);
            var defaults = new DefaultQueryParamsHandler(http2);
            _currencyHandler = new CurrencySanitizerHandler(defaults);

            HttpMessageHandler inner = new LoggingTimingHandler(_currencyHandler, _logger);

            // Optionally insert the raw-body capture handler just below the auth handler so it
            // observes the request as Firely serialized it and the response as delivered.
            if (_captureRawBodies)
            {
                _rawCaptureHandler = new RawCaptureHandler();
                _rawCaptureHandler.OnRequestCaptured += (_, e) =>
                {
                    lock (_rawCaptureLock) { _lastRawRequestBody = e.RequestBody; }
                    OnRawRequestCaptured?.Invoke(this, e);
                };
                _rawCaptureHandler.OnResponseCaptured += (_, e) =>
                {
                    lock (_rawCaptureLock) { _lastRawResponseBody = e.ResponseBody; }
                    OnRawResponseCaptured?.Invoke(this, e);
                };
                _rawCaptureHandler.InnerHandler = inner;
                inner = _rawCaptureHandler;

                _logger.LogInformation(
                    "Raw HTTP body capture is ENABLED for {BaseUrl}. Captured bodies may contain PHI; " +
                    "ensure they are only routed to PHI-safe destinations.", _baseUrl);
            }

            _authHandler = new BearerTokenHandler(_tokenProvider, _scope, _maxConnectionsPerServer);
            _authHandler.InnerHandler = inner;

            _retryHandler = new TransientFaultRetryHandler(_authHandler, _logger);
        }


        /// <summary>
        /// Reconfigures the configurator to a new system (e.g., switch from Test to Prod) and recreates the client.
        /// </summary>
        /// <param name="system">The newly active <see cref="FhirSystemOptions"/>.</param>
        /// <param name="systemName">Optional name used only for logging context.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="system"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown if required options are missing.</exception>
        public void Reconfigure(FhirSystemOptions system, string? systemName = null)
        {
            ThrowIfDisposed();
            if (system is null) throw new ArgumentNullException(nameof(system));
            if (string.IsNullOrWhiteSpace(system.BaseUrl))
                throw new InvalidOperationException("FHIR BaseUrl is missing for the new system.");
            if (system.Auth is null || string.IsNullOrWhiteSpace(system.Auth.Scope))
                throw new InvalidOperationException("FHIR Auth.Scope is missing for the new system.");

            lock (_syncRoot)
            {
                var oldBase = _baseUrl;
                var oldScope = _scope;

                _baseUrl = new Uri(system.BaseUrl);
                _scope = system.Auth.Scope;

                var scopeChanged = !string.Equals(oldScope, _scope, StringComparison.Ordinal);

                _logger.LogInformation(
                    "Reconfiguring ClientConfigurator to system {SystemName}. BaseUrl: {OldBaseUrl} -> {NewBaseUrl}. ScopeChanged={ScopeChanged}",
                    systemName ?? "(unknown)",
                    oldBase,
                    _baseUrl,
                    scopeChanged);

                // Recreate the client with the updated base/scope while preserving handlers
                // (The handler instance is reused; it picks up the new scope when sending requests.)
                try
                {
                    _fhirClient?.Dispose();
                    CreateClient();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to reconfigure client to {NewBaseUrl}. Rolling back to previous settings.", _baseUrl);
                    // Attempt rollback to previous state
                    _baseUrl = oldBase;
                    _scope = oldScope;
                    throw;
                }
            }
        }

        /// <summary>
        /// Creates a new <see cref="HttpClient"/> that shares the long‑lived auth handler and
        /// is preconfigured with the current base address.
        /// </summary>
        /// <remarks>
        /// The returned client should be disposed by the caller when no longer needed.
        /// The handler chain is not disposed and remains owned by this configurator.
        /// </remarks>
        /// <returns>An authenticated <see cref="HttpClient"/> with <see cref="HttpClient.BaseAddress"/> set.</returns>
        /// <exception cref="ObjectDisposedException">Thrown if the configurator has been disposed.</exception>
        public HttpClient CreateAuthenticatedHttpClient()
        {
            ThrowIfDisposed();
            lock (_syncRoot)
            {
                _authHandler ??= new BearerTokenHandler(_tokenProvider, _scope, _maxConnectionsPerServer);
                _logger.LogDebug("Creating authenticated HttpClient for {BaseUrl}.", _baseUrl);

                // Do NOT dispose the long‑lived handler; the configurator owns it.
                var http = new HttpClient(_authHandler, disposeHandler: false)
                {
                    BaseAddress = _baseUrl
                };
                return http;
            }
        }

        /// <summary>
        /// Returns the current base URL as a string (for diagnostics, headers, etc.).
        /// </summary>
        public string BaseUrl
        {
            get
            {
                ThrowIfDisposed();
                return _baseUrl.ToString();
            }
        }

        /// <summary>
        /// Disposes the configurator, its created <see cref="FhirClient"/>, and the internal auth handler.
        /// </summary>
        /// <remarks>Idempotent; subsequent calls after the first are no‑ops.</remarks>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_syncRoot)
            {
                _logger.LogDebug("Disposing ClientConfigurator and inner resources.");
                _fhirClient?.Dispose();
                _retryHandler?.Dispose(); // DelegatingHandler.Dispose() cascades to _authHandler
            }
        }

        /// <summary>
        /// Throws if this configurator has been disposed.
        /// </summary>
        /// <exception cref="ObjectDisposedException">Always thrown if disposed.</exception>
        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClientConfigurator));
        }

    }
}
