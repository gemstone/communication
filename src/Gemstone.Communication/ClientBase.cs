//******************************************************************************************************
//  ClientBase.cs - Gbtc
//
//  Copyright © 2015, Grid Protection Alliance.  All Rights Reserved.
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
//  06/01/2006 - Pinal C. Patel
//       Original version of source code generated.
//  09/06/2006 - J. Ritchie Carroll
//       Added bypass optimizations for high-speed client data access.
//  11/30/2007 - Pinal C. Patel
//       Modified the "design time" check in EndInit() method to use LicenseManager.UsageMode property
//       instead of DesignMode property as the former is more accurate than the latter.
//  02/19/2008 - Pinal C. Patel
//       Added code to detect and avoid redundant calls to Dispose().
//  09/29/2008 - J. Ritchie Carroll
//       Converted to C#.
//  06/18/2009 - Pinal C. Patel
//       Fixed the implementation of Enabled property.
//  07/02/2009 - Pinal C. Patel
//       Modified state altering properties to reconnect the client when changed.
//  07/08/2009 - J. Ritchie Carroll
//       Added WaitHandle return value from asynchronous connection.
//  07/15/2009 - Pinal C. Patel
//       Modified Connect() to wait for post-connection processing to complete.
//  07/17/2009 - Pinal C. Patel
//       Modified SharedSecret to be persisted as an encrypted value.
//  08/05/2009 - Josh L. Patterson
//       Edited Comments.
//  09/14/2009 - Stephen C. Wills
//       Added new header and license agreement.
//  11/29/2010 - Pinal C. Patel
//       Updated the implementation of Connect() method so it blocks correctly after updates made to 
//       ConnectAsync() method in the derived classes.
//  04/14/2011 - Pinal C. Patel
//       Updated to use new serialization methods in GSF.Serialization class.
//  12/02/2011 - J. Ritchie Carroll
//       Updated event data publication to provide "copy" of reusable buffer instead of original
//       buffer since you cannot assume how user will use the buffer (they may cache it).
//  12/29/2011 - J. Ritchie Carroll
//       Updated Status property to show ConnectionString information.
//  04/26/2012 - Pinal C. Patel
//       Updated Create() static method to apply settings from the connection string to the created 
//       client instance using reflection.
//  05/22/2015 - J. Ritchie Carroll
//       Added ZeroMQ to the create IClient options.
//
//******************************************************************************************************

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using Gemstone.ArrayExtensions;
using Gemstone.EventHandlerExtensions;
using Gemstone.StringExtensions;
using Gemstone.Units;

// ReSharper disable VirtualMemberCallInConstructor
namespace Gemstone.Communication
{
    /// <summary>
    /// Base class for a client involved in server-client communication.
    /// </summary>
    public abstract class ClientBase : IClient
    {
        #region [ Members ]

        // Constants

        /// <summary>
        /// Specifies the default value for the <see cref="MaxConnectionAttempts"/> property.
        /// </summary>
        public const int DefaultMaxConnectionAttempts = -1;

        /// <summary>
        /// Specifies the default value for the <see cref="SendBufferSize"/> property.
        /// </summary>
        public const int DefaultSendBufferSize = 32768;

        /// <summary>
        /// Specifies the default value for the <see cref="ReceiveBufferSize"/> property.
        /// </summary>
        public const int DefaultReceiveBufferSize = 32768;

        // Events

        /// <summary>
        /// Occurs when client is attempting connection to the server.
        /// </summary>
        public event EventHandler? ConnectionAttempt;

        /// <summary>
        /// Occurs when client connection to the server is established.
        /// </summary>
        public event EventHandler? ConnectionEstablished;

        /// <summary>
        /// Occurs when client connection to the server is terminated.
        /// </summary>
        public event EventHandler? ConnectionTerminated;

        /// <summary>
        /// Occurs when an <see cref="Exception"/> is encountered during connection attempt to the server.
        /// </summary>
        public event EventHandler<EventArgs<Exception>>? ConnectionException;

        /// <summary>
        /// Occurs when the client begins sending data to the server.
        /// </summary>
        public event EventHandler? SendDataStart;

        /// <summary>
        /// Occurs when the client has successfully sent data to the server.
        /// </summary>
        public event EventHandler? SendDataComplete;

