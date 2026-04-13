using UnityEngine;

public class FlyCamera : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 10f;       // 正常移动速度
    public float sprintMultiplier = 3f; // 按住 Shift 时的加速倍率

    [Header("Look Settings")]
    public float lookSpeed = 2f;        // 鼠标转向灵敏度
    public bool lockCursor = true;      // 是否锁定鼠标指针

    private float pitch = 0f; // 绕X轴旋转（上下）
    private float yaw = 0f;   // 绕Y轴旋转（左右）

    void Start()
    {
        // 初始化时获取当前相机的旋转角度
        Vector3 angles = transform.eulerAngles;
        pitch = angles.x;
        yaw = angles.y;

        if (lockCursor)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void Update()
    {
        HandleMouseLook();
        HandleMovement();
        HandleCursorLock();
    }

    private void HandleMouseLook()
    {
        // 获取鼠标输入
        yaw += lookSpeed * Input.GetAxis("Mouse X");
        pitch -= lookSpeed * Input.GetAxis("Mouse Y");

        // 限制上下低头抬头的角度，防止屏幕翻转
        pitch = Mathf.Clamp(pitch, -89f, 89f);

        // 应用旋转
        transform.eulerAngles = new Vector3(pitch, yaw, 0f);
    }

    private void HandleMovement()
    {
        // 判断是否按住 Shift 加速
        float currentSpeed = moveSpeed;
        if (Input.GetKey(KeyCode.LeftShift))
        {
            currentSpeed *= sprintMultiplier;
        }

        // 获取 WASD 输入
        float h = Input.GetAxis("Horizontal"); // A/D
        float v = Input.GetAxis("Vertical");   // W/S

        // 沿相机的本地坐标系移动
        Vector3 moveDir = new Vector3(h, 0, v);
        transform.Translate(moveDir * currentSpeed * Time.deltaTime, Space.Self);

        // 获取 QE 上下移动输入（世界坐标系下的升降）
        if (Input.GetKey(KeyCode.E))
        {
            transform.Translate(Vector3.up * currentSpeed * Time.deltaTime, Space.World);
        }
        if (Input.GetKey(KeyCode.Q))
        {
            transform.Translate(Vector3.down * currentSpeed * Time.deltaTime, Space.World);
        }
    }

    private void HandleCursorLock()
    {
        // 按 ESC 解锁鼠标，方便在 Editor 里点选东西
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // 点击鼠标左键重新锁定
        if (Input.GetMouseButtonDown(0))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }
}