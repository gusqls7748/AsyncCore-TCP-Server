using System;
using System.Runtime.InteropServices;

namespace Common
{
    // 패킷의 종류를 구분하는 번호판 (enum)
    public enum PacketType : ushort
    {
        LoginRequest = 1,  // 로그인 요청 (클라이언트 -> 서버)
        LoginResponse = 2, // 로그인 결과 (서버 -> 클라이언트)
        MoveNotify = 3,    // 이동 알림 (양방향)
        ChatNotify = 4     // 채팅 메시지 (양방향)
    }

    // 1. 모든 택배 상자의 겉면에 붙는 [공통 운송장] (4바이트)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PacketHeader
    {
        public ushort PacketSize;
        public ushort Id;
    }

    // 2. [기존] 이동 패킷 규격 (총 16바이트)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MovePacket
    {
        public PacketHeader Header;
        public int PlayerId;
        public float PosX;
        public float PosY;
    }

    // 3. [신규] 로그인 요청 패킷 규격
    // 고정 크기 배열을 쓰기 위해 MarshalAs를 사용합니다. (C++ 스타일 크기 고정)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct LoginRequestPacket
    {
        public PacketHeader Header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Username; // 유저 아이디 (최대 16바이트)
    }

    // 4. [신규] 채팅 패킷 규격
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ChatPacket
    {
        public PacketHeader Header;
        public int PlayerId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Message; // 채팅 메시지 (최대 64바이트)
    }
}