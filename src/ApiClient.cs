﻿// DragonFruit.Data Copyright DragonFruit Network
// Licensed under the MIT License. Please refer to the LICENSE file at the root of this project for details

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DragonFruit.Data.Exceptions;
using DragonFruit.Data.Headers;
using DragonFruit.Data.Serializers;
using DragonFruit.Data.Utils;
using Nito.AsyncEx;

#pragma warning disable 618

namespace DragonFruit.Data
{
    /// <summary>
    /// Managed wrapper for a <see cref="HttpClient"/> allowing easy header access, handler, serializing/deserializing and memory management
    /// </summary>
    public partial class ApiClient
    {
        private HttpClient _client;
        private Version _httpVersion;
        private Func<HttpMessageHandler> _handler;
        private long _clientAdjustmentRequestSignal;
        private readonly AsyncReaderWriterLock _lock;

        /// <summary>
        /// Initialises a new <see cref="ApiClient"/> using a user-set <see cref="ApiSerializer"/>
        /// </summary>
        public ApiClient(ApiSerializer serializer)
        {
            Headers = new HeaderCollection(this);
            Serializer = new SerializerResolver(serializer);

            _lock = new AsyncReaderWriterLock();

            RequestClientReset(true);
        }

        ~ApiClient()
        {
            _client?.Dispose();
        }

        static ApiClient()
        {
            // register generic xml document type
            SerializerResolver.Register<XmlDocument, ApiXmlSerializer>();

            // register stream resolver types (inwards only)
            SerializerResolver.Register<Stream, InternalStreamSerializer>(DataDirection.In);
            SerializerResolver.Register<FileStream, InternalStreamSerializer>(DataDirection.In);
            SerializerResolver.Register<MemoryStream, InternalStreamSerializer>(DataDirection.In);
        }

        #region Factories

        /// <summary>
        /// Checks the current <see cref="HttpClient"/> and replaces it if headers or <see cref="Handler"/> has been modified
        /// </summary>
        private (HttpClient Client, IDisposable Lock) GetClient()
        {
            // return current client if there are no changes
            var resetLevel = Interlocked.Exchange(ref _clientAdjustmentRequestSignal, 0);

            if (resetLevel > 0)
            {
                // block all reads and let all current requests finish
                using (_lock.WriterLock())
                {
                    // only reset the client if the handler has changed (signal = 2)
                    var resetClient = resetLevel == 2;

                    if (resetClient)
                    {
                        var handler = CreateHandler();

                        _client?.Dispose();
                        _client = handler != null ? new HttpClient(handler, true) : new HttpClient();
                    }

                    // apply new headers
                    Headers.ApplyTo(_client);

                    // allow the consumer to change the client
                    SetupClient(_client, resetClient);

                    // reset the state
                    Interlocked.Exchange(ref _clientAdjustmentRequestSignal, 0);
                }
            }

            return (_client, _lock.ReaderLock());
        }

        #endregion

        /// <summary>
        /// Internal procedure for performing a web-request
        /// </summary>
        /// <remarks>
        /// While the consumer has the option to prevent disposal of the <see cref="HttpResponseMessage"/> produced,
        /// the <see cref="HttpRequestMessage"/> passed is always disposed at the end of the request.
        /// </remarks>
        /// <param name="request">The request to perform</param>
        /// <param name="processResult"><see cref="Func{T,TResult}"/> to process the <see cref="HttpResponseMessage"/></param>
        /// <param name="disposeResponse">Whether to dispose of the <see cref="HttpResponseMessage"/> produced after <see cref="processResult"/> has been invoked.</param>
        /// <param name="token">(optional) <see cref="CancellationToken"/></param>
        protected async Task<T> InternalPerform<T>(HttpRequestMessage request, Func<HttpResponseMessage, Task<T>> processResult, bool disposeResponse, CancellationToken token = default)
        {
            var (client, clientLock) = GetClient();

            // post-modification
            SetupRequest(request);
            HttpResponseMessage response = null;

            try
            {
                // send request
                // ReSharper disable once MethodSupportsCancellation (we need to run regardless of cancellation to release lock)
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                // evaluate task status and update monitor
                return await processResult.Invoke(response).ConfigureAwait(false);
            }
            finally
            {
                request.Dispose();

                if (disposeResponse)
                {
                    response?.Dispose();
                }

                // exit the read lock after fully processing
                clientLock.Dispose();
            }
        }

        /// <summary>
        /// Validates the <see cref="HttpResponseMessage"/> and uses the <see cref="Serializer"/> to deserialize data (if successful)
        /// </summary>
        protected virtual async Task<T> ValidateAndProcess<T>(HttpResponseMessage response) where T : class
        {
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var serializer = Serializer.Resolve<T>(DataDirection.In);

            if (serializer is IAsyncSerializer asyncSerializer)
            {
                return await asyncSerializer.DeserializeAsync<T>(stream).ConfigureAwait(false);
            }

            return serializer.Deserialize<T>(stream);
        }

