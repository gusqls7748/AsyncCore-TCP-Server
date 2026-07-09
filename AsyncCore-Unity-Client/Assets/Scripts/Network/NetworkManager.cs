using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

public class NetworkManager : MonoBehaviour
{
    public static NetworkManager Instance { get; private set; }

    [Header("Player Settings")]
    public Transform playerTransform;
    public GameObject playerPrefab;

    private Dictionary<int, GameObject> _players = new Dictionary<int, GameObject>();

    private TcpClient _client;
    private NetworkStream _stream;
    private bool _isConnected = false;

    private byte[] _ringBufferArray;
    private int _writePos = 0;
    private int _readPos = 0;
    private const int BUFFER_SIZE = 4096;

    private int _myPlayerId = -1;

    private Queue<KeyValuePair<ushort, byte[]>> _packetQueue = new Queue<KeyValuePair<ushort, byte[]>>();
    private readonly object _queueLock = new object();

    // ★ 여기 있던 중복 PacketType / LoginRequestPacket / LoginResponsePacket 정의를
    //   모두 삭제했습니다. 이제 전역 Packet.cs의 정의(서버와 동일 SizeConst=16)를 사용합니다.

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            _ringBufferArray = new byte[BUFFER_SIZE];
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void Start()
    {
        ConnectToServer("127.0.0.1", 17777);
    }

    private void Update()
    {
        lock (_queueLock)
        {
            while (_packetQueue.Count > 0)
            {
                var packet = _packetQueue.Dequeue();
                HandlePacket(packet.Key, packet.Value);
            }
        }
    }

    public async Task ConnectToServer(string ip, int port)
    {
        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(ip, port);
            _stream = _client.GetStream();
            _isConnected = true;
            Debug.Log($"[네트워크] 서버({ip}:{port})에 성공적으로 연결되었습니다!");

            _ = Task.Run(() => ReceiveLoopAsync());

            SendLoginRequest("User_Chun");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[네트워크 연결 실패] {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] recvBuffer = new byte[1024];

        try
        {
            while (_isConnected)
            {
                int freeSize = BUFFER_SIZE - _writePos;
                if (freeSize <= 0)
                {
                    CleanUpBuffer();
                    freeSize = BUFFER_SIZE - _writePos;
                }

                int bytesRead = await _stream.ReadAsync(recvBuffer, 0, Math.Min(recvBuffer.Length, freeSize));
                if (bytesRead == 0) break;

                Buffer.BlockCopy(recvBuffer, 0, _ringBufferArray, _writePos, bytesRead);
                _writePos += bytesRead;

                while (true)
                {
                    int dataSize = _writePos - _readPos;
                    if (dataSize < 4) break;

                    byte[] tempHeader = new byte[4];
                    Buffer.BlockCopy(_ringBufferArray, _readPos, tempHeader, 0, 4);

                    GCHandle handle = GCHandle.Alloc(tempHeader, GCHandleType.Pinned);
                    PacketHeader header = (PacketHeader)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(PacketHeader));
                    handle.Free();

                    if (dataSize < header.PacketSize) break;

                    byte[] fullPacketBuffer = new byte[header.PacketSize];
                    Buffer.BlockCopy(_ringBufferArray, _readPos, fullPacketBuffer, 0, header.PacketSize);

                    _readPos += header.PacketSize;

                    lock (_queueLock)
                    {
                        _packetQueue.Enqueue(new KeyValuePair<ushort, byte[]>(header.Id, fullPacketBuffer));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[네트워크 수신 에러] {ex.Message}");
        }
    }

    private void CleanUpBuffer()
    {
        int dataSize = _writePos - _readPos;
        if (dataSize > 0)
        {
            Buffer.BlockCopy(_ringBufferArray, _readPos, _ringBufferArray, 0, dataSize);
            _readPos = 0;
            _writePos = dataSize;
        }
        else
        {
            _readPos = 0;
            _writePos = 0;
        }
    }

    private void HandlePacket(ushort packetId, byte[] rawData)
    {
        // ==========================================
        // 1. 로그인 응답 패킷 처리 분기
        // ==========================================
        if (packetId == (ushort)PacketType.LoginResponse)
        {
            GCHandle handle = GCHandle.Alloc(rawData, GCHandleType.Pinned);
            LoginResponsePacket loginRes = (LoginResponsePacket)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(LoginResponsePacket));
            handle.Free();

            // [체크] 서버와 bool 타입으로 통일하셨다면 그대로 사용, byte(0,1)라면 (loginRes.Success == 1)로 변경하세요.
            if (loginRes.Success)
            {
                _myPlayerId = loginRes.PlayerId;
                Debug.Log($"[네트워크 로그인 성공] 서버로부터 발급받은 내 고유 ID: {_myPlayerId}");
            }
            else
            {
                Debug.LogError("[네트워크 로그인 실패] 서버가 진입을 거부했습니다.");
            }
            return; // 로그인 처리가 끝났으므로 함수 탈출
        }

        // ==========================================
        // 2. 이동 알림(브로드캐스트) 패킷 처리 분기
        // ==========================================
        if (packetId == (ushort)PacketType.MoveNotify)
        {
            GCHandle handle = GCHandle.Alloc(rawData, GCHandleType.Pinned);
            MovePacket movePacket = (MovePacket)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(MovePacket));
            handle.Free();

            // 내 화면에 없는 새로운 유저라면 월드에 캐릭터 프리팹 생성(Spawn)
            if (!_players.ContainsKey(movePacket.PlayerId))
            {
                GameObject newPlayer = Instantiate(playerPrefab);
                _players.Add(movePacket.PlayerId, newPlayer);
                Debug.Log($"[생성] 새로운 가상 유저 {movePacket.PlayerId}번이 월드에 생성되었습니다.");
            }

            // 동기화할 타겟 캐릭터 오브젝트 추출
            GameObject targetPlayer = _players[movePacket.PlayerId];

            // [기능 1] 실시간 위치 동기화
            //targetPlayer.transform.position = new Vector3(movePacket.PosX, movePacket.PosY, 0);
            // 패킷을 보낸 유저의 ID가 '나(_myPlayerId)'라면 내 화면의 물리 부드러움을 위해 위치 강제 대입을 패스합니다!
            if (movePacket.PlayerId != _myPlayerId)
            {
                // [기능 1] 다른 유저나 봇의 실시간 위치만 동기화
                targetPlayer.transform.position = new Vector3(movePacket.PosX, movePacket.PosY, 0);
            }

            // [기능 2] 애니메이션 상태 동기화 (Idle <-> Run)
            Animator anim = targetPlayer.GetComponent<Animator>();
            if (anim != null)
            {
                // movePacket에 심어온 IsMoving이 1이면 달리기 애니메이션 On, 0이면 Off
                anim.SetBool("isMoving", movePacket.IsMoving == 1); // ◀ 여기 파라미터 이름!
            }

            // [기능 3] 바라보는 방향 동기화 (좌우 Flip)
            if (movePacket.DirX == 1)      // 오른쪽 바라봄
            {
                targetPlayer.transform.localScale = new Vector3(1, 1, 1);
            }
            else if (movePacket.DirX == 2) // 왼쪽 바라봄
            {
                targetPlayer.transform.localScale = new Vector3(-1, 1, 1); // X축 반전
            }
        }
    }

