using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Common;

namespace ClientMock
{
    class Program
    {
        private const string ServerIp = "127.0.0.1";
        private const int Port = 17777;
        private const int TotalBotCount = 100; // 100명 멀티 스트레스 테스트 그대로 유지

        static async Task Main(string[] args)
        {
            Console.WriteLine($"[멀티 패킷 테스트] 가상 유저 {TotalBotCount}명이 로그인/이동/채팅 시나리오를 시작합니다.");

            Task[] botTasks = new Task[TotalBotCount];

            for (int i = 0; i < TotalBotCount; i++)
            {
                int botId = i + 1;
                botTasks[i] = RunBotAsync(botId);
            }

            await Task.WhenAll(botTasks);

            Console.WriteLine("\n[테스트 종료] 모든 시나리오 봇 테스트가 완료되었습니다.");
            Console.ReadLine();
        }

        private static async Task RunBotAsync(int botId)
        {
            try
            {
                using (TcpClient client = new TcpClient())
                {
                    await client.ConnectAsync(ServerIp, Port);

                    using (NetworkStream stream = client.GetStream())
                    {
                        Random rand = new Random();

                        // --------------------------------------------------------
                        // 시나리오 ①: 접속하자마자 [LOGIN] 패킷 던지기
                        // --------------------------------------------------------
                        LoginRequestPacket loginPacket = new LoginRequestPacket();
                        loginPacket.Header.PacketSize = (ushort)Marshal.SizeOf(typeof(LoginRequestPacket));
                        loginPacket.Header.Id = (ushort)PacketType.LoginRequest;
                        loginPacket.Username = $"User_Chun_{botId}";

                        byte[] loginBuffer = StructureToBytes(loginPacket);
                        await stream.WriteAsync(loginBuffer, 0, loginBuffer.Length);

                        // --------------------------------------------------------
                        // ★ [핵심 추가] 서버가 주는 로그인 응답 패킷(LoginResponse) 기다려서 내 진짜 ID 받아내기
                        // --------------------------------------------------------
                        byte[] resHeaderBuffer = new byte[4];
                        await stream.ReadAsync(resHeaderBuffer, 0, 4); // 헤더 읽기

                        // 응답 패킷 전체 크기만큼 바디 읽기
                        int resBodySize = Marshal.SizeOf(typeof(LoginResponsePacket));
                        byte[] resFullBuffer = new byte[resBodySize];
                        Buffer.BlockCopy(resHeaderBuffer, 0, resFullBuffer, 0, 4);
                        await stream.ReadAsync(resFullBuffer, 4, resBodySize - 4);

                        // 마샬링으로 진짜 ID 추출
                        IntPtr ptr = Marshal.AllocHGlobal(resBodySize);
                        Marshal.Copy(resFullBuffer, 0, ptr, resBodySize);
                        LoginResponsePacket loginRes = (LoginResponsePacket)Marshal.PtrToStructure(ptr, typeof(LoginResponsePacket));
                        Marshal.FreeHGlobal(ptr);

                        int myServerPlayerId = loginRes.PlayerId; // ◀ 서버가 공인해 준 진짜 내 번호!

                        await Task.Delay(rand.Next(100, 300));

                        // --------------------------------------------------------
                        // 시나리오 ②: 공인된 진짜 ID로 [MOVE] 패킷 5번 던지기
                        // --------------------------------------------------------
                        for (int loop = 1; loop <= 5; loop++)
                        {
                            MovePacket movePacket = new MovePacket();
                            movePacket.Header.PacketSize = (ushort)Marshal.SizeOf(typeof(MovePacket));
                            movePacket.Header.Id = (ushort)PacketType.MoveNotify;

                            movePacket.PlayerId = myServerPlayerId; // ★ 가짜 botId 대신 진짜 ID 세팅!
                            movePacket.PosX = -5.0f + (rand.Next(-20, 20) * 0.1f); // 리얼한 난수 좌표
                            movePacket.PosY = -0.2f + (rand.Next(-20, 20) * 0.1f); // ★ Y축도 움직이게 난수 처리
                            movePacket.DirX = (byte)rand.Next(1, 3);               // 1: 오른쪽, 2: 왼쪽
                            movePacket.IsMoving = 1;                               // 달리는 중 상태 공유

                            byte[] moveBuffer = StructureToBytes(movePacket);
                            await stream.WriteAsync(moveBuffer, 0, moveBuffer.Length);

                            await Task.Delay(rand.Next(300, 700));
                        }

                        // --------------------------------------------------------
                        // 시나리오 ③: 나가기 전에 [CHAT] 패킷 한마디 던지기
                        // --------------------------------------------------------
                        ChatPacket chatPacket = new ChatPacket();
                        chatPacket.Header.PacketSize = (ushort)Marshal.SizeOf(typeof(ChatPacket));
                        chatPacket.Header.Id = (ushort)PacketType.ChatNotify;
                        chatPacket.PlayerId = myServerPlayerId; // ★ 진짜 ID 세팅!
                        chatPacket.Message = $"안녕하세요! {botId}번 봇입니다. 평면 이동 끝내주네요!";

                        byte[] chatBuffer = StructureToBytes(chatPacket);
                        await stream.WriteAsync(chatBuffer, 0, chatBuffer.Length);

                        await Task.Delay(rand.Next(1000, 2000)); // 연동 확인을 위해 조금 더 살아있다가 나가기
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[봇 {botId}번 에러] {ex.Message}");
            }
        }

        // 구조체 -> 바이트 배열 마샬링 함수
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
    }
}