        /// <summary>
        /// An overridable method for validating the request against the current <see cref="ApiClient"/>
        /// </summary>
        /// <param name="request">The request to validate</param>
        /// <exception cref="NullRequestException">The request can't be performed due to a poorly-formed url</exception>
        /// <exception cref="ClientValidationException">The client can't be used because there is no auth url.</exception>
        protected virtual void ValidateRequest(ApiRequest request)
        {
            request.OnRequestExecuting(this);

            // note request path is validated on build
            if (request.RequireAuth && string.IsNullOrEmpty(Authorization))
            {
                // check if we have a custom headerset in the request
                if (!request.CustomHeaderCollectionCreated || !request.Headers.Any(x => x.Key.Equals("Authorization")))
                {
                    throw new ClientValidationException("Authorization header was expected, but not found (in request or client)");
                }
            }
        }

        /// <summary>
        /// Requests the client is reset on the next request
        /// </summary>
        /// <param name="fullReset">Whether to reset the <see cref="HttpClient"/> as well as the headers</param>
        public void RequestClientReset(bool fullReset)
        {
            if (fullReset)
            {
                Interlocked.Exchange(ref _clientAdjustmentRequestSignal, 2);
            }
            else
            {
                Interlocked.CompareExchange(ref _clientAdjustmentRequestSignal, 1, 0);
            }
        }

        #region Properties

        /// <summary>
        /// The User-Agent header to pass in all requests
        /// </summary>
        public string UserAgent
        {
            get => Headers["User-Agent"];
            set => Headers["User-Agent"] = value;
        }

        /// <summary>
        /// The Authorization header value
        /// </summary>
        public string Authorization
        {
            get => Headers["Authorization"];
            set => Headers["Authorization"] = value;
        }

        /// <summary>
        /// Headers to be sent with the requests
        /// </summary>
        public HeaderCollection Headers { get; }

        /// <summary>
        /// Gets or sets the HTTP version to use on requests passed through the <see cref="ApiClient"/>.
        /// When setting this property, consider all target devices and whether they have support for the version targeted.
        /// </summary>
        public Version HttpVersion
        {
            get => _httpVersion ??= HttpVersionUtils.DefaultHttpVersion;
            set => _httpVersion = value;
        }

        /// <summary>
        /// Optional <see cref="HttpMessageHandler"/> factory to be consumed by the <see cref="HttpClient"/>
        /// </summary>
        /// <remarks>
        /// This must create a new handler each time as they are disposed alongside the client
        /// </remarks>
        public Func<HttpMessageHandler> Handler
        {
            get => _handler;
            set
            {
                _handler = value;
                RequestClientReset(true);
            }
        }

        /// <summary>
        /// The container for <see cref="ApiSerializer"/>s. The default serializer can be set at <see cref="SerializerResolver.Default"/>
        /// </summary>
        public SerializerResolver Serializer { get; }

        #endregion

        #region Empty Overrides (Inherited)

        /// <summary>
        /// Overridable method for creating a <see cref="HttpMessageHandler"/> to use with the <see cref="HttpClient"/>
        /// </summary>
        /// <remarks>
        /// This should be used when a library needs to enforce a <see cref="DelegatingHandler"/> is wrapped over the <see cref="Handler"/>.
        /// If overriden, it should be sealed to prevent misuse
        /// </remarks>
        protected virtual HttpMessageHandler CreateHandler() => Handler?.Invoke();

        /// <summary>
        /// Overridable method to customise the <see cref="HttpClient"/>.
        /// <para>
        /// Custom headers can be included here, but should be done in the <see cref="Headers"/> dictionary.
        /// </para>
        /// </summary>
        /// <remarks>
        /// This is called when the client or it's headers are reset.
        /// The <see cref="clientReset"/> is set to true to allow you to configure client settings (not headers) after creation.
        /// </remarks>
        /// <param name="client">The <see cref="HttpClient"/> to modify</param>
        /// <param name="clientReset">Whether the client were reset/disposed</param>
        protected virtual void SetupClient(HttpClient client, bool clientReset)
        {
        }

        /// <summary>
        /// When overridden, this can be used to alter all <see cref="HttpRequestMessage"/> created.
        /// </summary>
        protected virtual void SetupRequest(HttpRequestMessage request)
        {
            // HTTP versions need to be overriden at the request level - targeting the client won't work
            request.Version = HttpVersion;
        }

        #endregion
    }
}
