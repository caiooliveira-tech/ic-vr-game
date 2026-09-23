using UnityEngine;
using UnityEngine.InputSystem;
using VRSurgery.Tissue;
using VRSurgery.Tools;

namespace VRSurgery.Cutting
{
    /// <summary>
    /// Mouse adapter for the isolated table scene. Moves the real blade and feeds its swept
    /// segments into IncisionSystem. The VR interactor and mesh generation are unchanged.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class ThoraxDesktopController : MonoBehaviour
    {
        [SerializeField] private Camera viewCamera;
        [SerializeField] private CuttableTissue tissue;
        [SerializeField] private IncisionSystem incision;
        [SerializeField] private CuttingInteractor cutter;
        [SerializeField] private BladeTip blade;
        [SerializeField] private Transform scalpel;
        [SerializeField] private float travelSpeed = 0.09f;
        [SerializeField] private float penetration = 0.0045f;

        private MeshCollider _surface;
        private SkinDeformation _skin;
        private Plane _dragPlane;
        private Vector3 _dragOrigin;
        private Vector3 _dragInitialOffset;
        private ScalpelTool _tool;
        private Vector3 _target;
        private Vector3 _surfacePoint;
        private Vector3 _direction;
        private bool _hasTarget;
        private bool _pressed;
        private bool _following;
        private Vector3 _focus;

        private void Start()
        {
            _surface = tissue.GetComponent<MeshCollider>();
            _skin = tissue.GetComponent<SkinDeformation>();
            _tool = scalpel.GetComponent<ScalpelTool>();
            _focus = tissue.transform.position;
            _direction = tissue.transform.right;
            cutter.SetRequireHeld(false);
            cutter.BindTissue(incision);
            cutter.enabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Update()
        {
            if (_skin != null && Keyboard.current != null)
            {
                if (Keyboard.current.fKey.wasPressedThisFrame) { _skin.HoldCurrentDrag(); }
                if (Keyboard.current.spaceKey.wasPressedThisFrame) { _skin.ReleaseRetraction(); }
            }
            if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame)
            {
                StopCut();
                if (_skin != null) { _skin.RestoreRestPose(); }
                incision.ResetSession();
            }

            Mouse mouse = Mouse.current;
            if (mouse == null || !Application.isFocused)
            { StopCut(); if (_skin != null) { _skin.EndDrag(); } return; }
            if (mouse.leftButton.wasPressedThisFrame && _skin != null) { _skin.RestoreRestPose(); }
            Ray ray = viewCamera.ScreenPointToRay(mouse.position.ReadValue());
            _hasTarget = _surface.Raycast(ray, out RaycastHit hit, 5f);
            if (_hasTarget)
            {
                Vector3 local = tissue.transform.InverseTransformPoint(hit.point);
                // Leave an untouched rim around the extracted patch and its boundary T-junctions.
                _hasTarget = Mathf.Abs(local.x) <= 0.07f && Mathf.Abs(local.z) <= 0.044f;
                _target = hit.point;
            }
            if (_skin != null && !mouse.leftButton.isPressed)
            {
                if (mouse.rightButton.wasPressedThisFrame && _hasTarget)
                {
                    StopCut();
                    if (_skin.BeginDrag(hit))
                    {
                        _dragOrigin = hit.point;
                        _dragInitialOffset = _skin.DragOffset;
                        _dragPlane = new Plane(viewCamera.transform.forward, hit.point);
                    }
                }
                if (_skin.IsDragging && mouse.rightButton.isPressed)
                {
                    if (_dragPlane.Raycast(ray, out float enter))
                        _skin.SetDragOffset(_dragInitialOffset + tissue.transform.InverseTransformVector(ray.GetPoint(enter) - _dragOrigin));
                    _hasTarget = false; _pressed = false;
                    return;
                }
                if (_skin.IsDragging) { _skin.EndDrag(); }
            }
            _pressed = _hasTarget && mouse.leftButton.isPressed;
            if (!_pressed) { StopCut(); }

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f && !_pressed)
            {
                Vector3 offset = viewCamera.transform.position - _focus;
                float distance = Mathf.Clamp(offset.magnitude - Mathf.Sign(scroll) * 0.06f, 0.22f, 1.1f);
                viewCamera.transform.position = _focus + offset.normalized * distance;
            }
        }

        private void FixedUpdate()
        {
            if (!_hasTarget) { return; }
            Vector3 outward = tissue.transform.up;
            if (!_pressed)
            {
                _surfacePoint = _target;
                PlaceBlade(_target + outward * 0.012f, outward);
                blade.ResetHistory();
                return;
            }
            if (!_following)
            {
                _surfacePoint = _target;
                _following = true;
                PlaceBlade(_surfacePoint - outward * penetration, outward);
                blade.ResetHistory();
                blade.Sample(out _, out _);
                return;
            }

            Vector3 next = Vector3.MoveTowards(_surfacePoint, _target, travelSpeed * Time.fixedDeltaTime);
            // Reproject each physics step: interpolation must follow the curved chest, not a chord.
            if (!_surface.Raycast(new Ray(next + outward * 0.06f, -outward), out RaycastHit hit, 0.12f))
            {
                StopCut();
                return;
            }
            Vector3 delta = Vector3.ProjectOnPlane(hit.point - _surfacePoint, hit.normal);
            if (delta.sqrMagnitude > 1e-8f) { _direction = delta.normalized; }
            _surfacePoint = hit.point;
            PlaceBlade(hit.point - hit.normal * penetration, hit.normal);
            // Mouse motion arrives at render frequency, not physics frequency. A stationary
            // sample is a pause in the same held stroke, not a release/restart of the incision.
            // Call the existing public contact API only for movement, preserving one baseline.
            if (delta.sqrMagnitude > 1e-8f && _tool.HasCapability(ToolCapability.Cut))
            {
                blade.Sample(out Vector3 previous, out Vector3 current);
                incision.ProcessBladeSegment(previous, current, Time.fixedDeltaTime, _tool, blade);
            }
        }

        private void PlaceBlade(Vector3 tipPosition, Vector3 normal)
        {
            Vector3 tangent = Vector3.ProjectOnPlane(_direction, normal).normalized;
            if (tangent.sqrMagnitude < 0.01f) { tangent = tissue.transform.right; }
            scalpel.rotation = Quaternion.LookRotation((tangent - normal * 0.6f).normalized, normal);
            scalpel.position += tipPosition - blade.transform.position;
        }

        private void StopCut()
        {
            _pressed = false;
            _following = false;
            if (cutter != null) { cutter.enabled = false; }
            if (incision != null) { incision.EndBladeContact(); }
            if (blade != null) { blade.ResetHistory(); }
        }

        private void OnApplicationFocus(bool focused)
        { if (!focused) { StopCut(); if (_skin != null) { _skin.EndDrag(); } } }
        private void OnDisable()
        { StopCut(); if (_skin != null) { _skin.EndDrag(); } }
    }
}
