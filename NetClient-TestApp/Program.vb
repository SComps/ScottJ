Imports System
Imports System.Text
Imports System.Threading.Tasks
Imports ScottJ

Module Program
    Sub Main(args As String())
        Console.WriteLine("--- NetClient Automation Test ---")
        Try
            RunTest().GetAwaiter().GetResult()
        Catch ex As Exception
            Console.WriteLine($"Critical failure: {ex.Message}")
        End Try
    End Sub

    Async Function RunTest() As Task
        ' Create the client object
        Using client As New NetClient("192.168.12.84", 23)
            
            ' Setup event handlers for visibility
            AddHandler client.Connected, Sub(s, e) Console.WriteLine(vbCrLf & ">>> CONNECTED to VAX host.")
            AddHandler client.Disconnected, Sub(s, e) Console.WriteLine(vbCrLf & ">>> DISCONNECTED from VAX host.")
            AddHandler client.Error, Sub(s, ex) Console.WriteLine(vbCrLf & ">>> ERROR: " & ex.Message)
            
            ' Display all raw data received from the VAX to the console
            AddHandler client.DataReceived, Sub(s, data)
                                                Dim text = Encoding.UTF8.GetString(data)
                                                Console.Write(text)
                                            End Sub

            Try
                ' 1. Connect
                Console.WriteLine("Initiating connection...")
                Await client.Connect()

                ' 2. Wait for Username prompt and supply "SYSTEM"
                Console.WriteLine("Waiting for Username prompt...")
                Await WaitAndRespond(client, "Username:", "SYSTEM")

                ' 3. Wait for Password prompt and supply the password
                Console.WriteLine("Waiting for Password prompt...")
                Await WaitAndRespond(client, "Password:", "mlkhbu")

                ' 4. Wait for successful logon (the $ prompt)
                Console.WriteLine("Waiting for DCL prompt...")
                Await WaitForPrompt(client, "$")

                ' 5. Issue the LOG command to log out
                Console.WriteLine(vbCrLf & "Login successful. Issuing logout command...")
                Await Task.Delay(500) ' Slight pause for stability
                Await client.SendLine("LOG")

                ' Wait a few seconds for the logout response to be displayed
                Await Task.Delay(2000)

                ' 6. Disconnect
                Await client.Disconnect()

            Catch ex As Exception
                Console.WriteLine(vbCrLf & "Automation error: " & ex.Message)
            End Try
        End Using

        Console.WriteLine(vbCrLf & "Test execution completed.")
        Console.WriteLine("Press any key to close...")
        Console.ReadKey()
    End Function

    ''' <summary>
    ''' Waits for a specific string pattern and then sends a line of text.
    ''' </summary>
    Async Function WaitAndRespond(client As NetClient, prompt As String, response As String) As Task
        Await WaitForPrompt(client, prompt)
        Await client.SendLine(response)
    End Function

    ''' <summary>
    ''' Reads characters until the buffer ends with the specified prompt.
    ''' </summary>
    Async Function WaitForPrompt(client As NetClient, prompt As String) As Task
        Dim buffer As New StringBuilder()
        While True
            Try
                Dim ch = Await client.ReadChar()
                buffer.Append(ch)
                
                ' Check if we've reached the prompt
                If buffer.ToString().EndsWith(prompt) Then
                    Return
                End If
                
                ' Safety break if buffer gets too large without finding prompt
                If buffer.Length > 2000 Then
                    buffer.Remove(0, 1000)
                End If
            Catch ex As Exception
                Throw New Exception($"Prompt '{prompt}' not found: {ex.Message}")
            End Try
        End While
    End Function
End Module
