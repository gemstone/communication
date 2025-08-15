<img align="right" src="img/gemstone-wide-600.png" alt="gemstone logo">
<br/><br/><br/><br/>

# Communication
### GPA Gemstone Library

The Gemstone Communication Library organizes all Gemstone functionality related to communication, e.g., sockets and serial ports. Communication classes are defined with interfaces, [IServer](https://gemstone.github.io/communication/help/html/T_Gemstone_Communication_IServer.htm) and [IClient](https://gemstone.github.io/communication/help/html/T_Gemstone_Communication_IClient.htm), so communications can be abstracted.

[![GitHub license](https://img.shields.io/github/license/gemstone/communication?color=4CC61E)](https://github.com/gemstone/communication/blob/master/LICENSE)
[![Build status](https://ci.appveyor.com/api/projects/status/xj849729cx34ehki?svg=true)](https://ci.appveyor.com/project/ritchiecarroll/communication)
![CodeQL](https://github.com/gemstone/communication/workflows/CodeQL/badge.svg)
[![NuGet](https://img.shields.io/nuget/vpre/Gemstone.Communication)](https://www.nuget.org/packages/Gemstone.Communication#readme-body-tab)

This library includes helpful communication classes like the following:

* [TcpClient](https://gemstone.github.io/communication/help/html/T_Gemstone_Communication_TcpClient.htm):
  * Represents a TCP-based communication client.
* [TlsClient](https://gemstone.github.io/communication/help/html/T_Gemstone_Communication_TlsClient.htm):
  * Represents a TCP-based communication client with TLS authentication and encryption.
* [UdpClient](https://gemstone.github.io/communication/help/html/T_Gemstone_Communication_UdpClient.htm):
  * Represents a UDP-based communication client.
* [SerialClient](https://gemstone.github.io/communication/help/html/T_Gemstone_Communication_SerialClient.htm):
  * Represents a communication client based on SerialPort.
* [FileClient](https://gemstone.github.io/communication/help/html/T_Gemstone_Communication_FileClient.htm):
  * Represents a communication client based on FileStream.
* [TcpServer](https://gemstone.github.io/communication/help/html/T_Gemstone_Communication_TcpServer.htm):
  * Represents a TCP-based communication server.
* [TlsServer](https://gemstone.github.io/communication/help/html/T_Gemstone_Communication_TlsServer.htm):
  * Represents a TCP-based communication server with TLS authentication and encryption.
* [UdpServer](https://gemstone.github.io/communication/help/html/T_Gemstone_Communication_UdpServer.htm):
  * Represents a UDP-based communication server.

Among others.

### Documentation
[Full Library Documentation](https://gemstone.github.io/communication/help)
