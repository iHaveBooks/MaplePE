﻿using System;
using System.Net.Sockets;
using MaplePacketLib.Cryptography;
using System.Diagnostics;

namespace MaplePacketLib {
    public sealed class Session {
        private static readonly Random Random = new Random();
        public const short ReceiveSize = 1024;

        private readonly Socket m_socket;
        private readonly SessionType m_sessionType;
        private readonly AesCipher m_aesCipher;

        private MapleCipher m_clientCipher;
        private MapleCipher m_serverCipher;

        private bool m_encrypted;
        private bool m_connected;

        private byte[] m_recvBuffer;
        private byte[] m_packetBuffer;
        private int m_cursor;

        private object m_sendLock;

        public event EventHandler<ServerInfo> OnHandshake;
        public event EventHandler<byte[]> OnPacket;
        public event EventHandler OnDisconnected;

        public bool Connected {
            get {
                return m_connected;
            }
        }
        public bool Encrypted {
            get {
                return m_encrypted;
            }
        }
        public SessionType SessionType {
            get {
                return m_sessionType;
            }
        }

        internal Session(Socket socket, SessionType type, AesCipher aesCipher) {
            m_socket = socket;
            m_sessionType = type;
            m_aesCipher = aesCipher;

            if (type == SessionType.Client)
                m_encrypted = false;
            else
                m_encrypted = true;

            m_connected = true;

            m_packetBuffer = new byte[ReceiveSize];
            m_recvBuffer = new byte[ReceiveSize];

            m_cursor = 0;

            m_sendLock = new object();
        }

        internal void Start(ServerInfo info) {
            m_serverCipher = new MapleCipher(info.Version, info.Riv, m_aesCipher, CipherType.Decrypt);
            m_clientCipher = new MapleCipher(info.Version, info.Siv, m_aesCipher, CipherType.Encrypt);

            PacketWriter pw = new PacketWriter(15, 17);
            pw.WriteShort(info.Version);
            pw.WriteMapleString(info.Subversion);
            pw.WriteBytes(info.Riv);
            pw.WriteBytes(info.Siv);
            pw.WriteShort(info.Locale);

            SendRawPacket(pw.ToArray());

            Receive();

            Debug.WriteLine("Started local server (toClient)");
        }

        internal void Start() {
            Receive();
            Debug.WriteLine("Started local client (toServer)");
        }

        private void Receive() {
            if (m_connected) {
                var error = SocketError.Success;

                m_socket.BeginReceive(m_recvBuffer, 0, ReceiveSize, SocketFlags.None, out error, PacketCallback, null);

                if (error != SocketError.Success) {
                    Debug.WriteLine("Failed to Receive: " + error);
                    Disconnect();
                }
            }
        }

