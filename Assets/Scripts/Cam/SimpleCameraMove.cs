using UnityEngine;
using UnityEngine.InputSystem; // 신규 입력 시스템 네임스페이스 추가

public class SimpleCameraMove : MonoBehaviour
{
    public float moveSpeed = 10f;
    public float lookSpeed = 0.5f; // 마우스 감도 조절

    private float rotationX = 0f;
    private float rotationY = 0f;

    void Start()
    {
        Vector3 rot = transform.localRotation.eulerAngles;
        rotationY = rot.y;
        rotationX = rot.x;
    }

    void Update()
    {
        // 1. 키보드 이동 (New Input System 방식)
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            Vector3 moveDir = Vector3.zero;
            if (keyboard.wKey.isPressed) moveDir += transform.forward;
            if (keyboard.sKey.isPressed) moveDir -= transform.forward;
            if (keyboard.aKey.isPressed) moveDir -= transform.right;
            if (keyboard.dKey.isPressed) moveDir += transform.right;

            transform.position += moveDir * moveSpeed * Time.deltaTime;
        }

        // 2. 마우스 우클릭 회전
        var mouse = Mouse.current;
        if (mouse != null && mouse.rightButton.isPressed)
        {
            // 마우스 이동량(Delta) 가져오기
            Vector2 mouseDelta = mouse.delta.ReadValue();

            rotationY += mouseDelta.x * lookSpeed;
            rotationX -= mouseDelta.y * lookSpeed;
            rotationX = Mathf.Clamp(rotationX, -90f, 90f);

            transform.localRotation = Quaternion.Euler(rotationX, rotationY, 0);
        }
    }
}