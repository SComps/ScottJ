Imports System
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading.Tasks
Imports ScottJ

Module Program
    ' Windows API P/Invokes to enable VT100 support
    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Function GetStdHandle(nStdHandle As Integer) As IntPtr
    End Function

    <DllImport("kernel32.dll")>
    Private Function GetConsoleMode(hConsoleHandle As IntPtr, ByRef lpMode As Integer) As Boolean
    End Function

    <DllImport("kernel32.dll")>
    Private Function SetConsoleMode(hConsoleHandle As IntPtr, dwMode As Integer) As Boolean
    End Function

    Private Const STD_OUTPUT_HANDLE As Integer = -11
    Private Const ENABLE_VIRTUAL_TERMINAL_PROCESSING As Integer = &H4

    Sub Main(args As String())
        ' Enable Native VT100 support in the Windows console
        Try
            Dim hOut = GetStdHandle(STD_OUTPUT_HANDLE)
            Dim mode As Integer
            If GetConsoleMode(hOut, mode) Then
                SetConsoleMode(hOut, mode Or ENABLE_VIRTUAL_TERMINAL_PROCESSING)
            End If
        Catch
            ' Fallback if not supported
        End Try

        RunTerminal().GetAwaiter().GetResult()
    End Sub

    Async Function RunTerminal() As Task
        Try
            Console.Title = "NetClient Interactive Terminal (VT100)"
            Console.Clear()
        Catch
            ' Ignore console UI errors in headless environments
        End Try
        Console.WriteLine("--- NetClient VT100 Terminal ---")
        Console.WriteLine("Connecting to 192.168.12.84...")

        Using client As New NetClient("192.168.12.84", 23)
            
            AddHandler client.Connected, Sub() Console.WriteLine(">>> Connected to host.")
            AddHandler client.Disconnected, Sub() 
                                                Console.WriteLine(vbCrLf & ">>> Disconnected from host.")
                                                Environment.Exit(0)
                                            End Sub
            AddHandler client.Error, Sub(s, ex) Console.WriteLine(vbCrLf & ">>> [ERROR]: " & ex.Message)

            ' Stream incoming data directly to the console
            ' Since we enabled ENABLE_VIRTUAL_TERMINAL_PROCESSING, the console will
            ' handle VT100 escape sequences sent by the VAX automatically.
            AddHandler client.DataReceived, Sub(s, data)
                                                ' Filter out some Telnet IAC sequences if they clutter the view
                                                ' For a true terminal, we just pass through
                                                Console.Write(Encoding.UTF8.GetString(data))
                                            End Sub

            Try
                Await client.Connect()

                ' Interactive Loop
                ' We use Task.Run for input to avoid blocking the Main thread
                While True
                    Try
                        If Console.KeyAvailable Then
                            Dim key = Console.ReadKey(True)
                            
                            ' Map Keys to VT100 escape sequences for the VAX
                            Select Case key.Key
                                Case ConsoleKey.Enter
                                    Await client.SendAsync({13, 10}) ' CRLF
                                Case ConsoleKey.Escape
                                    Await client.SendByte(27)
                                Case ConsoleKey.Backspace
                                    Await client.SendByte(127) ' Typical VAX/VMS delete char
                                Case ConsoleKey.Tab
                                    Await client.SendByte(9)
                                
                                ' Arrow Keys mapped to VT100 sequences
                                Case ConsoleKey.UpArrow
                                    Await client.SendAsync({27, 91, 65}) ' ESC [ A
                                Case ConsoleKey.DownArrow
                                    Await client.SendAsync({27, 91, 66}) ' ESC [ B
                                Case ConsoleKey.RightArrow
                                    Await client.SendAsync({27, 91, 67}) ' ESC [ C
                                Case ConsoleKey.LeftArrow
                                    Await client.SendAsync({27, 91, 68}) ' ESC [ D
                                
                                Case Else
                                    If key.KeyChar <> Chr(0) Then
                                        Await client.SendChar(key.KeyChar)
                                    End If
                            End Select
                        End If
                    Catch
                        ' Input not available or redirected
                    End Try
                    
                    ' Yield to keep the system responsive
                    Await Task.Delay(5)
                End While

            Catch ex As OperationCanceledException
                ' Normal exit
            Catch ex As Exception
                Console.WriteLine(vbCrLf & "Terminal Session Error: " & ex.Message)
            End Try
        End Using
    End Function
End Module
