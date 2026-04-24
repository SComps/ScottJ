Imports System
Imports System.Collections.Concurrent
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Channels
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Threading.Tasks

Namespace ScottJ
    Public Class NetClient
        Implements IDisposable

        Private _socket As Socket
        Private _cancellationSource As CancellationTokenSource

        ' 8 KB shared receive buffer — written only by ReceiveLoop
        Private _receiveBuffer(8191) As Byte

        ' Channel(Of Byte) replaces ConcurrentQueue + SemaphoreSlim (E1, RS5)
        ' SingleWriter = ReceiveLoop; SingleReader = ReadByte/ReadChar/ReadLine
        Private _incomingChannel As Channel(Of Byte) =
            Channel.CreateUnbounded(Of Byte)(
                New UnboundedChannelOptions() With {.SingleReader = True, .SingleWriter = True})

        Private _syncLock As New SemaphoreSlim(1, 1)

        ' _disposed accessed only via Volatile.Read / Volatile.Write (R2)
        Private _disposed As Boolean = False

        ' --- Backing fields for guarded properties (S1) ---
        Private _host As String
        Private _port As Integer

        ''' <summary>Remote hostname or IP. Cannot be changed while connected.</summary>
        Public Property Host As String
            Get
                Return _host
            End Get
            Set(value As String)
                If _socket IsNot Nothing AndAlso _socket.Connected Then
                    Throw New InvalidOperationException("Cannot change Host while connected.")
                End If
                _host = value
            End Set
        End Property

        ''' <summary>Remote TCP port (1–65535). Cannot be changed while connected.</summary>
        Public Property Port As Integer
            Get
                Return _port
            End Get
            Set(value As Integer)
                If _socket IsNot Nothing AndAlso _socket.Connected Then
                    Throw New InvalidOperationException("Cannot change Port while connected.")
                End If
                _port = value
            End Set
        End Property

        Public Property ConnectionTimeout As Integer = 10000
        Public Property ReadTimeout As Integer = 30000
        Public Property Encoding As Encoding = Encoding.UTF8

        ''' <summary>If True, automatically attempts to reconnect if the connection drops unexpectedly.</summary>
        Public Property AutoReconnect As Boolean = False

        ''' <summary>Milliseconds to wait before attempting to reconnect. Default is 5000.</summary>
        Public Property ReconnectDelay As Integer = 5000

        ''' <summary>If True, enables TCP Keep-Alive to prevent idle connections from being dropped.</summary>
        Public Property KeepAlive As Boolean = False

        ''' <summary>Maximum size of the receive queue buffer. 0 = Unbounded.</summary>
        Public Property MaxReceiveBufferSize As Integer = 0

        ' Events
        Public Event Connected(sender As Object, e As EventArgs)
        Public Event Disconnected(sender As Object, e As EventArgs)
        Public Event Reconnecting(sender As Object, e As EventArgs)
        Public Event [Error](sender As Object, ex As Exception)
        Public Event DataReceived(sender As Object, data As Byte())

        Public Sub New()
            ' Default constructor
        End Sub

        Public Sub New(host As String, port As Integer)
            Me.Host = host
            Me.Port = port
        End Sub

        Public Async Function Connect() As Task
            If Volatile.Read(_disposed) Then Throw New ObjectDisposedException(Me.GetType().Name)

            Await _syncLock.WaitAsync()
            Try
                ' Validate Host and Port (RS4)
                If String.IsNullOrEmpty(Host) Then Throw New InvalidOperationException("Host must be specified.")
                If Port < 1 OrElse Port > 65535 Then Throw New InvalidOperationException("Port must be between 1 and 65535.")

                ' If already connected, disconnect first
                If _socket IsNot Nothing AndAlso _socket.Connected Then
                    Await DisconnectInternal()
                End If

                _cancellationSource = New CancellationTokenSource()

                ' Reset the channel for a fresh connection (E1)
                If MaxReceiveBufferSize > 0 Then
                    _incomingChannel = Channel.CreateBounded(Of Byte)(
                        New BoundedChannelOptions(MaxReceiveBufferSize) With {
                            .SingleReader = True,
                            .SingleWriter = True,
                            .FullMode = BoundedChannelFullMode.Wait
                        })
                Else
                    _incomingChannel = Channel.CreateUnbounded(Of Byte)(
                        New UnboundedChannelOptions() With {.SingleReader = True, .SingleWriter = True})
                End If

                ' Dual-stack socket: supports both IPv4 and IPv6 (RS2)
                _socket = New Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
                _socket.DualMode = True

                If KeepAlive Then
                    _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, True)
                End If

                ' Connection timeout
                Using ctsTimeout As New CancellationTokenSource(ConnectionTimeout)
                    Try
                        Await _socket.ConnectAsync(Host, Port, ctsTimeout.Token)
                    Catch ex As OperationCanceledException
                        Throw New TimeoutException($"Connection to {Host}:{Port} timed out after {ConnectionTimeout}ms.")
                    End Try
                End Using

                RaiseEvent Connected(Me, EventArgs.Empty)

                ' Start receive loop in the background
                Dim receiveTask = Task.Run(Function() ReceiveLoop(), _cancellationSource.Token)

            Catch ex As Exception
                RaiseEvent [Error](Me, ex)
                Throw
            Finally
                _syncLock.Release()
            End Try
        End Function

        Private Async Function ReceiveLoop() As Task
            ' Capture token once — avoids null-ref if _cancellationSource is replaced concurrently (R3)
            Dim token = _cancellationSource.Token
            Try
                While Not token.IsCancellationRequested
                    Dim bytesReceived As Integer =
                        Await _socket.ReceiveAsync(_receiveBuffer, SocketFlags.None, token)

                    If bytesReceived > 0 Then
                        ' Write bytes to the channel (E1)
                        For i As Integer = 0 To bytesReceived - 1
                            Await _incomingChannel.Writer.WriteAsync(_receiveBuffer(i), token)
                        Next

                        ' Raise DataReceived with a copy
                        Dim data(bytesReceived - 1) As Byte
                        Array.Copy(_receiveBuffer, data, bytesReceived)
                        RaiseEvent DataReceived(Me, data)
                    Else
                        ' Peer closed the connection
                        Exit While
                    End If
                End While
            Catch ex As OperationCanceledException
                ' Normal shutdown — no action needed
            Catch ex As Exception
                If Not token.IsCancellationRequested Then
                    RaiseEvent [Error](Me, ex)
                End If
            End Try

            ' Unblock any waiting readers before cleaning up (R1)
            _incomingChannel.Writer.TryComplete()

            Dim shouldReconnect As Boolean = False
            ' Disconnect only if not already being disposed (R1)
            If Not Volatile.Read(_disposed) Then
                If AutoReconnect AndAlso Not token.IsCancellationRequested Then
                    shouldReconnect = True
                End If
                Await DisconnectInternal(suppressEvents:=shouldReconnect)
            End If

            If shouldReconnect Then
