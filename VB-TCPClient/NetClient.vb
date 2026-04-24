Imports System
Imports System.Collections.Concurrent
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks

Namespace ScottJ
    Public Class NetClient
        Implements IDisposable

        Private _socket As Socket
        Private _cancellationSource As CancellationTokenSource
        Private _receiveBuffer(8191) As Byte
        
        ' Internal buffer for "Read" methods
        Private _incomingQueue As New ConcurrentQueue(Of Byte)
        Private _dataSignal As New SemaphoreSlim(0)
        Private _syncLock As New SemaphoreSlim(1, 1)
        
        Private _disposed As Boolean = False

        Public Property Host As String
        Public Property Port As Integer
        Public Property ConnectionTimeout As Integer = 10000
        Public Property ReadTimeout As Integer = 30000
        Public Property Encoding As Encoding = Encoding.UTF8

        ' Events requested by the user
        Public Event Connected(sender As Object, e As EventArgs)
        Public Event Disconnected(sender As Object, e As EventArgs)
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
            If _disposed Then Throw New ObjectDisposedException(Me.GetType().Name)

            Await _syncLock.WaitAsync()
            Try
                ' Validate Host and Port
                If String.IsNullOrEmpty(Host) Then Throw New InvalidOperationException("Host must be specified.")
                If Port <= 0 Then Throw New InvalidOperationException("Valid Port must be specified.")

                ' If already connected, disconnect first to ensure clean state
                If _socket IsNot Nothing AndAlso _socket.Connected Then
                    Await DisconnectInternal()
                End If

                ' Reset state for a fresh connection
                _cancellationSource = New CancellationTokenSource()
                
                ' Clear the queue
                While _incomingQueue.Count > 0
                    Dim b As Byte
                    _incomingQueue.TryDequeue(b)
                End While

                ' Reset the semaphore signal
                _dataSignal.Dispose()
                _dataSignal = New SemaphoreSlim(0)

                _socket = New Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)

                ' Implement connection timeout
                Using ctsTimeout As New CancellationTokenSource(ConnectionTimeout)
                    Try
                        Await _socket.ConnectAsync(Host, Port, ctsTimeout.Token)
                    Catch ex As OperationCanceledException
                        Throw New TimeoutException($"Connection to {Host}:{Port} timed out after {ConnectionTimeout}ms.")
                    End Try
                End Using
                
                RaiseEvent Connected(Me, EventArgs.Empty)

                ' Start receiving data in the background
                Dim receiveTask = Task.Run(Function() ReceiveLoop(), _cancellationSource.Token)

            Catch ex As Exception
                RaiseEvent [Error](Me, ex)
                Throw
            Finally
                _syncLock.Release()
            End Try
        End Function

        Private Async Function ReceiveLoop() As Task
            Try
                While Not _cancellationSource.Token.IsCancellationRequested
                    Dim bytesReceived As Integer = Await _socket.ReceiveAsync(_receiveBuffer, SocketFlags.None, _cancellationSource.Token)

                    If bytesReceived > 0 Then
                        ' Efficiently queue the data
                        For i As Integer = 0 To bytesReceived - 1
                            _incomingQueue.Enqueue(_receiveBuffer(i))
                        Next
                        _dataSignal.Release(bytesReceived)

                        ' Raise DataReceived event with a copy
                        Dim data(bytesReceived - 1) As Byte
                        Array.Copy(_receiveBuffer, data, bytesReceived)
                        RaiseEvent DataReceived(Me, data)
                    Else
                        ' Peer closed the connection
                        Exit While
                    End If
                End While
            Catch ex As OperationCanceledException
                ' Normal shutdown
            Catch ex As Exception
                If Not _cancellationSource.IsCancellationRequested Then
                    RaiseEvent [Error](Me, ex)
                End If
            End Try
            
            ' Ensure cleanup happens
            Dim cleanupTask = Disconnect()
        End Function

        ' --- Non-Blocking Send Methods ---

        Public Async Function SendByte(value As Byte) As Task
            Await SendAsync({value})
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
            If _disposed Then Throw New ObjectDisposedException(Me.GetType().Name)

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
            If _disposed Then Throw New ObjectDisposedException(Me.GetType().Name)
            
            Try
                Using ctsRead As New CancellationTokenSource(ReadTimeout)
                    Using linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctsRead.Token, _cancellationSource.Token)
                        Try
                            Await _dataSignal.WaitAsync(linkedCts.Token)
                        Catch ex As OperationCanceledException
                            If ctsRead.IsCancellationRequested Then
                                Throw New TimeoutException($"Read operation timed out after {ReadTimeout}ms.")
                            Else
                                Throw New InvalidOperationException("Connection closed.")
                            End If
                        End Try
                        
                        Dim b As Byte
                        If _incomingQueue.TryDequeue(b) Then
                            Return b
                        End If
                        Throw New InvalidOperationException("Failed to read byte.")
                    End Using
                End Using
            Catch ex As Exception
                RaiseEvent [Error](Me, ex)
                Throw
            End Try
        End Function

        Public Async Function ReadChar() As Task(Of Char)
            If _disposed Then Throw New ObjectDisposedException(Me.GetType().Name)
            
            Try
                Dim decoder = Encoding.GetDecoder()
                Dim bytes(0) As Byte
                Dim chars(0) As Char
                
                While True
                    ' Note: ReadByte already raises Error event on failure
                    bytes(0) = Await ReadByte()
                    Dim completed As Boolean
                    Dim bytesUsed As Integer
                    Dim charsUsed As Integer
                    decoder.Convert(bytes, 0, 1, chars, 0, 1, False, bytesUsed, charsUsed, completed)
                    if charsUsed > 0 Then
                        Return chars(0)
                    End If
                End While
            Catch ex As Exception
                ' Only raise if it's not already reported by ReadByte (though most will be)
                If Not (TypeOf ex Is TimeoutException Or TypeOf ex Is InvalidOperationException Or TypeOf ex Is SocketException) Then
                    RaiseEvent [Error](Me, ex)
                End If
                Throw
            End Try
            Throw New InvalidOperationException("Connection closed.")
        End Function

        Public Async Function ReadLine(Optional removeCRLF As Boolean = True) As Task(Of String)
            If _disposed Then Throw New ObjectDisposedException(Me.GetType().Name)

            Try
                Dim sb As New StringBuilder()
                Dim lastByte As Byte = 0
                
                While True
                    ' Note: ReadByte already raises Error event on failure
                    Dim b = Await ReadByte()
                    sb.Append(Encoding.GetChars({b}))
                    
                    If lastByte = 13 AndAlso b = 10 Then ' CR (13) and LF (10)
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
                ' Only raise if it's not already reported by ReadByte
                If Not (TypeOf ex Is TimeoutException Or TypeOf ex Is InvalidOperationException Or TypeOf ex Is SocketException) Then
                    RaiseEvent [Error](Me, ex)
                End If
                Throw
            End Try
            Throw New InvalidOperationException("Connection closed.")
        End Function

        Public Async Function Disconnect() As Task
            Await _syncLock.WaitAsync()
            Try
                Await DisconnectInternal()
            Finally
                _syncLock.Release()
            End Try
        End Function

        Private Function DisconnectInternal() As Task
            Try
                If _cancellationSource IsNot Nothing AndAlso Not _cancellationSource.IsCancellationRequested Then
                    _cancellationSource.Cancel()
                End If
                
                If _socket IsNot Nothing Then
                    Try
                        If _socket.Connected Then
                            _socket.Shutdown(SocketShutdown.Both)
                        End If
                    Catch
                        ' Shutdown might fail if already closed by peer
                    Finally
                        _socket.Close()
                        _socket.Dispose()
                        _socket = Nothing
                    End Try
                    RaiseEvent Disconnected(Me, EventArgs.Empty)
                End If

            Catch ex As Exception
                RaiseEvent [Error](Me, ex)
            End Try
            Return Task.CompletedTask
        End Function

        ' --- IDisposable Implementation ---

        Public Sub Dispose() Implements IDisposable.Dispose
            Dispose(True)
            GC.SuppressFinalize(Me)
        End Sub

        Protected Overridable Sub Dispose(disposing As Boolean)
            If Not _disposed Then
                If disposing Then
                    ' Free managed resources
                    DisconnectInternal().GetAwaiter().GetResult()
                    _syncLock?.Dispose()
                    _dataSignal?.Dispose()
                    _cancellationSource?.Dispose()
                End If
                
                ' Free unmanaged resources if any
                _disposed = True
            End If
        End Sub

        Protected Overrides Sub Finalize()
            Dispose(False)
        End Sub

        ' --- DNS Resolution Methods ---

        ''' <summary>
        ''' Resolves Me.Host to its IPv4 and IPv6 addresses as a comma-separated string.
        ''' </summary>
        ''' <param name="nameServer">Optional DNS server (IPv4, IPv6, or FQDN) to query. Uses system default if empty.</param>
        Public Async Function GetHostByName(Optional nameServer As String = Nothing) As Task(Of String)
            If _disposed Then Throw New ObjectDisposedException(Me.GetType().Name)
            If String.IsNullOrEmpty(Host) Then Throw New InvalidOperationException("Host must be specified.")
            Try
                If String.IsNullOrEmpty(nameServer) Then
                    Dim addresses = Await Dns.GetHostAddressesAsync(Host)
                    Return String.Join(", ", addresses.Select(Function(a) a.ToString()))
                Else
                    Dim v4 = Await QueryDnsServer(Host, nameServer, DnsRType.A)
                    Dim v6 = Await QueryDnsServer(Host, nameServer, DnsRType.AAAA)
                    Dim all = v4.Concat(v6).ToList()
                    Return If(all.Count > 0, String.Join(", ", all), String.Empty)
                End If
            Catch ex As Exception
                RaiseEvent [Error](Me, ex)
                Throw
            End Try
        End Function

        ''' <summary>
        ''' Performs a reverse DNS lookup on Me.Host, returning the canonical hostname as a string.
        ''' </summary>
        ''' <param name="nameServer">Optional DNS server (IPv4, IPv6, or FQDN) to query. Uses system default if empty.</param>
        Public Async Function GetNameByHost(Optional nameServer As String = Nothing) As Task(Of String)
            If _disposed Then Throw New ObjectDisposedException(Me.GetType().Name)
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

        ''' <summary>Converts an IP string to its in-addr.arpa / ip6.arpa PTR form.</summary>
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

        ''' <summary>Sends a DNS UDP query to the specified nameserver and returns matching records.</summary>
        Private Shared Async Function QueryDnsServer(name As String, nameServer As String, recordType As DnsRType) As Task(Of List(Of String))
            Dim nsAddr As IPAddress = Nothing
            If Not IPAddress.TryParse(nameServer, nsAddr) Then
                Dim candidates = Await Dns.GetHostAddressesAsync(nameServer)
                nsAddr = candidates.FirstOrDefault(Function(a) a.AddressFamily = AddressFamily.InterNetwork)
                If nsAddr Is Nothing Then nsAddr = candidates.FirstOrDefault()
            End If
            If nsAddr Is Nothing Then Throw New InvalidOperationException($"Cannot resolve nameserver: {nameServer}")

            Dim ep As New IPEndPoint(nsAddr, 53)
            Dim txId As UShort = CUShort(Environment.TickCount And &HFFFF)
            Dim query = BuildDnsQuery(name, txId, recordType)

            Using udp As New UdpClient()
                udp.Client.ReceiveTimeout = 5000
                Await udp.SendAsync(query, query.Length, ep)
                Dim reply = Await udp.ReceiveAsync()
                Return ParseDnsResponse(reply.Buffer, recordType)
            End Using
        End Function

        ''' <summary>Builds a minimal DNS query packet.</summary>
        Private Shared Function BuildDnsQuery(name As String, txId As UShort, recordType As DnsRType) As Byte()
            Using ms As New MemoryStream()
                ' Header
                ms.WriteByte(CByte(txId >> 8)) : ms.WriteByte(CByte(txId And &HFF))
                ms.WriteByte(&H1) : ms.WriteByte(&H0)   ' Flags: RD set
                ms.WriteByte(0) : ms.WriteByte(1)        ' QDCOUNT = 1
                ms.WriteByte(0) : ms.WriteByte(0)        ' ANCOUNT = 0
                ms.WriteByte(0) : ms.WriteByte(0)        ' NSCOUNT = 0
                ms.WriteByte(0) : ms.WriteByte(0)        ' ARCOUNT = 0
                ' Question: encoded FQDN
                For Each label In name.Split("."c)
                    Dim lb = Encoding.ASCII.GetBytes(label)
                    ms.WriteByte(CByte(lb.Length))
                    ms.Write(lb, 0, lb.Length)
                Next
                ms.WriteByte(0)  ' root label
                Dim rt = CUShort(recordType)
                ms.WriteByte(CByte(rt >> 8)) : ms.WriteByte(CByte(rt And &HFF))  ' QTYPE
                ms.WriteByte(0) : ms.WriteByte(1)                                 ' QCLASS = IN
                Return ms.ToArray()
            End Using
        End Function

        ''' <summary>Parses a DNS response packet and extracts records matching recordType.</summary>
        Private Shared Function ParseDnsResponse(data As Byte(), recordType As DnsRType) As List(Of String)
            Dim results As New List(Of String)()
            If data.Length < 12 Then Return results
            Dim anCount As Integer = (data(6) << 8) Or data(7)
            If anCount = 0 Then Return results

            Dim pos As Integer = 12
            pos = DnsSkipName(data, pos) : pos += 4  ' skip QNAME + QTYPE + QCLASS

            For i As Integer = 0 To anCount - 1
                If pos >= data.Length Then Exit For
                pos = DnsSkipName(data, pos)
                If pos + 10 > data.Length Then Exit For
                Dim rtype As UShort = CUShort((data(pos) << 8) Or data(pos + 1))
                pos += 8  ' skip type(2) + class(2) + TTL(4)
                Dim rdLen As Integer = (data(pos) << 8) Or data(pos + 1)
                pos += 2
                If rtype = CUShort(recordType) Then
                    Select Case recordType
                        Case DnsRType.A
                            If rdLen = 4 Then results.Add($"{data(pos)}.{data(pos+1)}.{data(pos+2)}.{data(pos+3)}")
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

        ''' <summary>Advances past a DNS name (handles compression pointers).</summary>
        Private Shared Function DnsSkipName(data As Byte(), pos As Integer) As Integer
            While pos < data.Length
                Dim len As Byte = data(pos)
                If len = 0 Then Return pos + 1
                If (len And &HC0) = &HC0 Then Return pos + 2
                pos += len + 1
            End While
            Return pos
        End Function

        ''' <summary>Reads a DNS name (following compression pointers).</summary>
        Private Shared Function DnsReadName(data As Byte(), pos As Integer) As String
            Dim sb As New StringBuilder()
            While pos < data.Length
                Dim len As Byte = data(pos)
                If len = 0 Then Exit While
                If (len And &HC0) = &HC0 Then
                    Dim ptr As Integer = ((len And &H3F) << 8) Or data(pos + 1)
                    If sb.Length > 0 Then sb.Append(".")
                    sb.Append(DnsReadName(data, ptr))
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
