using System;
using System.Net;
using System.Net.Sockets;
using SilverPinesCoop.Net;

namespace SilverPinesCoop.LocalTest
{
    /// <summary>
    /// Two copies of the game on one PC linked over 127.0.0.1 UDP instead of a Steam lobby (launch option
    /// <see cref="LaunchFlag"/>, started by coop_local_test.bat): the first copy binds BasePort, the second
    /// BasePort + 1, and each sends to the other. One person can test everything after the lobby — packets, the 2P
    /// puppet, rooms — with their own copy of the game. A datagram is the channel byte followed by the packet;
    /// loopback UDP does not drop, so channel 1 needs no retransmission here.
    /// </summary>
    internal sealed class LocalLink : IDisposable
    {
        public const string LaunchFlag = "--coop-local";
        private const int SioUdpConnReset = -1744830452;   // SIO_UDP_CONNRESET: no WSAECONNRESET after sending to a closed port

        private readonly Socket _socket;
        private readonly EndPoint _peer;
        private readonly byte[] _receiveBuffer = new byte[Packets.MaxMessageSize + 1];
        private readonly byte[] _sendBuffer = new byte[Packets.MaxMessageSize + 1];
        private EndPoint _from = new IPEndPoint(IPAddress.Loopback, 0);

        /// <summary>This copy's id is its UDP port; <see cref="Copy"/> is 1 or 2.</summary>
        public ulong LocalId { get; }
        public ulong PeerId { get; }
        public int Copy { get; }

        private LocalLink(Socket socket, int localPort, int peerPort, int copy)
        {
            _socket = socket;
            _peer = new IPEndPoint(IPAddress.Loopback, peerPort);
            LocalId = (ulong)localPort;
            PeerId = (ulong)peerPort;
            Copy = copy;
        }

        /// <summary>Binds BasePort, or BasePort + 1 when the other copy already has it; null when both are taken.</summary>
        public static LocalLink Open(int basePort)
        {
            for (var copy = 1; copy <= 2; copy++)
            {
                var port = basePort + copy - 1;
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    socket.ExclusiveAddressUse = true;
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    socket.Blocking = false;
                    try
                    {
                        socket.IOControl(SioUdpConnReset, new byte[4], null);
                    }
                    catch (Exception)
                    {
                        // not Windows: no such reset behaviour to switch off
                    }
                    return new LocalLink(socket, port, copy == 1 ? basePort + 1 : basePort, copy);
                }
                catch (SocketException)
                {
                    socket.Dispose();
                }
            }
            return null;
        }

        public bool Send(int channel, byte[] data, int length)
        {
            if (length < 0 || length > Packets.MaxMessageSize)
                return false;
            _sendBuffer[0] = (byte)channel;
            Buffer.BlockCopy(data, 0, _sendBuffer, 1, length);
            try
            {
                return _socket.SendTo(_sendBuffer, 0, length + 1, SocketFlags.None, _peer) == length + 1;
            }
            catch (SocketException)
            {
                return false;   // the other copy is not running (yet)
            }
        }

        /// <summary>The next datagram from the other copy, as a slice of an internal buffer valid until the next call.</summary>
        public bool TryReceive(out int channel, out byte[] buffer, out int offset, out int length)
        {
            channel = 0;
            buffer = _receiveBuffer;
            offset = 1;
            length = 0;
            while (true)
            {
                int n;
                try
                {
                    if (_socket.Available <= 0)
                        return false;
                    n = _socket.ReceiveFrom(_receiveBuffer, 0, _receiveBuffer.Length, SocketFlags.None, ref _from);
                }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.ConnectionReset
                                                    or SocketError.MessageSize)
                {
                    if (e.SocketErrorCode == SocketError.WouldBlock)
                        return false;
                    continue;   // a stale ICMP reset or an oversized datagram: skip it
                }
                if (n < 1 || _from is not IPEndPoint ep || (ulong)ep.Port != PeerId)
                    continue;   // not from the other copy
                channel = _receiveBuffer[0];
                length = n - 1;
                return true;
            }
        }

        public void Dispose() => _socket.Dispose();
    }
}
