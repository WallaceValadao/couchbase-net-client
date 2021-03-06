using System;
using System.Net;
using System.Threading.Tasks;
using Couchbase.Core.CircuitBreakers;
using Couchbase.Core.IO.Authentication;
using Couchbase.Core.IO.Transcoders;
using Couchbase.Utils;
using Microsoft.Extensions.Logging;

#nullable enable

namespace Couchbase.Core.DI
{
    /// <summary>
    /// Default implementation of <see cref="IClusterNodeFactory"/>.
    /// </summary>
    internal class ClusterNodeFactory : IClusterNodeFactory
    {
        private readonly ClusterContext _clusterContext;
        private readonly IConnectionFactory _connectionFactory;
        private readonly ILogger<ClusterNode> _logger;
        private readonly ITypeTranscoder _transcoder;
        private readonly ICircuitBreaker _circuitBreaker;
        private readonly ISaslMechanismFactory _saslMechanismFactory;

        public ClusterNodeFactory(ClusterContext clusterContext, IConnectionFactory connectionFactory, ILogger<ClusterNode> logger, ITypeTranscoder transcoder, ICircuitBreaker circuitBreaker, ISaslMechanismFactory saslMechanismFactory)
        {
            _clusterContext = clusterContext ?? throw new ArgumentNullException(nameof(clusterContext));
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _transcoder = transcoder ?? throw new ArgumentNullException(nameof(transcoder));
            _circuitBreaker = circuitBreaker ?? throw new ArgumentException(nameof(circuitBreaker));
            _saslMechanismFactory = saslMechanismFactory;
        }

        /// <inheritdoc />
        public async Task<IClusterNode> CreateAndConnectAsync(IPEndPoint endPoint)
        {
            var connection = await _connectionFactory.CreateAndConnectAsync(endPoint);
            var serverFeatures = await connection.Hello(_transcoder).ConfigureAwait(false);
            var errorMap = await connection.GetErrorMap(_transcoder).ConfigureAwait(false);

            var mechanismType = _clusterContext.ClusterOptions.EnableTls ? MechanismType.Plain : MechanismType.ScramSha1;
            var saslMechanism = _saslMechanismFactory.Create(mechanismType,
                _clusterContext.ClusterOptions.UserName ?? throw new ArgumentNullException(nameof(_clusterContext.ClusterOptions.UserName)),
                _clusterContext.ClusterOptions.Password ?? throw new ArgumentNullException(nameof(_clusterContext.ClusterOptions.Password)));

            await saslMechanism.AuthenticateAsync(connection, _clusterContext.CancellationToken).ConfigureAwait(false);

            var clusterNode = new ClusterNode(_clusterContext, _connectionFactory, _logger, _transcoder, _circuitBreaker, _saslMechanismFactory)
            {
                EndPoint = endPoint,
                Connection = connection,
                ServerFeatures = serverFeatures,
                ErrorMap = errorMap
            };

            clusterNode.BuildServiceUris();

            return clusterNode;
        }
    }
}
