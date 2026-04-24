Imports System
Imports System.Collections.Concurrent
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Channels
Imports System.Threading.Tasks
Imports System.Linq

Namespace ScottJ

    ' ==========================================================================
    '  ConnectionRequestEventArgs
    '  Passed to NetListen.ConnectionRequest so the handler can accept or reject.
    ' ==========================================================================
    Public Class ConnectionRequestEventArgs
        Inherits EventArgs

        ''' <summary>IP address of the connecting client (string form).</summary>
        Public ReadOnly Property RemoteAddress As String

        ''' <summary>Source port of the connecting client.</summary>
        Public ReadOnly Property RemotePort As Integer

        ''' <summary>Full remote endpoint of the connecting client.</summary>
        Public ReadOnly Property RemoteEndPoint As IPEndPoint

        ''' <summary>IP address of the local interface the client connected to.</summary>
        Public ReadOnly Property LocalAddress As String

        ''' <summary>Local port the client connected to.</summary>
        Public ReadOnly Property LocalPort As Integer

        ''' <summary>Full local endpoint the client connected to.</summary>
        Public ReadOnly Property LocalEndPoint As IPEndPoint

        ''' <summary>
        ''' Set to False in the ConnectionRequest handler to reject this connection.
        ''' Defaults to True — all connections are accepted unless explicitly rejected.
        ''' </summary>
        Public Property Accept As Boolean = True

        Friend Sub New(remoteEp As IPEndPoint, localEp As IPEndPoint)
            RemoteEndPoint = remoteEp
            RemoteAddress = remoteEp.Address.ToString()
            RemotePort = remoteEp.Port
            LocalEndPoint = localEp
            LocalAddress = localEp.Address.ToString()
            LocalPort = localEp.Port
        End Sub
    End Class

    ' ==========================================================================
    '  NetSession
    '  Represents a fully bidirectional accepted inbound connection.
    '  API mirrors NetClient: same send/read methods, same events, same patterns.
    ' ==========================================================================
    Public Class NetSession
        Implements IDisposable

        ' Socket is non-readonly so Interlocked.Exchange can null it atomically
        Private _socket As Socket
        Private _cancellationSource As CancellationTokenSource
        Private _receiveBuffer(8191) As Byte
        Private _incomingChannel As Channel(Of Byte)
        Private _disposed As Boolean = False

        ' --- Connection identity (read-only after construction) ---

        ''' <summary>Unique identifier for this session.</summary>
        Public ReadOnly Property Id As Guid = Guid.NewGuid()

        ''' <summary>Remote client IP address string.</summary>
        Public ReadOnly Property RemoteAddress As String

        ''' <summary>Remote client source port.</summary>
        Public ReadOnly Property RemotePort As Integer

        ''' <summary>Full remote endpoint.</summary>
        Public ReadOnly Property RemoteEndPoint As IPEndPoint

        ''' <summary>Local IP address string.</summary>
        Public ReadOnly Property LocalAddress As String

        ''' <summary>Local port.</summary>
        Public ReadOnly Property LocalPort As Integer

        ''' <summary>Full local endpoint.</summary>
        Public ReadOnly Property LocalEndPoint As IPEndPoint

        ' --- Configurable (consistent with NetClient) ---

        ''' <summary>Milliseconds ReadByte / ReadChar / ReadLine will wait for data. Default 30000.</summary>
        Public Property ReadTimeout As Integer = 30000

        ''' <summary>Encoding used by SendChar, SendLine, ReadChar, ReadLine. Default UTF-8.</summary>
        Public Property Encoding As Encoding = Encoding.UTF8

        ' --- Events (consistent with NetClient) ---

        Public Event Disconnected(sender As Object, e As EventArgs)
        Public Event [Error](sender As Object, ex As Exception)
        Public Event DataReceived(sender As Object, data As Byte())

        ' --- Internal construction (called by NetListen only) ---

        Friend Sub New(acceptedSocket As Socket, maxReceiveBufferSize As Integer)
            _socket = acceptedSocket
            Dim remoteEp = CType(_socket.RemoteEndPoint, IPEndPoint)
            RemoteEndPoint = remoteEp
            RemoteAddress = remoteEp.Address.ToString()
            RemotePort = remoteEp.Port

            Dim localEp = CType(_socket.LocalEndPoint, IPEndPoint)
            LocalEndPoint = localEp
            LocalAddress = localEp.Address.ToString()
            LocalPort = localEp.Port

            If maxReceiveBufferSize > 0 Then
                _incomingChannel = Channel.CreateBounded(Of Byte)(
                    New BoundedChannelOptions(maxReceiveBufferSize) With {
                        .SingleReader = True,
                        .SingleWriter = True,
                        .FullMode = BoundedChannelFullMode.Wait
                    })
            Else
                _incomingChannel = Channel.CreateUnbounded(Of Byte)(
                    New UnboundedChannelOptions() With {.SingleReader = True, .SingleWriter = True})
            End If
        End Sub

        ''' <summary>Starts the background receive loop. Called by NetListen after acceptance.</summary>
        Friend Sub StartReceiving()
            _cancellationSource = New CancellationTokenSource()
            Task.Run(Function() ReceiveLoop(), _cancellationSource.Token)
        End Sub

        ' --- Receive Loop ---

        Private Async Function ReceiveLoop() As Task
            Dim token = _cancellationSource.Token
            Try
                While Not token.IsCancellationRequested
                    Dim bytesReceived As Integer =
                        Await _socket.ReceiveAsync(_receiveBuffer, SocketFlags.None, token)

                    If bytesReceived > 0 Then
                        For i As Integer = 0 To bytesReceived - 1
                            Await _incomingChannel.Writer.WriteAsync(_receiveBuffer(i), token)
                        Next
                        Dim data(bytesReceived - 1) As Byte
                        Array.Copy(_receiveBuffer, data, bytesReceived)
                        RaiseEvent DataReceived(Me, data)
                    Else
                        Exit While   ' Peer closed connection
                    End If
                End While
            Catch ex As OperationCanceledException
                ' Normal shutdown
            Catch ex As Exception
                If Not token.IsCancellationRequested Then
                    RaiseEvent [Error](Me, ex)
                End If
            End Try

            _incomingChannel.Writer.TryComplete()
            If Not Volatile.Read(_disposed) Then
                Await DisconnectInternal()
            End If
        End Function

        ' --- Send Methods ---

        Public Async Function SendAsync(data As Byte()) As Task
            If Volatile.Read(_disposed) Then Throw New ObjectDisposedException(Me.GetType().Name)
            Try
                If _socket IsNot Nothing AndAlso _socket.Connected Then
                    Await _socket.SendAsync(data, SocketFlags.None)
                Else
                    Throw New InvalidOperationException("Session is not connected.")
                End If
            Catch ex As Exception
                RaiseEvent [Error](Me, ex)
                Throw
            End Try
        End Function

        Public Async Function SendByte(value As Byte) As Task
            Dim buf(0) As Byte : buf(0) = value
            Await SendAsync(buf)
        End Function

        Public Async Function SendChar(value As Char) As Task
            Await SendAsync(Encoding.GetBytes({value}))
        End Function

        Public Async Function SendLine(data As String) As Task
            Dim line = If(data.EndsWith(vbCrLf), data, data & vbCrLf)
            Await SendAsync(Encoding.GetBytes(line))
        End Function

        ' --- Read Methods ---

        Public Async Function ReadByte() As Task(Of Byte)
            If Volatile.Read(_disposed) Then Throw New ObjectDisposedException(Me.GetType().Name)
            Try
                Using ctsRead As New CancellationTokenSource(ReadTimeout)
                    Using linked = CancellationTokenSource.CreateLinkedTokenSource(ctsRead.Token, _cancellationSource.Token)
                        Try
                            If Not Await _incomingChannel.Reader.WaitToReadAsync(linked.Token) Then
                                Throw New InvalidOperationException("Session closed.")
                            End If
                            Dim b As Byte
                            If _incomingChannel.Reader.TryRead(b) Then Return b
                            Throw New InvalidOperationException("Failed to read byte.")
                        Catch ex As OperationCanceledException
                            If ctsRead.IsCancellationRequested Then
                                Throw New TimeoutException($"Read timed out after {ReadTimeout}ms.")
                            Else
                                Throw New InvalidOperationException("Session closed.")
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
                    Dim completed As Boolean, bytesUsed As Integer, charsUsed As Integer
                    decoder.Convert(bytes, 0, 1, chars, 0, 1, False, bytesUsed, charsUsed, completed)
                    If charsUsed > 0 Then Return chars(0)
                End While
            Catch ex As Exception
                If Not (TypeOf ex Is TimeoutException OrElse
                        TypeOf ex Is InvalidOperationException OrElse
                        TypeOf ex Is SocketException) Then
                    RaiseEvent [Error](Me, ex)
                End If
                Throw
            End Try
            Throw New InvalidOperationException("Session closed.")   ' Compiler flow
        End Function

        Public Async Function ReadLine(Optional removeCRLF As Boolean = True) As Task(Of String)
            If Volatile.Read(_disposed) Then Throw New ObjectDisposedException(Me.GetType().Name)
            Try
                Dim sb As New StringBuilder()
                Dim lastByte As Byte = 0
                Dim rawByte(0) As Byte
                While True
                    Dim b = Await ReadByte()
                    rawByte(0) = b
                    sb.Append(Encoding.GetChars(rawByte))
                    If lastByte = 13 AndAlso b = 10 Then
                        Dim result = sb.ToString()
                        Return If(removeCRLF, result.Substring(0, result.Length - 2), result)
                    End If
                    lastByte = b
                End While
            Catch ex As Exception
                If Not (TypeOf ex Is TimeoutException OrElse
                        TypeOf ex Is InvalidOperationException OrElse
                        TypeOf ex Is SocketException) Then
                    RaiseEvent [Error](Me, ex)
                End If
                Throw
            End Try
            Throw New InvalidOperationException("Session closed.")   ' Compiler flow
        End Function

        Public Async Function TryReadLineAsync(Optional removeCRLF As Boolean = True) As Task(Of Tuple(Of Boolean, String))
            Try
                Dim result = Await ReadLine(removeCRLF)
                Return Tuple.Create(True, result)
            Catch ex As TimeoutException
                Return Tuple.Create(False, String.Empty)
            End Try
        End Function

        ' --- Disconnect ---

        Public Async Function Disconnect(Optional force As Boolean = False) As Task
            Await DisconnectInternal(force:=force)
        End Function

        Private Function DisconnectInternal(Optional suppressEvents As Boolean = False, Optional force As Boolean = False) As Task
            Try
                If _cancellationSource IsNot Nothing AndAlso
                   Not _cancellationSource.IsCancellationRequested Then
                    _cancellationSource.Cancel()
                End If
                Dim sock As Socket = Interlocked.Exchange(_socket, Nothing)
                If sock IsNot Nothing Then
                    Try
                        If force Then
                            sock.LingerState = New LingerOption(True, 0)
                        End If
                        If sock.Connected Then sock.Shutdown(SocketShutdown.Both)
                    Catch
                    Finally
                        sock.Close()
                        sock.Dispose()
                    End Try
                    If Not suppressEvents Then RaiseEvent Disconnected(Me, EventArgs.Empty)
                End If
            Catch ex As Exception
                If Not suppressEvents Then RaiseEvent [Error](Me, ex)
            End Try
            Return Task.CompletedTask
        End Function

        ' --- IDisposable ---

        Public Sub Dispose() Implements IDisposable.Dispose
            Dispose(True)
            GC.SuppressFinalize(Me)
        End Sub

        Protected Overridable Sub Dispose(disposing As Boolean)
            If Not Volatile.Read(_disposed) Then
                If disposing Then
                    _incomingChannel.Writer.TryComplete()
                    DisconnectInternal(suppressEvents:=True).GetAwaiter().GetResult()
                    _cancellationSource?.Dispose()
                End If
                Volatile.Write(_disposed, True)
            End If
        End Sub

        Protected Overrides Sub Finalize()
            Dispose(False)
        End Sub

    End Class

    ' ==========================================================================
    '  NetListen
    '  TCP listener. Fires ConnectionRequest for each inbound connection;
    '  creates a NetSession on acceptance and fires SessionConnected.
    ' ==========================================================================
    Public Class NetListen
        Implements IDisposable

        Private _listenSocket As Socket
        Private _cancellationSource As CancellationTokenSource
        Private _disposed As Boolean = False
        Private _running As Boolean = False

        ' --- Backing fields for guarded properties ---
        Private _port As Integer
        Private _bindAddress As String = Nothing

        ''' <summary>TCP port to listen on (1–65535). Cannot be changed while running.</summary>
        Public Property Port As Integer
            Get
                Return _port
            End Get
            Set(value As Integer)
                If Volatile.Read(_running) Then
                    Throw New InvalidOperationException("Cannot change Port while listening.")
                End If
                _port = value
            End Set
        End Property

        ''' <summary>
        ''' IP address to bind to. Nothing or empty binds to all interfaces (dual-stack).
        ''' Cannot be changed while running.
        ''' </summary>
        Public Property BindAddress As String
            Get
                Return _bindAddress
            End Get
            Set(value As String)
                If Volatile.Read(_running) Then
                    Throw New InvalidOperationException("Cannot change BindAddress while listening.")
                End If
                _bindAddress = value
            End Set
        End Property

        ''' <summary>Maximum pending connections in the accept queue. Default 10.</summary>
        Public Property Backlog As Integer = 10

        ''' <summary>If True, enables TCP Keep-Alive on accepted connections.</summary>
        Public Property KeepAlive As Boolean = False

        ''' <summary>Maximum size of the receive queue buffer for accepted sessions. 0 = Unbounded.</summary>
        Public Property MaxReceiveBufferSize As Integer = 0

        ''' <summary>Thread-safe dictionary of all active sessions, keyed by their unique Guid.</summary>
        Public ReadOnly Property Sessions As New ConcurrentDictionary(Of Guid, NetSession)()

        ' --- Events ---

        ''' <summary>
        ''' Raised for every incoming connection request before it is accepted.
        ''' Inspect e.RemoteAddress / e.RemotePort. Set e.Accept = False to reject.
        ''' If no handler is attached, all connections are accepted.
        ''' </summary>
        Public Event ConnectionRequest(sender As Object, e As ConnectionRequestEventArgs)

        ''' <summary>
        ''' Raised when a connection is accepted and its NetSession is ready.
        ''' The session's receive loop is already running when this fires.
        ''' </summary>
        Public Event SessionConnected(sender As Object, session As NetSession)

        ''' <summary>Raised when the listener binds and begins accepting.</summary>
        Public Event Started(sender As Object, e As EventArgs)

        ''' <summary>Raised when the listener stops (graceful or error).</summary>
        Public Event Stopped(sender As Object, e As EventArgs)

        ''' <summary>Raised on socket-level errors in the accept loop.</summary>
        Public Event [Error](sender As Object, ex As Exception)

        ' --- Broadcast Methods ---

        ''' <summary>Asynchronously broadcasts a line of text to all currently active sessions.</summary>
        Public Async Function BroadcastLineAsync(data As String) As Task
            Dim activeSessions = Sessions.Values.ToList()
            If activeSessions.Count > 0 Then
                Dim tasks = activeSessions.Select(Function(s) s.SendLine(data))
                Await Task.WhenAll(tasks)
            End If
        End Function

        ''' <summary>Asynchronously broadcasts raw bytes to all currently active sessions.</summary>
        Public Async Function BroadcastAsync(data As Byte()) As Task
            Dim activeSessions = Sessions.Values.ToList()
            If activeSessions.Count > 0 Then
                Dim tasks = activeSessions.Select(Function(s) s.SendAsync(data))
                Await Task.WhenAll(tasks)
            End If
        End Function

        ' --- Constructors ---

        Public Sub New()
        End Sub

        Public Sub New(port As Integer)
            Me.Port = port
        End Sub

        Public Sub New(port As Integer, bindAddress As String)
            Me.Port = port
            Me.BindAddress = bindAddress
        End Sub

        ' --- Listener Control ---

        ''' <summary>
        ''' Binds to Port (and optionally BindAddress), begins listening,
        ''' fires Started, then runs the accept loop in the background.
        ''' </summary>
        Public Async Function Start() As Task
            If Volatile.Read(_disposed) Then Throw New ObjectDisposedException(Me.GetType().Name)
            If Volatile.Read(_running) Then Throw New InvalidOperationException("Already listening.")
            If Port < 1 OrElse Port > 65535 Then Throw New InvalidOperationException("Port must be between 1 and 65535.")

            Try
                _cancellationSource = New CancellationTokenSource()

                ' Resolve bind address
                Dim bindEp As IPEndPoint
                If String.IsNullOrEmpty(BindAddress) Then
                    bindEp = New IPEndPoint(IPAddress.IPv6Any, Port)   ' All interfaces, dual-stack
                Else
                    Dim addr As IPAddress = Nothing
                    If Not IPAddress.TryParse(BindAddress, addr) Then
                        Dim resolved = Await Dns.GetHostAddressesAsync(BindAddress)
                        addr = resolved.FirstOrDefault()
                        If addr Is Nothing Then
                            Throw New InvalidOperationException($"Cannot resolve BindAddress: {BindAddress}")
                        End If
                    End If
                    bindEp = New IPEndPoint(addr, Port)
                End If

                ' Create dual-stack listening socket
                _listenSocket = New Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
                _listenSocket.DualMode = True
                _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, True)
                _listenSocket.Bind(bindEp)
                _listenSocket.Listen(Backlog)

                Volatile.Write(_running, True)
                RaiseEvent Started(Me, EventArgs.Empty)

                ' Accept loop runs in background — intentional fire-and-forget
#Disable Warning BC42358
                Task.Run(Function() AcceptLoop(), _cancellationSource.Token)
#Enable Warning BC42358

            Catch ex As Exception
                Volatile.Write(_running, False)
                RaiseEvent [Error](Me, ex)
                Throw
            End Try
        End Function

        ''' <summary>Stops the listener gracefully.</summary>
        Public Async Function [Stop](Optional force As Boolean = False) As Task
            Await StopInternal(suppressEvents:=False, force:=force)
        End Function

        ' --- Accept Loop ---

        Private Async Function AcceptLoop() As Task
            Dim token = _cancellationSource.Token
            Try
                While Not token.IsCancellationRequested
                    Dim accepted As Socket
                    Try
                        accepted = Await _listenSocket.AcceptAsync(token)
                    Catch ex As OperationCanceledException
                        Exit While
                    Catch ex As SocketException When token.IsCancellationRequested
                        Exit While
                    Catch ex As SocketException
                        ' Transient error — log and keep listening
                        RaiseEvent [Error](Me, ex)
                        Continue While
                    End Try

                    ' Handle each connection on the thread pool; keep accept loop hot — intentional fire-and-forget
                    Dim capturedSocket = accepted
