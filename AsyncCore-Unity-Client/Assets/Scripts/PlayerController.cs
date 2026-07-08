using UnityEngine;

public class PlayerController : MonoBehaviour
{
    [SerializeField] private float _moveSpeed = 5.0f; // 캐릭터 이동 속도

    private void Update()
    {
        // 1. 키보드 입력 받기 (방향키 또는 WASD)
        float h = Input.GetAxisRaw("Horizontal"); // 왼쪽(-1), 오른쪽(1)
        float v = Input.GetAxisRaw("Vertical");   // 아래(-1), 위(1)

        // 2. 이동 방향 벡터 설정
        Vector3 moveDir = new Vector3(h, v, 0).normalized;

        // 3. 실제로 입력이 있을 때만 이동 처리 및 서버 전송
        if (moveDir.magnitude > 0)
        {
            // 화면에서 캐릭터 위치 이동
            transform.position += moveDir * _moveSpeed * Time.deltaTime;

            // 현재 이동한 캐릭터의 최신 좌표 확보
            float currentX = transform.position.x;
            float currentY = transform.position.y;

            // ★ [실시간 엔진 연결] 움직일 때마다 싱글톤 매니저를 통해 서버로 무한 폭격!
            if (NetworkManager.Instance != null)
            {
                NetworkManager.Instance.SendMovePacket(currentX, currentY);
            }

            // [로그 확인] 캐릭터가 움직일 때마다 좌표가 실시간으로 찍힙니다.
            // Debug.Log($"[이동] X: {currentX:F2}, Y: {currentY:F2}");

            // TODO: 3단계에서 NetworkManager.Instance.SendMovePacket(currentX, currentY); 연동 예정!
        }
    }
}