    public void SendMovePacket(float x, float y, byte dirX, byte isMoving)
    {
        if (!_isConnected || _stream == null || _myPlayerId == -1) return;

        try
        {
            MovePacket packet = new MovePacket();
            packet.Header.PacketSize = (ushort)Marshal.SizeOf(typeof(MovePacket));
            packet.Header.Id = (ushort)PacketType.MoveNotify;
            packet.PlayerId = _myPlayerId;
            packet.PosX = x;
            packet.PosY = y;

            // ★ [여기가 핵심] 구조체에 새로 정의한 변수들을 매개변수로 받아온 값으로 채워줍니다.
            packet.DirX = dirX;
            packet.IsMoving = isMoving;

            int size = Marshal.SizeOf(packet);
            byte[] sendBuffer = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(packet, ptr, false);
            Marshal.Copy(ptr, sendBuffer, 0, size);
            Marshal.FreeHGlobal(ptr);

            _stream.Write(sendBuffer, 0, sendBuffer.Length);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[패킷 송신 에러] {ex.Message}");
        }
    }

    public void SendLoginRequest(string username)
    {
        if (!_isConnected || _stream == null) return;

        try
        {
            LoginRequestPacket packet = new LoginRequestPacket();
            packet.Header.PacketSize = (ushort)Marshal.SizeOf(typeof(LoginRequestPacket));
            packet.Header.Id = (ushort)PacketType.LoginRequest;
            packet.Username = username;

            int size = Marshal.SizeOf(packet);
            byte[] sendBuffer = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(packet, ptr, false);
            Marshal.Copy(ptr, sendBuffer, 0, size);
            Marshal.FreeHGlobal(ptr);

            _stream.Write(sendBuffer, 0, sendBuffer.Length);
            Debug.Log($"[네트워크] 서버에 로그인 요청을 보냈습니다. (Username: {username})");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[로그인 송신 에러] {ex.Message}");
        }
    }

    private void OnApplicationQuit()
    {
        _isConnected = false;
        _stream?.Close();
        _client?.Close();
    }
}