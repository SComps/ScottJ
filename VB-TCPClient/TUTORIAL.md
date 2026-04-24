# ScottJ TCP Library: The Complete Beginner's Guide

Welcome to the **ScottJ TCP Library**! If you've ever tried to build a network application using raw sockets in .NET, you know it can be painful. Dealing with byte arrays, multi-threading, dropped connections, and thread locks usually requires hundreds of lines of confusing boilerplate code.

This library does all the heavy lifting for you. It's designed so that whether you are building a simple client to fetch web data, or a complex multi-user chat server, the code you write is clean, simple, and identical on both sides.

---

## 1. The Three Main Actors

There are only three classes you really need to know:

1.  **`NetClient`:** Use this when you want your program to dial out and connect to *someone else* (like connecting to Google, or connecting to your own server).
2.  **`NetListen`:** Use this when you want your program to wait and *listen* for incoming connections from other people (like hosting a web server or a chat room).
3.  **`NetSession`:** When `NetListen` successfully accepts an incoming connection, it hands you a `NetSession`. This represents the active conversation with that specific client.

> **💡 The Best Part:** `NetClient` and `NetSession` share the exact same methods! If you learn how to send a message on a client, you already know how to send a message on a server.

---

## 2. Your First Client (`NetClient`)

Let's build a simple console application that connects to an imaginary server, says "Hello", and waits for a response.

### Setting it up

```vb
Imports System.Threading.Tasks
Imports ScottJ

Module Program
    Async Function Main() As Task
        ' 1. Create the client and point it to a server
        Dim client As New NetClient("example.com", 8080)

        ' 2. Hook up an error handler just in case
        AddHandler client.Error, Sub(sender, ex)
                                     Console.WriteLine($"Oops! An error occurred: {ex.Message}")
                                 End Sub

        Try
            ' 3. Connect!
            Await client.Connect()
            Console.WriteLine("Connected to the server!")

            ' 4. Send a message
            Await client.SendLine("Hello Server!")

            ' 5. Wait for the server to reply
            Dim reply As String = Await client.ReadLine()
            Console.WriteLine($"Server said: {reply}")

        Catch ex As Exception
            Console.WriteLine("Could not connect or communicate.")
        Finally
            ' 6. Always clean up when you are done
            Await client.Disconnect()
            client.Dispose()
        End Try
    End Function
End Module
```

### The Magic of `ReadLine()`
Normally, data arrives over the network in tiny, unpredictable chunks (like 3 bytes, then 50 bytes, then 2 bytes). The `Await client.ReadLine()` method automatically waits and buffers all those tiny chunks in the background until it sees a standard "Enter/Return" carriage-return (`CRLF`), and hands you the complete string.

---

## 3. Your First Server (`NetListen` & `NetSession`)

Now let's build the server that accepts the connection.

### Setting it up

```vb
Imports System.Threading.Tasks
Imports ScottJ

Module ServerProgram
    Async Function Main() As Task
        ' 1. Create a listener on port 8080
        Dim server As New NetListen(8080)

        ' 2. Tell the server what to do when a client connects
        AddHandler server.SessionConnected, Sub(sender, session)
            Console.WriteLine($"New connection from {session.RemoteAddress}!")
            
            ' Run the conversation in the background so the server can keep listening for others
            Task.Run(Function() HandleConversation(session))
        End Sub

        ' 3. Start listening!
        Await server.Start()
        Console.WriteLine("Server is listening on port 8080...")

        ' Keep the console open forever
        Await Task.Delay(-1)
    End Function

    ' 4. The Conversation Logic
    Async Function HandleConversation(session As NetSession) As Task
        Try
            ' Wait for the client to send us a line
            Dim incomingMessage = Await session.ReadLine()
            Console.WriteLine($"Client said: {incomingMessage}")

            ' Send a reply back
            Await session.SendLine("Hello to you too!")
            
        Catch ex As Exception
            Console.WriteLine("The client disconnected or an error occurred.")
        Finally
            Await session.Disconnect()
        End Try
    End Function
End Module
```

### Broadcasting (Chat Rooms)
`NetListen` has a built-in **Session Manager**. It remembers every single `NetSession` that is currently connected. 

If you want to send a message to *everyone* currently connected to your server, you don't need loops. Just do this:

```vb
Await server.BroadcastLineAsync("Attention all clients: The server is rebooting!")
```

---

## 4. Being the Bouncer (Rejecting Bad IP Addresses)

Sometimes you want to ban an IP address from your server. `NetListen` has a special event called `ConnectionRequest` that fires *before* the connection is actually allowed in.

```vb
AddHandler server.ConnectionRequest, Sub(sender, e)
    If e.RemoteAddress = "192.168.1.50" Then
        Console.WriteLine("Blocked a banned IP address!")
        e.Accept = False ' This slams the door on them instantly!
    End If
End Sub
```

---

## 5. Advanced Superpowers

Once you are comfortable with the basics, this library has some highly advanced features built right in:

*   **`AutoReconnect`:** If you are building a client that must run 24/7 (like an IoT device), just set `client.AutoReconnect = True`. If the Wi-Fi drops or the server reboots, your client will automatically loop and try to reconnect every 5 seconds until it succeeds.
*   **Keep-Alive (`KeepAlive = True`):** Firewalls often drop connections if no data is sent for 5 minutes. Turning this on sends invisible "ping" packets at the system level to keep the connection open indefinitely.
*   **The "Slam Disconnect":** Normally, calling `Disconnect()` says a polite goodbye to the other side. If you catch a hacker and want to cut them off violently and instantly, use `Disconnect(force:=True)`.
*   **Custom DNS:** `NetClient` has a `GetHostByName` method. Usually, DNS goes through Windows. But you can tell `NetClient` to bypass Windows entirely and ask a *specific* DNS server (like Google's `8.8.8.8` or Cloudflare's `1.1.1.1`) directly via raw UDP packets!
*   **Memory Safety (Backpressure):** If a server is sending your client a 10 Gigabyte file, but your program is processing it slowly, the library will eventually run out of RAM. To fix this, set `MaxReceiveBufferSize = 1048576` (1 MB). The library will tell the server to pause sending automatically until your program catches up!

---

## Summary Cheat Sheet

**Sending Data:**
*   `Await SendLine("Hello")` - Sends text with a newline.
*   `Await SendChar("Y"c)` - Sends a single character.
*   `Await SendAsync(byteArray)` - Sends raw byte data.

**Receiving Data:**
*   `Dim text = Await ReadLine()` - Waits for a full line of text.
*   `Dim text = Await TryReadLineAsync()` - Tries to read, but won't crash if it times out.
*   `Dim b = Await ReadByte()` - Waits for exactly one byte.
