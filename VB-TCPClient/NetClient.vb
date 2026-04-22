Imports System
Imports System.Collections.Concurrent
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks

    Public Class NetClient
        Implements IDisposable

        Private _socket As Socket
        Private _receiveBuffer(8191) As Byte
        Private _cancellationSource As CancellationTokenSource
        
        ' Internal buffer for "Read" methods
        Private _incomingQueue As New ConcurrentQueue(Of Byte)
        Private _dataSignal As New SemaphoreSlim(0)
        
        Private _disposed As Boolean = False

        Public Property Host As String
        Public Property Port As Integer
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

            Try
                ' Validate Host and Port
                If String.IsNullOrEmpty(Host) Then Throw New InvalidOperationException("Host must be specified.")
                If Port <= 0 Then Throw New InvalidOperationException("Valid Port must be specified.")

                ' If already connected, disconnect first to ensure clean state
                If _socket IsNot Nothing AndAlso _socket.Connected Then
                    Await Disconnect()
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

                Await _socket.ConnectAsync(Host, Port)
                
                RaiseEvent Connected(Me, EventArgs.Empty)

                ' Start receiving data in the background
                Dim t = Task.Run(Function() ReceiveLoop(), _cancellationSource.Token)

            Catch ex As Exception
                RaiseEvent [Error](Me, ex)
                Throw
            End Try
        End Function

        Private Async Function ReceiveLoop() As Task
            Try
                While Not _cancellationSource.Token.IsCancellationRequested
                    Dim bytesReceived As Integer = Await _socket.ReceiveAsync(_receiveBuffer, SocketFlags.None, _cancellationSource.Token)

                    If bytesReceived > 0 Then
                        Dim data(bytesReceived - 1) As Byte
                        Array.Copy(_receiveBuffer, data, bytesReceived)
                        
                        ' Add to queue for Read* methods
                        For Each b In data
                            _incomingQueue.Enqueue(b)
                        Next
                        _dataSignal.Release(bytesReceived)

                        RaiseEvent DataReceived(Me, data)
                    Else
                        ' Peer closed the connection
                        Exit While
                    End If
                End While
            Catch ex As OperationCanceledException
                ' Normal shutdown
            Catch ex As SocketException
                If Not _cancellationSource.IsCancellationRequested Then
                    RaiseEvent [Error](Me, ex)
                End If
            Catch ex As Exception
                If Not _cancellationSource.IsCancellationRequested Then
                    RaiseEvent [Error](Me, ex)
                End If
            End Try
            Await Disconnect()
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
            Await _dataSignal.WaitAsync(_cancellationSource.Token)
            Dim b As Byte
            If _incomingQueue.TryDequeue(b) Then
                Return b
            End If
            Throw New InvalidOperationException("Failed to read byte.")
        End Function

        Public Async Function ReadChar() As Task(Of Char)
            Dim decoder = Encoding.GetDecoder()
            Dim bytes(0) As Byte
            Dim chars(0) As Char
            
            While True
                bytes(0) = Await ReadByte()
                Dim completed As Boolean
                Dim bytesUsed As Integer
                Dim charsUsed As Integer
                decoder.Convert(bytes, 0, 1, chars, 0, 1, False, bytesUsed, charsUsed, completed)
                If charsUsed > 0 Then
                    Return chars(0)
                End If
            End While
            Throw New InvalidOperationException("Connection closed.")
        End Function

        Public Async Function ReadLine(Optional removeCRLF As Boolean = True) As Task(Of String)
            Dim sb As New StringBuilder()
            Dim lastByte As Byte = 0
            
            While True
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
            Throw New InvalidOperationException("Connection closed.")
        End Function

        Public Function Disconnect() As Task
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
                    Disconnect().GetAwaiter().GetResult()
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
    End Class
