using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// =========================================================================
// 1. 에셋의 원래 기반인 추상 클래스 정의 (깔끔하게 정돈)
// =========================================================================
public abstract class PlayerControllerBase : MonoBehaviour
{
    public bool IsSit = false;
    public int currentJumpCount = 0;
    public bool isGrounded = false;
    public bool OnceJumpRayCheck = false;

    public bool Is_DownJump_GroundCheck = false;
    protected float m_MoveX;
    public Rigidbody2D m_rigidbody;
    protected CapsuleCollider2D m_CapsulleCollider;
    protected Animator m_Anim;

    [Header("[Setting]")]
    public float MoveSpeed = 6;
    public int JumpCount = 2;
    public float jumpForce = 15f;

    protected void AnimUpdate()
    {
        if (!m_Anim.GetCurrentAnimatorStateInfo(0).IsName("Attack"))
        {
            if (Input.GetKey(KeyCode.Mouse0))
            {
                m_Anim.Play("Attack");
            }
            else
            {
                if (m_MoveX == 0)
                {
                    if (!OnceJumpRayCheck)
                        m_Anim.Play("Idle");
                }
                else
                {
                    m_Anim.Play("Run");
                }
            }
        }
    }

    protected void Filp(bool bLeft)
    {
        transform.localScale = new Vector3(bLeft ? 1 : -1, 1, 1);
    }

    protected void prefromJump()
    {
        m_Anim.Play("Jump");
        m_rigidbody.linearVelocity = new Vector2(0, 0);
        m_rigidbody.AddForce(Vector2.up * jumpForce, ForceMode2D.Impulse);

        OnceJumpRayCheck = true;
        isGrounded = false;
        currentJumpCount++;
    }

    protected void DownJump()
    {
        if (!isGrounded) return;

        if (!Is_DownJump_GroundCheck)
        {
            m_Anim.Play("Jump");
            m_rigidbody.AddForce(-Vector2.up * 10);
            isGrounded = false;
            m_CapsulleCollider.enabled = false;
            StartCoroutine(GroundCapsulleColliderTimmerFuc());
        }
    }

    IEnumerator GroundCapsulleColliderTimmerFuc()
    {
        yield return new WaitForSeconds(0.3f);
        m_CapsulleCollider.enabled = true;
    }

    float PretmpY;
    float GroundCheckUpdateTic = 0;
    float GroundCheckUpdateTime = 0.01f;

    protected void GroundCheckUpdate()
    {
        if (!OnceJumpRayCheck) return;

        GroundCheckUpdateTic += Time.deltaTime;

        if (GroundCheckUpdateTic > GroundCheckUpdateTime)
        {
            GroundCheckUpdateTic = 0;

            if (PretmpY == 0)
            {
                PretmpY = transform.position.y;
                return;
            }

            float reY = transform.position.y - PretmpY;

            if (reY <= 0)
            {
                if (isGrounded)
                {
                    LandingEvent();
                    OnceJumpRayCheck = false;
                }
            }
            PretmpY = transform.position.y;
        }
    }

    protected abstract void LandingEvent();
}

// =========================================================================
// 2. 2D 평면(Top-down) 조작 및 실시간 네트워크 패킷 처리용 진짜 컨트롤러
// =========================================================================
public class PlayerController : PlayerControllerBase
{
    private byte _lastIsMoving = 0;

    private void Start()
    {
        if (m_rigidbody == null) m_rigidbody = GetComponent<Rigidbody2D>();
        m_CapsulleCollider = GetComponent<CapsuleCollider2D>();
        m_Anim = GetComponent<Animator>();
    }

    // 상하좌우를 완벽히 감지하는 탑다운 전용 애니메이션 체인저
    private void MyPlaneAnimUpdate(float moveY)
    {
        if (m_Anim.GetCurrentAnimatorStateInfo(0).IsName("Attack")) return;

        if (Input.GetKey(KeyCode.Mouse0))
        {
            m_Anim.Play("Attack");
        }
        else
        {
            if (m_MoveX == 0 && moveY == 0)
            {
                m_Anim.Play("Idle");
            }
            else
            {
                m_Anim.Play("Run");
            }
        }
    }

    private void Update()
    {
        // 1. 키보드 입력 받기 (상하좌우 완벽 대응)
        m_MoveX = Input.GetAxisRaw("Horizontal");
        float moveY = Input.GetAxisRaw("Vertical");

        // 부모의 횡스크롤용 함수 대신 탑다운용 애니메이션 업데이트 가동
        MyPlaneAnimUpdate(moveY);

        // 2. 입력 상태에 맞는 메타 데이터 빌드
        byte isMoving = (byte)((Mathf.Abs(m_MoveX) > 0 || Mathf.Abs(moveY) > 0) ? 1 : 0);
        byte dirX = 0;

        if (m_MoveX > 0)
        {
            dirX = 1;       // 오른쪽 바라봄
            Filp(false);
        }
        else if (m_MoveX < 0)
        {
            dirX = 2;       // 왼쪽 바라봄
            Filp(true);
        }

        // 3. 실시간 물리 이동 연산 및 패킷 폭격
        if (isMoving == 1)
        {
            Vector2 moveVector = new Vector2(m_MoveX, moveY).normalized;
            m_rigidbody.linearVelocity = moveVector * MoveSpeed;

            float currentX = transform.position.x;
            float currentY = transform.position.y;

            if (NetworkManager.Instance != null)
            {
                NetworkManager.Instance.SendMovePacket(currentX, currentY, dirX, isMoving);
            }
        }
        else
        {
            // 멈추면 물리 제동
            m_rigidbody.linearVelocity = Vector2.zero;

            // 정지한 순간 딱 한 번 멈춤 패킷 송신
            if (_lastIsMoving == 1)
            {
                float currentX = transform.position.x;
                float currentY = transform.position.y;

                if (NetworkManager.Instance != null)
                {
                    NetworkManager.Instance.SendMovePacket(currentX, currentY, 0, 0);
                }
            }
        }

        _lastIsMoving = isMoving;
    }

    protected override void LandingEvent()
    {
        currentJumpCount = 0;
        isGrounded = true;
    }
}