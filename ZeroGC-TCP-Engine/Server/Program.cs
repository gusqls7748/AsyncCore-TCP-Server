using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Common;
using System.Collections.Concurrent;

namespace Server
{
    class ClientSession
    {
        public int PlayerId { get; set; }
        public string Username { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public NetworkStream Stream { get; set; }

        // ★ 이 세션의 스트림에 대한 쓰기 작업을 한 번에 하나씩만 허용하는 락
        // (여러 클라이언트의 스레드가 동시에 같은 스트림에 Write하는 걸 방지)
        public readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
    }

    class Program
    {
        private const int Port = 17777;

        // ★ 핸들러가 비동기로 SafeSend를 호출해야 하므로 Func<..., Task>로 변경
        private static Dictionary<ushort, Func<byte[], NetworkStream, Task>> _packetHandlers =
            new Dictionary<ushort, Func<byte[], NetworkStream, Task>>();

        private static ConcurrentDictionary<int, ClientSession> _sessions =
            new ConcurrentDictionary<int, ClientSession>();

        private static int _nextPlayerId = 0;

        static async Task Main(string[] args)
        {
            RegisterPacketHandlers();

            TcpListener listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
            Console.WriteLine($"[서버] 고성능 멀티 패킷 서버가 {Port}번 포트에서 시작되었습니다.");

            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();
                Console.WriteLine($"[접속] 새로운 가상 클라이언트가 서버에 연결되었습니다.");
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private static void RegisterPacketHandlers()
        {
            _packetHandlers.Add((ushort)PacketType.LoginRequest, OnRecvLoginRequest);
            _packetHandlers.Add((ushort)PacketType.MoveNotify, OnRecvMoveNotify);
            _packetHandlers.Add((ushort)PacketType.ChatNotify, OnRecvChatNotify);
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            RingBuffer ringBuffer = new RingBuffer(4096);

            using (NetworkStream stream = client.GetStream())
            {
                byte[] recvBuffer = ArrayPool<byte>.Shared.Rent(1024);

                try
                {
                    while (true)
                    {
                        int bytesRead = await stream.ReadAsync(recvBuffer, 0, Math.Min(recvBuffer.Length, ringBuffer.FreeSize));
                        if (bytesRead == 0) break;

                        Buffer.BlockCopy(recvBuffer, 0, ringBuffer.GetBuffer(), ringBuffer.WriteOffset, bytesRead);
                        ringBuffer.OnWrite(bytesRead);

                        while (true)
                        {
                            if (ringBuffer.DataSize < 4) break;

                            int readOffset = ringBuffer.ReadOffset;
                            byte[] mainBuf = ringBuffer.GetBuffer();

                            byte[] tempHeader = new byte[4];
                            Buffer.BlockCopy(mainBuf, readOffset, tempHeader, 0, 4);
                            PacketHeader header = BytesToStructure<PacketHeader>(tempHeader);

                            if (header.PacketSize <= 0 || ringBuffer.DataSize < header.PacketSize) break;

                            byte[] exactPacketBuffer = new byte[header.PacketSize];
                            Buffer.BlockCopy(mainBuf, readOffset, exactPacketBuffer, 0, header.PacketSize);

                            ringBuffer.OnRead(header.PacketSize);

                            if (_packetHandlers.TryGetValue(header.Id, out Func<byte[], NetworkStream, Task> handler))
                            {
                                // ★ await로 처리: 이 커넥션의 패킷은 순서대로 처리되고,
                                //   그 안에서 벌어지는 브로드캐스트 Write는 SafeSend가 안전하게 직렬화함
                                await handler.Invoke(exactPacketBuffer, stream);
                            }
                            else
                            {
                                Console.WriteLine($"[경고] 등록되지 않은 패킷 번호: {header.Id}");
                            }
                        }

                        ringBuffer.CleanUp();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[서버 세션 에러 발생] {ex.Message}");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(recvBuffer);

                    foreach (var pair in _sessions)
                    {
                        if (pair.Value != null && ReferenceEquals(pair.Value.Stream, stream))
                        {
                            if (_sessions.TryRemove(pair.Key, out ClientSession removed))
                            {
                                Console.WriteLine($"[정리] 유저 {removed.Username}({pair.Key}번) 세션이 제거되었습니다.");
                            }
                            break;
                        }
                    }
                }
            }

            Console.WriteLine($"[종료] 클라이언트 연결이 안전하게 해제되었습니다.");
        }

        // ★ [핵심 추가] 세션별 락을 통해 같은 스트림에 대한 동시 Write를 방지하는 안전한 전송 함수
        private static async Task SafeSend(ClientSession targetSession, byte[] buffer)
        {
            if (targetSession?.Stream == null || !targetSession.Stream.CanWrite) return;

            await targetSession.SendLock.WaitAsync();
            try
            {
                await targetSession.Stream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch
            {
                // 연결이 끊긴 세션에 쓰다가 발생하는 예외는 무시
                // (다음 ReadAsync에서 0바이트 수신으로 감지되어 자연스럽게 정리됨)
            }
            finally
            {
                targetSession.SendLock.Release();
            }
        }

        #region 【 패킷 개별 처리 함수 공간 (Packet Handlers) 】

        private static async Task OnRecvLoginRequest(byte[] packetBuffer, NetworkStream stream)
        {
            LoginRequestPacket packet = BytesToStructure<LoginRequestPacket>(packetBuffer);

            int newPlayerId = Interlocked.Increment(ref _nextPlayerId);

            ClientSession session = new ClientSession
            {
                PlayerId = newPlayerId,
                Username = packet.Username,
                PosX = 0,
                PosY = 0,
                Stream = stream
            };

            _sessions.TryAdd(newPlayerId, session);

            Console.WriteLine($"[GAME - LOGIN] 세션 생성 성공! [ID: {newPlayerId}] 유저네임: {session.Username}님이 월드에 진입했습니다.");

            LoginResponsePacket responsePacket = new LoginResponsePacket();
            responsePacket.Header.PacketSize = (ushort)Marshal.SizeOf(typeof(LoginResponsePacket));
            responsePacket.Header.Id = (ushort)PacketType.LoginResponse;
            responsePacket.PlayerId = newPlayerId;
            responsePacket.Success = true;

            byte[] sendBuffer = StructureToBytes(responsePacket);

            // ★ 직접 stream.Write 대신 SafeSend로 안전하게 전송
            await SafeSend(session, sendBuffer);

            Console.WriteLine($"[GAME - LOGIN] 유저 [ID: {newPlayerId}]에게 로그인 응답 패킷을 전송했습니다.");
        }

        private static async Task OnRecvMoveNotify(byte[] packetBuffer, NetworkStream stream)
        {
            MovePacket packet = BytesToStructure<MovePacket>(packetBuffer);

            if (_sessions.TryGetValue(packet.PlayerId, out ClientSession session))
            {
                session.PosX = packet.PosX;
                session.PosY = packet.PosY;

                Console.WriteLine($"[GAME - WORLD] 유저 {session.Username}({packet.PlayerId}번) 위치 동기화 -> X: {session.PosX:F1}, Y: {session.PosY:F1}");

                // ★ 다른 모든 유저에게 브로드캐스트 (각 타겟 세션의 SendLock으로 보호됨)
                List<Task> sendTasks = new List<Task>();

                foreach (var pair in _sessions)
                {
                    ClientSession targetSession = pair.Value;
                    if (targetSession.PlayerId == packet.PlayerId) continue;

                    sendTasks.Add(SafeSend(targetSession, packetBuffer));
                }

                await Task.WhenAll(sendTasks);
            }
            else
            {
                Console.WriteLine($"[GAME - ERROR] 로그인하지 않은 가상 유저 {packet.PlayerId}번이 이동을 시도했습니다.");
            }
        }

        private static async Task OnRecvChatNotify(byte[] packetBuffer, NetworkStream stream)
        {
            ChatPacket packet = BytesToStructure<ChatPacket>(packetBuffer);

            if (_sessions.TryGetValue(packet.PlayerId, out ClientSession senderSession))
            {
                Console.WriteLine($"[서버 내부 로직] <{senderSession.Username}>의 채팅을 모든 유저에게 브로드캐스트 합니다.");

                List<Task> sendTasks = new List<Task>();

                foreach (var pair in _sessions)
                {
                    sendTasks.Add(SafeSend(pair.Value, packetBuffer));
                }

                await Task.WhenAll(sendTasks);
            }
        }

        #endregion

        private static byte[] StructureToBytes<T>(T str) where T : struct
        {
            int size = Marshal.SizeOf(str);
            byte[] arr = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(str, ptr, false);
            Marshal.Copy(ptr, arr, 0, size);
            Marshal.FreeHGlobal(ptr);
            return arr;
        }

        private static T BytesToStructure<T>(byte[] bytes) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.Copy(bytes, 0, ptr, size);
            T str = (T)Marshal.PtrToStructure(ptr, typeof(T));
            Marshal.FreeHGlobal(ptr);
            return str;
        }
    }
}