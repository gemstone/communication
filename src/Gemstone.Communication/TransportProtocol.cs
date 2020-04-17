﻿//******************************************************************************************************
//  TransportProtocol.cs - Gbtc
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
//  12/03/2008 - Pinal C. Patel
//       Generated original version of source code.
//  09/14/2009 - Stephen C. Wills
//       Added new header and license agreement.
//  12/13/2012 - Starlynn Danyelle Gilliam
//       Modified Header.
//
//******************************************************************************************************

namespace Gemstone.Communication
{
    /// <summary>
    /// Indicates the protocol used in server-client communication.
    /// </summary>
    public enum TransportProtocol
    {
        /// <summary>
        /// <see cref="TransportProtocol"/> is Transmission Control Protocol.
        /// </summary>
        Tcp,
        /// <summary>
        /// <see cref="TransportProtocol"/> is User Datagram Protocol.
        /// </summary>
        Udp,
        /// <summary>
        /// <see cref="TransportProtocol"/> is serial interface.
        /// </summary>
        Serial,
        /// <summary>
        /// <see cref="TransportProtocol"/> is file-system based.
        /// </summary>
        File
    }
}