        private void PacketCallback(IAsyncResult iar) {
            if (m_connected) {
                var error = SocketError.Success;

                int length = m_socket.EndReceive(iar, out error);

                if (length == 0 || error != SocketError.Success) {
                    Debug.WriteLine("Failed to PacketCallback: " + error);
                    Disconnect();
                } else {
                    Append(length);
                    ManipulateBuffer();
                    Receive();
                }
            }
        }
        private void Append(int length) {
            if (m_packetBuffer.Length - m_cursor < length) {
                int newSize = m_packetBuffer.Length * 2;

                while (newSize < m_cursor + length)
                    newSize *= 2;

                Array.Resize<byte>(ref m_packetBuffer, newSize);
            }

            Buffer.BlockCopy(m_recvBuffer, 0, m_packetBuffer, m_cursor, length);

            m_cursor += length;
        }
        private void ManipulateBuffer() {
            if (m_encrypted) {
                const int HeaderSize = 4;

                while (m_cursor > HeaderSize && m_connected) //header room + still connected
                {
                    int packetSize = MapleCipher.GetPacketLength(m_packetBuffer);

                    if (m_cursor < packetSize + HeaderSize) //header + packet room
                    {
                        break;
                    }

                    byte[] buffer = new byte[packetSize];
                    Buffer.BlockCopy(m_packetBuffer, HeaderSize, buffer, 0, packetSize); //copy packet
                    m_serverCipher.Transform(buffer); //decrypt

                    m_cursor -= packetSize + HeaderSize; //fix len

                    if (m_cursor > 0) //move reamining bytes
                    {
                        Buffer.BlockCopy(m_packetBuffer, packetSize + HeaderSize, m_packetBuffer, 0, m_cursor);
                    }

                    if (OnPacket != null)
                        OnPacket(this, buffer);

                    buffer = null; //get rid of buffer
                }
            } else if (m_cursor >= 2) {
                const int HeaderSize = 2;

                short packetSize = BitConverter.ToInt16(m_packetBuffer, 0);

                if (m_cursor >= packetSize + HeaderSize) {
                    byte[] buffer = new byte[packetSize];
                    Buffer.BlockCopy(m_packetBuffer, HeaderSize, buffer, 0, packetSize);

                    PacketReader packet = new PacketReader(buffer);

                    short major = packet.ReadShort();
                    string minor = packet.ReadMapleString();

                    byte[] riv = packet.ReadBytes(4);
                    byte[] siv = packet.ReadBytes(4);

                    m_clientCipher = new MapleCipher(major, riv, m_aesCipher, CipherType.Encrypt);
                    m_serverCipher = new MapleCipher(major, siv, m_aesCipher, CipherType.Decrypt);

                    byte locale = packet.ReadByte();

                    m_encrypted = true; //start waiting for encrypted packets

                    if (OnHandshake != null) {
                        var info = new ServerInfo() {
                            Version = major,
                            Subversion = minor,
                            Riv = riv,
                            Siv = siv,
                            Locale = locale
                        };

                        OnHandshake(this, info);
                    }

                    buffer = null; //get rid of buffer
                    m_cursor = 0; //reset stream
                }
            }
        }

        public void SendPacket(PacketWriter packet) {
            SendPacket(packet.ToArray());
        }

        public void SendPacket(byte[] data) {
            if (!m_connected) {
                throw new InvalidOperationException("Socket is not connected");
            }
            if (!m_encrypted) {
                throw new InvalidOperationException("Handshake has not been received yet");
            }
            if (data.Length < 2) {
                throw new ArgumentOutOfRangeException("Packet length must be greater than 2", "packet");
            }
            //check for odd number of chars
            //check for invalid chars

            const int HeaderSize = 4;

            lock (m_sendLock) {
                byte[] final = new byte[data.Length + HeaderSize];

                switch (m_sessionType) {
                    case SessionType.Client:
                        m_clientCipher.GetHeaderToServer(data.Length, final);
                        break;
                    case SessionType.Server:
                        m_clientCipher.GetHeaderToClient(data.Length, final);
                        break;
                }

                m_clientCipher.Transform(data);

                Buffer.BlockCopy(data, 0, final, HeaderSize, data.Length);

                SendRawPacket(final);
            }
        }

        private void SendRawPacket(byte[] packet) {
            int offset = 0;

            while (offset < packet.Length) {
                SocketError errorCode = SocketError.Success;
                int sent = m_socket.Send(packet, offset, packet.Length - offset, SocketFlags.None, out errorCode);

                if (sent == 0 || errorCode != SocketError.Success) {
                    Disconnect();
                    return;
                }

                offset += sent;
            }
        }

        public void Disconnect(bool finished = true) {
            if (m_connected) {
                m_connected = false;
                m_encrypted = false;
                m_cursor = 0;

                m_socket.Shutdown(SocketShutdown.Both);
                m_socket.Disconnect(false);
                m_socket.Dispose();

                if (finished) {
                    m_clientCipher = null;
                    m_serverCipher = null;

                    if (OnDisconnected != null)
                        OnDisconnected(this, null);
                }
            }

        }
    }
}
