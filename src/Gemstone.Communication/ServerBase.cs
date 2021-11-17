//******************************************************************************************************
//  ServerBase.cs - Gbtc
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
//       Added bypass optimizations for high-speed server data access.
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
//       Modified state alternating properties to restart the server when changed.
//  07/17/2009 - Pinal C. Patel
//       Modified SharedSecret to be persisted as an encrypted value.
//  08/05/2009 - Josh L. Patterson
//      Edited Comments.
//  09/14/2009 - Stephen C. Wills
//       Added new header and license agreement.
//  04/14/2011 - Pinal C. Patel
//       Updated to use new serialization methods in GSF.Serialization class.
//  12/02/2011 - J. Ritchie Carroll
//       Updated event data publication to provide "copy" of reusable buffer instead of original
//       buffer since you cannot assume how user will use the buffer (they may cache it).
//  12/04/2011 - J. Ritchie Carroll
//       Modified to use concurrent dictionary.
//  04/26/2012 - Pinal C. Patel
//       Updated Create() static method to apply settings from the configuration string to the created 
//       server instance using reflection.
//  12/13/2012 - Starlynn Danyelle Gilliam
//       Modified Header.
//  05/22/2015 - J. Ritchie Carroll
//       Added ZeroMQ to the create IServer options.
//
//******************************************************************************************************

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Gemstone.ArrayExtensions;
using Gemstone.EventHandlerExtensions;
using Gemstone.StringExtensions;
using Gemstone.Units;

// ReSharper disable VirtualMemberCallInConstructor
// ReSharper disable ConvertToAutoPropertyWhenPossible
namespace Gemstone.Communication
{
    /// <summary>
    /// Base class for a server involved in server-client communication.
    /// </summary>
    public abstract class ServerBase : IServer
    {
        #region [ Members ]

        // Constants

        /// <summary>
        /// Specifies the default value for the <see cref="MaxClientConnections"/> property.
        /// </summary>
        public const int DefaultMaxClientConnections = -1;

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
        /// Occurs when the server is started.
        /// </summary>
        public event EventHandler? ServerStarted;

        /// <summary>
        /// Occurs when the server is stopped.
        /// </summary>
        public event EventHandler? ServerStopped;

        /// <summary>
        /// Occurs when a client connects to the server.
        /// </summary>
        /// <remarks>
        /// <see cref="EventArgs{T}.Argument"/> is the ID of the client that connected to the server.
        /// </remarks>
        public event EventHandler<EventArgs<Guid>>? ClientConnected;

        /// <summary>
        /// Occurs when a client disconnects from the server.
        /// </summary>
        /// <remarks>
        /// <see cref="EventArgs{T}.Argument"/> is the ID of the client that disconnected from the server.
        /// </remarks>
        public event EventHandler<EventArgs<Guid>>? ClientDisconnected;

        /// <summary>
        /// Occurs when an exception is encountered while a client is connecting.
        /// </summary>
        /// <remarks>
        /// <see cref="EventArgs{T}.Argument"/> is the <see cref="Exception"/> encountered when connecting to the client.
        /// </remarks>
        public event EventHandler<EventArgs<Exception>>? ClientConnectingException;

        /// <summary>
        /// Occurs when data is being sent to a client.
        /// </summary>
        /// <remarks>
        /// <see cref="EventArgs{T}.Argument"/> is the ID of the client to which the data is being sent.
        /// </remarks>
        public event EventHandler<EventArgs<Guid>>? SendClientDataStart;

        /// <summary>
        /// Occurs when data has been sent to a client.
        /// </summary>
        /// <remarks>
        /// <see cref="EventArgs{T}.Argument"/> is the ID of the client to which the data has been sent.
        /// </remarks>
        public event EventHandler<EventArgs<Guid>>? SendClientDataComplete;

        /// <summary>
        /// Occurs when an <see cref="Exception"/> is encountered when sending data to a client.
        /// </summary>
        /// <remarks>
        /// <see cref="EventArgs{T1,T2}.Argument1"/> is the ID of the client to which the data was being sent.<br/>
        /// <see cref="EventArgs{T1,T2}.Argument2"/> is the <see cref="Exception"/> encountered when sending data to a client.
        /// </remarks>
        public event EventHandler<EventArgs<Guid, Exception>>? SendClientDataException;

