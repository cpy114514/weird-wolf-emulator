using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody2D))]
[RequireComponent(typeof(Collider2D))]
public sealed class SimpleWolfController : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 2.5f;
    [SerializeField] private float acceleration = 18f;
    [SerializeField] private float deceleration = 22f;
    [SerializeField] private float jumpSpeed = 4.5f;
    [SerializeField] private LayerMask groundLayers = ~0;
    [SerializeField] private string idleStateName = "weirdwolf_idle";
    [SerializeField] private string walkStateName = "wierdwolf_walk";
    [SerializeField] private float walkAnimationSpeed = 1f;
    [SerializeField] private float animationSpeedChangeRate = 6f;
    [SerializeField] private float walkTransitionSeconds = 0.05f;
    [SerializeField] private float straightenTransitionSeconds = 0.14f;

    private Rigidbody2D body;
    private Collider2D bodyCollider;
    private Animator animator;
    private int idleStateHash;
    private int walkStateHash;
    private int activeStateHash;
    private float facingScaleX;
    private float animationSpeed;
    private float moveInput;
    private bool jumpQueued;

    private void Awake()
    {
        body = GetComponent<Rigidbody2D>();
        bodyCollider = GetComponent<Collider2D>();
        animator = GetComponent<Animator>();
        idleStateHash = Animator.StringToHash(idleStateName);
        walkStateHash = Animator.StringToHash(walkStateName);
        facingScaleX = Mathf.Abs(transform.localScale.x);

        body.freezeRotation = true;
        body.gravityScale = 2.5f;

        if (animator != null && animator.HasState(0, idleStateHash))
        {
            animator.Play(idleStateHash, 0, 0f);
            activeStateHash = idleStateHash;
            animationSpeed = 1f;
            animator.speed = 1f;
        }
        else if (animator != null && animator.HasState(0, walkStateHash))
        {
            animator.Play(walkStateHash, 0, 0f);
            animator.Update(0f);
            activeStateHash = walkStateHash;
            animator.speed = 0f;
        }
    }

    private void Update()
    {
        moveInput = ReadMoveInput();
        UpdateFacing();
        UpdateAnimation();

        if (ReadJumpPressed())
        {
            jumpQueued = true;
        }
    }

    private void FixedUpdate()
    {
        var velocity = body.linearVelocity;
        var targetSpeed = moveInput * moveSpeed;
        var speedChangeRate = Mathf.Abs(moveInput) > 0.01f ? acceleration : deceleration;
        velocity.x = Mathf.MoveTowards(velocity.x, targetSpeed, speedChangeRate * Time.fixedDeltaTime);

        if (jumpQueued && IsGrounded())
        {
            velocity.y = jumpSpeed;
        }

        body.linearVelocity = velocity;
        jumpQueued = false;
    }

    private bool IsGrounded()
    {
        return bodyCollider != null && bodyCollider.IsTouchingLayers(groundLayers);
    }

    private void UpdateFacing()
    {
        if (Mathf.Approximately(moveInput, 0f))
        {
            return;
        }

        var scale = transform.localScale;
        scale.x = moveInput < 0f ? -facingScaleX : facingScaleX;
        transform.localScale = scale;
    }

    private void UpdateAnimation()
    {
        if (animator == null)
        {
            return;
        }

        var isMoving = Mathf.Abs(moveInput) > 0.01f || Mathf.Abs(body.linearVelocity.x) > 0.05f;
        if (isMoving && animator.HasState(0, walkStateHash))
        {
            CrossFadeIfNeeded(walkStateHash, walkTransitionSeconds);
        }
        else if (!isMoving && animator.HasState(0, idleStateHash))
        {
            CrossFadeIfNeeded(idleStateHash, straightenTransitionSeconds);
        }

        var speedRatio = moveSpeed > 0f ? Mathf.Clamp01(Mathf.Abs(body.linearVelocity.x) / moveSpeed) : 0f;
        var targetAnimationSpeed = isMoving ? Mathf.Lerp(0.55f, walkAnimationSpeed, speedRatio) : 1f;
        animationSpeed = Mathf.MoveTowards(
            animationSpeed,
            targetAnimationSpeed,
            animationSpeedChangeRate * Time.deltaTime);
        animator.speed = animationSpeed;
    }

    private void CrossFadeIfNeeded(int stateHash, float duration)
    {
        if (activeStateHash == stateHash)
        {
            return;
        }

        animator.CrossFadeInFixedTime(stateHash, duration, 0, 0f);
        activeStateHash = stateHash;
    }

    private static float ReadMoveInput()
    {
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return 0f;
        }

        var left = keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed;
        var right = keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed;
        return (right ? 1f : 0f) - (left ? 1f : 0f);
#else
        var left = Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow);
        var right = Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow);
        return (right ? 1f : 0f) - (left ? 1f : 0f);
#endif
    }

    private static bool ReadJumpPressed()
    {
#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        return keyboard != null && keyboard.spaceKey.wasPressedThisFrame;
#else
        return Input.GetKeyDown(KeyCode.Space);
#endif
    }
}
