// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Adapter.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Https.Internal
{
    public class HttpsConnectionAdapter : IConnectionAdapter
    {
        // See http://oid-info.com/get/1.3.6.1.5.5.7.3.1
        // Indicates that a certificate can be used as a SSL server certificate
        private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

        private static readonly ClosedAdaptedConnection _closedAdaptedConnection = new ClosedAdaptedConnection();

        private readonly HttpsConnectionAdapterOptions _options;
        private readonly X509Certificate2 _serverCertificate;
        private readonly ILogger _logger;

        public HttpsConnectionAdapter(HttpsConnectionAdapterOptions options)
            : this(options, loggerFactory: null)
        {
        }

        public HttpsConnectionAdapter(HttpsConnectionAdapterOptions options, ILoggerFactory loggerFactory)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.ServerCertificate == null)
            {
                throw new ArgumentException(HttpsStrings.ServiceCertificateRequired, nameof(options));
            }

            // capture the certificate now so it can be switched after validation
            _serverCertificate = options.ServerCertificate;

            EnsureCertificateIsAllowedForServerAuth(_serverCertificate);

            _options = options;
            _logger = loggerFactory?.CreateLogger(nameof(HttpsConnectionAdapter));
        }

        public bool IsHttps => true;

        public Task<IAdaptedConnection> OnConnectionAsync(ConnectionAdapterContext context)
        {
            // Don't trust SslStream not to block.
            return Task.Run(() => InnerOnConnectionAsync(context));
        }

        private async Task<IAdaptedConnection> InnerOnConnectionAsync(ConnectionAdapterContext context)
        {
            SslStream sslStream;
            bool certificateRequired;
            var feature = new TlsConnectionFeature();
            context.Features.Set<ITlsConnectionFeature>(feature);

            if (_options.ClientCertificateMode == ClientCertificateMode.NoCertificate)
            {
                sslStream = new SslStream(context.ConnectionStream);
                certificateRequired = false;
            }
            else
            {
                sslStream = new SslStream(context.ConnectionStream,
                    leaveInnerStreamOpen: false,
                    userCertificateValidationCallback: (sender, certificate, chain, sslPolicyErrors) =>
                    {
                        if (certificate == null)
                        {
                            return _options.ClientCertificateMode != ClientCertificateMode.RequireCertificate;
                        }

                        if (_options.ClientCertificateValidation == null)
                        {
                            if (sslPolicyErrors != SslPolicyErrors.None)
                            {
                                return false;
                            }
                        }

                        var certificate2 = ConvertToX509Certificate2(certificate);
                        if (certificate2 == null)
                        {
                            return false;
                        }

                        if (_options.ClientCertificateValidation != null)
                        {
                            if (!_options.ClientCertificateValidation(certificate2, chain, sslPolicyErrors))
                            {
                                return false;
                            }
                        }

                        return true;
                    });

                certificateRequired = true;
            }

            var timeoutFeature = context.Features.Get<IConnectionTimeoutFeature>();
            timeoutFeature.SetTimeout(_options.HandshakeTimeout);

            try
            {
#if NETCOREAPP2_1
                var sslOptions = new SslServerAuthenticationOptions()
                {
                    ServerCertificate = _serverCertificate,
                    ClientCertificateRequired = certificateRequired,
                    EnabledSslProtocols = _options.SslProtocols,
                    CertificateRevocationCheckMode = _options.CheckCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                    ApplicationProtocols = new List<SslApplicationProtocol>()
                };

                // This is order sensitive
                if ((_options.HttpProtocols & HttpProtocols.Http2) != 0)
                {
                    sslOptions.ApplicationProtocols.Add(SslApplicationProtocol.Http2);
                }

                if ((_options.HttpProtocols & HttpProtocols.Http1) != 0)
                {
                    sslOptions.ApplicationProtocols.Add(SslApplicationProtocol.Http11);
                }

                await sslStream.AuthenticateAsServerAsync(sslOptions, CancellationToken.None);
#else
                await sslStream.AuthenticateAsServerAsync(_serverCertificate, certificateRequired,
                        _options.SslProtocols, _options.CheckCertificateRevocation);
#endif
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation(2, HttpsStrings.AuthenticationTimedOut);
                sslStream.Dispose();
                return _closedAdaptedConnection;
            }
            catch (IOException ex)
            {
                _logger?.LogInformation(1, ex, HttpsStrings.AuthenticationFailed);
                sslStream.Dispose();
                return _closedAdaptedConnection;
            }
            finally
            {
                timeoutFeature.CancelTimeout();
            }

#if NETCOREAPP2_1
            // Don't allocate in the common case, see https://github.com/dotnet/corefx/issues/25432
            if (sslStream.NegotiatedApplicationProtocol == SslApplicationProtocol.Http11)
            {
                feature.ApplicationProtocol = "http/1.1";
            }
            else if (sslStream.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
            {
                feature.ApplicationProtocol = "h2";
            }
            else
            {
                feature.ApplicationProtocol = sslStream.NegotiatedApplicationProtocol.ToString();
            }

            context.Features.Set<ITlsApplicationProtocolFeature>(feature);
#endif
            feature.ClientCertificate = ConvertToX509Certificate2(sslStream.RemoteCertificate);

            return new HttpsAdaptedConnection(sslStream);
        }

        private static void EnsureCertificateIsAllowedForServerAuth(X509Certificate2 certificate)
        {
            /* If the Extended Key Usage extension is included, then we check that the serverAuth usage is included. (http://oid-info.com/get/1.3.6.1.5.5.7.3.1)
             * If the Extended Key Usage extension is not included, then we assume the certificate is allowed for all usages.
             *
             * See also https://blogs.msdn.microsoft.com/kaushal/2012/02/17/client-certificates-vs-server-certificates/
             *
             * From https://tools.ietf.org/html/rfc3280#section-4.2.1.13 "Certificate Extensions: Extended Key Usage"
             *
             * If the (Extended Key Usage) extension is present, then the certificate MUST only be used
             * for one of the purposes indicated.  If multiple purposes are
             * indicated the application need not recognize all purposes indicated,
             * as long as the intended purpose is present.  Certificate using
             * applications MAY require that a particular purpose be indicated in
             * order for the certificate to be acceptable to that application.
             */

            var hasEkuExtension = false;

            foreach (var extension in certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>())
            {
                hasEkuExtension = true;
                foreach (var oid in extension.EnhancedKeyUsages)
                {
                    if (oid.Value.Equals(ServerAuthenticationOid, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }

            if (hasEkuExtension)
            {
                throw new InvalidOperationException(HttpsStrings.FormatInvalidServerCertificateEku(certificate.Thumbprint));
            }
        }

        private static X509Certificate2 ConvertToX509Certificate2(X509Certificate certificate)
        {
            if (certificate == null)
            {
                return null;
            }

            if (certificate is X509Certificate2 cert2)
            {
                return cert2;
            }

            return new X509Certificate2(certificate);
        }

        private class HttpsAdaptedConnection : IAdaptedConnection
        {
            private readonly SslStream _sslStream;

            public HttpsAdaptedConnection(SslStream sslStream)
            {
                _sslStream = sslStream;
            }

            public Stream ConnectionStream => _sslStream;

            public void Dispose()
            {
                _sslStream.Dispose();
            }
        }

        private class ClosedAdaptedConnection : IAdaptedConnection
        {
            public Stream ConnectionStream { get; } = new ClosedStream();

            public void Dispose()
            {
            }
        }
    }
}
