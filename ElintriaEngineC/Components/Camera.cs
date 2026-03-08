using ElintriaEngineC.Components;
using OpenTK.Mathematics;

namespace Elintria.Engine.Rendering
{
    public class Camera : Component
    {
        public Vector3 Position;
        public float Pitch;
        public float Yaw = -90f; // look forward by default

        public float Speed = 5f;
        public float Sensitivity = 0.15f;
        public float Fov = 60f;

        public Camera(Vector3 position)
        {
            Position = position;
        }

        public Matrix4 GetViewMatrix()
        {
            return Matrix4.LookAt(
                Position,
                Position + Front,
                Vector3.UnitY
            );
        }

        public Vector3 Front
        {
            get
            {
                Vector3 front;
                front.X = MathF.Cos(MathHelper.DegreesToRadians(Yaw)) *
                          MathF.Cos(MathHelper.DegreesToRadians(Pitch));
                front.Y = MathF.Sin(MathHelper.DegreesToRadians(Pitch));
                front.Z = MathF.Sin(MathHelper.DegreesToRadians(Yaw)) *
                          MathF.Cos(MathHelper.DegreesToRadians(Pitch));
                return Vector3.Normalize(front);
            }
        }

        public Vector3 Right => Vector3.Normalize(Vector3.Cross(Front, Vector3.UnitY));
        public Vector3 Up => Vector3.Normalize(Vector3.Cross(Right, Front));
    }
}
