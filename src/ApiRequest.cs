﻿// DragonFruit.Data Copyright DragonFruit Network
// Licensed under the MIT License. Please refer to the LICENSE file at the root of this project for details

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using DragonFruit.Data.Exceptions;
using DragonFruit.Data.Parameters;
using DragonFruit.Data.Serializers;
using DragonFruit.Data.Utils;

namespace DragonFruit.Data
{
    public abstract class ApiRequest
    {
        private List<KeyValuePair<string, string>> _headers;

        /// <summary>
        /// The path (including host, protocol and non-standard port) to the web resource
        /// </summary>
        public abstract string Path { get; }

        /// <summary>
        /// The method to use/request verb
        /// </summary>
        protected virtual Methods Method => Methods.Get;

        /// <summary>
        /// The <see cref="BodyType"/> to use (if there is a body to be sent)
        /// </summary>
        protected virtual BodyType BodyType { get; }

        /// <summary>
        /// Whether an auth header is required. Throws on check failure (before sending request)
        /// </summary>
        protected internal virtual bool RequireAuth => false;

        /// <summary>
        /// Custom Headers to send with this request. Overrides any custom header set in the <see cref="HttpClient"/> with the same name.
        /// </summary>
        /// <remarks>
        /// Headers to be set in all requests should be set at <see cref="ApiClient"/>-level, using the <see cref="ApiClient.Headers"/> Dictionary.
        /// </remarks>
        public List<KeyValuePair<string, string>> Headers => _headers ??= new List<KeyValuePair<string, string>>();

        /// <summary>
        /// Internal check for whether the custom header collection (<see cref="Headers"/>) has been initialised
        /// </summary>
        internal bool CustomHeaderCollectionCreated => _headers != null;

        /// <summary>
        /// The fully compiled url
        /// </summary>
        public string FullUrl => UrlCompiler;

        /// <summary>
        /// Getter for fully compiled url (internally visible)
        /// </summary>
        internal virtual string UrlCompiler => Path + QueryString;

        /// <summary>
        /// Overridable property for configuring a custom body for this request
        /// <para>
        /// Only used when the <see cref="BodyType"/> is equal to <see cref="BodyType.Custom"/>
        /// </para>
        /// </summary>
        protected virtual HttpContent BodyContent { get; }

        /// <summary>
        /// Overridable culture for serialising requests.
        /// Defaults to <see cref="CultureUtils.DefaultCulture"/>
        /// </summary>
        protected virtual CultureInfo RequestCulture => CultureUtils.DefaultCulture;

        /// <summary>
        /// Query string generated from all filled <see cref="QueryParameter"/>-attributed properties
        /// </summary>
        internal string QueryString
        {
            get
            {
                var queries = ParameterUtils.GetParameter<QueryParameter>(this, RequestCulture);

                if (AdditionalQueries != null)
                {
                    queries = queries.Concat(AdditionalQueries);
                }

                return QueryUtils.QueryStringFrom(queries);
            }
        }

        /// <summary>
        /// Additional abstract collection of queries provided as an <see cref="IEnumerable{T}"/> of <see cref="KeyValuePair{TKey,TValue}"/>
        /// </summary>
        protected virtual IEnumerable<KeyValuePair<string, string>> AdditionalQueries { get; }

        /// <summary>
        /// Overridable method for specifying an action to occur before sending the request to the <see cref="HttpClient"/>
        /// </summary>
        protected internal virtual void OnRequestExecuting(ApiClient client)
        {
        }

        /// <summary>
        /// Create a <see cref="HttpResponseMessage"/> for this <see cref="ApiRequest"/>, which can then be modified manually or overriden by <see cref="ApiClient.SetupRequest"/>
        /// </summary>
        public HttpRequestMessage Build(ApiClient client)
        {
            return Build(client.Serializer);
        }

        /// <summary>
        /// Create a <see cref="HttpResponseMessage"/> for this <see cref="ApiRequest"/>, which can then be modified manually or overriden by <see cref="ApiClient.SetupRequest"/>
        /// </summary>
        /// <remarks>
        /// This validates the <see cref="Path"/> and <see cref="RequireAuth"/> properties, throwing a <see cref="ClientValidationException"/> if it's unsatisfied with the constraints
        /// </remarks>
        public HttpRequestMessage Build(SerializerResolver serializer)
        {
            if (!Path.StartsWith("http") && !Path.StartsWith("//"))
            {
                throw new HttpRequestException("The request path is invalid (it must start with http or https)");
            }

            var request = new HttpRequestMessage
            {
                RequestUri = new Uri(FullUrl)
            };

            // generic setup
            switch (Method)
            {
                case Methods.Get:
                    request.Method = HttpMethod.Get;
                    break;

                case Methods.Post:
                    request.Method = HttpMethod.Post;
                    request.Content = GetContent(serializer);
                    break;

                case Methods.Put:
                    request.Method = HttpMethod.Put;
                    request.Content = GetContent(serializer);
                    break;

                case Methods.Patch:
#if NETSTANDARD2_0
                    // .NET Standard 2.0 doesn't have a PATCH method...
                    request.Method = new HttpMethod("PATCH");
#else
                    request.Method = HttpMethod.Patch;
#endif
                    request.Content = GetContent(serializer);
                    break;

                case Methods.Delete:
                    request.Method = HttpMethod.Delete;
                    request.Content = GetContent(serializer);
                    break;

                case Methods.Head:
                    request.Method = HttpMethod.Head;
                    break;

                case Methods.Trace:
                    request.Method = HttpMethod.Trace;
                    break;

                default:
                    throw new NotImplementedException();
            }

            if (CustomHeaderCollectionCreated)
            {
                foreach (var header in Headers)
                {
                    request.Headers.Add(header.Key, header.Value);
                }
            }

            if (!request.Headers.Contains("Accept"))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(serializer.Resolve(GetType(), DataDirection.In).ContentType));
            }

            return request;
        }

        private HttpContent GetContent(SerializerResolver serializer)
        {
            switch (BodyType)
            {
                case BodyType.Encoded:
                    return new FormUrlEncodedContent(ParameterUtils.GetParameter<FormParameter>(this, RequestCulture));

                case BodyType.Serialized:
                    return serializer.Resolve(GetType(), DataDirection.Out).Serialize(this);

                case BodyType.SerializedProperty:
                    var body = serializer.Resolve(GetType(), DataDirection.Out).Serialize(ParameterUtils.GetSingleParameterObject<RequestBody>(this));
                    return body;

                case BodyType.Custom:
                    return BodyContent;

                default:
                    //todo custom exception - there should have been a datatype specified
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
