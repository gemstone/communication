﻿//******************************************************************************************************
//  TlsClient.cs - Gbtc
//
//  Copyright © 2012, Grid Protection Alliance.  All Rights Reserved.
//
//  Licensed to the Grid Protection Alliance (GPA) under one or more contributor license agreements. See
//  the NOTICE file distributed with this work for additional information regarding copyright ownership.
//  The GPA licenses this file to you under the MIT License (MIT), the "License"; you may
//  not use this file except in compliance with the License. You may obtain a copy of the License at:
//
//      http://www.opensource.org/licenses/MIT
//
//  Unless agreed to in writing, the subject software distributed under the License is distributed on an
//  "AS-IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. Refer to the
//  License for the specific language governing permissions and limitations.
//
//  Code Modification History:
//  ----------------------------------------------------------------------------------------------------
//  07/12/2012 - Stephen C. Wills
//       Generated original version of source code.
//  12/13/2012 - Starlynn Danyelle Gilliam
//       Modified Header.
//
//******************************************************************************************************

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Gemstone.ActionExtensions;
using Gemstone.ArrayExtensions;
using Gemstone.IO;
using Gemstone.Net.Security;
using Gemstone.StringExtensions;
using Gemstone.Threading.SynchronizedOperations;

// ReSharper disable AccessToDisposedClosure
// ReSharper disable ArrangeAccessorOwnerBody
namespace Gemstone.Communication
{
    /// <summary>
    /// Represents a TCP-based communication client with SSL authentication and encryption.
    /// </summary>
    /// <seealso cref="TcpClient"/>
    public class TlsClient : ClientBase
    {
        #region [ Members ]

        // Nested Types
        private sealed class ConnectState : IDisposable
        {
            public Socket? Socket;
            public NetworkStream? NetworkStream;
            public SslStream? SslStream;
            public NegotiateStream? NegotiateStream;

            public readonly SocketAsyncEventArgs ConnectArgs = new();
            public int ConnectionAttempts;

            public readonly CancellationToken Token = new();
            public Func<bool> CancelTimeout = () => false;

            public void Dispose()
            {
                ConnectArgs.Dispose();
                Socket?.Dispose();
                NetworkStream?.Dispose();
                SslStream?.Dispose();
                NegotiateStream?.Dispose();
            }
        }

        private sealed class ReceiveState : IDisposable
        {
            public Socket? Socket;
            public NetworkStream? NetworkStream;
            public SslStream? SslStream;

            public byte[]? Buffer;
            public int Offset;
            public int PayloadLength = -1;

            public CancellationToken? Token;

            public void Dispose()
            {
                Dispose(Socket);
                Dispose(NetworkStream);
                Dispose(SslStream);
            }

            private static void Dispose(IDisposable? obj) => obj?.Dispose();
        }

        private sealed class SendState : IDisposable
        {
            public Socket? Socket;
            public NetworkStream? NetworkStream;
            public SslStream? SslStream;

            public readonly ConcurrentQueue<TlsClientPayload> SendQueue = new();
            public TlsClientPayload? Payload;
            public int Sending;

            public CancellationToken? Token;

            public void Dispose()
            {
                Dispose(Socket);
                Dispose(NetworkStream);
                Dispose(SslStream);

                while (SendQueue.TryDequeue(out TlsClientPayload payload))
                {
                    payload.WaitHandle.Set();
                    payload.WaitHandle.Dispose();
                }
            }

            private static void Dispose(IDisposable? obj) => obj?.Dispose();
        }

        private class TlsClientPayload
        {
            public byte[]? Data;
            public int Offset;
            public int Length;
            public readonly ManualResetEventSlim WaitHandle = new();
        }

        private class CancellationToken
        {
            private int m_cancelled;

            public bool Cancelled => Interlocked.CompareExchange(ref m_cancelled, 0, 0) != 0;

            public bool Cancel() => Interlocked.Exchange(ref m_cancelled, 1) != 0;
        }

        // Constants

        /// <summary>
        /// Specifies the default value for the <see cref="TrustedCertificatesPath"/> property.
        /// </summary>
        public readonly string DefaultTrustedCertificatesPath = FilePath.GetAbsolutePath(@"Certs\Remotes");

        /// <summary>
        /// Specifies the default value for the <see cref="PayloadAware"/> property.
        /// </summary>
        public const bool DefaultPayloadAware = false;

        /// <summary>
        /// Specifies the default value for the <see cref="IntegratedSecurity"/> property.
        /// </summary>
        public const bool DefaultIntegratedSecurity = false;

        /// <summary>
        /// Specifies the default value for the <see cref="IgnoreInvalidCredentials"/> property.
        /// </summary>
        public const bool DefaultIgnoreInvalidCredentials = false;

        /// <summary>
        /// Specifies the default value for the <see cref="AllowDualStackSocket"/> property.
        /// </summary>
        public const bool DefaultAllowDualStackSocket = true;

        /// <summary>
        /// Specifies the default value for the <see cref="MaxSendQueueSize"/> property.
        /// </summary>
        public const int DefaultMaxSendQueueSize = 500000;

        /// <summary>
        /// Specifies the default value for the <see cref="NoDelay"/> property.
        /// </summary>
        public const bool DefaultNoDelay = false;

        /// <summary>
        /// Specifies the default value for the <see cref="ClientBase.ConnectionString"/> property.
        /// </summary>
        public const string DefaultConnectionString = "Server=localhost:8888";

        // Fields
        private readonly SimpleCertificateChecker m_defaultCertificateChecker;
        private ICertificateChecker m_certificateChecker = default!;
        private readonly X509Certificate2Collection m_clientCertificates;
        private string? m_certificateFile;
        private byte[] m_payloadMarker;
        private EndianOrder m_payloadEndianOrder;
        private IPStack m_ipStack;
        private readonly ShortSynchronizedOperation m_dumpPayloadsOperation;
        private string[]? m_serverList;
        private int m_serverIndex;
        private Dictionary<string, string> m_connectData = DefaultConnectionString.ParseKeyValuePairs();
        private ManualResetEvent? m_connectWaitHandle;
        private ConnectState? m_connectState;
        private ReceiveState? m_receiveState;
        private SendState? m_sendState;
        private bool m_disposed;

        #endregion

        #region [ Constructors ]