#Disable Warning BC42358
                    Task.Run(Sub() HandleIncoming(capturedSocket))
#Enable Warning BC42358
                End While
            Catch ex As Exception
                If Not token.IsCancellationRequested Then
                    RaiseEvent [Error](Me, ex)
                End If
            Finally
                Volatile.Write(_running, False)
                RaiseEvent Stopped(Me, EventArgs.Empty)
            End Try
        End Function

        Private Sub HandleIncoming(accepted As Socket)
            Try
                Dim remoteEp = CType(accepted.RemoteEndPoint, IPEndPoint)
                Dim localEp = CType(accepted.LocalEndPoint, IPEndPoint)
                Dim args As New ConnectionRequestEventArgs(remoteEp, localEp)

                ' Raise ConnectionRequest — handler sets args.Accept = False to block
                RaiseEvent ConnectionRequest(Me, args)

                If Not args.Accept Then
                    ' Rejected — close cleanly
                    Try
                        accepted.Shutdown(SocketShutdown.Both)
                    Catch
                    End Try
                    accepted.Close()
                    accepted.Dispose()
                    Return
                End If

                ' Apply Keep-Alive to accepted socket if enabled
                If KeepAlive Then
                    accepted.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, True)
                End If

                ' Accepted — create session and start its receive loop
                Dim session As New NetSession(accepted, MaxReceiveBufferSize)
                
                ' Add to session manager
                Sessions.TryAdd(session.Id, session)
                
                ' Remove from session manager upon disconnection
                AddHandler session.Disconnected, Sub(s, e)
                                                     Dim removed As NetSession = Nothing
                                                     Sessions.TryRemove(session.Id, removed)
                                                 End Sub

                session.StartReceiving()
                RaiseEvent SessionConnected(Me, session)

            Catch ex As Exception
                RaiseEvent [Error](Me, ex)
                Try
                    accepted?.Close()
                    accepted?.Dispose()
                Catch
                End Try
            End Try
        End Sub

        ' --- Internal Stop ---

        Private Async Function StopInternal(Optional suppressEvents As Boolean = False, Optional force As Boolean = False) As Task
            Try
                If _cancellationSource IsNot Nothing AndAlso
                   Not _cancellationSource.IsCancellationRequested Then
                    _cancellationSource.Cancel()
                End If
                Dim sock As Socket = Interlocked.Exchange(_listenSocket, Nothing)
                If sock IsNot Nothing Then
                    Try
                        sock.Close()
                        sock.Dispose()
                    Catch
                    End Try
                End If

                ' Disconnect all active sessions
                Dim activeSessions = Sessions.Values.ToList()
                If activeSessions.Count > 0 Then
                    Dim disconnectTasks = activeSessions.Select(Function(s) s.Disconnect(force))
                    Await Task.WhenAll(disconnectTasks)
                End If
            Catch ex As Exception
                If Not suppressEvents Then RaiseEvent [Error](Me, ex)
            End Try
        End Function

        ' --- IDisposable ---

        Public Sub Dispose() Implements IDisposable.Dispose
            Dispose(True)
            GC.SuppressFinalize(Me)
        End Sub

        Protected Overridable Sub Dispose(disposing As Boolean)
            If Not Volatile.Read(_disposed) Then
                If disposing Then
                    StopInternal(suppressEvents:=True).GetAwaiter().GetResult()
                    _cancellationSource?.Dispose()
                End If
                Volatile.Write(_disposed, True)
            End If
        End Sub

        Protected Overrides Sub Finalize()
            Dispose(False)
        End Sub

    End Class

End Namespace