        /// <summary>
        /// Occurs when unprocessed data has been received from a client.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This event can be used to receive a notification that client data has arrived. The <see cref="Read"/> method can then be used
        /// to copy data to an existing buffer. In many cases it will be optimal to use an existing buffer instead of subscribing to the
        /// <see cref="ReceiveClientDataComplete"/> event, however, the data that is available after calling the <see cref="Read"/> method
        /// will be the original unprocessed data received by the client, i.e., not optionally decrypted or decompressed data.
        /// </para>
        /// <para>
        /// <see cref="EventArgs{T1,T2}.Argument1"/> is the ID of the client from which data is received.<br/>
        /// <see cref="EventArgs{T1,T2}.Argument2"/> is the number of bytes received in the buffer from the client.
        /// </para>
        /// </remarks>
        public event EventHandler<EventArgs<Guid, int>>? ReceiveClientData;

        /// <summary>
        /// Occurs when data received from a client has been processed and is ready for consumption.
        /// </summary>
        /// <remarks>
        /// <see cref="EventArgs{T1,T2,T3}.Argument1"/> is the ID of the client from which data is received.<br/>
        /// <see cref="EventArgs{T1,T2,T3}.Argument2"/> is a new buffer containing post-processed data received from the client starting at index zero.<br/>
        /// <see cref="EventArgs{T1,T2,T3}.Argument3"/> is the number of post-processed bytes received in the buffer from the client.
        /// </remarks>
        public event EventHandler<EventArgs<Guid, byte[], int>>? ReceiveClientDataComplete;

        /// <summary>
        /// Occurs when an <see cref="Exception"/> is encountered when receiving data from a client.
        /// </summary>
        /// <remarks>
        /// <see cref="EventArgs{T1,T2}.Argument1"/> is the ID of the client from which the data was being received.<br/>
        /// <see cref="EventArgs{T1,T2}.Argument2"/> is the <see cref="Exception"/> encountered when receiving data from a client.
        /// </remarks>
        public event EventHandler<EventArgs<Guid, Exception>>? ReceiveClientDataException;

        /// <summary>
        /// Occurs when the <see cref="ServerBase"/> has been disposed.
        /// </summary>
        public event EventHandler? Disposed;

        // Fields
        private string? m_configurationString;
        private int m_maxClientConnections;
        private int m_sendBufferSize;
        private int m_receiveBufferSize;
        private Encoding m_textEncoding;
        private readonly TransportProtocol m_transportProtocol;
        private Ticks m_stopTime;
        private Ticks m_startTime;

        #endregion

        #region [ Constructors ]

        /// <summary>
        /// Initializes a new instance of the server.
        /// </summary>
        protected ServerBase()
        {
            ServerID = Guid.NewGuid();
            ReadIndicies = new ConcurrentDictionary<Guid, int>();
            m_textEncoding = Encoding.ASCII;
            CurrentState = ServerState.NotRunning;
            m_maxClientConnections = DefaultMaxClientConnections;
            m_sendBufferSize = DefaultSendBufferSize;
            m_receiveBufferSize = DefaultReceiveBufferSize;
            Name = GetType().Name;
        }

        /// <summary>
        /// Initializes a new instance of the server.
        /// </summary>
        /// <param name="transportProtocol">One of the <see cref="TransportProtocol"/> values.</param>
        /// <param name="configurationString">The data used by the server for initialization.</param>
        protected ServerBase(TransportProtocol transportProtocol, string configurationString) : this()
        {
            m_transportProtocol = transportProtocol;
            ConfigurationString = configurationString;
        }

        #endregion

        #region [ Properties ]

