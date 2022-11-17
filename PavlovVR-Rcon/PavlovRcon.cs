﻿using Newtonsoft.Json;
using PavlovVR_Rcon.Exceptions;
using PavlovVR_Rcon.Models.Replies;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PavlovVR_Rcon;

/// <summary>
///     A single server connection to Pavlov VR RCON
/// </summary>
public class PavlovRcon : IDisposable
{
    private const string passwordPromptText = "Password: ";
    private readonly bool forceIpv4;
    private readonly string hashedPassword;
    private Socket? socket;

    public PavlovRcon(string host, int port, string password, bool forceIpv4 = false)
    {
        this.forceIpv4 = forceIpv4;
        this.Host = host;
        this.Port = port;

        this.hashedPassword = Convert.ToHexString(MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(password))).ToLower();
    }

    public string Host { get; }
    public int Port { get; }

    /// <summary>
    ///     Amount of time to wait for a reply from the server when sending a command before aborting, in milliseconds
    /// </summary>
    public int CommandTimeout { get; set; } = 2000;

    public bool Connected => this.socket?.Connected ?? false;

    public void Dispose()
    {
        this.socket?.Dispose();
    }

    public async Task Connect(CancellationToken cancellationToken)
    {
        if (this.socket != null)
        {
            try
            {
                await this.socket.DisconnectAsync(true, new CancellationTokenSource(500).Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        if (this.forceIpv4)
        {
            this.socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
        else
        {
            this.socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        }

        await this.socket.ConnectAsync(this.Host, this.Port, cancellationToken);

        await using NetworkStream stream = new(this.socket);
        using StreamReader reader = new(stream);

        Memory<char> passwordPrompt = new(new char[PavlovRcon.passwordPromptText.Length]);

        int promptCharsCount = await reader.ReadBlockAsync(passwordPrompt, cancellationToken);
        if (promptCharsCount != PavlovRcon.passwordPromptText.Length || passwordPrompt.ToString() != PavlovRcon.passwordPromptText)
        {
            throw new MissingAuthenticationPromptException(this);
        }

        await using StreamWriter writer = new(stream);
        await writer.WriteAsync(this.hashedPassword);
        await writer.FlushAsync();

        if (await reader.ReadLineAsync() != "Authenticated=1")
        {
            throw new AuthenticationFailedException(this);
        }
    }

    public async Task<string> SendTextCommand(string command, string[]? parameters = null)
    {
        return (await this.SendCommand<BaseReply>(command, parameters)).RawReply;
    }

    public async Task<T> SendCommand<T>(string command, string[]? parameters = null) where T : BaseReply
    {
        if (this.socket is not { Connected: true, })
        {
            throw new NotConnectedException(this);
        }

        await using NetworkStream stream = new(this.socket);
        await using StreamWriter writer = new(stream);

        StringBuilder commandBuilder = new();
        commandBuilder.Append(command);

        if (parameters != null)
        {
            commandBuilder.Append(' ');
            commandBuilder.Append(string.Join(' ', parameters));
        }

        commandBuilder.Append('\n');

        await writer.WriteAsync(commandBuilder.ToString());
        await writer.FlushAsync();

        T reply;
        try
        {
            reply = await this.readCommandReply<T>(command, new CancellationTokenSource(this.CommandTimeout).Token);
        }
        catch (OperationCanceledException ex)
        {
            throw new CommandFailedException(this, command, parameters, new CommandTimeoutException(this, ex));
        }
        catch (Exception ex)
        {
            throw new CommandFailedException(this, command, parameters, ex);
        }

        if (!reply.Successful)
        {
            throw new CommandFailedException(this, command, parameters);
        }

        return reply;
    }

    private async Task<T> readCommandReply<T>(string command, CancellationToken cancellationToken) where T : BaseReply
    {
        if (this.socket is not { Connected: true, })
        {
            throw new NotConnectedException(this);
        }

        await using NetworkStream stream = new(this.socket);
        using StreamReader reader = new(stream);
        StringBuilder jsonBlock = new();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            Memory<char> buffer = new(new char[1024]);
            int readCharCount = await reader.ReadAsync(buffer, cancellationToken);

            if (readCharCount == 0 && !cancellationToken.IsCancellationRequested)
            {
                // The C# discord is uncertain whether ReadAsync throws or returns 0 when cancelled
                // so we will handle both
                continue;
            }

            jsonBlock.Append(buffer.Slice(0, readCharCount).ToString());
        } while (!cancellationToken.IsCancellationRequested && !PavlovRcon.checkFullJsonBlock(jsonBlock.ToString()) && this.socket.Connected);

        cancellationToken.ThrowIfCancellationRequested();

        if (!this.socket.Connected)
        {
            throw new NotConnectedException(this);
        }

        T? reply;

        try
        {
            reply = JsonConvert.DeserializeObject<T>(jsonBlock.ToString());
        }
        catch (Exception ex)
        {
            throw new UnexpectedRconResponseException(this, jsonBlock.ToString(), ex);
        }

        if (reply == null || !string.Equals(reply.Command, command, StringComparison.CurrentCultureIgnoreCase))
        {
            throw new UnexpectedRconResponseException(this, jsonBlock.ToString());
        }

        reply.RawReply = jsonBlock.ToString();

        return reply;
    }

    private static bool checkFullJsonBlock(string jsonBlock)
    {
        int openBracesCount = 0;
        bool rootBlockOpened = false;
        bool rootBlockClosed = false;

        for (int i = 0; i < jsonBlock.Length; i++)
        {
            switch (jsonBlock[i])
            {
                case '{':
                    if (rootBlockClosed)
                    {
                        return false;
                    }

                    if (i > 0 && jsonBlock[i - 1] == '\\')
                    {
                        continue;
                    }

                    rootBlockOpened = true;

                    openBracesCount++;
                    break;
                case '}':
                    if (rootBlockClosed)
                    {
                        return false;
                    }

                    if (i > 0 && jsonBlock[i - 1] == '\\')
                    {
                        continue;
                    }

                    openBracesCount--;

                    switch (openBracesCount)
                    {
                        case 0:
                            rootBlockClosed = true;
                            break;
                        case < 0:
                            return false;
                    }

                    break;
            }
        }

        if (!rootBlockOpened)
        {
            return false;
        }

        return openBracesCount == 0;
    }
}