#Disable Warning BC42358
                Task.Run(Async Function()
                             While Not Volatile.Read(_disposed)
                                 RaiseEvent Reconnecting(Me, EventArgs.Empty)
                                 Try
                                     Await Task.Delay(ReconnectDelay)
                                     Await Connect()
                                     Exit While
                                 Catch
                                     ' Try again on next loop iteration
                                 End Try
                             End While
                         End Function)
#Enable Warning BC42358
            End If
        End Function

        ' --- Non-Blocking Send Methods ---

        Public Async Function SendByte(value As Byte) As Task
            Dim buf(0) As Byte
            buf(0) = value
            Await SendAsync(buf)
        End Function

        Public Async Function SendChar(value As Char) As Task
            Dim data = Encoding.GetBytes({value})
            Await SendAsync(data)
        End Function

        Public Async Function SendLine(data As String) As Task
            Dim line = data
            If Not line.EndsWith(vbCrLf) Then
                line &= vbCrLf
            End If
            Dim bytes = Encoding.GetBytes(line)
            Await SendAsync(bytes)
        End Function

        Public Async Function SendAsync(data As Byte()) As Task
            If Volatile.Read(_disposed) Then Throw New ObjectDisposedException(Me.GetType().Name)

            Try
                If _socket IsNot Nothing AndAlso _socket.Connected Then
                    Await _socket.SendAsync(data, SocketFlags.None)
                Else
                    Throw New InvalidOperationException("Not connected to a host.")
                End If
            Catch ex As Exception
                RaiseEvent [Error](Me, ex)
                Throw
            End Try
        End Function

        ' --- Non-Blocking Read Methods ---

        Public Async Function ReadByte() As Task(Of Byte)
            If Volatile.Read(_disposed) Then Throw New ObjectDisposedException(Me.GetType().Name)

            Try
                Using ctsRead As New CancellationTokenSource(ReadTimeout)
                    Using linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctsRead.Token, _cancellationSource.Token)
                        Try
                            ' WaitToReadAsync returns False when the channel is completed (connection closed)
                            If Not Await _incomingChannel.Reader.WaitToReadAsync(linkedCts.Token) Then
                                Throw New InvalidOperationException("Connection closed.")
                            End If
                            Dim b As Byte
                            If _incomingChannel.Reader.TryRead(b) Then Return b
                            Throw New InvalidOperationException("Failed to read byte.")
                        Catch ex As OperationCanceledException
                            If ctsRead.IsCancellationRequested Then
                                Throw New TimeoutException($"Read operation timed out after {ReadTimeout}ms.")
                            Else
                                Throw New InvalidOperationException("Connection closed.")
                            End If
                        End Try
                    End Using
                End Using
            Catch ex As Exception
                RaiseEvent [Error](Me, ex)
                Throw
            End Try
        End Function

        Public Async Function ReadChar() As Task(Of Char)
            If Volatile.Read(_disposed) Then Throw New ObjectDisposedException(Me.GetType().Name)

            Try
                Dim decoder = Encoding.GetDecoder()
                Dim bytes(0) As Byte
                Dim chars(0) As Char

                While True
                    bytes(0) = Await ReadByte()
                    Dim completed As Boolean
                    Dim bytesUsed As Integer
                    Dim charsUsed As Integer
                    decoder.Convert(bytes, 0, 1, chars, 0, 1, False, bytesUsed, charsUsed, completed)
                    If charsUsed > 0 Then Return chars(0)
                End While
            Catch ex As Exception
                If Not (TypeOf ex Is TimeoutException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is SocketException) Then
                    RaiseEvent [Error](Me, ex)
                End If
                Throw
            End Try

            ' R7: unreachable — kept only to satisfy compiler flow analysis
            Throw New InvalidOperationException("Connection closed.")
        End Function

        Public Async Function ReadLine(Optional removeCRLF As Boolean = True) As Task(Of String)
            If Volatile.Read(_disposed) Then Throw New ObjectDisposedException(Me.GetType().Name)

            Try
                Dim sb As New StringBuilder()
                Dim lastByte As Byte = 0
                Dim rawByte(0) As Byte   ' Pre-allocated to avoid per-byte heap allocation (E3)

                While True
                    Dim b = Await ReadByte()
                    rawByte(0) = b
                    sb.Append(Encoding.GetChars(rawByte))

                    If lastByte = 13 AndAlso b = 10 Then   ' CRLF
                        Dim result = sb.ToString()
                        If removeCRLF Then
                            Return result.Substring(0, result.Length - 2)
                        Else
                            Return result
                        End If
                    End If
                    lastByte = b
                End While
            Catch ex As Exception
                If Not (TypeOf ex Is TimeoutException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is SocketException) Then
                    RaiseEvent [Error](Me, ex)
                End If
                Throw
            End Try

            ' R7: unreachable — kept only to satisfy compiler flow analysis
            Throw New InvalidOperationException("Connection closed.")
        End Function

        Public Async Function TryReadLineAsync(Optional removeCRLF As Boolean = True) As Task(Of Tuple(Of Boolean, String))
            Try
                Dim result = Await ReadLine(removeCRLF)
                Return Tuple.Create(True, result)
            Catch ex As TimeoutException
                Return Tuple.Create(False, String.Empty)
            End Try
        End Function

        Public Async Function Disconnect(Optional force As Boolean = False) As Task
            Await _syncLock.WaitAsync()
            Try
                Await DisconnectInternal(force:=force)
            Finally
                _syncLock.Release()
            End Try
        End Function

        ''' <summary>
        ''' Core disconnect logic. Thread-safe via Interlocked.Exchange on the socket reference (R1).
        ''' </summary>
        ''' <param name="suppressEvents">True when called from Dispose to avoid raising events on a torn-down object (R6).</param>
        Private Function DisconnectInternal(Optional suppressEvents As Boolean = False, Optional force As Boolean = False) As Task
            Try
                If _cancellationSource IsNot Nothing AndAlso Not _cancellationSource.IsCancellationRequested Then
                    _cancellationSource.Cancel()
                End If

                ' Atomically take ownership of the socket — prevents double-close races (R1)
                Dim sock As Socket = Interlocked.Exchange(_socket, Nothing)
                If sock IsNot Nothing Then
                    Try
                        If force Then
                            sock.LingerState = New LingerOption(True, 0)
                        End If
                        If sock.Connected Then sock.Shutdown(SocketShutdown.Both)
                    Catch
                        ' Shutdown may fail if peer already closed
                    Finally
                        sock.Close()
                        sock.Dispose()
                    End Try
                    If Not suppressEvents Then
                        RaiseEvent Disconnected(Me, EventArgs.Empty)
                    End If
                End If

            Catch ex As Exception
                If Not suppressEvents Then
                    RaiseEvent [Error](Me, ex)
                End If
            End Try
            Return Task.CompletedTask
        End Function

        ' --- IDisposable Implementation ---

        Public Sub Dispose() Implements IDisposable.Dispose
            Dispose(True)
            GC.SuppressFinalize(Me)
        End Sub

        Protected Overridable Sub Dispose(disposing As Boolean)
            If Not Volatile.Read(_disposed) Then
                If disposing Then
                    ' Suppress events during disposal (R6); complete channel before socket teardown (R1)
                    _incomingChannel.Writer.TryComplete()
                    DisconnectInternal(suppressEvents:=True).GetAwaiter().GetResult()
                    _syncLock?.Dispose()
                    _cancellationSource?.Dispose()
                End If
                Volatile.Write(_disposed, True)   ' R2: ensure visibility across threads
            End If
        End Sub

        Protected Overrides Sub Finalize()
            Dispose(False)
        End Sub

        ' --- DNS Resolution Methods ---

        ''' <summary>
        ''' Resolves Me.Host to its IPv4 and IPv6 addresses as a comma-separated string.
        ''' </summary>
        ''' <param name="nameServer">Optional DNS server (IPv4, IPv6, or FQDN). Uses system default if empty.</param>
        Public Async Function GetHostByName(Optional nameServer As String = Nothing) As Task(Of String)
            If Volatile.Read(_disposed) Then Throw New ObjectDisposedException(Me.GetType().Name)
            If String.IsNullOrEmpty(Host) Then Throw New InvalidOperationException("Host must be specified.")
            Try
                If String.IsNullOrEmpty(nameServer) Then
                    Dim addresses = Await Dns.GetHostAddressesAsync(Host)
                    Return String.Join(", ", addresses.Select(Function(a) a.ToString()))
                Else
                    ' Fire A and AAAA queries in parallel (E2)
                    Dim v4Task = QueryDnsServer(Host, nameServer, DnsRType.A)
                    Dim v6Task = QueryDnsServer(Host, nameServer, DnsRType.AAAA)
                    Await Task.WhenAll(v4Task, v6Task)
                    Dim all = v4Task.Result.Concat(v6Task.Result).ToList()
                    Return If(all.Count > 0, String.Join(", ", all), String.Empty)
                End If
            Catch ex As Exception
                RaiseEvent [Error](Me, ex)
                Throw
            End Try
        End Function

        ''' <summary>
        ''' Performs a reverse DNS lookup on Me.Host, returning the canonical hostname.
        ''' </summary>
        ''' <param name="nameServer">Optional DNS server (IPv4, IPv6, or FQDN). Uses system default if empty.</param>
        Public Async Function GetNameByHost(Optional nameServer As String = Nothing) As Task(Of String)
            If Volatile.Read(_disposed) Then Throw New ObjectDisposedException(Me.GetType().Name)
            If String.IsNullOrEmpty(Host) Then Throw New InvalidOperationException("Host must be specified.")
            Try
                If String.IsNullOrEmpty(nameServer) Then
                    Dim entry = Await Dns.GetHostEntryAsync(Host)
                    Return entry.HostName
                Else
                    Dim ptrName = BuildPtrName(Host)
                    Dim results = Await QueryDnsServer(ptrName, nameServer, DnsRType.PTR)
                    Return If(results.Count > 0, String.Join(", ", results), String.Empty)
                End If
            Catch ex As Exception
                RaiseEvent [Error](Me, ex)
                Throw
            End Try
        End Function

        ' --- Private DNS Helpers ---

        Private Enum DnsRType As UShort
            A = 1
            PTR = 12
            AAAA = 28
        End Enum

        ''' <summary>Converts an IP string to its .in-addr.arpa / .ip6.arpa PTR form.</summary>
        Private Shared Function BuildPtrName(ipOrHost As String) As String
            Dim addr As IPAddress = Nothing
            If IPAddress.TryParse(ipOrHost, addr) Then
                If addr.AddressFamily = AddressFamily.InterNetwork Then
                    Dim parts = addr.ToString().Split("."c)
                    Array.Reverse(parts)
                    Return String.Join(".", parts) & ".in-addr.arpa"
                Else
                    Dim nibbles = addr.GetAddressBytes() _
                        .SelectMany(Function(b) New String() {(b >> 4).ToString("x"), (b And &HF).ToString("x")}) _
                        .ToArray()
                    Array.Reverse(nibbles)
                    Return String.Join(".", nibbles) & ".ip6.arpa"
                End If
            End If
            Return ipOrHost
        End Function

        ''' <summary>
        ''' Sends a DNS UDP query to the specified nameserver, with retry (RS3) and txId verification (RS3).
        ''' </summary>
        Private Shared Async Function QueryDnsServer(name As String, nameServer As String, recordType As DnsRType) As Task(Of List(Of String))
            Dim nsAddr As IPAddress = Nothing
            If Not IPAddress.TryParse(nameServer, nsAddr) Then
                Dim candidates = Await Dns.GetHostAddressesAsync(nameServer)
                nsAddr = candidates.FirstOrDefault(Function(a) a.AddressFamily = AddressFamily.InterNetwork)
                If nsAddr Is Nothing Then nsAddr = candidates.FirstOrDefault()
            End If
            If nsAddr Is Nothing Then Throw New InvalidOperationException($"Cannot resolve nameserver: {nameServer}")

            Dim ep As New IPEndPoint(nsAddr, 53)

            ' Cryptographically random transaction ID (S2, E5)
            Dim txId As UShort = CUShort(RandomNumberGenerator.GetInt32(0, 65536))
            Dim query = BuildDnsQuery(name, txId, recordType)

            ' Retry up to 3 attempts on timeout (RS3)
            Const maxAttempts As Integer = 3
            For attempt As Integer = 1 To maxAttempts
                Using udp As New UdpClient()
                    udp.Client.ReceiveTimeout = 3000
                    Try
                        Await udp.SendAsync(query, query.Length, ep)
                        Dim reply = Await udp.ReceiveAsync()

                        ' Verify reply transaction ID matches query (RS3)
                        Dim replyId As UShort = CUShort((reply.Buffer(0) << 8) Or reply.Buffer(1))
                        If replyId <> txId Then Continue For   ' stale/mismatched — retry

                        Return ParseDnsResponse(reply.Buffer, recordType)
                    Catch ex As SocketException When ex.SocketErrorCode = SocketError.TimedOut
                        If attempt = maxAttempts Then Throw
                        ' else retry
                    End Try
                End Using
            Next

            Return New List(Of String)()
        End Function

        ''' <summary>Builds a minimal RFC 1035 DNS query packet.</summary>
        Private Shared Function BuildDnsQuery(name As String, txId As UShort, recordType As DnsRType) As Byte()
            Using ms As New MemoryStream(64)   ' Pre-sized: typical query is 30–50 bytes (E6)
                ' Header
                ms.WriteByte(CByte(txId >> 8)) : ms.WriteByte(CByte(txId And &HFF))
                ms.WriteByte(&H1) : ms.WriteByte(&H0)    ' Flags: RD set
                ms.WriteByte(0) : ms.WriteByte(1)        ' QDCOUNT = 1
                ms.WriteByte(0) : ms.WriteByte(0)        ' ANCOUNT = 0
                ms.WriteByte(0) : ms.WriteByte(0)        ' NSCOUNT = 0
                ms.WriteByte(0) : ms.WriteByte(0)        ' ARCOUNT = 0
                ' Encoded FQDN
                For Each label In name.Split("."c)
                    Dim lb = Encoding.ASCII.GetBytes(label)
                    ms.WriteByte(CByte(lb.Length))
                    ms.Write(lb, 0, lb.Length)
                Next
                ms.WriteByte(0)   ' root label
                Dim rt = CUShort(recordType)
                ms.WriteByte(CByte(rt >> 8)) : ms.WriteByte(CByte(rt And &HFF))   ' QTYPE
                ms.WriteByte(0) : ms.WriteByte(1)                                  ' QCLASS = IN
                Return ms.ToArray()
            End Using
        End Function

        ''' <summary>Parses a DNS response packet and returns records matching recordType.</summary>
        Private Shared Function ParseDnsResponse(data As Byte(), recordType As DnsRType) As List(Of String)
            Dim results As New List(Of String)()
            If data.Length < 12 Then Return results

            ' Cap anCount to a sane limit to prevent DoS from crafted packets (S4)
            Dim anCount As Integer = Math.Min((data(6) << 8) Or data(7), 100)
            If anCount = 0 Then Return results

            Dim pos As Integer = 12
            pos = DnsSkipName(data, pos) : pos += 4   ' skip QNAME + QTYPE + QCLASS

            For i As Integer = 0 To anCount - 1
                If pos >= data.Length Then Exit For
                pos = DnsSkipName(data, pos)
                If pos + 10 > data.Length Then Exit For

                Dim rtype As UShort = CUShort((data(pos) << 8) Or data(pos + 1))
                pos += 8   ' skip type(2) + class(2) + TTL(4)
                Dim rdLen As Integer = (data(pos) << 8) Or data(pos + 1)
                pos += 2

                ' Bounds-check rdLen before trusting it (R4, S4)
                If rdLen < 0 OrElse pos + rdLen > data.Length Then Exit For

                If rtype = CUShort(recordType) Then
                    Select Case recordType
                        Case DnsRType.A
                            If rdLen = 4 Then results.Add($"{data(pos)}.{data(pos + 1)}.{data(pos + 2)}.{data(pos + 3)}")
                        Case DnsRType.AAAA
                            If rdLen = 16 Then
                                Dim b(15) As Byte : Array.Copy(data, pos, b, 0, 16)
                                results.Add(New IPAddress(b).ToString())
                            End If
                        Case DnsRType.PTR
                            results.Add(DnsReadName(data, pos))
                    End Select
                End If
                pos += rdLen
            Next
            Return results
        End Function

        ''' <summary>Advances past a DNS name field (handles compression pointers).</summary>
        Private Shared Function DnsSkipName(data As Byte(), pos As Integer) As Integer
            While pos < data.Length
                Dim len As Byte = data(pos)
                If len = 0 Then Return pos + 1
                If (len And &HC0) = &HC0 Then Return pos + 2   ' compression pointer
                pos += len + 1
            End While
            Return pos
        End Function

        ''' <summary>
        ''' Reads a DNS name, following compression pointers.
        ''' depth guard prevents infinite recursion on circular pointer chains (R5).
        ''' </summary>
        Private Shared Function DnsReadName(data As Byte(), pos As Integer, Optional depth As Integer = 0) As String
            ' Limit recursion depth to guard against circular pointer chains (R5)
            If depth > 10 OrElse pos >= data.Length Then Return String.Empty

            Dim sb As New StringBuilder()
            While pos < data.Length
                Dim len As Byte = data(pos)
                If len = 0 Then Exit While
                If (len And &HC0) = &HC0 Then
                    ' Compression pointer
                    If pos + 1 >= data.Length Then Exit While
                    Dim ptr As Integer = ((len And &H3F) << 8) Or data(pos + 1)
                    If sb.Length > 0 Then sb.Append(".")
                    sb.Append(DnsReadName(data, ptr, depth + 1))
                    Exit While
                End If
                pos += 1
                If sb.Length > 0 Then sb.Append(".")
                sb.Append(Encoding.ASCII.GetString(data, pos, len))
                pos += len
            End While
            Return sb.ToString()
        End Function

    End Class
End Namespace