        /// <summary>
        /// Occurs when an <see cref="Exception"/> is encountered when sending data to the server.
        /// </summary>
        /// <remarks>
        /// <see cref="EventArgs{T}.Argument"/> is the <see cref="Exception"/> encountered when sending data to the server.
        /// </remarks>
        public event EventHandler<EventArgs<Exception>>? SendDataException;

        /// <summary>
        /// Occurs when unprocessed data has been received from the server.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This event can be used to receive a notification that server data has arrived. The <see cref="Read"/> method can then be used
        /// to copy data to an existing buffer. In many cases it will be optimal to use an existing buffer instead of subscribing to the
        /// <see cref="ReceiveDataComplete"/> event.
        /// </para>
        /// <para>
        /// <see cref="EventArgs{T}.Argument"/> is the number of bytes received in the buffer from the server.
        /// </para>
        /// </remarks>
        public event EventHandler<EventArgs<int>>? ReceiveData;

        /// <summary>
        /// Occurs when data received from the server has been processed and is ready for consumption.
        /// </summary>
        /// <remarks>
        /// <see cref="EventArgs{T1,T2}.Argument1"/> is a new buffer containing post-processed data received from the server starting at index zero.<br/>
        /// <see cref="EventArgs{T1,T2}.Argument2"/> is the number of post-processed bytes received in the buffer from the server.
        /// </remarks>
        public event EventHandler<EventArgs<byte[], int>>? ReceiveDataComplete;

        /// <summary>
        /// Occurs when an <see cref="Exception"/> is encountered when receiving data from the server.
        /// </summary>
        /// <remarks>
        /// <see cref="EventArgs{T}.Argument"/> is the <see cref="Exception"/> encountered when receiving data from the server.
        /// </remarks>
        public event EventHandler<EventArgs<Exception>>? ReceiveDataException;

        /// <summary>
        /// Occurs when the <see cref="ClientBase"/> has been disposed.
        /// </summary>
        public event EventHandler? Disposed;

        // Fields
        private string? m_connectionString;
        private int m_maxConnectionAttempts;
        private int m_sendBufferSize;
        private int m_receiveBufferSize;
        private Encoding m_textEncoding;
        private ClientState m_currentState;
        private readonly TransportProtocol m_transportProtocol;
        private Ticks m_connectTime;
        private Ticks m_disconnectTime;
        private ManualResetEvent? m_connectHandle;
        private bool m_initialized;

        private readonly Action<int> m_updateBytesSent;
        private readonly Action<int> m_updateBytesReceived;

        #endregion

        #region [ Constructors ]

        /// <summary>
        /// Initializes a new instance of the client.
        /// </summary>
        protected ClientBase()
        {
            m_textEncoding = Encoding.ASCII;
            m_currentState = ClientState.Disconnected;
            m_maxConnectionAttempts = DefaultMaxConnectionAttempts;
            m_sendBufferSize = DefaultSendBufferSize;
            m_receiveBufferSize = DefaultReceiveBufferSize;
            Statistics = new TransportStatistics();
            m_updateBytesSent = TrackStatistics ? UpdateBytesSent : new Action<int>(_ => { });
            m_updateBytesReceived = TrackStatistics ? UpdateBytesReceived : new Action<int>(_ => { });
            Name = GetType().Name;
        }

        /// <summary>
        /// Initializes a new instance of the client.
        /// </summary>
        /// <param name="transportProtocol">One of the <see cref="TransportProtocol"/> values.</param>
        /// <param name="connectionString">The data used by the client for connection to a server.</param>
        protected ClientBase(TransportProtocol transportProtocol, string connectionString) : this()
        {
            m_transportProtocol = transportProtocol;
            ConnectionString = connectionString;
        }

        #endregion

        #region [ Properties ]

        /// <summary>
        /// Gets the server URI.
        /// </summary>
        public abstract string ServerUri { get; }

