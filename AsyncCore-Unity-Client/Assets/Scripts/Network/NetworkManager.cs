using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using UnityEngine;
using System.Collections.Generic;



public class NetworkManager : MonoBehaviour
{
    // 1. 클래스 상단에 참조를 추가하세요 (사각형 오브젝트를 제어하기 위함)
    public Transform playerTransform; // 유니티 인스펙터에서 사각형 오브젝트를 끌어다 놓으세요!

    // NetworkManager.cs에 추가할 내용
    public GameObject playerPrefab; // 유니티에서 캐릭터 프리팹을 할당하세요!

    // 내 화면에 생성된 다른 플레이어들을 관리
    private Dictionary<int, GameObject> _players = new Dictionary<int, GameObject>();

    public static NetworkManager Instance { get; private set; }

    private TcpClient _tcpClient;
    private NetworkStream _stream;
    private bool _isConnected = false;

    // Chun님의 기존 링 버퍼와 호환되도록 내부 버퍼와 인덱스 바늘을 직접 제어합니다.
    private byte[] _ringBufferArray;
    private int _writePos = 0;
    private int _readPos = 0;
    private const int BUFFER_SIZE = 4096;

    // 유니티 메인 스레드 안전 구역으로 패킷을 토스할 수신 주머니(Queue)
    private Queue<KeyValuePair<ushort, byte[]>> _packetQueue = new Queue<KeyValuePair<ushort, byte[]>>();
    private readonly object _queueLock = new object();

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
        ConnectToServer("127.0.0.1", 7777);
    }

    private void Update()
    {
        // ★ [유니티 핵심 관문 통과]
        // 매 프레임마다 메인 스레드(Update) 안에서 주머니에 쌓인 패킷을 안전하게 꺼내어 로직을 터트립니다!
        lock (_queueLock)
        {
            while (_packetQueue.Count > 0)
            {
                var packet = _packetQueue.Dequeue();
                HandlePacket(packet.Key, packet.Value);
            }
        }
    }

    private async void ConnectToServer(string ip, int port)
    {
        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(ip, port);

            _stream = _tcpClient.GetStream();
            _isConnected = true;

            Debug.Log($"[네트워크] 서버({ip}:{port})에 성공적으로 연결되었습니다!");

            _ = ReceiveLoopAsync();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[네트워크 연결 실패] 서버가 켜져 있는지 확인하세요: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] recvBuffer = new byte[1024];

        try
        {
            while (_isConnected)
            {
                // 남은 빈 공간(FreeSize) 계산
                int freeSize = BUFFER_SIZE - _writePos;
                if (freeSize <= 0)
                {
                    // 버퍼가 꽉 찼으면 앞으로 당겨서 청소(CleanUp)
                    CleanUpBuffer();
                    freeSize = BUFFER_SIZE - _writePos;
                }

                int bytesRead = await _stream.ReadAsync(recvBuffer, 0, Math.Min(recvBuffer.Length, freeSize));
                if (bytesRead == 0) break;

                // 완충지대에 임시 통장 기록 복사
                Buffer.BlockCopy(recvBuffer, 0, _ringBufferArray, _writePos, bytesRead);
                _writePos += bytesRead; // 쓰기 바늘 전진

                while (true)
                {
                    // 쌓여있는 데이터 크기(DataSize) 계산
                    int dataSize = _writePos - _readPos;
                    if (dataSize < 4) break; // 최소 헤더 크기 부족

                    // 임시 헤더 4바이트 컷팅 및 로우레벨 마샬링 복원
                    byte[] tempHeader = new byte[4];
                    Buffer.BlockCopy(_ringBufferArray, _readPos, tempHeader, 0, 4);

                    GCHandle handle = GCHandle.Alloc(tempHeader, GCHandleType.Pinned);
                    PacketHeader header = (PacketHeader)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(PacketHeader));
                    handle.Free();

                    // 몸통 데이터가 아직 덜 수신되었다면 break하고 다음 수신 유도
                    if (dataSize < header.PacketSize) break;

                    // [완전한 패킷 추출 성립]
                    byte[] fullPacketBuffer = new byte[header.PacketSize];
                    Buffer.BlockCopy(_ringBufferArray, _readPos, fullPacketBuffer, 0, header.PacketSize);

                    _readPos += header.PacketSize; // 읽기 바늘 전진

                    // 멀티스레드 락을 걸고 주머니에 안전하게 보관
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
            // 남은 파편 데이터를 맨 앞으로 당겨 정렬
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
        // 패킷 ID가 MoveNotify(3번)인 경우
        if (packetId == (ushort)PacketType.MoveNotify)
        {
            // 1. 바이트 배열을 MovePacket 구조체로 변환
            GCHandle handle = GCHandle.Alloc(rawData, GCHandleType.Pinned);
            MovePacket movePacket = (MovePacket)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(MovePacket));
            handle.Free();

            // [핵심] 해당 PlayerId를 가진 오브젝트가 있는지 확인
            if (!_players.ContainsKey(movePacket.PlayerId))
            {
                // 없으면 생성 (Spawn)
                GameObject newPlayer = Instantiate(playerPrefab);
                _players.Add(movePacket.PlayerId, newPlayer);
                Debug.Log($"[생성] 새로운 유저 {movePacket.PlayerId} 생성 완료!");
            }

            // 딕셔너리에서 찾아 위치 업데이트
            _players[movePacket.PlayerId].transform.position = new Vector3(movePacket.PosX, movePacket.PosY, 0);
        }
    }

    private void OnApplicationQuit()
    {
        _isConnected = false;
        _stream?.Close();
        _tcpClient?.Close();
    }

    // [NetworkManager 내부 추가] 서버로 이동 패킷을 쏘는 핵심 함수
    public void SendMovePacket(float x, float y)
    {
        if (!_isConnected || _stream == null) return;

        try
        {
            // 1. 패킷 조립
            MovePacket packet = new MovePacket();
            packet.Header.PacketSize = (ushort)Marshal.SizeOf(typeof(MovePacket));
            packet.Header.Id = (ushort)PacketType.MoveNotify; // 3번 사용!
            packet.PlayerId = 1; // [테스트용] 나중에 로그인 성공 후 받은 실제 ID로 변경 예정
            packet.PosX = x;
            packet.PosY = y;

            // 2. 마샬링 및 전송 로직 (기존과 동일)
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
            // 1. 패킷 조립
            LoginRequestPacket packet = new LoginRequestPacket();
            packet.Header.PacketSize = (ushort)Marshal.SizeOf(typeof(LoginRequestPacket));
            packet.Header.Id = (ushort)PacketType.LoginRequest;
            packet.Username = username;

            // 2. 마샬링 (구조체 -> 바이트 배열)
            int size = Marshal.SizeOf(packet);
            byte[] sendBuffer = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);

            Marshal.StructureToPtr(packet, ptr, false);
            Marshal.Copy(ptr, sendBuffer, 0, size);
            Marshal.FreeHGlobal(ptr);

            // 3. 전송
            _stream.Write(sendBuffer, 0, sendBuffer.Length);
            Debug.Log($"[로그인] {username} 이름으로 서버에 접속 요청을 보냈습니다.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[로그인 송신 에러] {ex.Message}");
        }
    }

    // 서버로부터 이동 패킷을 받았을 때
    private void OnMoveReceived(int playerId, float x, float y)
    {
        if (_players.ContainsKey(playerId))
        {
            // 이미 생성된 플레이어라면 위치만 갱신
            _players[playerId].transform.position = new Vector3(x, y, 0);
        }
        else
        {
            // 만약 없는 ID라면? 여기서 캐릭터를 생성(Spawn)해야 합니다!
        }
    }
}