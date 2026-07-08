using System;
using System.Buffers;
using System.Collections.Generic; // Dictionary를 쓰기 위해 추가
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Common;
using System.Collections.Concurrent; // 맨 위에 추가 필요

namespace Server
{
    // Program 클래스 내부 상단에 추가해 주세요.
    class ClientSession
    {
        public int PlayerId { get; set; }
        public string Username { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public NetworkStream Stream { get; set; } // 나중에 브로드캐스트(역전송)할 때 쓸 통로
    }


    class Program
    {
        private const int Port = 7777;

        // ★ [핵심] 패킷 종류(PacketType)와 이를 처리할 함수(Action)를 짝지어두는 대형 보관함입니다.
        // C++의 함수 포인터 맵과 완전히 동일한 고성능 매핑 구조입니다.
        private static Dictionary<ushort, Action<byte[]>> _packetHandlers = new Dictionary<ushort, Action<byte[]>>();

        // ★ [신규] 현재 서버에 접속해서 로그인한 유저들을 관리하는 멀티스레드 세이프 보관소
        private static ConcurrentDictionary<int, ClientSession> _sessions = new ConcurrentDictionary<int, ClientSession>();

        static async Task Main(string[] args)
        {
            // 서버가 켜질 때 핸들러 매핑 보관함에 처리 함수들을 미리 등록(초기화)해 둡니다.
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

        // 각 패킷 번호와 처리 함수를 1:1로 묶어주는 등록 함수
        private static void RegisterPacketHandlers()
        {
            _packetHandlers.Add((ushort)PacketType.LoginRequest, OnRecvLoginRequest);
            _packetHandlers.Add((ushort)PacketType.MoveNotify, OnRecvMoveNotify);
            _packetHandlers.Add((ushort)PacketType.ChatNotify, OnRecvChatNotify);
        }

        private static async Task HandleClientAsync(TcpClient client)
        {
            // ★ [새로운 기술] 이 유저만을 위한 4KB짜리 전용 임시 저축 통장(링 버퍼)을 개설합니다.
            RingBuffer ringBuffer = new RingBuffer(4096);

            using (NetworkStream stream = client.GetStream())
            {
                // 메모리 풀에서 꺼내 쓸 임시 수신 바구니 (한 번에 최대 1024바이트씩 인터넷선에서 퍼옵니다)
                byte[] recvBuffer = ArrayPool<byte>.Shared.Rent(1024);

                try
                {
                    while (true)
                    {
                        // 1. 인터넷선(Socket)에 데이터가 들어왔는지 확인하고 퍼옵니다.
                        // 링 버퍼의 남은 빈 공간 크기만큼만 안전하게 받아옵니다.
                        int bytesRead = await stream.ReadAsync(recvBuffer, 0, Math.Min(recvBuffer.Length, ringBuffer.FreeSize));
                        if (bytesRead == 0) break; // 연결 종료됨

                        // 2. 퍼온 데이터를 우선 링 버퍼 통장에 차곡차곡 기록합니다.
                        Buffer.BlockCopy(recvBuffer, 0, ringBuffer.GetBuffer(), ringBuffer.WriteOffset, bytesRead);
                        ringBuffer.OnWrite(bytesRead);

                        // 3. ★ [링 버퍼 핵심 루프]: 통장에 쌓인 데이터를 패킷 단위로 칼같이 썰어냅니다.
                        while (true)
                        {
                            // 🔍 최소한 운송장(헤더: 4바이트) 크기만큼은 모였는지 체크
                            if (ringBuffer.DataSize < 4)
                            {
                                break; // 헤더도 다 안 왔으니 다음 데이터를 더 기다리러 나갑니다.
                            }

                            // 🔍 헤더가 모였다면, 임시로 헤더 주소를 짚어서 패킷의 전체 크기를 읽어옵니다.
                            // 복사본 버퍼를 만들지 않고 링 버퍼 내부 배열을 마샬링으로 직접 슥 읽어 연산 효율을 높입니다.
                            int readOffset = ringBuffer.ReadOffset;
                            byte[] mainBuf = ringBuffer.GetBuffer();

                            // 헤더 데이터를 구조체로 복원하여 패킷 크기 파악
                            byte[] tempHeader = new byte[4];
                            Buffer.BlockCopy(mainBuf, readOffset, tempHeader, 0, 4);
                            PacketHeader header = BytesToStructure<PacketHeader>(tempHeader);

                            // 🔍 "통장에 쌓인 진짜 데이터"가 "이 패킷이 요구하는 크기"보다 작은지 체크
                            if (ringBuffer.DataSize < header.PacketSize)
                            {
                                break; // 몸통 데이터가 쪼개져서 아직 덜 온 것이므로, 다음 데이터를 더 기다립니다.
                            }

                            // 🔍 [패킷 커팅 성공!]: 완전한 패킷이 통장에 다 모였습니다.
                            // 메모리 풀에서 딱 그 패킷 크기만큼 바구니를 빌려서 알맹이를 쏙 복사합니다.
                            byte[] fullPacketBuffer = ArrayPool<byte>.Shared.Rent(header.PacketSize);
                            Buffer.BlockCopy(mainBuf, readOffset, fullPacketBuffer, 0, header.PacketSize);

                            // 패킷 하나를 안전하게 꺼냈으니, 링 버퍼의 읽기 바늘을 패킷 크기만큼 전진시킵니다.
                            ringBuffer.OnRead(header.PacketSize);

                            // 4. 운송장 번호(header.Id)를 보고 O(1) 속도로 담당 부서(함수)로 즉시 토스!
                            if (_packetHandlers.TryGetValue(header.Id, out Action<byte[]> handler))
                            {
                                handler.Invoke(fullPacketBuffer);
                            }
                            else
                            {
                                Console.WriteLine($"[경고] 등록되지 않은 패킷 번호: {header.Id}");
                            }

                            // 빌렸던 바구니는 볼일이 끝났으니 즉시 반납 (Zero-GC)
                            ArrayPool<byte>.Shared.Return(fullPacketBuffer);

                            // 혹시 뒤에 패킷이 더 뭉쳐서 들어왔을 수 있으니, while 루프를 다시 돌며 검사합니다!
                        }

                        // 5. 바늘이 너무 뒤로 갔다면 데이터를 앞으로 당겨서 빈 공간을 확보합니다.
                        ringBuffer.CleanUp();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[에러 발생] {ex.Message}");
                }
                finally
                {
                    // 수신용 임시 바구니 반납
                    ArrayPool<byte>.Shared.Return(recvBuffer);
                }
            }

            Console.WriteLine($"[종료] 클라이언트 연결이 안전하게 해제되었으며 월드에서 제거되었습니다.");
        }

        #region 【 패킷 개별 처리 함수 공간 (Packet Handlers) 】

        // ① 로그인 패킷 전용 처리 함수
        private static void OnRecvLoginRequest(byte[] packetBuffer)
        {
            LoginRequestPacket packet = BytesToStructure<LoginRequestPacket>(packetBuffer);

            // 임의로 패킷 ID나 해시코드를 이용해 고유 PlayerId를 부여합니다.
            // 여기서는 간단하게 보관소의 현재 크기 + 1을 고유 ID로 쓰거나 시뮬레이션용 ID를 씁니다.
            int dummyPlayerId = _sessions.Count + 1;

            ClientSession session = new ClientSession
            {
                PlayerId = dummyPlayerId,
                Username = packet.Username,
                PosX = 0,
                PosY = 0
            };

            // 보관소에 등록
            _sessions.TryAdd(dummyPlayerId, session);

            Console.WriteLine($"[GAME - LOGIN] 세션 생성 성공! [ID: {dummyPlayerId}] 유저네임: {session.Username}님이 월드에 진입했습니다.");
        }
        

        // ② 이동 패킷 전용 처리 함수 (기존 로직)
        private static void OnRecvMoveNotify(byte[] packetBuffer)
        {
            MovePacket packet = BytesToStructure<MovePacket>(packetBuffer);

            // 보관소에서 해당 유저를 찾아서 좌표를 갱신합니다.
            if (_sessions.TryGetValue(packet.PlayerId, out ClientSession session))
            {
                session.PosX = packet.PosX;
                session.PosY = packet.PosY;

                Console.WriteLine($"[GAME - WORLD] 유저 {session.Username}({packet.PlayerId}번) 위치 동기화 -> X: {session.PosX:F1}, Y: {session.PosY:F1}");
            }
            else
            {
                // 만약 로그인을 안 하고 이동부터 보낸 경우 예외 처리
                Console.WriteLine($"[GAME - ERROR] 로그인하지 않은 가상 유저 {packet.PlayerId}번이 이동을 시도했습니다.");
            }
        }

        // ③ 채팅 패킷 처리: 내가 받은 채팅을 월드의 모든 유저에게 다시 쏴주기! (브로드캐스트)
        private static void OnRecvChatNotify(byte[] packetBuffer)
        {
            ChatPacket packet = BytesToStructure<ChatPacket>(packetBuffer);

            // 보관소에서 이 채팅을 보낸 유저를 찾습니다.
            if (_sessions.TryGetValue(packet.PlayerId, out ClientSession senderSession))
            {
                Console.WriteLine($"[서버 내부 로직] <{senderSession.Username}>의 채팅을 모든 유저에게 브로드캐스트 합니다.");

                // ★ [새로운 기술] 보관소에 등록된 모든 유저를 하나씩 꺼내서 전단을 돌립니다 (foreach)
                foreach (var pair in _sessions)
                {
                    ClientSession targetSession = pair.Value;

                    // 유저의 파이프라인(Stream)이 살아있다면 메시지를 역전송합니다.
                    if (targetSession.Stream != null && targetSession.Stream.CanWrite)
                    {
                        try
                        {
                            // 클라이언트가 보낸 채팅 패킷 그대로 다른 사람들에게 토스!
                            // 원래는 동기(Write)로 짜면 렉 걸리지만, 실무형 서버답게 던지고 바로 다음 일 하도록 유도합니다.
                            targetSession.Stream.Write(packetBuffer, 0, packetBuffer.Length);
                        }
                        catch
                        {
                            // 연결이 끊긴 유저는 무시하고 넘어갑니다.
                        }
                    }
                }
            }
        }

        #endregion

        // [로우레벨] 구조체 변환기
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