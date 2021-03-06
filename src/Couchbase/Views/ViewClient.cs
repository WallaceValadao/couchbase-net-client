using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core.Exceptions;
using Couchbase.Core.Exceptions.View;
using Couchbase.Core.IO.HTTP;
using Couchbase.Core.IO.Serializers;
using Couchbase.Core.Logging;
using Microsoft.Extensions.Logging;

#nullable enable

namespace Couchbase.Views
{
    internal class ViewClient : HttpServiceBase, IViewClient
    {
        private readonly ITypeSerializer _serializer;
        private readonly ILogger<ViewClient> _logger;
        protected const string Success = "Success";

        public ViewClient(CouchbaseHttpClient httpClient, ITypeSerializer serializer, ILogger<ViewClient> logger)
            : base(httpClient)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(ITypeSerializer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // set timeout to infinite so we can stream results without the connection
            // closing part way through
            httpClient.Timeout = Timeout.InfiniteTimeSpan;
        }

        /// <inheritdoc />
        public async Task<IViewResult<TKey, TValue>> ExecuteAsync<TKey, TValue>(IViewQueryable query)
        {
            var uri = query.RawUri();
            ViewResultBase<TKey, TValue> viewResult;

            var body = query.CreateRequestBody();
            try
            {
                _logger.LogDebug("Sending view request to: {0}", uri.ToString());
                var content = new StringContent(body, Encoding.UTF8, MediaType.Json);
                var response = await HttpClient.PostAsync(uri, content).ConfigureAwait(false);

                var serializer = query.Serializer ?? _serializer;
                if (response.IsSuccessStatusCode)
                {
                    if (serializer is IStreamingTypeDeserializer streamingTypeDeserializer)
                    {
                        viewResult = new StreamingViewResult<TKey, TValue>(
                            response.StatusCode,
                            Success,
                            await response.Content.ReadAsStreamAsync().ConfigureAwait(false),
                            streamingTypeDeserializer
                        );
                    }
                    else
                    {
                        viewResult = new BlockViewResult<TKey, TValue>(
                            response.StatusCode,
                            Success,
                            await response.Content.ReadAsStreamAsync().ConfigureAwait(false),
                            serializer
                        );
                    }

                    await viewResult.InitializeAsync().ConfigureAwait(false);
                }
                else
                {
                    if (serializer is IStreamingTypeDeserializer streamingTypeDeserializer)
                    {
                        viewResult = new StreamingViewResult<TKey, TValue>(
                            response.StatusCode,
                            await response.Content.ReadAsStringAsync().ConfigureAwait(false),
                            streamingTypeDeserializer
                        );
                    }
                    else
                    {
                        viewResult = new BlockViewResult<TKey, TValue>(
                            response.StatusCode,
                            await response.Content.ReadAsStringAsync().ConfigureAwait(false),
                            serializer
                        );
                    }

                    await viewResult.InitializeAsync().ConfigureAwait(false);

                    if (viewResult.ShouldRetry())
                    {
                        UpdateLastActivity();
                        return viewResult;
                    }

                    if (viewResult.ViewNotFound())
                    {
                        throw new ViewNotFoundException(uri.ToString());
                    }
                }
            }
            catch (OperationCanceledException e)
            {
                _logger.LogDebug(LoggingEvents.ViewEvent, e, "View request timeout.");
                throw new AmbiguousTimeoutException("The view query was timed out via the Token.", e);
            }
            catch (HttpRequestException e)
            {
                _logger.LogDebug(LoggingEvents.QueryEvent, e, "View request cancelled.");
                throw new RequestCanceledException("The view query was canceled.", e);
            }

            UpdateLastActivity();
            return viewResult;
        }

        protected static HttpStatusCode GetStatusCode(string message)
        {
            var httpStatusCode = HttpStatusCode.ServiceUnavailable;
            var codes = Enum.GetValues(typeof(HttpStatusCode));
            foreach (int code in codes)
            {
                if (message.Contains(code.ToString(CultureInfo.InvariantCulture)))
                {
                    httpStatusCode = (HttpStatusCode)code;
                    break;
                }
            }
            return httpStatusCode;
        }
    }
}

#region [ License information          ]

/* ************************************************************
 *
 *    @author Couchbase <info@couchbase.com>
 *    @copyright 2017 Couchbase, Inc.
 *
 *    Licensed under the Apache License, Version 2.0 (the "License");
 *    you may not use this file except in compliance with the License.
 *    You may obtain a copy of the License at
 *
 *        http://www.apache.org/licenses/LICENSE-2.0
 *
 *    Unless required by applicable law or agreed to in writing, software
 *    distributed under the License is distributed on an "AS IS" BASIS,
 *    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *    See the License for the specific language governing permissions and
 *    limitations under the License.
 *
 * ************************************************************/

#endregion