        /// <summary>
        /// Initializes a new instance of the <see cref="TlsClient"/> class.
        /// </summary>
        public TlsClient() : this(DefaultConnectionString)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TlsClient"/> class.
        /// </summary>
        /// <param name="connectString">Connect string of the <see cref="TlsClient"/>. See <see cref="DefaultConnectionString"/> for format.</param>
        public TlsClient(string connectString) : base(TransportProtocol.Tcp, connectString)
        {
            m_defaultCertificateChecker = new SimpleCertificateChecker();
            LocalCertificateSelectionCallback = DefaultLocalCertificateSelectionCallback;
            m_clientCertificates = new X509Certificate2Collection();
            EnabledSslProtocols = SslProtocols.Tls12;
            CheckCertificateRevocation = true;

            TrustedCertificatesPath = DefaultTrustedCertificatesPath;
            PayloadAware = DefaultPayloadAware;
            m_payloadMarker = Payload.DefaultMarker;
            m_payloadEndianOrder = EndianOrder.LittleEndian;
            IntegratedSecurity = DefaultIntegratedSecurity;
            IgnoreInvalidCredentials = DefaultIgnoreInvalidCredentials;
            AllowDualStackSocket = DefaultAllowDualStackSocket;
            MaxSendQueueSize = DefaultMaxSendQueueSize;
            NoDelay = DefaultNoDelay;
            m_dumpPayloadsOperation = new ShortSynchronizedOperation(DumpPayloads, OnSendDataException);
        }

        #endregion

        #region [ Properties ]

        /// <summary>
        /// Gets or sets a boolean value that indicates whether the payload boundaries are to be preserved during transmission.
        /// </summary>
        public bool PayloadAware { get; set; }

        /// <summary>
        /// Gets or sets the byte sequence used to mark the beginning of a payload in a <see cref="PayloadAware"/> transmission.
        /// </summary>
        /// <remarks>
        /// Setting property to <c>null</c> will create a zero-length payload marker.
        /// </remarks>
        public byte[] PayloadMarker
        {
            get => m_payloadMarker;
            set => m_payloadMarker = value ?? Array.Empty<byte>();
        }

        /// <summary>
        /// Gets or sets the endian order to apply for encoding and decoding payload size in a <see cref="PayloadAware"/> transmission.
        /// </summary>
        /// <remarks>
        /// Setting property to <c>null</c> will force use of little-endian encoding.
        /// </remarks>
        public EndianOrder PayloadEndianOrder
        {
            get => m_payloadEndianOrder;
            set => m_payloadEndianOrder = value ?? EndianOrder.LittleEndian;
        }

        /// <summary>
        /// Gets or sets a boolean value that indicates whether the current Windows account credentials are used for authentication.
        /// </summary>
        public bool IntegratedSecurity { get; set; }

        /// <summary>
        /// Gets or sets a boolean value that indicates whether the server
        /// should ignore errors when the client's credentials are invalid.
        /// </summary>
        /// <remarks>
        /// This property should only be set to true if there is an alternative by which
        /// to authenticate the client when integrated security fails.
        /// </remarks>
        public bool IgnoreInvalidCredentials { get; set; }

        /// <summary>
        /// Gets or sets a boolean value that determines if dual-mode socket is allowed when endpoint address is IPv6.
        /// </summary>
        public bool AllowDualStackSocket { get; set; }

        /// <summary>
        /// Gets or sets the maximum size for the send queue before payloads are dumped from the queue.
        /// </summary>
        public int MaxSendQueueSize { get; set; }

        /// <summary>
        /// Gets or sets a boolean value that determines if small packets are delivered to the remote host without delay.
        /// </summary>
        public bool NoDelay { get; set; }

        /// <summary>
        /// Gets the <see cref="Socket"/> object for the <see cref="TlsClient"/>.
        /// </summary>
        public Socket? Client => m_connectState?.Socket;

        /// <summary>
        /// Gets the <see cref="SslStream"/> object for the <see cref="TlsClient"/>.
        /// </summary>
        public SslStream? SslStream => m_connectState?.SslStream;

        /// <summary>
        /// Gets the server URI of the <see cref="TlsClient"/>.
        /// </summary>
        public override string ServerUri => $"{TransportProtocol}://{ServerList[m_serverIndex]}".ToLower();

        /// <summary>
        /// Gets or sets network credential that is used when
        /// <see cref="IntegratedSecurity"/> is set to <c>true</c>.
        /// </summary>
        public NetworkCredential? NetworkCredential { get; set; }

        /// <summary>
        /// Gets or sets the certificate checker used to validate remote certificates.
        /// </summary>
        /// <remarks>
        /// The certificate checker will only be used to validate certificates if
        /// the <see cref="RemoteCertificateValidationCallback"/> is set to null.
        /// </remarks>
        public ICertificateChecker CertificateChecker
        {
            get => m_certificateChecker ?? m_defaultCertificateChecker;
            set => m_certificateChecker = value;
        }

        /// <summary>
        /// Gets or sets the callback used to verify remote certificates.
        /// </summary>
        /// <remarks>
        /// Setting this property overrides the validation
        /// callback in the <see cref="CertificateChecker"/>.
        /// </remarks>
        public RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; set; }

        /// <summary>
        /// Gets or sets the callback used to select a local certificate.
        /// </summary>
        public LocalCertificateSelectionCallback? LocalCertificateSelectionCallback { get; set; }

        /// <summary>
        /// Gets the collection of X509 certificates for this client.
        /// </summary>
        public X509CertificateCollection ClientCertificates => m_clientCertificates;

        /// <summary>
        /// Gets or sets a set of flags which determine the enabled <see cref="SslProtocols"/>.
        /// </summary>
        /// <exception cref="SecurityException">Failed to write event log entry for security warning about use of less secure TLS/SSL protocols.</exception>
        public SslProtocols EnabledSslProtocols { get; set; }

        /// <summary>
        /// Gets or sets a boolean value that determines whether the certificate revocation list is checked during authentication.
        /// </summary>
        public bool CheckCertificateRevocation { get; set; }

