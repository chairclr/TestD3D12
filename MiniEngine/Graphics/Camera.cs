using System.Numerics;

namespace MiniEngine.Graphics;

public class Camera
{
    private Vector3 _position;
    private Quaternion _rotation;

    // Cached values of view and projection matrix
    private Matrix4x4 _viewMatrix;
    private Matrix4x4 _projectionMatrix;

    // Cached values of forward, left, and up unit vectors
    private Vector3 _forward;
    private Vector3 _left;
    private Vector3 _up;

    public Matrix4x4 ViewMatrix => _viewMatrix;
    public Matrix4x4 ProjectionMatrix => _projectionMatrix;

    public Vector3 Forward => _forward;
    public Vector3 Backward => -_forward;

    public Vector3 Left => _left;
    public Vector3 Right => -_left;

    public Vector3 Up => _up;
    public Vector3 Down => -_up;

    public Vector3 Position
    {
        get => _position;
        set
        {
            _position = value;
            UpdateView();
        }
    }

    public Quaternion Rotation
    {
        get => _rotation;
        set
        {
            _rotation = value;
            UpdateView();
        }
    }

    public Vector3 YawPitchRoll
    {
        set => Rotation = Quaternion.CreateFromYawPitchRoll(value.X, value.Y, value.Z);
    }

    public Camera(float fovDegrees, float aspectRatio, float nearZ, float farZ)
    {
        SetProjection(fovDegrees, aspectRatio, nearZ, farZ);

        Rotation = Quaternion.Identity;
    }

    public void SetProjection(float fovDegrees, float aspectRatio, float nearZ, float farZ)
    {
        float fovRadians = (fovDegrees / 360.0f) * (MathF.PI * 2f);

        _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(fovRadians, aspectRatio, nearZ, farZ);
    }

    private void UpdateView()
    {
        Matrix4x4 rotationMatrix = Matrix4x4.CreateFromQuaternion(_rotation);

        _left = Vector3.Transform(-Vector3.UnitX, rotationMatrix);
        _up = Vector3.Transform(Vector3.UnitY, rotationMatrix);
        _forward = Vector3.Transform(Vector3.UnitZ, rotationMatrix);

        _viewMatrix = Matrix4x4.CreateLookAt(_position, _position + _forward, _up);
    }
}