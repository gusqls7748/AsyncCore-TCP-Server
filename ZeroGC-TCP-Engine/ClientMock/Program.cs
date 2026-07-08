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
        private const int Port = 7777;
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
                        loginPacket.Username = $"User_Chun_{botId}"; // Chun님의 고유 유저 아이디 생성

                        byte[] loginBuffer = StructureToBytes(loginPacket);
                        await stream.WriteAsync(loginBuffer, 0, loginBuffer.Length);

                        // 패킷이 겹치지 않게 살짝 텀 주기
                        await Task.Delay(rand.Next(100, 300));

                        // --------------------------------------------------------
                        // 시나리오 ②: 기존대로 [MOVE] 패킷을 5번 반복해서 던지기
                        // --------------------------------------------------------
                        for (int loop = 1; loop <= 5; loop++)
                        {
                            MovePacket movePacket = new MovePacket();
                            movePacket.Header.PacketSize = (ushort)Marshal.SizeOf(typeof(MovePacket));
                            movePacket.Header.Id = (ushort)PacketType.MoveNotify;

                            movePacket.PlayerId = botId;
                            movePacket.PosX = 10.0f * loop;
                            movePacket.PosY = 20.0f * loop;

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
                        chatPacket.PlayerId = botId;
                        chatPacket.Message = $"안녕하세요! {botId}번 봇입니다. 성능 끝내주네요!";

                        byte[] chatBuffer = StructureToBytes(chatPacket);
                        await stream.WriteAsync(chatBuffer, 0, chatBuffer.Length);

                        await Task.Delay(rand.Next(200, 500));
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