using System.Runtime.InteropServices;


public enum PacketType : ushort
{
    LoginRequest = 1,
    LoginResponse = 2,
    MoveNotify = 3,  // ◀ 서버와 3번으로 정확히 일치!
    ChatNotify = 4
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PacketHeader
{
    public ushort PacketSize;
    public ushort Id;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MovePacket
{
    public PacketHeader Header;
    public int PlayerId;
    public float PosX;
    public float PosY;

    // ====== ★ 애니메이션 동기화를 위해 추가된 멤버 ======
    public byte DirX;     // 1: 오른쪽, 2: 왼쪽 (0은 중립)
    public byte IsMoving; // 0: Idle, 1: Move
}

// Packet.cs 에 추가
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LoginRequestPacket
{
    public PacketHeader Header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
    public string Username;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LoginResponsePacket
{
    public PacketHeader Header;
    public int PlayerId;
    public bool Success;   // ← byte 아니고 bool
}