        /// <summary>
        /// Gets or sets the path to the certificate used for authentication.
        /// </summary>
        public string? CertificateFile
        {
            get => m_certificateFile;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    m_certificateFile = null;
                    Certificate = null;
                }
                else
                {
                    m_certificateFile = FilePath.GetAbsolutePath(value!);

                    if (File.Exists(m_certificateFile))
                        Certificate = new X509Certificate2(m_certificateFile);
                }
            }
        }

        /// <summary>
        /// Gets or sets the local certificate selected by the default <see cref="LocalCertificateSelectionCallback"/>.
        /// </summary>
        public X509Certificate? Certificate { get; set; }

        /// <summary>
        /// Gets or sets the path to the directory containing the trusted certificates.
        /// </summary>
        public string TrustedCertificatesPath { get; set; }

        /// <summary>
        /// Gets or sets the set of valid policy errors when validating remote certificates.
        /// </summary>
        public SslPolicyErrors ValidPolicyErrors
        {
            get => m_defaultCertificateChecker.ValidPolicyErrors;
            set => m_defaultCertificateChecker.ValidPolicyErrors = value;
        }

        /// <summary>
        /// Gets or sets the set of valid chain flags used when validating remote certificates.
        /// </summary>
        public X509ChainStatusFlags ValidChainFlags
        {
            get => m_defaultCertificateChecker.ValidChainFlags;
            set => m_defaultCertificateChecker.ValidChainFlags = value;
        }

        /// <summary>
        /// Determines whether the base class should track statistics.
        /// </summary>
        protected override bool TrackStatistics => false;

        // Gets server connect data as an array - will always be at least one empty string, not null
        private string[] ServerList
        {
            get
            {
                if (m_serverList is not null)
                    return m_serverList;

                if (m_connectData is not null && m_connectData.ContainsKey("server"))
                    m_serverList = m_connectData["server"].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(server => server.Trim()).ToArray();

                return m_serverList ?? Array.Empty<string>();
            }
        }

        /// <summary>
        /// Gets the descriptive status of the client.
        /// </summary>
        public override string Status
        {
            get
            {
                SendState? sendState = m_sendState;
                StringBuilder statusBuilder = new(base.Status);

                if (sendState is not null)
                    statusBuilder.AppendLine($"           Queued payloads: {sendState.SendQueue.Count:N0}");

                return statusBuilder.ToString();
            }
        }

        #endregion

        #region [ Methods ]

        /// <summary>
        /// Connects the <see cref="TlsClient"/> to the server asynchronously.
        /// </summary>
        /// <exception cref="InvalidOperationException">Attempt is made to connect the <see cref="TlsClient"/> when it is not disconnected.</exception>
        /// <returns><see cref="WaitHandle"/> for the asynchronous operation.</returns>
        public override WaitHandle? ConnectAsync()
        {
            ConnectState? connectState = null;

            // If the client is already connecting or connected, there is nothing to do
            if (CurrentState != ClientState.Disconnected || m_disposed)
                return m_connectWaitHandle;

            try
            {
                // If we do not already have a wait handle to use
                // for connections, get one from the base class
                m_connectWaitHandle ??= (ManualResetEvent?)base.ConnectAsync();

                // Create state object for the asynchronous connection loop
                connectState = new ConnectState();

                // Store connectState in m_connectState so that calls to Disconnect
                // and Dispose can dispose resources and cancel asynchronous loops
                m_connectState = connectState;

                // Prepare for connection attempt
                OnConnectionAttempt();
                m_connectWaitHandle?.Reset();

                // Overwrite setting from the config file if integrated security exists in connection string
                if (m_connectData.TryGetValue("integratedSecurity", out string integratedSecuritySetting))
                    IntegratedSecurity = integratedSecuritySetting.ParseBoolean();

                // TODO: Check if this works on Linux
                //// Force integrated security to be False under Mono since it's not supported
                //IntegratedSecurity = false;

                // Overwrite config file if no delay exists in connection string.
                if (m_connectData.TryGetValue("noDelay", out string noDelaySetting))
                    NoDelay = noDelaySetting.ParseBoolean();

                // Initialize state object for the asynchronous connection loop
                Match endpoint = Regex.Match(ServerList[m_serverIndex], Transport.EndpointFormatRegex);
                connectState.ConnectArgs.RemoteEndPoint = Transport.CreateEndPoint(endpoint.Groups["host"].Value, int.Parse(endpoint.Groups["port"].Value), m_ipStack);
                connectState.ConnectArgs.SocketFlags = SocketFlags.None;
                connectState.ConnectArgs.UserToken = connectState;
                connectState.ConnectArgs.Completed += (sender, args) => ProcessConnect((ConnectState)args.UserToken);

                // Create client socket
                connectState.Socket = Transport.CreateSocket(m_connectData["interface"], 0, ProtocolType.Tcp, m_ipStack, AllowDualStackSocket);
                connectState.Socket.NoDelay = NoDelay;

                // Initiate the asynchronous connection loop
                ConnectAsync(connectState);
            }
            catch (Exception ex)
            {
                // Log exception during connection attempt
                OnConnectionException(ex);

                // Terminate the connection
                if (connectState is not null)
                    TerminateConnection(connectState.Token);

                // Ensure that the wait handle is set so that operations waiting
                // for completion of the asynchronous connection loop can continue
                m_connectWaitHandle?.Set();
            }
            finally
            {
                // If the operation was canceled during execution,
                // make sure to dispose of erroneously allocated resources
                if (connectState is { Token.Cancelled: true })
                    connectState.Dispose();
            }

            // Return the wait handle that signals completion
            // of the asynchronous connection loop
            return m_connectWaitHandle;
        }

        /// <summary>
        /// Initiates an asynchronous connection attempt.
        /// </summary>
        private void ConnectAsync(ConnectState connectState)
        {
            if (connectState.Token.Cancelled)
                return;

            if (!(connectState.Socket?.ConnectAsync(connectState.ConnectArgs) ?? false))
                ThreadPool.QueueUserWorkItem(state => ProcessConnect(connectState));
        }

        /// <summary>
        /// Raises the <see cref="ClientBase.ConnectionException"/> event.
        /// </summary>
        /// <param name="ex">Exception to send to <see cref="ClientBase.ConnectionException"/> event.</param>
        protected override void OnConnectionException(Exception ex)
        {
            int serverListLength = ServerList.Length;

            if (serverListLength > 1)
            {
                // When multiple servers are available, move to next server connection
                m_serverIndex++;

                if (m_serverIndex >= serverListLength)
                    m_serverIndex = 0;
            }

            base.OnConnectionException(ex);
        }

        private void ProcessConnect(ConnectState connectState)
        {
            try
            {
                // Quit if this connection loop has been canceled
                if (connectState.Token.Cancelled)
                    return;

                // Increment the number of connection attempts that
                // have occurred in this asynchronous connection loop
                connectState.ConnectionAttempts++;

                // Check the SocketAsyncEventArgs for errors during the asynchronous connection attempt
                if (connectState.ConnectArgs.SocketError != SocketError.Success)
                    throw new SocketException((int)connectState.ConnectArgs.SocketError);

                if (connectState.Socket is null)
                    throw new InvalidOperationException("No socket available while attempting to process connection.");

                // Set the size of the buffer used by the socket to store incoming data from the server
                connectState.Socket.ReceiveBufferSize = ReceiveBufferSize;

                // Create the SslStream object used to perform
                // send and receive operations on the socket
                connectState.NetworkStream = new NetworkStream(connectState.Socket, true);
                connectState.SslStream = new SslStream(connectState.NetworkStream, false, RemoteCertificateValidationCallback ?? CertificateChecker.ValidateRemoteCertificate, LocalCertificateSelectionCallback);

                // Load trusted certificates from
                // the trusted certificates directory
                LoadTrustedCertificates();

                // Begin authentication with the TlsServer
                Match endpoint = Regex.Match(ServerList[m_serverIndex], Transport.EndpointFormatRegex);

                if (!connectState.Token.Cancelled)
                {
                    connectState.CancelTimeout = new Action(() =>
                    {
                        SocketException ex = new((int)SocketError.TimedOut);
                        OnConnectionException(ex);
                        TerminateConnection(connectState.Token);
                        connectState.Dispose();
                    }).DelayAndExecute(15000);

                    try
                    {
                        connectState.SslStream.BeginAuthenticateAsClient(endpoint.Groups["host"].Value, m_clientCertificates, EnabledSslProtocols, CheckCertificateRevocation, ProcessTlsAuthentication, connectState);
                    }
                    catch
                    {
                        connectState.CancelTimeout();
                        throw;
                    }
                }
            }
            catch (SocketException ex)
            {
                // Log exception during connection attempt
                OnConnectionException(ex);

                // If the connection is refused by the server,
                // keep trying until we reach our maximum connection attempts
                if (ex.SocketErrorCode == SocketError.ConnectionRefused && (MaxConnectionAttempts == -1 || connectState.ConnectionAttempts < MaxConnectionAttempts))
                {
                    try
                    {
                        ConnectAsync(connectState);
                    }
                    catch
                    {
                        TerminateConnection(connectState.Token);
                    }
                }
                else
                {
                    // For any other socket exception,
                    // terminate the connection
                    TerminateConnection(connectState.Token);
                }
            }
            catch (Exception ex)
            {
                // Log exception during connection attempt
                OnConnectionException(ex);

                // Terminate the connection
                TerminateConnection(connectState.Token);
            }
            finally
            {
                // If the operation was canceled during execution,
                // make sure to dispose of erroneously allocated resources
                if (connectState.Token.Cancelled)
                    connectState.Dispose();
            }
        }

        /// <summary>
        /// Callback method for asynchronous authenticate operation.
        /// </summary>
        private void ProcessTlsAuthentication(IAsyncResult asyncResult)
        {
            ConnectState? connectState = null;
            ReceiveState? receiveState = null;
            SendState? sendState = null;

            try
            {
                // Get the connect state from the async result
                connectState = (ConnectState)asyncResult.AsyncState;

                if (connectState is null)
                    throw new InvalidOperationException("Connect state null while attempting to process integrated security authentication.");

                // Attempt to cancel the timeout operation
                if (!connectState.CancelTimeout())
                    return;

                // Quit if this connection loop has been canceled
                if (connectState.Token.Cancelled)
                    return;

                // Complete the operation to authenticate with the server
                connectState.SslStream?.EndAuthenticateAsClient(asyncResult);

                // Ensure that this client is authenticated and encrypted
                if (EnabledSslProtocols != SslProtocols.None)
                {
                    if (!(connectState.SslStream?.IsAuthenticated ?? false))
                        throw new InvalidOperationException("Connection could not be established because we could not authenticate with the server.");

                    if (!(connectState.SslStream?.IsEncrypted ?? false))
                        throw new InvalidOperationException("Connection could not be established because the data stream is not encrypted.");
                }

                if (IntegratedSecurity)
                {
                    // Check the state of cancellation one more time before
                    // proceeding to the next step of the connection loop
                    if (connectState.Token.Cancelled)
                        return;

                    // Create the NegotiateStream to begin authentication of the user's Windows credentials
                    connectState.NegotiateStream = new NegotiateStream(connectState.SslStream, true);

                    connectState.CancelTimeout = new Action(() =>
                    {
                        SocketException ex = new((int)SocketError.TimedOut);
                        OnConnectionException(ex);
                        TerminateConnection(connectState.Token);
                        connectState.Dispose();
                    }).DelayAndExecute(15000);

                    try
                    {
                        connectState.NegotiateStream.BeginAuthenticateAsClient(NetworkCredential ?? (NetworkCredential)CredentialCache.DefaultCredentials, string.Empty, ProcessIntegratedSecurityAuthentication, connectState);
                    }
                    catch
                    {
                        connectState.CancelTimeout();
                        throw;
                    }
                }
                else
                {
                    // Initialize state object for the asynchronous send loop
                    sendState = new SendState
                    {
                        Socket = connectState.Socket,
                        NetworkStream = connectState.NetworkStream,
                        SslStream = connectState.SslStream,
                        Token = connectState.Token
                    };

                    // Store sendState in m_sendState so that calls to Disconnect
                    // and Dispose can dispose resources and cancel asynchronous loops
                    m_sendState = sendState;

                    // Check the state of cancellation one more time before
                    // proceeding to the next step of the connection loop
                    if (connectState.Token.Cancelled)
                        return;

                    // Notify of established connection
                    m_connectWaitHandle?.Set();
                    OnConnectionEstablished();

                    // Initialize state object for the asynchronous receive loop
                    receiveState = new ReceiveState
                    {
                        Socket = connectState.Socket,
                        NetworkStream = connectState.NetworkStream,
                        SslStream = connectState.SslStream,
                        Buffer = new byte[m_payloadMarker.Length + Payload.LengthSegment],
                        Token = connectState.Token
                    };

                    // Store receiveState in m_receiveState so that calls to Disconnect
                    // and Dispose can dispose resources and cancel asynchronous loops
                    m_receiveState = receiveState;

                    // Start receiving data
                    if (PayloadAware)
                        ReceivePayloadAwareAsync(receiveState);
                    else
                        ReceivePayloadUnawareAsync(receiveState);

                    // Further socket interactions are handled through the SslStream
                    // object, so the SocketAsyncEventArgs is no longer needed
                    connectState.ConnectArgs.Dispose();
                }
            }
            catch (SocketException ex)
            {
                // Log exception during connection attempt
                OnConnectionException(ex);

                // If connectState is null, we cannot proceed
                if (connectState is null)
                    return;

                // If the connection is refused by the server,
                // keep trying until we reach our maximum connection attempts
                if (ex.SocketErrorCode == SocketError.ConnectionRefused &&
                    (MaxConnectionAttempts == -1 || connectState.ConnectionAttempts < MaxConnectionAttempts))
                {
                    try
                    {
                        ConnectAsync(connectState);
                    }
                    catch
                    {
                        TerminateConnection(connectState.Token);
                    }
                }
                else
                {
                    // For any other socket exception,
                    // terminate the connection
                    TerminateConnection(connectState.Token);
                }
            }
            catch (Exception ex)
            {
                // Log exception during connection attempt
                string errorMessage = $"Unable to authenticate connection to server: {CertificateChecker.ReasonForFailure ?? ex.Message}";
                OnConnectionException(new Exception(errorMessage, ex));

                // Terminate the connection
                if (connectState is not null)
                    TerminateConnection(connectState.Token);
            }
            finally
            {
                // If the operation was canceled during execution,
                // make sure to dispose of erroneously allocated resources
                if (connectState is { Token.Cancelled: true })
                    connectState.Dispose();

                if (receiveState?.Token is { Cancelled: true })
                    receiveState.Dispose();

                if (sendState?.Token is { Cancelled: true })
                    sendState.Dispose();
            }
        }

        private void ProcessIntegratedSecurityAuthentication(IAsyncResult asyncResult)
        {
            ConnectState? connectState = null;
            ReceiveState? receiveState = null;
            SendState? sendState = null;

            try
            {
                // Get the connect state from the async result
                connectState = (ConnectState)asyncResult.AsyncState;

                // Attempt to cancel the timeout operation
                if (!connectState.CancelTimeout())
                    return;

                // Quit if this connection loop has been canceled
                if (connectState.Token.Cancelled)
                    return;

                try
                {
                    // Complete the operation to authenticate with the server
                    connectState.NegotiateStream?.EndAuthenticateAsClient(asyncResult);
                }
                catch (InvalidCredentialException)
                {
                    if (!IgnoreInvalidCredentials)
                        throw;
                }

                // Initialize state object for the asynchronous send loop
                sendState = new SendState
                {
                    Socket = connectState.Socket,
                    NetworkStream = connectState.NetworkStream,
                    SslStream = connectState.SslStream,
                    Token = connectState.Token
                };

                // Store sendState in m_sendState so that calls to Disconnect
                // and Dispose can dispose resources and cancel asynchronous loops
                m_sendState = sendState;

                // Check the state of cancellation one more time before
                // proceeding to the next step of the connection loop
                if (connectState.Token.Cancelled)
                    return;

                // Notify of established connection
                // and begin receiving data.
                m_connectWaitHandle?.Set();
                OnConnectionEstablished();

                // Initialize state object for the asynchronous receive loop
                receiveState = new ReceiveState
                {
                    Token = connectState.Token,
                    Socket = connectState.Socket,
                    NetworkStream = connectState.NetworkStream,
                    SslStream = connectState.SslStream,
                    Buffer = new byte[ReceiveBufferSize]
                };

                // Store receiveState in m_receiveState so that calls to Disconnect
                // and Dispose can dispose resources and cancel asynchronous loops
                m_receiveState = receiveState;

                // Start receiving data
                if (PayloadAware)
                    ReceivePayloadAwareAsync(receiveState);
                else
                    ReceivePayloadUnawareAsync(receiveState);

                // Further socket interactions are handled through the SslStream
                // object, so the SocketAsyncEventArgs is no longer needed
                connectState.ConnectArgs.Dispose();
            }
            catch (SocketException ex)
            {
                // Log exception during connection attempt
                OnConnectionException(ex);

                // If connectState is null, we cannot proceed
                if (connectState is null)
                    return;

                // If the connection is refused by the server,
                // keep trying until we reach our maximum connection attempts
                if (ex.SocketErrorCode == SocketError.ConnectionRefused && (MaxConnectionAttempts == -1 || connectState.ConnectionAttempts < MaxConnectionAttempts))
                {
                    try
                    {
                        ConnectAsync(connectState);
                    }
                    catch
                    {
                        TerminateConnection(connectState.Token);
                    }
                }
                else
                {
                    // For any other socket exception,
                    // terminate the connection
                    TerminateConnection(connectState.Token);
                }
            }
            catch (Exception ex)
            {
                // Log exception during connection attempt
                string errorMessage = $"Unable to authenticate connection to server: {CertificateChecker.ReasonForFailure ?? ex.Message}";
                OnConnectionException(new Exception(errorMessage, ex));

                // Terminate the connection
                if (connectState is not null)
                    TerminateConnection(connectState.Token);
            }
            finally
            {
                if (connectState is not null)
                {
                    // If the operation was canceled during execution,
                    // make sure to dispose of erroneously allocated resources;
                    // otherwise, dispose of the NegotiateStream which is only used for authentication
                    if (connectState.Token.Cancelled)
                        connectState.Dispose();
                    else
                        connectState.NegotiateStream?.Dispose();
                }

                if (receiveState?.Token is { Cancelled: true })
                    receiveState.Dispose();

                if (sendState?.Token is { Cancelled: true })
                    sendState.Dispose();
            }
        }

        /// <summary>
        /// Initiate method for asynchronous receive operation of payload data in "payload-aware" mode.
        /// </summary>
        private void ReceivePayloadAwareAsync(ReceiveState receiveState)
        {
            // Quit if this receive loop has been canceled
            if (receiveState.Token is null || receiveState.Token.Cancelled || receiveState.SslStream is null)
                return;

            int length = receiveState.PayloadLength < 0 ? m_payloadMarker.Length + Payload.LengthSegment : receiveState.PayloadLength;

            receiveState.SslStream.BeginRead(receiveState.Buffer,
                receiveState.Offset,
                length - receiveState.Offset,
                ProcessReceivePayloadAware,
                receiveState);
        }

        /// <summary>
        /// Callback method for asynchronous receive operation of payload data in "payload-aware" mode.
        /// </summary>
        private void ProcessReceivePayloadAware(IAsyncResult asyncResult)
        {
            // Get the receive state from the async result
            ReceiveState receiveState = (ReceiveState)asyncResult.AsyncState;

            // Quit if this receive loop has been canceled
            if (receiveState.Token is null || receiveState.Token.Cancelled || receiveState.SslStream is null)
                return;

            try
            {

                // Determine if the server disconnected gracefully
                if (!(receiveState.Socket?.Connected ?? false))
                    throw new SocketException((int)SocketError.Disconnecting);

                // Update statistics and bytes received
                int bytesReceived = receiveState.SslStream.EndRead(asyncResult);
                UpdateBytesReceived(bytesReceived);
                receiveState.Offset += bytesReceived;

                // Sanity check to determine if the server disconnected gracefully
                if (bytesReceived == 0)
                    throw new SocketException((int)SocketError.Disconnecting);

                if (receiveState is { PayloadLength: < 0, Buffer: not null })
                {
                    // If we haven't parsed the length of the payload yet, attempt to parse it
                    receiveState.PayloadLength = Payload.ExtractLength(receiveState.Buffer, receiveState.Offset, m_payloadMarker, m_payloadEndianOrder);

                    if (receiveState.PayloadLength > 0)
                    {
                        receiveState.Offset = 0;

                        if (receiveState.Buffer.Length < receiveState.PayloadLength)
                            receiveState.Buffer = new byte[receiveState.PayloadLength];
                    }
                }
                else if (receiveState.Offset == receiveState.PayloadLength)
                {
                    // We've received the entire payload so notify the user
                    OnReceiveDataComplete(receiveState.Buffer, receiveState.PayloadLength);

                    // Reset payload length
                    receiveState.Offset = 0;
                    receiveState.PayloadLength = -1;
                }

                // Continue asynchronous loop
                ReceivePayloadAwareAsync(receiveState);
            }
            catch (ObjectDisposedException)
            {
                // Make sure connection is terminated when client is disposed
                if (receiveState is not null)
                    TerminateConnection(receiveState.Token);
            }
            catch (SocketException ex)
            {
                // Log exception during receive operation
                OnReceiveDataException(ex);

                // Terminate connection when socket exception is encountered
                if (receiveState is not null)
                    TerminateConnection(receiveState.Token);
            }
            catch (Exception ex)
            {
                try
                {
                    // For any other exception, notify and resume
                    OnReceiveDataException(ex);
                    ReceivePayloadAwareAsync(receiveState);
                }
                catch
                {
                    // Terminate connection if resume fails
                    if (receiveState is not null)
                        TerminateConnection(receiveState.Token);
                }
            }
            finally
            {
                // If the operation was canceled during execution,
                // make sure to dispose of allocated resources
                if (receiveState is not null && receiveState.Token.Cancelled)
                    receiveState.Dispose();
            }
        }

        /// <summary>
        /// Initiate method for asynchronous receive operation of payload data in "payload-unaware" mode.
        /// </summary>
        private void ReceivePayloadUnawareAsync(ReceiveState receiveState)
        {
            // Quit if this receive loop has been canceled
            if (receiveState.Token is null || receiveState.Token.Cancelled || receiveState.SslStream is null)
                return;

            receiveState.SslStream.BeginRead(receiveState.Buffer,
                                            0,
                                            receiveState.Buffer?.Length ?? 0,
                                            ProcessReceivePayloadUnaware,
                                            receiveState);
        }

        /// <summary>
        /// Callback method for asynchronous receive operation of payload data in "payload-unaware" mode.
        /// </summary>
        private void ProcessReceivePayloadUnaware(IAsyncResult asyncResult)
        {
            // Get the receive state from the async result
            ReceiveState receiveState = (ReceiveState)asyncResult.AsyncState;

            if (receiveState.Token is null || receiveState.Token.Cancelled || receiveState.SslStream is null)
                return;

            try
            {
                // Determine if the server disconnected gracefully
                if (!(receiveState.Socket?.Connected ?? false))
                    throw new SocketException((int)SocketError.Disconnecting);

                // Update bytes received
                int bytesReceived = receiveState.SslStream.EndRead(asyncResult);
                UpdateBytesReceived(bytesReceived);
                receiveState.PayloadLength = bytesReceived;

                // Sanity check to determine if the server disconnected gracefully
                if (bytesReceived == 0)
                    throw new SocketException((int)SocketError.Disconnecting);

                // Notify of received data and resume the asynchronous loop
                OnReceiveDataComplete(receiveState.Buffer, bytesReceived);
                ReceivePayloadUnawareAsync(receiveState);
            }
            catch (ObjectDisposedException)
            {
                // Make sure connection is terminated when client is disposed
                if (receiveState is not null)
                    TerminateConnection(receiveState.Token);
            }
            catch (SocketException ex)
            {
                // Log exception during receive operation
                OnReceiveDataException(ex);

                // Terminate connection when socket exception is encountered
                if (receiveState is not null)
                    TerminateConnection(receiveState.Token);
            }
            catch (Exception ex)
            {
                try
                {
                    // For any other exception, notify and resume
                    OnReceiveDataException(ex);
                    ReceivePayloadUnawareAsync(receiveState);
                }
                catch
                {
                    // Terminate connection if resume fails
                    if (receiveState is not null)
                        TerminateConnection(receiveState.Token);
                }
            }
            finally
            {
                // If the operation was canceled during execution,
                // make sure to dispose of allocated resources
                if (receiveState is not null && receiveState.Token.Cancelled)
                    receiveState.Dispose();
            }
        }

        /// <summary>
        /// When overridden in a derived class, reads a number of bytes from the current received data buffer and writes those bytes into a byte array at the specified offset.
        /// </summary>
        /// <param name="buffer">Destination buffer used to hold copied bytes.</param>
        /// <param name="startIndex">0-based starting index into destination <paramref name="buffer"/> to begin writing data.</param>
        /// <param name="length">The number of bytes to read from current received data buffer and write into <paramref name="buffer"/>.</param>
        /// <returns>The number of bytes read.</returns>
        /// <remarks>
        /// This function should only be called from within the <see cref="ClientBase.ReceiveData"/> event handler. Calling this method outside this event
        /// will have unexpected results.
        /// </remarks>
        public override int Read(byte[] buffer, int startIndex, int length)
        {
            ReceiveState? receiveState = m_receiveState;

            if (receiveState?.Token is null || receiveState.Token.Cancelled)
                return 0;

            buffer.ValidateParameters(startIndex, length);

            if (receiveState.Buffer is null)
                throw new InvalidOperationException("No received data buffer has been defined to read.");

            int sourceLength = receiveState.PayloadLength - ReadIndex;
            int readBytes = length > sourceLength ? sourceLength : length;
            Buffer.BlockCopy(receiveState.Buffer, ReadIndex, buffer, startIndex, readBytes);

            // Update read index for next call
            ReadIndex += readBytes;

            if (ReadIndex >= receiveState.PayloadLength)
                ReadIndex = 0;

            return readBytes;
        }

        /// <summary>
        /// When overridden in a derived class, sends data to the server asynchronously.
        /// </summary>
        /// <param name="data">The buffer that contains the binary data to be sent.</param>
        /// <param name="offset">The zero-based position in the <paramref name="data"/> at which to begin sending data.</param>
        /// <param name="length">The number of bytes to be sent from <paramref name="data"/> starting at the <paramref name="offset"/>.</param>
        /// <returns><see cref="WaitHandle"/> for the asynchronous operation.</returns>
        protected override WaitHandle? SendDataAsync(byte[] data, int offset, int length)
        {
            SendState? sendState = null;

            try
            {
                // Get the current send state
                sendState = m_sendState;

                // Quit if the send loop has been canceled
                if (sendState?.Token is null || sendState.Token.Cancelled)
                    return null;

                // Prepare for payload-aware transmission
                if (PayloadAware)
                    Payload.AddHeader(ref data, ref offset, ref length, m_payloadMarker, m_payloadEndianOrder);

                // Create payload and wait handle.
                TlsClientPayload payload = new()
                {
                    Data = data,
                    Offset = offset,
                    Length = length
                };

                // Execute operation to take action if the client
                // has reached the maximum send queue size
                m_dumpPayloadsOperation.TryRun();

                // Queue payload for sending
                sendState.SendQueue.Enqueue(payload);

                // If the send loop is not already running, start the send loop
                if (!sendState.Token.Cancelled)
                {
                    if (Interlocked.CompareExchange(ref sendState.Sending, 1, 0) == 0)
                        SendPayloadAsync(sendState);

                    // Notify that the send operation has started.
                    OnSendDataStart();

                    // Return the async handle that can be used to wait for the async operation to complete
                    return payload.WaitHandle.WaitHandle;
                }
            }
            catch (Exception ex)
            {
                // Log exception during send operation
                OnSendDataException(ex);
            }
            finally
            {
                // If the operation was canceled during execution,
                // make sure to dispose of allocated resources
                if (sendState?.Token is { Cancelled: true })
                    sendState.Dispose();
            }

            return null;
        }

        /// <summary>
        /// Sends a payload on the socket.
        /// </summary>
        private void SendPayloadAsync(SendState sendState)
        {
            try
            {
                // Quit if this send loop has been canceled
                if (sendState.Token is null || sendState.Token.Cancelled)
                    return;

                if (sendState.SendQueue.TryDequeue(out TlsClientPayload payload))
                {
                    // Save the payload currently
                    // being sent to the send state
                    sendState.Payload = payload;

                    byte[]? data = payload.Data;
                    int offset = payload.Offset;
                    int length = payload.Length;

                    // Send payload to the client asynchronously.
                    sendState.SslStream?.BeginWrite(data, offset, length, ProcessSend, sendState);
                }
                else
                {
                    // No more payloads to send, so stop sending payloads
                    Interlocked.Exchange(ref sendState.Sending, 0);

                    // Double-check to ensure that a new payload didn't appear before exiting the send loop
                    if (!sendState.SendQueue.IsEmpty && Interlocked.CompareExchange(ref sendState.Sending, 1, 0) == 0)
                        ThreadPool.QueueUserWorkItem(state => SendPayloadAsync((SendState)state), sendState);
                }
            }
            catch (Exception ex)
            {
                // Log exception during send operation
                OnSendDataException(ex);

                // Continue asynchronous send loop
                ThreadPool.QueueUserWorkItem(state => SendPayloadAsync((SendState)state), sendState);
            }
            finally
            {
                // If the operation was canceled during execution,
                // make sure to dispose of allocated resources
                if (sendState.Token is { Cancelled: true })
                    sendState.Dispose();
            }
        }

        /// <summary>
        /// Callback method for asynchronous send operation.
        /// </summary>
        private void ProcessSend(IAsyncResult asyncResult)
        {
            // Get the send state from the async result
            SendState? sendState = (SendState)asyncResult.AsyncState;
            ManualResetEventSlim? handle = null;

            if (sendState?.Token is null || sendState.Payload is null)
                return;

            try
            {
                // Get the current payload and its wait handle
                TlsClientPayload payload = sendState.Payload;
                handle = payload.WaitHandle;

                // Quit if this send loop has been canceled
                if (sendState.Token is null || sendState.Token.Cancelled)
                    return;

                // Determine if the server disconnected gracefully
                if (!(sendState.Socket?.Connected ?? false))
                    throw new SocketException((int)SocketError.Disconnecting);

                // Complete the send operation
                sendState.SslStream?.EndWrite(asyncResult);

                try
                {
                    // Set the wait handle to indicate
                    // the send operation has finished
                    handle.Set();
                }
                catch (ObjectDisposedException)
                {
                    // Ignore if the consumer has
                    // disposed of the wait handle
                }

                // Notify that the send operation is complete
                UpdateBytesSent(payload.Length);
                OnSendDataComplete();
            }
            catch (ObjectDisposedException)
            {
                // Make sure connection is terminated when client is disposed
                if (sendState.Token is not null)
                    TerminateConnection(sendState.Token);
            }
            catch (SocketException ex)
            {
                // Log exception during send operation
                OnSendDataException(ex);

                // Terminate connection when socket exception is encountered
                if (sendState.Token is not null)
                    TerminateConnection(sendState.Token);
            }
            catch (Exception ex)
            {
                // For any other exception, notify and resume
                OnSendDataException(ex);
            }
            finally
            {
                // If the operation was canceled during execution,
                // make sure to dispose of allocated resources
                if (sendState.Token is { Cancelled: true })
                    sendState.Dispose();

                try
                {
                    // Make sure to set the wait handle
                    // even if an exception occurs
                    handle?.Set();
                }
                catch (ObjectDisposedException)
                {
                    // Ignore if the consumer has
                    // disposed of the wait handle
                }

                // Attempt to send the next payload
                SendPayloadAsync(sendState);
            }
        }

        /// <summary>
        /// When overridden in a derived class, disconnects client from the server synchronously.
        /// </summary>
        public override void Disconnect()
        {
            try
            {
                if (CurrentState == ClientState.Disconnected)
                    return;

                ConnectState? connectState = m_connectState;
                ReceiveState? receiveState = m_receiveState;
                SendState? sendState = m_sendState;

                if (connectState is not null)
                {
                    TerminateConnection(connectState.Token);
                    connectState.Socket?.Disconnect(false);
                    connectState.Dispose();
                }

                receiveState?.Dispose();
                sendState?.Dispose();
                m_connectWaitHandle?.Set();
            }
            catch (ObjectDisposedException)
            {
                // This can be safely ignored
            }
            catch (Exception ex)
            {
                OnSendDataException(new InvalidOperationException($"Disconnect exception: {ex.Message}", ex));
            }
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="TlsClient"/> and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            if (m_disposed)
                return;

            try
            {
                if (!disposing)
                    return;

                if (m_connectState is not null)
                {
                    TerminateConnection(m_connectState.Token);
                    m_connectState.Dispose();
                    m_connectState = null;
                }

                if (m_receiveState is not null)
                {
                    m_receiveState.Dispose();
                    m_receiveState = null;
                }

                if (m_sendState is not null)
                {
                    m_sendState.Dispose();
                    m_sendState = null;
                }

                if (m_connectWaitHandle is not null)
                {
                    m_connectWaitHandle.Set();
                    m_connectWaitHandle.Dispose();
                    m_connectWaitHandle = null;
                }
            }
            finally
            {
                m_disposed = true;          // Prevent duplicate dispose.
                base.Dispose(disposing);    // Call base class Dispose().
            }
        }

        /// <summary>
        /// When overridden in a derived class, validates the specified <paramref name="connectionString"/>.
        /// </summary>
        /// <param name="connectionString">The connection string to be validated.</param>
        protected override void ValidateConnectionString(string connectionString)
        {
            m_connectData = connectionString.ParseKeyValuePairs();

            // Derive desired IP stack based on specified "interface" setting, adding setting if it's not defined
            m_ipStack = Transport.GetInterfaceIPStack(m_connectData);

            // Check if 'server' property is missing.
            if (!m_connectData.ContainsKey("server") || string.IsNullOrWhiteSpace(m_connectData["server"]))
                throw new ArgumentException($"Server property is missing (Example: {DefaultConnectionString})");

            // Backwards compatibility adjustments.
            // New Format: Server=localhost:8888
            // Old Format: Server=localhost; Port=8888
            if (m_connectData.ContainsKey("port") && !m_connectData["server"].Contains(','))
                m_connectData["server"] = $"{m_connectData["server"]}:{m_connectData["port"]}";

            m_serverList = null;

            foreach (string server in ServerList)
            {
                // Check if 'server' property is valid.
                Match endpoint = Regex.Match(server, Transport.EndpointFormatRegex);

                if (endpoint == Match.Empty)
                    throw new FormatException($"Server property is invalid (Example: {DefaultConnectionString})");

                if (!Transport.IsPortNumberValid(endpoint.Groups["port"].Value))
                    throw new ArgumentOutOfRangeException(nameof(connectionString), $"Server port must between {Transport.PortRangeLow} and {Transport.PortRangeHigh}");
            }
        }

        /// <summary>
        /// Raises the <see cref="ClientBase.SendDataException"/> event.
        /// </summary>
        /// <param name="ex">Exception to send to <see cref="ClientBase.SendDataException"/> event.</param>
        protected override void OnSendDataException(Exception ex)
        {
            if (CurrentState != ClientState.Disconnected)
                base.OnSendDataException(ex);
            else
                LibraryEvents.OnSuppressedException(this, ex);
        }

        /// <summary>
        /// Raises the <see cref="ClientBase.ReceiveDataException"/> event.
        /// </summary>
        /// <param name="ex">Exception to send to <see cref="ClientBase.ReceiveDataException"/> event.</param>
        protected void OnReceiveDataException(SocketException ex)
        {
            if (ex.SocketErrorCode != SocketError.Disconnecting)
                OnReceiveDataException((Exception)ex);
            else
                LibraryEvents.OnSuppressedException(this, ex);
        }

        /// <summary>
        /// Raises the <see cref="ClientBase.ReceiveDataException"/> event.
        /// </summary>
        /// <param name="ex">Exception to send to <see cref="ClientBase.ReceiveDataException"/> event.</param>
        protected override void OnReceiveDataException(Exception ex)
        {
            if (CurrentState != ClientState.Disconnected)
                base.OnReceiveDataException(ex);
            else
                LibraryEvents.OnSuppressedException(this, ex);
        }

        /// <summary>
        /// Dumps payloads from the send queue when the send queue grows too large.
        /// </summary>
        private void DumpPayloads()
        {
            SendState? sendState = m_sendState;

            // Quit if this send loop has been canceled
            if (sendState?.Token is null || sendState.Token.Cancelled)
                return;

            // Check to see if the client has reached the maximum send queue size.
            if (MaxSendQueueSize <= 0 || sendState.SendQueue.Count < MaxSendQueueSize)
                return;

            for (int i = 0; i < MaxSendQueueSize; i++)
            {
                if (sendState.Token.Cancelled)
                    return;

                if (sendState.SendQueue.TryDequeue(out TlsClientPayload payload))
                {
                    payload.WaitHandle.Set();
                    payload.WaitHandle.Dispose();
                }
            }

            throw new InvalidOperationException($"TLS client reached maximum send queue size. {MaxSendQueueSize} payloads dumped from the queue.");
        }

        /// <summary>
        /// Processes the termination of client.
        /// </summary>
        private void TerminateConnection(CancellationToken cancellationToken)
        {
            try
            {
                // Cancel all asynchronous loops associated with the cancellation token and notify user
                // of terminated connection if the connection had not previously been terminated
                if (!cancellationToken.Cancel())
                    OnConnectionTerminated();
            }
            catch (ThreadAbortException)
            {
                // This is a normal exception
                throw;
            }
            catch (Exception ex)
            {
                // Other exceptions can happen (e.g., NullReferenceException) if thread
                // resumes and the class is disposed middle way through this method
                LibraryEvents.OnSuppressedException(this, ex);
            }
        }

        /// <summary>
        /// Returns the certificate set by the user.
        /// </summary>
        private X509Certificate? DefaultLocalCertificateSelectionCallback(object? sender, string targetHost, X509CertificateCollection localCertificates, X509Certificate remoteCertificate, string[] acceptableIssuers)
        {
            return Certificate;
        }

        /// <summary>
        /// Loads the list of trusted certificates into the default certificate checker.
        /// </summary>
        private void LoadTrustedCertificates()
        {
            if (RemoteCertificateValidationCallback is not null || m_certificateChecker is not null)
                return;

            m_defaultCertificateChecker.TrustedCertificates.Clear();

            string trustedCertificatesPath = FilePath.AddPathSuffix(FilePath.GetAbsolutePath(TrustedCertificatesPath));

            foreach (string fileName in FilePath.GetFileList(trustedCertificatesPath))
                m_defaultCertificateChecker.TrustedCertificates.Add(new X509Certificate2(fileName));
        }

        #endregion
    }
}
