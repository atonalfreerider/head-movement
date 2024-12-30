using UnityEngine;
using UnityEngine.InputSystem;

namespace VRTKLite.Controllers
{
    public class CameraControl : MonoBehaviour
    {
        public float Speed = 0.3f;          // Speed of movement and rotation
        public Vector3 Center = Vector3.zero; // The point the camera orbits around
        float rad = 2f;            // Radius (distance from center)
        float alpha = Mathf.PI / 2;      // Polar angle (from Y-axis)
        float phi = Mathf.PI / 2;              // Azimuthal angle (around Y-axis)
    
        public delegate void MovementUpdate();
        public MovementUpdate MovementUpdater;

        void Start()
        {
            UpdateCameraPosition();
            transform.LookAt(Center);
        }

        void Update()
        {
            MoveCamera();
            transform.LookAt(Center); // Ensure the camera always looks at the center
        }

        void MoveCamera()
        {
            bool isMoving = false; // Flag to check if any movement key is pressed

            // Zoom In (W key)
            if (Keyboard.current.wKey.isPressed)
            {
                rad -= Speed * Time.deltaTime;
                rad = Mathf.Max(rad, 0.3f); // Clamp to a minimum radius
                isMoving = true;
            }

            // Zoom Out (S key)
            if (Keyboard.current.sKey.isPressed)
            {
                rad += Speed * Time.deltaTime;
                rad = Mathf.Min(rad, 10f); // Clamp to a maximum radius
                isMoving = true;
            }

            // Orbit Left (A key) - Adjust phi
            if (Keyboard.current.dKey.isPressed)
            {
                phi += Speed * Time.deltaTime;
                phi = NormalizeAngle(phi);
                isMoving = true;
            }

            // Orbit Right (D key) - Adjust phi
            if (Keyboard.current.aKey.isPressed)
            {
                phi -= Speed * Time.deltaTime;
                phi = NormalizeAngle(phi);
                isMoving = true;
            }

            // Move Up (E key) - Adjust alpha (towards north pole)
            if (Keyboard.current.qKey.isPressed)
            {
                alpha += Speed * Time.deltaTime;
                alpha = Mathf.Clamp(alpha, 0.01f, Mathf.PI - 0.01f); // Prevent gimbal lock at poles
                isMoving = true;
            }

            // Move Down (Q key) - Adjust alpha (towards south pole)
            if (Keyboard.current.eKey.isPressed)
            {
                alpha -= Speed * Time.deltaTime;
                alpha = Mathf.Clamp(alpha, 0.01f, Mathf.PI - 0.01f); // Prevent gimbal lock at poles
                isMoving = true;
            }

            // Update the camera position based on spherical coordinates
            UpdateCameraPosition();

            // Invoke movement update if any key was pressed
            if (isMoving && MovementUpdater != null)
            {
                MovementUpdater.Invoke();
            }
        }

        /// <summary>
        /// Updates the camera's position based on the current spherical coordinates.
        /// </summary>
        void UpdateCameraPosition()
        {
            // Convert spherical coordinates to Cartesian coordinates
            Vector3 newPosition = Center + new Vector3(
                rad * Mathf.Sin(alpha) * Mathf.Cos(phi), // X component
                rad * Mathf.Cos(alpha),                  // Y component
                rad * Mathf.Sin(alpha) * Mathf.Sin(phi)  // Z component
            );

            transform.position = newPosition;
        }

        /// <summary>
        /// Normalizes an angle to the range [-PI, PI].
        /// </summary>
        /// <param name="angle">The angle in radians.</param>
        /// <returns>The normalized angle.</returns>
        static float NormalizeAngle(float angle)
        {
            while (angle > Mathf.PI)
                angle -= 2 * Mathf.PI;
            while (angle < -Mathf.PI)
                angle += 2 * Mathf.PI;
            return angle;
        }
    }
}
