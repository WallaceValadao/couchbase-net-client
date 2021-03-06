using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Couchbase.Core.Exceptions;
using Couchbase.Core.IO.Operations;
using Couchbase.Core.Logging;
using Couchbase.Core.Retry.Query;
using Microsoft.Extensions.Logging;

namespace Couchbase.Core.Retry
{
    internal class RetryOrchestrator : IRetryOrchestrator
    {
        private readonly ILogger<RetryOrchestrator> _logger;

        public RetryOrchestrator(ILogger<RetryOrchestrator> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<T> RetryAsync<T>(Func<Task<T>> send, IRequest request) where T : IServiceResult
        {
            var token = request.Token;
            if (token == CancellationToken.None)
            {
                var cts = new CancellationTokenSource(request.Timeout);
                token = cts.Token;
            }

            //for measuring the capped duration
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var backoff = ControlledBackoff.Create();
            do
            {
                if (token.IsCancellationRequested)
                {
                    if (request.Idempotent)
                    {
                        UnambiguousTimeoutException.ThrowWithRetryReasons(request);
                    }
                    AmbiguousTimeoutException.ThrowWithRetryReasons(request);
                }

                try
                {
                    var result = await send().ConfigureAwait(false);
                    var reason = result.RetryReason;
                    if (reason == RetryReason.NoRetry) return result;

                    if (reason.AlwaysRetry())
                    {
                        _logger.LogDebug(
                            $"Retrying query {request.ClientContextId}/{request.Statement} because {reason}.");

                        await backoff.Delay(request).ConfigureAwait(false);
                        request.IncrementAttempts(reason);
                        continue;
                    }

                    var strategy = request.RetryStrategy;
                    var action = strategy.RetryAfter(request, reason);
                    if (action.Retry)
                    {
                        _logger.LogDebug(LoggingEvents.QueryEvent,
                            $"Retrying query {request.ClientContextId}/{request.Statement} because {reason}.");

                        var duration = action.DurationValue;
                        if (duration.HasValue)
                        {
                            _logger.LogDebug($"Timeout for {request.ClientContextId} is {request.Timeout.TotalMilliseconds} and duration is {duration.Value.TotalMilliseconds} and elasped is {stopwatch.ElapsedMilliseconds}");
                            var cappedDuration =
                                request.Timeout.CappedDuration(duration.Value, stopwatch.Elapsed);

                            _logger.LogDebug($"Timeout for {request.ClientContextId} capped duration is {cappedDuration.TotalMilliseconds} and elasped is {stopwatch.ElapsedMilliseconds}");
                            await Task.Delay(cappedDuration,
                                CancellationTokenSource.CreateLinkedTokenSource(token).Token).ConfigureAwait(false);
                            request.IncrementAttempts(reason);

                            //temp fix for query unit tests
                            if (request.Attempts > 9)
                            {
                                if (request.Idempotent)
                                {
                                    UnambiguousTimeoutException.ThrowWithRetryReasons(request,
                                        new InvalidOperationException($"Too many retries: {request.Attempts}."));
                                }
                                AmbiguousTimeoutException.ThrowWithRetryReasons(request,
                                    new InvalidOperationException($"Too many retries: {request.Attempts}."));
                            }
                        }
                        else
                        {
                            if (request.Idempotent)
                            {
                                UnambiguousTimeoutException.ThrowWithRetryReasons(request);
                            }

                            AmbiguousTimeoutException.ThrowWithRetryReasons(request);
                        }
                    }
                }
                catch (TaskCanceledException _)
                {
                    _logger.LogDebug($"Request was canceled after {stopwatch.ElapsedMilliseconds}.");
                    //timed out while waiting
                    if (request.Idempotent)
                    {
                        UnambiguousTimeoutException.ThrowWithRetryReasons(request, _);
                    }

                    AmbiguousTimeoutException.ThrowWithRetryReasons(request, _);
                }
            } while (true);
        }

        public async Task RetryAsync(BucketBase bucket, IOperation operation, CancellationToken token = default,
            TimeSpan? timeout = null)
        {
            try
            {
                var backoff = ControlledBackoff.Create();
                do
                {
                    try
                    {
                        operation.Attempts++;
                        await bucket.SendAsync(operation, token, timeout).ConfigureAwait(false);
                        break;
                    }
                    catch (CouchbaseException e)
                    {
                        if (e is IRetryable)
                        {
                            var reason = e.ResolveRetryReason();
                            if (reason.AlwaysRetry())
                            {
                                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

                                _logger.LogDebug($"Retrying op {operation.Opaque}/{operation.Key} because {reason}.");
                                await backoff.Delay(operation).ConfigureAwait(false);
                                continue;
                            }

                            var strategy = operation.RetryStrategy;
                            var action = strategy.RetryAfter(operation, reason);

                            if (action.DurationValue.HasValue)
                            {
                                _logger.LogDebug($"Retrying op {operation.Opaque}/{operation.Key} because {reason}.");
                                await Task.Delay(action.DurationValue.Value, token).ConfigureAwait(false);
                            }
                            else
                            {
                               break; //don't retry
                            }
                        }
                        else
                        {
                            throw;
                        }
                    }
                } while (true);
            }
            catch (TaskCanceledException){ ThrowTimeoutException(operation, timeout); }
            catch (OperationCanceledException) { ThrowTimeoutException(operation, timeout); }
        }

        private static void ThrowTimeoutException(TimeSpan? timeout)
        {
            throw new TimeoutException($"The query timed out after {timeout}.");
        }

        private static void ThrowTimeoutException(IOperation operation, TimeSpan? timeout)
        {
            throw new TimeoutException(
                $"The operation {operation.Opaque}/{operation.Opaque} timed out after {timeout}. " +
                $"It was retried {operation.Attempts} times using {operation.RetryStrategy.GetType()}.");
        }
    }
}