        /// <summary>
        /// Gets or sets the data required by the client to connect to the server.
        /// </summary>
        public virtual string ConnectionString
        {
            get => m_connectionString ?? "";
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentNullException(nameof(value));

                ValidateConnectionString(value);

                m_connectionString = value;
                ReConnect();
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of times the client will attempt to connect to the server.
        /// </summary>
        /// <remarks>Set <see cref="MaxConnectionAttempts"/> to -1 for infinite connection attempts.</remarks>
        public virtual int MaxConnectionAttempts
        {
            get => m_maxConnectionAttempts;
            set => m_maxConnectionAttempts = value < 1 ? -1 : value;
        }

        /// <summary>
        /// Gets or sets the size of the buffer used by the client for sending data to the server.
        /// </summary>
        /// <exception cref="ArgumentException">The value being assigned is either zero or negative.</exception>
        public virtual int SendBufferSize
        {
            get => m_sendBufferSize;
            set
            {
                if (value < 1)
                    throw new ArgumentException("Value cannot be zero or negative");

                m_sendBufferSize = value;
            }
        }

        /// <summary>
        /// Gets or sets the size of the buffer used by the client for receiving data from the server.
        /// </summary>
        /// <exception cref="ArgumentException">The value being assigned is either zero or negative.</exception>
        public virtual int ReceiveBufferSize
        {
            get => m_receiveBufferSize;
            set
            {
                if (value < 1)
                    throw new ArgumentException("Value cannot be zero or negative");

                m_receiveBufferSize = value;
            }
        }

        /// <summary>
        /// Gets or sets a boolean value that indicates whether the client is currently enabled.
        /// </summary>
        /// <remarks>
        /// Setting <see cref="Enabled"/> to true will start connection cycle for the client if it
        /// is not connected, setting to false will disconnect the client if it is connected.
        /// </remarks>
        public bool Enabled
        {
            get => m_currentState == ClientState.Connected;
            set
            {
                if (value && !Enabled)
                    Connect();
                else if (!value && Enabled)
                    Disconnect();
            }
        }

        /// <summary>
        /// Gets a flag that indicates whether the object has been disposed.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Gets or sets the <see cref="Encoding"/> to be used for the text sent to the server.
        /// </summary>
        public virtual Encoding TextEncoding
        {
            get => m_textEncoding;
            set => m_textEncoding = value;
        }

        /// <summary>
        /// Gets the current <see cref="ClientState"/>.
        /// </summary>
        public virtual ClientState CurrentState => m_currentState;

        /// <summary>
        /// Gets the <see cref="TransportProtocol"/> used by the client for the transportation of data with the server.
        /// </summary>
        public virtual TransportProtocol TransportProtocol => m_transportProtocol;

        /// <summary>
        /// Gets the <see cref="Time"/> for which the client has been connected to the server.
        /// </summary>
        public virtual Time ConnectionTime
        {
            get
            {
                Time clientConnectionTime = 0.0D;

                if (m_connectTime > 0)
                {
                    if (m_currentState == ClientState.Connected)
                    {
                        // Client is connected to the server.
                        clientConnectionTime = (DateTime.UtcNow.Ticks - m_connectTime).ToSeconds();
                    }
                    else
                    {
                        // Client is not connected to the server.
                        clientConnectionTime = (m_disconnectTime - m_connectTime).ToSeconds();
                    }
                }

                return clientConnectionTime;
            }
        }

        /// <summary>
        /// Gets or sets current read index for received data buffer incremented at each <see cref="Read"/> call.
        /// </summary>
        protected int ReadIndex { get; set; }

        /// <summary>
        /// Gets or sets the unique identifier of the server.
        /// </summary>
        public virtual string Name { get; set; }

        /// <summary>
        /// Gets the <see cref="TransportStatistics"/> for the client connection.
        /// </summary>
        public TransportStatistics Statistics { get; }

        /// <summary>
        /// Determines whether the base class should track statistics.
        /// </summary>
        protected virtual bool TrackStatistics => true;

        /// <summary>
        /// Gets the descriptive status of the client.
        /// </summary>
        public virtual string Status
        {
            get
            {
                StringBuilder status = new();

                status.AppendLine($"               Client name: {Name}");
                status.AppendLine($"              Client state: {m_currentState}");
                status.AppendLine($"           Connection time: {ConnectionTime.ToString(3)}");
                status.AppendLine($"            Receive buffer: {m_receiveBufferSize:N0}");
                status.AppendLine($"        Transport protocol: {m_transportProtocol}");
                status.AppendLine($"        Text encoding used: {m_textEncoding.EncodingName}");

                return status.ToString();
            }
        }

        #endregion

        #region [ Methods ]

        /// <summary>
        /// Releases all the resources used by the <see cref="ClientBase"/> object.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="ClientBase"/> object and optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            try
            {
                // This will be done regardless of whether the object is finalized or disposed.
                if (disposing)
                {
                    // This will be done only when the object is disposed by calling Dispose().
                    Disconnect();
                    m_connectHandle?.Dispose();
                }
            }
            finally
            {
                IsDisposed = true;  // Prevent duplicate dispose.
                Disposed?.SafeInvoke(this, EventArgs.Empty);
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
        /// This function should only be called from within the <see cref="ReceiveData"/> event handler. Calling this method outside this event
        /// will have unexpected results.
        /// </remarks>
        public abstract int Read(byte[] buffer, int startIndex, int length);

        /// <summary>
        /// When overridden in a derived class, validates the specified <paramref name="connectionString"/>.
        /// </summary>
        /// <param name="connectionString">The connection string to be validated.</param>
        protected abstract void ValidateConnectionString(string connectionString);

        /// <summary>
        /// When overridden in a derived class, sends data to the server asynchronously.
        /// </summary>
        /// <param name="data">The buffer that contains the binary data to be sent.</param>
        /// <param name="offset">The zero-based position in the <paramref name="data"/> at which to begin sending data.</param>
        /// <param name="length">The number of bytes to be sent from <paramref name="data"/> starting at the <paramref name="offset"/>.</param>
        /// <returns><see cref="WaitHandle"/> for the asynchronous operation.</returns>
        protected abstract WaitHandle? SendDataAsync(byte[] data, int offset, int length);

        /// <summary>
        /// Initializes the client.
        /// </summary>
        /// <remarks>
        /// <see cref="Initialize()"/> is to be called by user-code directly only if the client is not consumed through the designer surface of the IDE.
        /// </remarks>
        public void Initialize()
        {
            if (m_initialized)
                return;

            m_initialized = true;   // Initialize only once.
        }

        /// <summary>
        /// Connects the client to the server synchronously.
        /// </summary>
        public virtual void Connect()
        {
            // Start asynchronous connection attempt.
            ConnectAsync();

            // Block until connection is established.
            do
            {
                Thread.Sleep(100);
            }
            while (m_currentState == ClientState.Connecting);
        }

        /// <summary>
        /// Connects the client to the server asynchronously.
        /// </summary>
        /// <exception cref="FormatException">Server property in <see cref="ConnectionString"/> is invalid.</exception>
        /// <exception cref="InvalidOperationException">Attempt is made to connect the client when it is not disconnected.</exception>
        /// <returns><see cref="WaitHandle"/> for the asynchronous operation.</returns>
        /// <remarks>
        /// Derived classes are expected to override this method with protocol specific connection operations. Call the base class
        /// method to obtain an operational wait handle if protocol connection operation doesn't provide one already.
        /// </remarks>
        public virtual WaitHandle? ConnectAsync()
        {
            if (CurrentState != ClientState.Disconnected)
                throw new InvalidOperationException("Client is currently not disconnected");

            // Initialize if uninitialized.
            if (!m_initialized)
                Initialize();

            // Set up connection event wait handle
            m_connectHandle = new ManualResetEvent(false);
            return m_connectHandle;
        }

        /// <summary>
        /// Sends data to the server synchronously.
        /// </summary>
        /// <param name="data">The plain-text data that is to be sent.</param>
        public virtual void Send(string data) => Send(m_textEncoding.GetBytes(data));

        /// <summary>
        /// Sends data to the server synchronously.
        /// </summary>
        /// <param name="data">The binary data that is to be sent.</param>
        public virtual void Send(byte[] data) => Send(data, 0, data.Length);

        /// <summary>
        /// Sends data to the server synchronously.
        /// </summary>
        /// <param name="data">The buffer that contains the binary data to be sent.</param>
        /// <param name="offset">The zero-based position in the <paramref name="data"/> at which to begin sending data.</param>
        /// <param name="length">The number of bytes to be sent from <paramref name="data"/> starting at the <paramref name="offset"/>.</param>
        public virtual void Send(byte[] data, int offset, int length) => SendAsync(data, offset, length)?.WaitOne();

        /// <summary>
        /// Sends data to the server asynchronously.
        /// </summary>
        /// <param name="data">The plain-text data that is to be sent.</param>
        /// <returns><see cref="WaitHandle"/> for the asynchronous operation.</returns>
        public virtual WaitHandle? SendAsync(string data) => SendAsync(m_textEncoding.GetBytes(data));

        /// <summary>
        /// Sends data to the server asynchronously.
        /// </summary>
        /// <param name="data">The binary data that is to be sent.</param>
        /// <returns><see cref="WaitHandle"/> for the asynchronous operation.</returns>
        public virtual WaitHandle? SendAsync(byte[] data) => SendAsync(data, 0, data.Length);

        /// <summary>
        /// Sends data to the server asynchronously.
        /// </summary>
        /// <param name="data">The buffer that contains the binary data to be sent.</param>
        /// <param name="offset">The zero-based position in the <paramref name="data"/> at which to begin sending data.</param>
        /// <param name="length">The number of bytes to be sent from <paramref name="data"/> starting at the <paramref name="offset"/>.</param>
        /// <returns><see cref="WaitHandle"/> for the asynchronous operation.</returns>
        public virtual WaitHandle? SendAsync(byte[] data, int offset, int length)
        {
            if (m_currentState != ClientState.Connected)
                throw new InvalidOperationException("Client is not connected");

            // Update transport statistics
            m_updateBytesSent(length);

            // Initiate send operation
            return SendDataAsync(data, offset, length);
        }

        /// <summary>
        /// When overridden in a derived class, disconnects client from the server synchronously.
        /// </summary>
        public virtual void Disconnect() => m_currentState = ClientState.Disconnected;

        /// <summary>
        /// Updates the <see cref="Statistics"/> pertaining to bytes sent.
        /// </summary>
        /// <param name="bytes"></param>
        protected void UpdateBytesSent(int bytes)
        {
            Statistics.LastSend = DateTime.UtcNow;
            Statistics.LastBytesSent = bytes;
            Statistics.TotalBytesSent += bytes;
        }

        /// <summary>
        /// Updates the <see cref="Statistics"/> pertaining to bytes received.
        /// </summary>
        /// <param name="bytes"></param>
        protected void UpdateBytesReceived(int bytes)
        {
            Statistics.LastReceive = DateTime.UtcNow;
            Statistics.LastBytesReceived = bytes;
            Statistics.TotalBytesReceived += bytes;
        }

        /// <summary>
        /// Raises the <see cref="ConnectionAttempt"/> event.
        /// </summary>
        protected virtual void OnConnectionAttempt()
        {
            m_currentState = ClientState.Connecting;
            ConnectionAttempt?.SafeInvoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Raises the <see cref="ConnectionEstablished"/> event.
        /// </summary>
        protected virtual void OnConnectionEstablished()
        {
            m_currentState = ClientState.Connected;
            m_disconnectTime = 0;

            // Save the time when the client connected to the server.
            m_connectTime = DateTime.UtcNow.Ticks;

            // Signal any waiting threads about successful connection.
            m_connectHandle?.Set();

            ConnectionEstablished?.SafeInvoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Raises the <see cref="ConnectionTerminated"/> event.
        /// </summary>
        protected virtual void OnConnectionTerminated()
        {
            m_currentState = ClientState.Disconnected;

            // Save the time when client was disconnected from the server.
            m_disconnectTime = DateTime.UtcNow.Ticks;

            ConnectionTerminated?.SafeInvoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Raises the <see cref="ConnectionException"/> event.
        /// </summary>
        /// <param name="ex">Exception to send to <see cref="ConnectionException"/> event.</param>
        protected virtual void OnConnectionException(Exception ex)
        {
            if (ex is not ObjectDisposedException)
                ConnectionException?.SafeInvoke(this, new EventArgs<Exception>(ex));
        }

        /// <summary>
        /// Raises the <see cref="SendDataStart"/> event.
        /// </summary>
        protected virtual void OnSendDataStart()
        {
            SendDataStart?.SafeInvoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Raises the <see cref="SendDataComplete"/> event.
        /// </summary>
        protected virtual void OnSendDataComplete()
        {
            SendDataComplete?.SafeInvoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Raises the <see cref="SendDataException"/> event.
        /// </summary>
        /// <param name="ex">Exception to send to <see cref="SendDataException"/> event.</param>
        protected virtual void OnSendDataException(Exception ex)
        {
            if (ex is not ObjectDisposedException)
                SendDataException?.SafeInvoke(this, new EventArgs<Exception>(ex));
        }

        /// <summary>
        /// Raises the <see cref="ReceiveData"/> event.
        /// </summary>
        /// <param name="size">Number of bytes received from the client.</param>
        /// <remarks>
        /// This event is automatically raised by call to <see cref="OnReceiveDataComplete"/> so that inheritors
        /// never need to worry about raising this event. This method is only included here in case any custom client
        /// implementations need to explicitly raise this event.
        /// </remarks>
        protected virtual void OnReceiveData(int size)
        {
            ReceiveData?.SafeInvoke(this, new EventArgs<int>(size));
        }

        /// <summary>
        /// Raises the <see cref="ReceiveDataComplete"/> event.
        /// </summary>
        /// <param name="data">Data received from the client.</param>
        /// <param name="size">Number of bytes received from the client.</param>
        protected virtual void OnReceiveDataComplete(byte[]? data, int size)
        {
            if (data is null)
                return;

            // Update transport statistics
            m_updateBytesReceived(size);

            // Reset buffer index used by read method
            ReadIndex = 0;

            // Notify users of data ready
            ReceiveData?.SafeInvoke(this, new EventArgs<int>(size));

            // Most inheritors of this class "reuse" an existing buffer, as such you cannot assume what the user is going to do
            // with the buffer provided, so we pass in a "copy" of the buffer for the user since they may assume control of and
            // possibly even cache the provided buffer (e.g., passing the buffer to a process queue)
            ReceiveDataComplete?.SafeInvoke(this, new EventArgs<byte[], int>(data.BlockCopy(0, size), size));
        }

        /// <summary>
        /// Raises the <see cref="ReceiveDataException"/> event.
        /// </summary>
        /// <param name="ex">Exception to send to <see cref="ReceiveDataException"/> event.</param>
        protected virtual void OnReceiveDataException(Exception ex)
        {
            if (ex is not ObjectDisposedException)
                ReceiveDataException?.SafeInvoke(this, new EventArgs<Exception>(ex));
        }

        /// <summary>
        /// Re-connects the client if currently connected.
        /// </summary>
        private void ReConnect()
        {
            if (m_currentState != ClientState.Connected)
                return;

            Disconnect();

            while (m_currentState != ClientState.Disconnected)
                Thread.Sleep(100);

            Connect();
        }

        #endregion

        #region [ Static ]

        /// <summary>
        /// Create a communications client
        /// </summary>
        /// <remarks>
        /// Note that typical connection string should be prefixed with a "protocol=tcp", "protocol=udp", "protocol=serial" or "protocol=file"
        /// </remarks>
        /// <returns>A communications client.</returns>
        /// <param name="connectionString">Connection string for the client.</param>
        public static IClient Create(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentNullException(nameof(connectionString));

            Dictionary<string, string> settings = connectionString.ParseKeyValuePairs();
            IClient client;

            if (settings.TryGetValue("protocol", out string protocol))
            {
                settings.Remove("protocol");
                StringBuilder protocolSettings = new();

                foreach (string key in settings.Keys)
                {
                    protocolSettings.Append(key);
                    protocolSettings.Append("=");
                    protocolSettings.Append(settings[key]);
                    protocolSettings.Append(";");
                }

                // Create a client instance for the specified protocol.
                client = (protocol.Trim().ToLowerInvariant()) switch
                {
                    "tls" => new TlsClient(protocolSettings.ToString()),
                    "tcp" => new TcpClient(protocolSettings.ToString()),
                    "udp" => new UdpClient(protocolSettings.ToString()),
                    "file" => new FileClient(protocolSettings.ToString()),
                    "serial" => new SerialClient(protocolSettings.ToString()),
                    _ => throw new ArgumentException($"{protocol} is not a supported client transport protocol"),
                };

                // Apply client settings from the connection string to the client.
                foreach (KeyValuePair<string, string> setting in settings)
                {
                    PropertyInfo property = client.GetType().GetProperty(setting.Key, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                    property?.SetValue(client, Convert.ChangeType(setting.Value, property.PropertyType), null);
                }
            }
            else
            {
                throw new ArgumentException("Transport protocol must be specified");
            }

            return client;
        }

        #endregion
    }
}