        /// <summary>
        /// Gets or sets the data required by the server to initialize.
        /// </summary>
        public virtual string ConfigurationString
        {
            get => m_configurationString ?? "";
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentNullException(nameof(value));

                ValidateConfigurationString(value);

                m_configurationString = value;
                ReStart();
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of clients that can connect to the server.
        /// </summary>
        /// <remarks>
        /// Set <see cref="MaxClientConnections"/> to -1 to allow infinite client connections.
        /// </remarks>
        public virtual int MaxClientConnections
        {
            get => m_maxClientConnections;
            set => m_maxClientConnections = value < 1 ? -1 : value;
        }

        /// <summary>
        /// Gets or sets the size of the buffer used by the server for sending data to the clients.
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
        /// Gets or sets the size of the buffer used by the server for receiving data from the clients.
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
        /// Gets or sets a boolean value that indicates whether the server is currently enabled.
        /// </summary>
        /// <remarks>
        /// Setting <see cref="Enabled"/> to true will start the server if it is not running, setting
        /// to false will stop the server if it is running.
        /// </remarks>
        public bool Enabled
        {
            get => CurrentState == ServerState.Running;
            set
            {
                if (value && !Enabled)
                    Start();
                else if (!value && Enabled)
                    Stop();
            }
        }

        /// <summary>
        /// Gets a flag that indicates whether the object has been disposed.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Gets or sets the <see cref="Encoding"/> to be used for the text sent to the connected clients.
        /// </summary>
        public virtual Encoding TextEncoding
        {
            get => m_textEncoding;
            set => m_textEncoding = value;
        }

        /// <summary>
        /// Gets the current <see cref="ServerState"/>.
        /// </summary>
        public ServerState CurrentState { get; private set; }

        /// <summary>
        /// Gets the <see cref="TransportProtocol"/> used by the server for the transportation of data with the clients.
        /// </summary>
        public virtual TransportProtocol TransportProtocol => m_transportProtocol;

        /// <summary>
        /// Gets the server's ID.
        /// </summary>
        public virtual Guid ServerID { get; }

        /// <summary>
        /// Gets the IDs of clients connected to the server.
        /// </summary>
        public virtual Guid[] ClientIDs => ReadIndicies.Keys.ToArray();

        /// <summary>
        /// Gets the <see cref="Time"/> for which the server has been running.
        /// </summary>
        public virtual Time RunTime
        {
            get
            {
                Time serverRunTime = 0.0D;

                if (m_startTime > 0)
                {
                    if (CurrentState == ServerState.Running)
                    {
                        // Server is running.
                        serverRunTime = (DateTime.UtcNow.Ticks - m_startTime).ToSeconds();
                    }
                    else
                    {
                        // Server is not running.
                        serverRunTime = (m_stopTime - m_startTime).ToSeconds();
                    }
                }
                return serverRunTime;
            }
        }

        /// <summary>
        /// Gets current read indices for received data buffers incremented at each <see cref="Read"/> call.
        /// </summary>
        protected ConcurrentDictionary<Guid, int> ReadIndicies { get;
        }

        /// <summary>
        /// Gets or sets the unique identifier of the server.
        /// </summary>
        public virtual string Name { get; set; }

        /// <summary>
        /// Gets the descriptive status of the server.
        /// </summary>
        public virtual string Status
        {
            get
            {
                StringBuilder status = new StringBuilder();

                status.AppendLine($"               Server name: {Name}");
                status.AppendLine($"              Server state: {CurrentState}");
                status.AppendLine($"            Server runtime: {RunTime.ToString(3)}");
                status.AppendLine($"         Connected clients: {ReadIndicies.Count:N0}");
                status.AppendLine($"           Maximum clients: {(m_maxClientConnections == -1 ? "Infinite" : m_maxClientConnections.ToString("N0"))}");
                status.AppendLine($"            Receive buffer: {m_receiveBufferSize:N0}");
                status.AppendLine($"        Transport protocol: {m_transportProtocol}");
                status.AppendLine($"        Text encoding used: {m_textEncoding.EncodingName}");

                return status.ToString();
            }
        }

        /// <summary>
        /// Gets a boolean value that indicates
        /// whether the server has been initialized.
        /// </summary>
        protected bool Initialized { get; private set; }

        #endregion

        #region [ Methods ]

        /// <summary>
        /// Releases all the resources used by the <see cref="ServerBase"/> object.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="ServerBase"/> object and optionally releases the managed resources.
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
                    Stop();
                }
            }
            finally
            {
                IsDisposed = true;          // Prevent duplicate dispose.
                Disposed?.SafeInvoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// When overridden in a derived class, starts the server.
        /// </summary>
        public abstract void Start();

        /// <summary>
        /// When overridden in a derived class, stops the server.
        /// </summary>
        public abstract void Stop();

        /// <summary>
        /// When overridden in a derived class, reads a number of bytes from the current received data buffer and writes those bytes into a byte array at the specified offset.
        /// </summary>
        /// <param name="clientID">ID of the client from which data buffer should be read.</param>
        /// <param name="buffer">Destination buffer used to hold copied bytes.</param>
        /// <param name="startIndex">0-based starting index into destination <paramref name="buffer"/> to begin writing data.</param>
        /// <param name="length">The number of bytes to read from current received data buffer and write into <paramref name="buffer"/>.</param>
        /// <returns>The number of bytes read.</returns>
        /// <remarks>
        /// This function should only be called from within the <see cref="ReceiveClientData"/> event handler. Calling this method outside this event
        /// will have unexpected results.
        /// </remarks>
        public abstract int Read(Guid clientID, byte[] buffer, int startIndex, int length);

        /// <summary>
        /// When overridden in a derived class, disconnects a connected client.
        /// </summary>
        /// <param name="clientID">ID of the client to be disconnected.</param>
        public abstract void DisconnectOne(Guid clientID);

        /// <summary>
        /// When overridden in a derived class, validates the specified <paramref name="configurationString"/>.
        /// </summary>
        /// <param name="configurationString">The configuration string to be validated.</param>
        protected abstract void ValidateConfigurationString(string configurationString);

        /// <summary>
        /// When overridden in a derived class, sends data to the specified client asynchronously.
        /// </summary>
        /// <param name="clientID">ID of the client to which the data is to be sent.</param>
        /// <param name="data">The buffer that contains the binary data to be sent.</param>
        /// <param name="offset">The zero-based position in the <paramref name="data"/> at which to begin sending data.</param>
        /// <param name="length">The number of bytes to be sent from <paramref name="data"/> starting at the <paramref name="offset"/>.</param>
        /// <returns><see cref="WaitHandle"/> for the asynchronous operation.</returns>
        protected abstract WaitHandle SendDataToAsync(Guid clientID, byte[] data, int offset, int length);

        /// <summary>
        /// Initializes the server.
        /// </summary>
        /// <remarks>
        /// <see cref="Initialize()"/> is to be called by user-code directly only if the server is not consumed through the designer surface of the IDE.
        /// </remarks>
        public void Initialize()
        {
            if (Initialized)
                return;

            Initialized = true;   // Initialize only once.
        }

        /// <summary>
        /// Sends data to the specified client synchronously.
        /// </summary>
        /// <param name="clientID">ID of the client to which the data is to be sent.</param>
        /// <param name="data">The plain-text data that is to be sent.</param>
        public virtual void SendTo(Guid clientID, string data) => SendTo(clientID, m_textEncoding.GetBytes(data));

        /// <summary>
        /// Sends data to the specified client synchronously.
        /// </summary>
        /// <param name="clientID">ID of the client to which the data is to be sent.</param>
        /// <param name="data">The binary data that is to be sent.</param>
        public virtual void SendTo(Guid clientID, byte[] data) => SendTo(clientID, data, 0, data.Length);

        /// <summary>
        /// Sends data to the specified client synchronously.
        /// </summary>
        /// <param name="clientID">ID of the client to which the data is to be sent.</param>
        /// <param name="data">The buffer that contains the binary data to be sent.</param>
        /// <param name="offset">The zero-based position in the <paramref name="data"/> at which to begin sending data.</param>
        /// <param name="length">The number of bytes to be sent from <paramref name="data"/> starting at the <paramref name="offset"/>.</param>
        public virtual void SendTo(Guid clientID, byte[] data, int offset, int length) => SendToAsync(clientID, data, offset, length).WaitOne();

        /// <summary>
        /// Sends data to all of the connected clients synchronously.
        /// </summary>
        /// <param name="data">The plain-text data that is to be sent.</param>
        public virtual void Multicast(string data) => Multicast(m_textEncoding.GetBytes(data));

        /// <summary>
        /// Sends data to all of the connected clients synchronously.
        /// </summary>
        /// <param name="data">The binary data that is to be sent.</param>
        public virtual void Multicast(byte[] data) => Multicast(data, 0, data.Length);

        /// <summary>
        /// Sends data to all of the connected clients synchronously.
        /// </summary>
        /// <param name="data">The buffer that contains the binary data to be sent.</param>
        /// <param name="offset">The zero-based position in the <paramref name="data"/> at which to begin sending data.</param>
        /// <param name="length">The number of bytes to be sent from <paramref name="data"/> starting at the <paramref name="offset"/>.</param>
        public virtual void Multicast(byte[] data, int offset, int length)
        {
            // Perform asynchronous transmissions.
            WaitHandle[] handles = MulticastAsync(data, offset, length);

            // Wait for transmissions to complete.
            if (handles.Length > 0)
                WaitHandle.WaitAll(handles);
        }

        /// <summary>
        /// Disconnects all of the connected clients.
        /// </summary>
        public virtual void DisconnectAll()
        {
            foreach (Guid clientID in ClientIDs)
                DisconnectOne(clientID);
        }

        /// <summary>
        /// Sends data to the specified client asynchronously.
        /// </summary>
        /// <param name="clientID">ID of the client to which the data is to be sent.</param>
        /// <param name="data">The plain-text data that is to be sent.</param>
        /// <returns><see cref="WaitHandle"/> for the asynchronous operation.</returns>
        public virtual WaitHandle SendToAsync(Guid clientID, string data) => SendToAsync(clientID, m_textEncoding.GetBytes(data));

        /// <summary>
        /// Sends data to the specified client asynchronously.
        /// </summary>
        /// <param name="clientID">ID of the client to which the data is to be sent.</param>
        /// <param name="data">The binary data that is to be sent.</param>
        /// <returns><see cref="WaitHandle"/> for the asynchronous operation.</returns>
        public virtual WaitHandle SendToAsync(Guid clientID, byte[] data) => SendToAsync(clientID, data, 0, data.Length);

        /// <summary>
        /// Sends data to the specified client asynchronously.
        /// </summary>
        /// <param name="clientID">ID of the client to which the data is to be sent.</param>
        /// <param name="data">The buffer that contains the binary data to be sent.</param>
        /// <param name="offset">The zero-based position in the <paramref name="data"/> at which to begin sending data.</param>
        /// <param name="length">The number of bytes to be sent from <paramref name="data"/> starting at the <paramref name="offset"/>.</param>
        /// <returns><see cref="WaitHandle"/> for the asynchronous operation.</returns>
        public virtual WaitHandle SendToAsync(Guid clientID, byte[] data, int offset, int length)
        {
            if (CurrentState == ServerState.Running)
                return SendDataToAsync(clientID, data, offset, length);

            throw new InvalidOperationException("Server is not running");
        }

        /// <summary>
        /// Sends data to all of the connected clients asynchronously.
        /// </summary>
        /// <param name="data">The plain-text data that is to be sent.</param>
        /// <returns>Array of <see cref="WaitHandle"/> for the asynchronous operation.</returns>
        public virtual WaitHandle[] MulticastAsync(string data) => MulticastAsync(m_textEncoding.GetBytes(data));

        /// <summary>
        /// Sends data to all of the connected clients asynchronously.
        /// </summary>
        /// <param name="data">The binary data that is to be sent.</param>
        /// <returns>Array of <see cref="WaitHandle"/> for the asynchronous operation.</returns>
        public virtual WaitHandle[] MulticastAsync(byte[] data) => MulticastAsync(data, 0, data.Length);

        /// <summary>
        /// Sends data to all of the connected clients asynchronously.
        /// </summary>
        /// <param name="data">The buffer that contains the binary data to be sent.</param>
        /// <param name="offset">The zero-based position in the <paramref name="data"/> at which to begin sending data.</param>
        /// <param name="length">The number of bytes to be sent from <paramref name="data"/> starting at the <paramref name="offset"/>.</param>
        /// <returns>Array of <see cref="WaitHandle"/> for the asynchronous operation.</returns>
        public virtual WaitHandle[] MulticastAsync(byte[] data, int offset, int length) => ClientIDs.Select(clientID => SendToAsync(clientID, data, offset, length)).ToArray();

        /// <summary>
        /// Determines whether the given client is currently connected to the server.
        /// </summary>
        /// <param name="clientID">The ID of the client.</param>
        /// <returns>True if the client is connected; false otherwise.</returns>
        public bool IsClientConnected(Guid clientID) => ReadIndicies.ContainsKey(clientID);

        /// <summary>
        /// Raises the <see cref="ServerStarted"/> event.
        /// </summary>
        protected virtual void OnServerStarted()
        {
            CurrentState = ServerState.Running;
            m_stopTime = 0;

            // Save the time when server is started.
            m_startTime = DateTime.UtcNow.Ticks;

            ServerStarted?.SafeInvoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Raises the <see cref="ServerStopped"/> event.
        /// </summary>
        protected virtual void OnServerStopped()
        {
            CurrentState = ServerState.NotRunning;

            // Save the time when server is stopped.
            m_stopTime = DateTime.UtcNow.Ticks;

            ServerStopped?.SafeInvoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Raises the <see cref="ClientConnected"/> event.
        /// </summary>
        /// <param name="clientID">ID of client to send to <see cref="ClientConnected"/> event.</param>
        protected virtual void OnClientConnected(Guid clientID)
        {
            if (ReadIndicies.TryAdd(clientID, 0))
                ClientConnected?.SafeInvoke(this, new EventArgs<Guid>(clientID));
        }

        /// <summary>
        /// Raises the <see cref="ClientDisconnected"/> event.
        /// </summary>
        /// <param name="clientID">ID of client to send to <see cref="ClientDisconnected"/> event.</param>
        protected virtual void OnClientDisconnected(Guid clientID)
        {
            if (ReadIndicies.TryRemove(clientID, out int _))
                ClientDisconnected?.SafeInvoke(this, new EventArgs<Guid>(clientID));
        }

        /// <summary>
        /// Raises the <see cref="ClientConnectingException"/> event.
        /// </summary>
        /// <param name="ex">The <see cref="Exception"/> encountered when connecting to the client.</param>
        protected virtual void OnClientConnectingException(Exception ex)
        {
            ClientConnectingException?.SafeInvoke(this, new EventArgs<Exception>(ex));
        }

        /// <summary>
        /// Raises the <see cref="SendClientDataStart"/> event.
        /// </summary>
        /// <param name="clientID">ID of client to send to <see cref="SendClientDataStart"/> event.</param>
        protected virtual void OnSendClientDataStart(Guid clientID)
        {
            SendClientDataStart?.SafeInvoke(this, new EventArgs<Guid>(clientID));
        }

        /// <summary>
        /// Raises the <see cref="SendClientDataComplete"/> event.
        /// </summary>
        /// <param name="clientID">ID of client to send to <see cref="SendClientDataComplete"/> event.</param>
        protected virtual void OnSendClientDataComplete(Guid clientID)
        {
            SendClientDataComplete?.SafeInvoke(this, new EventArgs<Guid>(clientID));
        }

        /// <summary>
        /// Raises the <see cref="SendClientDataException"/> event.
        /// </summary>
        /// <param name="clientID">ID of client to send to <see cref="SendClientDataException"/> event.</param>
        /// <param name="ex">Exception to send to <see cref="SendClientDataException"/> event.</param>
        protected virtual void OnSendClientDataException(Guid clientID, Exception ex)
        {
            if (ex is not ObjectDisposedException)
                SendClientDataException?.SafeInvoke(this, new EventArgs<Guid, Exception>(clientID, ex));
        }

        /// <summary>
        /// Raises the <see cref="ReceiveClientData"/> event.
        /// </summary>
        /// <param name="clientID">ID of the client from which data is received.</param>
        /// <param name="size">Number of bytes received from the client.</param>
        /// <remarks>
        /// This event is automatically raised by call to <see cref="OnReceiveClientDataComplete"/> so that inheritors
        /// never need to worry about raising this event. This method is only included here in case any custom server
        /// implementations need to explicitly raise this event.
        /// </remarks>
        protected virtual void OnReceiveClientData(Guid clientID, int size)
        {
            ReceiveClientData?.SafeInvoke(this, new EventArgs<Guid, int>(clientID, size));
        }

        /// <summary>
        /// Raises the <see cref="ReceiveClientDataComplete"/> event.
        /// </summary>
        /// <param name="clientID">ID of the client from which data is received.</param>
        /// <param name="data">Data received from the client.</param>
        /// <param name="size">Number of bytes received from the client.</param>
        protected virtual void OnReceiveClientDataComplete(Guid clientID, byte[] data, int size)
        {
            // Reset buffer index used by read method
            ReadIndicies[clientID] = 0;

            // Notify users of data ready
            ReceiveClientData?.SafeInvoke(this, new EventArgs<Guid, int>(clientID, size));

            // Most inheritors of this class "reuse" an existing buffer, as such you cannot assume what the user is going to do
            // with the buffer provided, so we pass in a "copy" of the buffer for the user since they may assume control of and
            // possibly even cache the provided buffer (e.g., passing the buffer to a process queue)
            ReceiveClientDataComplete?.SafeInvoke(this, new EventArgs<Guid, byte[], int>(clientID, data.BlockCopy(0, size), size));
        }

        /// <summary>
        /// Raises the <see cref="ReceiveClientDataException"/> event.
        /// </summary>
        /// <param name="clientID">ID of client to send to <see cref="ReceiveClientDataException"/> event.</param>
        /// <param name="ex">Exception to send to <see cref="ReceiveClientDataException"/> event.</param>
        protected virtual void OnReceiveClientDataException(Guid clientID, Exception ex)
        {
            if (ex is not ObjectDisposedException)
                ReceiveClientDataException?.SafeInvoke(this, new EventArgs<Guid, Exception>(clientID, ex));
        }

        /// <summary>
        /// Re-starts the server if currently running.
        /// </summary>
        protected void ReStart()
        {
            if (CurrentState != ServerState.Running)
                return;

            Stop();
            
            while (CurrentState != ServerState.NotRunning)
                Thread.Sleep(100);

            Start();
        }

        #endregion

        #region [ Static ]

        /// <summary>
        /// Create a communications server
        /// </summary>
        /// <remarks>
        /// Note that typical configuration string should be prefixed with a "protocol=tcp" or a "protocol=udp"
        /// </remarks>
        /// <param name="configurationString">The configuration string for the server.</param>
        /// <returns>A communications server.</returns>
        public static IServer Create(string configurationString)
        {
            if (string.IsNullOrWhiteSpace(configurationString))
                throw new ArgumentNullException(nameof(configurationString));

            Dictionary<string, string> settings = configurationString.ParseKeyValuePairs();
            IServer server;

            if (settings.TryGetValue("protocol", out string protocol))
            {
                settings.Remove("protocol");
                StringBuilder protocolSettings = new StringBuilder();

                foreach (string key in settings.Keys)
                {
                    protocolSettings.Append(key);
                    protocolSettings.Append("=");
                    protocolSettings.Append(settings[key]);
                    protocolSettings.Append(";");
                }

                // Create a server instance for the specified protocol.
                server = (protocol.Trim().ToLowerInvariant()) switch
                {
                    "tls" => new TlsServer(protocolSettings.ToString()),
                    "tcp" => new TcpServer(protocolSettings.ToString()),
                    "udp" => new UdpServer(protocolSettings.ToString()),
                    _ => throw new ArgumentException($"{protocol} is not a supported server transport protocol"),
                };

                // Apply server settings from the connection string to the client.
                foreach (KeyValuePair<string, string> setting in settings)
                {
                    PropertyInfo property = server.GetType().GetProperty(setting.Key, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                    property?.SetValue(server, Convert.ChangeType(setting.Value, property.PropertyType), null);
                }
            }
            else
            {
                throw new ArgumentException("Transport protocol must be specified");
            }

            return server;
        }

        #endregion
    }
}
