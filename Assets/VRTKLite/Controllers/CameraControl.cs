using UnityEngine;
using UnityEngine.InputSystem;

namespace VRTKLite.Controllers
{
    public class CameraControl : MonoBehaviour
    {
        // From:
        // http://answers.unity3d.com/questions/29741/mouse-look-script.html

        public float Speed = .3f;
        public Vector3 Center = new(0f, 0.7f, 0f);
        float alpha = 0;
        float height = 0.7f;
        float rad = 2;

        void Update()
        {
            MoveCamera();
            transform.LookAt(new Vector3(Center.x, 0.7f, Center.z));
        }

        static float ClampAngle(float angle, float min, float max)
        {
            if (angle < -360f)
            {
                angle += 360f;
            }

            if (angle > 360f)
            {
                angle -= 360f;
            }

            return Mathf.Clamp(angle, min, max);
        }

        void MoveCamera()
        {
            if (Keyboard.current.wKey.isPressed)
            {
                rad -= Speed * Time.deltaTime;
                if (rad < .3f)
                {
                    rad = .3f;
                }
            }
            
            if (Keyboard.current.sKey.isPressed)
            {
                rad += Speed * Time.deltaTime;
                if (rad > 10f)
                {
                    rad = 10f;
                }
            }

            if (Keyboard.current.aKey.isPressed)
            {
                alpha += Speed * Time.deltaTime;
                if (alpha > Mathf.PI)
                {
                    alpha = -Mathf.PI;
                }
            }

            if (Keyboard.current.dKey.isPressed)
            {
                alpha -= Time.deltaTime * Speed;
                if (alpha < -Mathf.PI)
                {
                    alpha = Mathf.PI;
                }
            }

            if (Keyboard.current.qKey.isPressed)
            {
                height -= Speed * Time.deltaTime;
            }

            if (Keyboard.current.eKey.isPressed)
            {
                height += Speed * Time.deltaTime;
            }
            
            transform.position = Center + new Vector3(rad * Mathf.Sin(alpha), height, rad * Mathf.Cos(alpha));
        }
    }
}